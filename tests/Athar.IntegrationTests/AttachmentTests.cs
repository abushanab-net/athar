using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Worker.Jobs.Upload;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig.Writer;

namespace Athar.IntegrationTests;

/// <summary>AT-36 (FR-PG-06): uploads are sniffed and limited, served with nosniff, and PDF/DOCX text becomes searchable.</summary>
[Collection(AtharCollection.Name)]
public class AttachmentTests(AtharApp athar)
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task AT_36_images_render_inline_with_nosniff()
    {
        var ct = AtharApp.Timeout();
        using var http = Bearer(QaSeed.AgentPat);
        var uploaded = await Upload(http, Png, "dot.png", "image/png", ct);
        Assert.Equal(HttpStatusCode.OK, uploaded.Status);
        Assert.Equal("skipped", uploaded.Body.GetProperty("extract_status").GetString()); // no OCR

        using var res = await http.GetAsync(uploaded.Body.GetProperty("url").GetString(), ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Null(res.Content.Headers.ContentDisposition); // inline
        Assert.Equal(Png, await res.Content.ReadAsByteArrayAsync(ct));
        using (var unknown = await http.GetAsync($"/attachments/{Guid.NewGuid()}", ct)) Assert.True(unknown.StatusCode == HttpStatusCode.NotFound, await unknown.Content.ReadAsStringAsync(ct));
        using (var readOnly = Bearer(QaSeed.MemberReadPat)) // an agent without the write scope can't upload
            Assert.Equal(HttpStatusCode.Forbidden, (await Upload(readOnly, Png, "dot.png", "image/png", ct)).Status);
    }

    [Fact]
    public async Task AT_36_svg_html_mismatched_oversized_and_zip_bombs_are_rejected()
    {
        var ct = AtharApp.Timeout();
        using var http = Bearer(QaSeed.AgentPat);
        async Task Rejected(byte[] bytes, string name, string type)
        {
            var r = await Upload(http, bytes, name, type, ct);
            Assert.True(r.Status == HttpStatusCode.BadRequest, $"{name}: {r.Status} {r.Body}");
        }
        await Rejected("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(), "x.svg", "image/svg+xml");
        await Rejected("<!doctype html><script>alert(1)</script>"u8.ToArray(), "x.html", "text/html");
        await Rejected(Png, "dot.jpg", "image/jpeg"); // declared type disagrees with the bytes
        await Rejected(new byte[25 * 1024 * 1024 + 1], "big.bin", "application/octet-stream");
        await Rejected(Zip(z => { for (var i = 0; i < 2001; i++) z.CreateEntry($"word/p{i}.xml"); }), "many.docx", Domain.Wiki.AttachmentRules.Docx);
        await Rejected(Zip(z =>
        {
            z.CreateEntry("[Content_Types].xml");
            z.CreateEntry("word/document.xml");
            using var s = z.CreateEntry("word/bomb.bin", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 101; i++) s.Write(zeros); // 101 MB expanded, ~100 KB compressed
        }), "bomb.docx", Domain.Wiki.AttachmentRules.Docx);

        await using var db = athar.Db(QaSeed.Org);
        Assert.False(await db.Attachments.AnyAsync(a => a.FileName == "bomb.docx" || a.FileName == "x.svg", ct));
    }

    [Fact]
    public async Task AT_36_pdf_and_docx_text_is_searchable()
    {
        // The worker extracts, then re-indexes the page; under a full suite its queue is long, so allow more than the default.
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(8)).Token;
        using var http = Bearer(QaSeed.AgentPat);
        var pdfWord = $"quokka{Guid.NewGuid():N}"[..20];
        var docxWord = $"wombat{Guid.NewGuid():N}"[..20];

        var pdf = await Upload(http, Pdf($"The rollout checklist mentions {pdfWord} twice."), "checklist.pdf", "application/pdf", ct);
        var docx = await Upload(http, Docx($"Escalation contacts: {docxWord}."), "contacts.docx", Domain.Wiki.AttachmentRules.Docx, ct);
        Assert.Equal(HttpStatusCode.OK, pdf.Status);
        Assert.Equal(HttpStatusCode.OK, docx.Status);

        using (var dl = await http.GetAsync(pdf.Body.GetProperty("url").GetString(), ct)) // documents are downloads only
            Assert.Equal("attachment", dl.Content.Headers.ContentDisposition?.DispositionType);

        // Extraction first (the attachment rows), then search: the page's hit must carry the word itself, since hybrid search
        // can return the runbook page for any query through its vectors (a 3 s false pass before extraction was done), so the chunks are checked too.
        await using var db = athar.Db(QaSeed.Org);
        while (await db.Attachments.CountAsync(a => (a.FileName == "checklist.pdf" || a.FileName == "contacts.docx")
            && a.ExtractStatus == ExtractStatus.Done && a.TextContent!.Contains(a.FileName == "checklist.pdf" ? pdfWord : docxWord), ct) < 2)
            await Task.Delay(1000, ct);
        foreach (var word in new[] { pdfWord, docxWord })
        {
            // The page's own chunks carry the attachment text (so full-text search matches it), then search returns the page.
            while (!await db.Chunks.AnyAsync(c => c.OwnerKind == ChunkOwner.Page && c.OwnerId == QaSeed.DeployRunbookPage && c.SearchText.Contains(word), ct))
                await Task.Delay(1000, ct);
            await UntilFound(http, word, QaSeed.DeployRunbookPage, ct);
        }
    }

    [Fact]
    public async Task AT_36_a_parser_timeout_is_enforced()
    {
        var (text, error) = await ExtractAttachmentJob.WithTimeoutAsync(() => { Thread.Sleep(3000); return "late"; }, TimeSpan.FromMilliseconds(200));
        Assert.Null(text);
        Assert.Contains("longer than", error);
    }

    private HttpClient Bearer(string pat)
    {
        var http = athar.Web();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        return http;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Upload(HttpClient http, byte[] bytes, string name, string type, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        form.Add(file, "file", name);
        using var res = await http.PostAsync($"/api/v1/pages/{QaSeed.DeployRunbookPage}/attachments", form, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    /// <summary>
    /// Polls search until <paramref name="page"/> is a hit for <paramref name="word"/> (the worker extracts, then re-indexes).
    /// Any hit isn't enough: with vectors, hybrid search returns nearby pages for any query.
    /// </summary>
    private static async Task UntilFound(HttpClient http, string word, Guid page, CancellationToken ct)
    {
        while (true)
        {
            var result = await http.GetFromJsonAsync<JsonElement>($"/api/v1/search?q={word}&kinds=page", ct);
            if (result.GetProperty("hits").EnumerateArray().Any(h => h.GetProperty("id").GetGuid() == page)) return;
            await Task.Delay(1000, ct);
        }
    }

    private static byte[] Zip(Action<ZipArchive> fill)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) fill(zip);
        return ms.ToArray();
    }

    internal static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        return builder.Build();
    }

    internal static byte[] Docx(string text)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
        }
        return ms.ToArray();
    }
}
