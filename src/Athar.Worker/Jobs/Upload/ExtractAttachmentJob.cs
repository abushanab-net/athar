using System.Text;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Domain.Wiki;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace Athar.Worker.Jobs.Upload;

/// <summary>
/// <c>extract-attachment</c> (FR-PG-06): the text of an uploaded PDF or DOCX, one file at a time, with a 30 s parser timeout,
/// the DOCX zip-bomb limits re-checked, and the text capped at 2 MB. Configured vision transcribes supported raster images.
/// The page's next <c>embed-page</c> includes the text, so search and its permissions treat it as part of the page.
/// </summary>
public sealed class ExtractAttachmentJob(AttachmentStore store, TimeProvider clock, ILogger<ExtractAttachmentJob> log, AiSettingsResolver? ai = null) : IJob
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public string Kind => AttachmentRules.ExtractKind;

    public async Task RunAsync(Job job, AtharDbContext db, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<AttachmentRules.ExtractPayload>(job.Payload, IndexJobs.Json)!;
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == p.AttachmentId, ct);
        if (attachment is not { ExtractStatus: ExtractStatus.Pending }) return; // gone, or already done
        var vision = AttachmentRules.Vision(attachment.MimeSniffed);
        void Finish(ExtractStatus status)
        {
            if (vision) db.Audit(job.OrgId, null, "attachment.vision", "attachment", attachment.Id,
                before: new { Status = attachment.ExtractStatus.ToString() },
                after: new { Status = status.ToString(), TextCharacters = attachment.TextContent?.Length ?? 0 });
            attachment.ExtractStatus = status;
        }
        var page = await db.Pages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attachment.PageId && x.DeletedAt == null, ct);
        if (page is null) { Finish(ExtractStatus.Skipped); return; }

        string? text, error;
        if (vision)
        {
            var settings = ai is null ? null : await ai.ResolveAsync(job.OrgId, ct);
            if (settings is null || !settings.Gateway || string.IsNullOrWhiteSpace(settings.VisionModel)
                || attachment.Size > AiGatewayClient.MaxImageBytes || !await db.Spaces.AnyAsync(s => s.Id == page.SpaceId && s.AiPolicy.Read, ct))
            { Finish(ExtractStatus.Skipped); return; }
            try
            {
                var path = store.PathOf(attachment.StorageKey);
                if (new FileInfo(path).Length > AiGatewayClient.MaxImageBytes) throw new AiProviderException("configuration_invalid");
                var bytes = await File.ReadAllBytesAsync(path, ct);
                text = await new AiGatewayClient(settings).TranscribeAsync(bytes, attachment.MimeSniffed, ct);
                error = null;
            }
            catch (AiProviderException e) when (e.Retryable) { throw new JobDeferred(e.Code, TimeSpan.FromMinutes(1)); }
            catch (AiProviderException e)
            { text = null; error = e.Code; }
            if (!await db.Pages.AsNoTracking().AnyAsync(p => p.Id == page.Id && p.DeletedAt == null
                && db.Spaces.Any(s => s.Id == p.SpaceId && s.AiPolicy.Read), ct))
            { Finish(ExtractStatus.Skipped); return; }
        }
        else
        {
            await OneAtATime.WaitAsync(ct);
            try
            {
                var bytes = await File.ReadAllBytesAsync(store.PathOf(attachment.StorageKey), ct);
                (text, error) = await WithTimeoutAsync(() => Parse(attachment.MimeSniffed, bytes), AttachmentRules.ExtractTimeout);
            }
            finally { OneAtATime.Release(); }
        }

        if (text is null)
        {
            // Permanent failure: keep the upload and expose its failed status.
            log.LogWarning("Attachment {Id} text extraction failed: {Error}", attachment.Id, error);
            Finish(ExtractStatus.Failed);
            return;
        }
        page = await db.Pages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attachment.PageId && x.DeletedAt == null, ct);
        if (page is null) { Finish(ExtractStatus.Skipped); return; }
        attachment.TextContent = text.Length > AttachmentRules.MaxTextChars ? text[..AttachmentRules.MaxTextChars] : text;
        Finish(ExtractStatus.Done);
        if (vision && string.IsNullOrWhiteSpace(text)) return;
        db.Jobs.Add(new Job
        {
            OrgId = page.OrgId, Kind = IndexJobs.EmbedPage, Key = $"{page.Id}:{page.Revision}:attachment:{attachment.Id}",
            Status = JobStatus.Ready, RunAfter = clock.GetUtcNow(),
            Payload = JsonSerializer.Serialize(new IndexJobs.PagePayload(page.Id, page.Revision), IndexJobs.Json),
        });
    }

    /// <summary>
    /// Runs a parser on its own thread and stops waiting at <paramref name="timeout"/>.
    /// ponytail: PdfPig and OpenXml can't be cancelled, so a timed-out parse keeps its thread until it ends; move parsing
    /// to a child process if that ever piles up.
    /// </summary>
    public static async Task<(string? Text, string? Error)> WithTimeoutAsync(Func<string> parse, TimeSpan timeout)
    {
        try { return (await Task.Run(parse).WaitAsync(timeout), null); }
        catch (TimeoutException) { return (null, $"parsing took longer than {timeout.TotalSeconds:0} s"); }
        catch (Exception e) { return (null, e.Message); }
    }

    public static string Parse(string mime, byte[] bytes) => mime switch
    {
        AttachmentRules.Pdf => Pdf(bytes),
        AttachmentRules.Docx => Docx(bytes),
        _ => throw new InvalidOperationException($"No text in {mime}."),
    };

    internal static string Pdf(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        var text = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            text.AppendLine(string.Join(' ', page.GetWords().Select(w => w.Text))).AppendLine();
            if (text.Length > AttachmentRules.MaxTextChars) break;
        }
        return text.ToString().Trim();
    }

    internal static string Docx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        if (AttachmentRules.CheckDocx(stream) is { } why) throw new InvalidDataException(why); // re-checked: never trust the upload path alone
        stream.Position = 0;
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var text = new StringBuilder();
        foreach (var paragraph in doc.MainDocumentPart?.Document?.Body?.Descendants<Paragraph>() ?? [])
        {
            text.AppendLine(paragraph.InnerText);
            if (text.Length > AttachmentRules.MaxTextChars) break;
        }
        return text.ToString().Trim();
    }
}
