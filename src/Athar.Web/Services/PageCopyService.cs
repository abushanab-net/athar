using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Athar.Data;
using Athar.Domain.Model;
using Athar.Domain.Wiki;
using Athar.Domain.Memory;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

public sealed record CopyResult(Guid PageId, int Pages);
public sealed record CopyTarget(Guid SpaceId, string Name);

/// <summary>
/// T117 (U-5): Duplicate a page, or copy it to another space of the same workspace, with its subpages. Every copy is a draft
/// (U-1) made by the person copying, with new ids. Content goes through <see cref="PageOpsService"/> as the editor's JSON (the
/// live document if it's open), so formatting survives; attachments are copied as new files and their links rewritten;
/// comment anchors are dropped (the threads stay on the original). People only, from the wiki: no MCP or REST contract.
/// <list type="bullet">
/// <item>Only pages the person can read are copied. A restricted page (or one under it) is never copied: the copy would
/// lose its restriction. Copying a restricted page itself is refused.</item>
/// <item>A confidential space's pages only go to a confidential space (the strictest classification wins, FR-CL).</item>
/// <item>The person must be an editor where the copy lands (<see cref="PageService.CreateAsync"/> checks it).</item>
/// </list>
/// The op id makes a retry land on the same pages: new ids are derived from it. Links and mentions between pages of the
/// copied subtree still point at the originals. Copied attachment text is searchable once the copy's content is saved (the
/// save queues the page's re-index), so a page with files but no content isn't indexed until someone edits it.
/// </summary>
public sealed class PageCopyService(AtharDbContext db, PageOpsService ops, PageService pages, AttachmentStore store)
{
    /// <summary>
    /// ponytail: the copy runs page by page in the request (two collab round trips per page, plus files); above this it's
    /// refused. A background job (with progress) if bigger subtrees need copying.
    /// </summary>
    public const int MaxPages = 200;

    /// <summary>Spaces this page can be copied to: same workspace, editor or admin there, and classification allowing it.</summary>
    public async Task<List<CopyTarget>> TargetsAsync(Actor actor, Guid pageId, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var from = await SourceSpaceAsync(actor, pageId, ct);
        var candidates = await db.SpacesFor(actor.Caller).AsNoTracking()
            .Where(s => s.WorkspaceId == from.WorkspaceId && (!from.IsConfidential || s.IsConfidential))
            .OrderBy(s => s.Name).Select(s => new CopyTarget(s.Id, s.Name)).ToListAsync(ct);
        var targets = new List<CopyTarget>();
        foreach (var t in candidates)
            if (await db.SpaceRoleAsync(actor.UserId, t.SpaceId, ct) is SpaceRole.Editor or SpaceRole.Admin) targets.Add(t);
        return targets;
    }

    /// <summary>Duplicate: a copy beside the original, in the same space, titled "<paramref name="title"/>" (default "… (copy)").</summary>
    public async Task<CopyResult> DuplicateAsync(Actor actor, Guid pageId, string opId, string? title = null, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var page = await db.PagesFor(actor.Caller).AsNoTracking().SingleOrDefaultAsync(p => p.Id == pageId && p.DeletedAt == null, ct)
            ?? throw new AtharError("not_found", "Page not found.");
        return await CopyAsync(actor, page.Id, page.SpaceId, page.ParentId, title ?? $"{page.Title} (copy)", opId, ct);
    }

    /// <summary>Copy to space (U-5): under <paramref name="parentId"/> or at the top of <paramref name="spaceId"/>.</summary>
    public Task<CopyResult> CopyToSpaceAsync(Actor actor, Guid pageId, Guid spaceId, Guid? parentId, string opId, CancellationToken ct = default) =>
        CopyAsync(actor, pageId, spaceId, parentId, null, opId, ct);

