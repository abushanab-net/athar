using System.Text.Json;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Domain.Wiki;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

public sealed record AttachmentView(Guid AttachmentId, Guid PageId, string FileName, string Mime, long Size, string Url, string ExtractStatus);

/// <summary>What <c>GET /attachments/{id}</c> serves.</summary>
public sealed record AttachmentFile(Stream Content, string Mime, string FileName, bool Inline);

/// <summary>
/// FR-PG-06 uploads and downloads. Uploading needs edit rights on the page (agents: the write scope and the space's AI
/// policy too); downloading needs read rights. The extraction job (worker, <c>extract-attachment</c>) is queued in the
/// same transaction as the row.
/// </summary>
public sealed class AttachmentService(AtharDbContext db, AttachmentStore store, TimeProvider clock, AiSettingsResolver? ai = null)
{
    /// <param name="maxBytes">25 MB for people and agents (FR-PG-06); imports pass no cap of their own (T164, owner 2026-10-15).</param>
    public async Task<AttachmentView> UploadAsync(Actor actor, Guid pageId, string? fileName, string? declaredType, Stream content, CancellationToken ct,
        long maxBytes = AttachmentRules.MaxBytes)
    {
        await RequireAsync(actor, pageId, PageAction.Edit, ct);

        // Streamed to its file, counting as it goes: an oversized upload stops at one chunk past the limit and is removed.
        var key = AttachmentStore.NewKey();
        var path = store.PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var head = new byte[16];
        var headLength = 0;
        long size = 0;
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(chunk, ct)) > 0)
                {
                    if (headLength < head.Length)
                    {
                        var take = Math.Min(head.Length - headLength, read);
                        Array.Copy(chunk, 0, head, headLength, take);
                        headLength += take;
                    }
                    size += read;
                    if (size > maxBytes)
                        throw new AtharError("invalid_input", maxBytes == AttachmentRules.MaxBytes ? "Files are limited to 25 MB." : $"Files are limited to {maxBytes / (1024 * 1024)} MB.");
                    await file.WriteAsync(chunk.AsMemory(0, read), ct);
                }
            }
            if (size == 0) throw new AtharError("invalid_input", "The file is empty.");
            var sniffed = AttachmentRules.Sniff(head.AsSpan(0, headLength))
                ?? throw new AtharError("invalid_input", "Only PNG, JPEG, GIF, WebP, PDF and DOCX files are accepted.");
            if (!AttachmentRules.DeclaredMatches(declaredType, sniffed))
                throw new AtharError("invalid_input", $"The file is {sniffed}, not {declaredType}.");
            if (sniffed == AttachmentRules.Docx)
            {
                await using var docx = File.OpenRead(path);
                if (AttachmentRules.CheckDocx(docx) is { } why) throw new AtharError("invalid_input", why);
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        var mime = AttachmentRules.Sniff(head.AsSpan(0, headLength))!;
        // Very large documents are kept but not read: extraction holds a file in memory (T164).
        var extract = AttachmentRules.HasText(mime) && size <= AttachmentRules.MaxExtractBytes;
        try
        {
            if (AttachmentRules.Vision(mime) && size <= AiGatewayClient.MaxImageBytes && ai is not null)
            {
                var settings = await ai.ResolveAsync(actor.OrgId, ct);
                extract = settings.Gateway && !string.IsNullOrWhiteSpace(settings.VisionModel)
                    && await db.Pages.AnyAsync(p => p.Id == pageId && p.DeletedAt == null && db.Spaces.Any(s => s.Id == p.SpaceId && s.AiPolicy.Read), ct);
            }
            await RequireAsync(actor, pageId, PageAction.Edit, ct);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        var attachment = new Attachment
        {
            OrgId = actor.OrgId, PageId = pageId, FileName = AttachmentRules.FileName(fileName), MimeSniffed = mime, Size = size,
            StorageKey = key, ExtractStatus = extract ? ExtractStatus.Pending : ExtractStatus.Skipped,
        };
        db.Attachments.Add(attachment);
        if (extract)
            db.Jobs.Add(new Job
            {
                OrgId = actor.OrgId, Kind = AttachmentRules.ExtractKind, Key = attachment.Id.ToString(), Status = JobStatus.Ready, RunAfter = clock.GetUtcNow(),
                Payload = JsonSerializer.Serialize(new AttachmentRules.ExtractPayload(attachment.Id), IndexJobs.Json),
            });
        db.Audit(actor.OrgId, actor.UserId, "attachment.upload", "attachment", attachment.Id,
            after: new { page_id = pageId, attachment.FileName, mime, size }, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return View(attachment);
    }

    /// <summary>The page's attachments, for anyone who can read it.</summary>
    public async Task<List<AttachmentView>> ListAsync(Actor actor, Guid pageId, CancellationToken ct)
    {
        await RequireAsync(actor, pageId, PageAction.Read, ct);
        var rows = await db.Attachments.AsNoTracking().Where(a => a.PageId == pageId).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return [.. rows.Select(View)];
    }

    public async Task<AttachmentFile> OpenAsync(Actor actor, Guid attachmentId, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var a = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attachmentId, ct)
            ?? throw new AtharError("not_found", "Attachment not found.");
        await RequireAsync(actor, a.PageId, PageAction.Read, ct);
        return new(store.Open(a.StorageKey), a.MimeSniffed, a.FileName, AttachmentRules.Inline(a.MimeSniffed));
    }

    private static AttachmentView View(Attachment a) =>
        new(a.Id, a.PageId, a.FileName, a.MimeSniffed, a.Size, $"/attachments/{a.Id}", a.ExtractStatus.ToString().ToLowerInvariant());

    /// <summary>Page rights through Athar.Domain.Access (which applies the AI policy to agents); unknown and unreadable look alike.</summary>
    private async Task RequireAsync(Actor actor, Guid pageId, PageAction action, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var caller = await db.ActorAsync(actor.UserId, actor.CredentialId, ct) ?? throw new AtharError("unauthorized", "The user is inactive or the credential is revoked.");
        if (action != PageAction.Read && caller.Agent && !actor.Scopes.Contains("write")) throw new AtharError("forbidden", "The token lacks the 'write' scope.");
        if (await db.CanAsync(caller, pageId, action, ct) && await db.Pages.AnyAsync(p => p.Id == pageId && p.DeletedAt == null, ct)) return;
        throw action != PageAction.Read && await db.CanAsync(caller, pageId, PageAction.Read, ct)
            ? new AtharError("forbidden", "You can't change this page.")
            : new AtharError("not_found", "Page not found.");
    }
}