    private async Task<CopyResult> CopyAsync(Actor actor, Guid rootId, Guid spaceId, Guid? parentId, string? rootTitle, string opId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opId)) throw new AtharError("invalid_input", "op_id is required.");
        if (actor.Caller.Agent) throw new AtharError("forbidden", "Copying pages is a wiki action.");
        db.CurrentOrgId = actor.OrgId;
        var caller = actor.Caller;
        var from = await SourceSpaceAsync(actor, rootId, ct);
        var to = await db.Spaces.AsNoTracking().SingleOrDefaultAsync(s => s.Id == spaceId, ct) ?? throw new AtharError("not_found", "Space not found.");
        if (to.WorkspaceId != from.WorkspaceId) throw new AtharError("forbidden", "Pages can only be copied within their workspace.");
        if (from.IsConfidential && !to.IsConfidential) throw new AtharError("forbidden", "A confidential space's pages can only be copied to a confidential space.");

        // What the person reads in the source space, minus restricted pages and everything under them.
        var readable = await db.PagesFor(caller).AsNoTracking().Where(p => p.SpaceId == from.Id && p.DeletedAt == null).ToListAsync(ct);
        var ids = readable.Select(p => p.Id).ToList();
        var restricted = (await db.PageRestrictions.Where(r => ids.Contains(r.PageId)).Select(r => r.PageId).Distinct().ToListAsync(ct)).ToHashSet();
        var root = readable.Single(p => p.Id == rootId);
        if (restricted.Contains(root.Id) || root.AncestorIds.Any(restricted.Contains))
            throw new AtharError("forbidden", "Restricted pages can't be copied: the copy would lose the restriction.");
        var children = readable.Where(p => !restricted.Contains(p.Id)).ToLookup(p => p.ParentId);

        // Parents before children, each placed under its copied parent.
        var order = new List<(Page Source, Guid? NewParent)> { (root, parentId) };
        for (var i = 0; i < order.Count; i++)
            order.AddRange(children[order[i].Source.Id].OrderBy(c => c.Position).Select(c => (c, (Guid?)NewId(opId, order[i].Source.Id))));

        if (order.Count > MaxPages)
            throw new AtharError("too_large", $"This page has {order.Count} pages with its subpages; at most {MaxPages} can be copied at once.");

        var wiki = new WikiActor(actor.UserId, actor.OrgId);
        foreach (var (source, newParent) in order)
        {
            var id = NewId(opId, source.Id);
            if (!await db.Pages.AnyAsync(p => p.Id == id, ct))
                await pages.CreateAsync(wiki, spaceId, newParent, source.Id == rootId ? rootTitle ?? source.Title : source.Title, ct, id: id, draft: true);
            var json = (await ops.GetPageAsync(actor, source.Id, "json", ct)).Content;
            json = await CopyAttachmentsAsync(source.Id, id, opId, json, ct);
            var doc = JsonNode.Parse(json)!.AsObject();
            WithoutCommentAnchors(doc);
            if (doc["content"] is JsonArray { Count: > 0 })
                await ops.RunAsync(actor, id, $"{opId}:{source.Id:N}", new PageOp("restore", Json: JsonSerializer.Deserialize<JsonElement>(doc.ToJsonString())), ct);
        }

        var newRoot = NewId(opId, rootId);
        db.Audit(actor.OrgId, actor.UserId, "page.copy", "page", newRoot.ToString(),
            after: new { source_page_id = rootId, space_id = spaceId, parent_id = parentId, pages = order.Count });
        await db.SaveChangesAsync(ct);
        return new(newRoot, order.Count);
    }

    /// <summary>The source page's space, if the person can read the page.</summary>
    private async Task<Space> SourceSpaceAsync(Actor actor, Guid pageId, CancellationToken ct) =>
        await db.PagesFor(actor.Caller).Where(p => p.Id == pageId && p.DeletedAt == null)
            .Join(db.Spaces, p => p.SpaceId, s => s.Id, (p, s) => s).AsNoTracking().SingleOrDefaultAsync(ct)
        ?? throw new AtharError("not_found", "Page not found.");

    /// <summary>Each attachment of <paramref name="from"/> as a new file on <paramref name="to"/>; links in the content follow.</summary>
    private async Task<string> CopyAttachmentsAsync(Guid from, Guid to, string opId, string json, CancellationToken ct)
    {
        foreach (var a in await db.Attachments.AsNoTracking().Where(a => a.PageId == from).ToListAsync(ct))
        {
            var id = NewId(opId, a.Id);
            if (!await db.Attachments.AnyAsync(x => x.Id == id, ct))
            {
                var key = AttachmentStore.NewKey();
                byte[] bytes;
                await using (var file = store.Open(a.StorageKey)) { using var copy = new MemoryStream(); await file.CopyToAsync(copy, ct); bytes = copy.ToArray(); }
                await store.WriteAsync(key, bytes, ct);
                db.Attachments.Add(new Attachment
                {
                    Id = id, PageId = to, FileName = a.FileName, MimeSniffed = a.MimeSniffed, Size = a.Size, StorageKey = key,
                    TextContent = a.TextContent, ExtractStatus = a.ExtractStatus,
                });
                if (a.ExtractStatus == ExtractStatus.Pending)
                    db.Jobs.Add(new Job { Kind = AttachmentRules.ExtractKind, Key = id.ToString(), RunAfter = DateTimeOffset.UtcNow,
                        Payload = JsonSerializer.Serialize(new AttachmentRules.ExtractPayload(id), IndexJobs.Json) });
                await db.SaveChangesAsync(ct);
            }
            json = json.Replace($"/attachments/{a.Id}", $"/attachments/{id}", StringComparison.OrdinalIgnoreCase);
        }
        return json;
    }

    /// <summary>Removes <c>commentAnchor</c> marks: the threads belong to the original page.</summary>
    public static void WithoutCommentAnchors(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["marks"] is JsonArray marks)
                {
                    foreach (var m in marks.Where(m => m?["type"]?.GetValue<string>() == "commentAnchor").ToList()) marks.Remove(m);
                    if (marks.Count == 0) o.Remove("marks");
                }
                foreach (var (_, child) in o.ToList()) WithoutCommentAnchors(child);
                break;
            case JsonArray a:
                foreach (var child in a) WithoutCommentAnchors(child);
                break;
        }
    }

    /// <summary>A stable id for (op, original): a retry with the same op id finds the pages and files it already made.</summary>
    private static Guid NewId(string opId, Guid original)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{opId}\n{original:N}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // version 8 (custom)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
