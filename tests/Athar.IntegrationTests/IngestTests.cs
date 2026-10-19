using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Sources;
using Athar.Worker.Jobs.Ingest;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Athar.IntegrationTests;

/// <summary>T142 (FR-KS-06): URLs and documents as project sources → AT-65.</summary>
[Collection(AtharCollection.Name)]
public class IngestTests(AtharApp athar)
{
    [Fact]
    public void AT_65_only_public_addresses_are_fetched_and_every_document_type_yields_its_text()
    {
        foreach (var blocked in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "192.168.1.1", "169.254.169.254", "100.64.0.1", "0.0.0.0", "224.0.0.1",
                     "::1", "fe80::1", "fc00::1", "fd12::1", "::ffff:10.0.0.1", "::ffff:127.0.0.1", "2001:db8::1" })
            Assert.False(IngestRules.IsPublic(IPAddress.Parse(blocked)), blocked);
        foreach (var open in new[] { "8.8.8.8", "1.1.1.1", "2606:4700:4700::1111" })
            Assert.True(IngestRules.IsPublic(IPAddress.Parse(open)), open);
        Assert.Null(IngestRules.ParseUrl("ftp://example.com/x"));
        Assert.Null(IngestRules.ParseUrl("https://user:pass@example.com/"));
        Assert.Null(IngestRules.ParseUrl("/relative"));
        Assert.NotNull(IngestRules.ParseUrl(" https://example.com/docs "));

        var (title, text) = IngestRules.HtmlToText("<html><head><title>Guide &amp; notes</title><style>p{}</style></head><body><script>alert(1)</script>" +
            "<h2>Setup</h2><p>Run the <b>build</b>.</p><ul><li>one</li><li>two</li></ul></body></html>");
        Assert.Equal("Guide & notes", title);
        foreach (var part in new[] { "## Setup", "Run the build", "- one", "- two" }) Assert.Contains(part, text);
        Assert.DoesNotContain("alert", text);

        foreach (var (media, bytes) in new (string, byte[])[]
        {
            ("text/markdown", "# Notes\n\nmarkdownword"u8.ToArray()),
            ("text/plain", "plainword"u8.ToArray()),
            ("text/html", "<p>htmlword</p>"u8.ToArray()),
            ("application/pdf", AttachmentTests.Pdf("pdfword")),
            (IngestRules.Types[".docx"], AttachmentTests.Docx("docxword")),
            (IngestRules.Types[".pptx"], Pptx("pptxword")),
            (IngestRules.Types[".xlsx"], Xlsx("xlsxword")),
        })
            Assert.Contains(media.Split('/')[^1] switch { "markdown" => "markdownword", "plain" => "plainword", "html" => "htmlword", "pdf" => "pdfword",
                var o when o.Contains("wordprocessing") => "docxword", var o when o.Contains("presentation") => "pptxword", _ => "xlsxword" },
                IngestSourceJob.Parse(media, bytes).Text);
        Assert.Equal("text/markdown", IngestRules.MediaTypeOf("Guide.MD"));
        Assert.Null(IngestRules.MediaTypeOf("photo.png"));
    }

    [Fact]
    public async Task AT_65_members_add_pages_and_documents_that_only_project_members_find_and_refresh_takes_a_new_snapshot()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token; // the worker fetches, extracts and indexes
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (pageWord, newWord, docWord, mcpWord) = ($"okapi{tag}", $"quokka{tag}", $"narwhal{tag}", $"axolotl{tag}");
        var body = $"<html><head><title>Runbook {tag}</title></head><body><h1>Deploys</h1><p>We deploy with {pageWord}.</p></body></html>";
        await using var site = await FakeSite.StartAsync(() => body);

        using var member = Bearer(QaSeed.MemberPat);           // PilotProject member, write scope
        using var outsider = Bearer(QaSeed.KnowledgeOwnerPat); // not in PilotProject
        using var readOnly = Bearer(QaSeed.MemberReadPat);     // PilotProject member without the write scope

        // A URL: members add it (write scope), nobody else; the worker snapshots it and only members find it.
        Assert.Equal(HttpStatusCode.Forbidden, (await AddUrl(readOnly, $"{site.Url}/runbook", ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddUrl(member, "ftp://example.com/x", ct)).Status);
        var added = await AddUrl(member, $"{site.Url}/runbook", ct);
        Assert.Equal(HttpStatusCode.OK, added.Status);
        Assert.Equal("url", added.Body.GetProperty("kind").GetString());
        var source = added.Body.GetProperty("source_id").GetGuid();
        await Until(() => Finds(member, pageWord, ct), true, ct);
        Assert.False(await Finds(outsider, pageWord, ct));
        var hit = await Hit(member, pageWord, ct);
        Assert.Equal(($"Runbook {tag}", $"{site.Url}/runbook"), (hit.GetProperty("title").GetString(), hit.GetProperty("url").GetString()));

        // Refresh takes a new snapshot: the old text goes, the new text comes. Adding the same URL again refreshes it too.
        body = body.Replace(pageWord, newWord);
        using (var refresh = await member.PostAsync($"/api/v1/sources/{source}/refresh", null, ct)) Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        await Until(() => Finds(member, newWord, ct), true, ct);
        Assert.False(await Finds(member, pageWord, ct));
        Assert.Equal(source, (await AddUrl(member, $"{site.Url}/runbook", ct)).Body.GetProperty("source_id").GetGuid());

        // SSRF: a page that redirects to the cloud metadata address, or a private host, is refused and the source shows an error.
        var redirected = (await AddUrl(member, $"{site.Url}/to-metadata", ct)).Body.GetProperty("source_id").GetGuid();
        var privateHost = (await AddUrl(member, "http://10.255.255.1/secret", ct)).Body.GetProperty("source_id").GetGuid();
        await Until(async () => await Status(member, redirected, ct) == "error" && await Status(member, privateHost, ct) == "error", true, ct);

        // A document, uploaded by a member: its text is indexed for the project, linked to the project page.
        using var form = new MultipartFormDataContent
        {
            { new StringContent(QaSeed.PilotProject.ToString()), "project_id" },
            { new ByteArrayContent(AttachmentTests.Docx($"The handbook says {docWord}.")) { Headers = { ContentType = new(IngestRules.Types[".docx"]) } }, "file", "handbook.docx" },
        };
        using (var upload = await member.PostAsync("/api/v1/sources/documents", form, ct))
        {
            var json = JsonDocument.Parse(await upload.Content.ReadAsStringAsync(ct)).RootElement;
            Assert.True(upload.StatusCode == HttpStatusCode.OK, json.GetRawText());
            Assert.Equal(("document", "handbook.docx"), (json.GetProperty("kind").GetString(), json.GetProperty("repo").GetString()));
        }
        await Until(() => Finds(member, docWord, ct), true, ct);
        Assert.Equal($"/projects/{QaSeed.PilotProject}", (await Hit(member, docWord, ct)).GetProperty("url").GetString());
        Assert.False(await Finds(outsider, docWord, ct));
        using (var bad = new MultipartFormDataContent { { new StringContent(QaSeed.PilotProject.ToString()), "project_id" }, { new ByteArrayContent([1, 2, 3]), "file", "photo.png" } })
        using (var refused = await member.PostAsync("/api/v1/sources/documents", bad, ct))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // MCP add_source, as an agent would.
        await using var agent = await Mcp.Connect(athar.Web(), new Uri(athar.App.GetEndpoint("web", "https"), "/mcp"), QaSeed.MemberPat, ct);
        body = $"<p>Agents add {mcpWord} too.</p>";
        var viaMcp = await Mcp.Call(agent, "add_source", new() { ["project_id"] = QaSeed.PilotProject, ["url"] = $"{site.Url}/agent-page" }, ct);
        Assert.Equal("url", viaMcp.GetProperty("kind").GetString());
        await Until(() => Finds(member, mcpWord, ct), true, ct);
    }

    [Fact]
    public async Task AT_65_athar_ingest_sends_a_folders_text_documents_then_only_changes_and_removes_deleted_files()
    {
        // T143: the CLI's own logic first (gitignore, change planning), then the real CLI against the server.
        var (selfTest, selfOut) = await Node(null, [Path.Combine(RepoRoot(), "tests", "cli", "athar.test.mjs")], CancellationToken.None);
        Assert.True(selfTest == 0, selfOut);

        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (keep, edited, removed, ignored) = ($"pangolin{tag}", $"tapir{tag}", $"gecko{tag}", $"lemur{tag}");
        var dir = Directory.CreateTempSubdirectory("athar-ingest-");
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(dir.FullName, "home")).FullName;
            var folder = Directory.CreateDirectory(Path.Combine(dir.FullName, $"handbook-{tag}")).FullName;
            Directory.CreateDirectory(Path.Combine(home, ".athar"));
            await File.WriteAllTextAsync(Path.Combine(home, ".athar", "config.json"),
                JsonSerializer.Serialize(new { url = athar.App.GetEndpoint("web", "https").ToString().TrimEnd('/'), token = QaSeed.MemberPat }), ct);
            void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, path))!); File.WriteAllText(Path.Combine(folder, path), text); }
            Put(".gitignore", "drafts/\n");
            Put("README.md", $"# Handbook\n\nWe say {keep}.");
            Put("docs/onboarding.txt", $"Day one: {edited}.");
            Put("docs/old.md", $"# Old\n\nObsolete {removed}.");
            Put("drafts/wip.md", $"Not yet {ignored}.");
            Put("diagram.png", "not text");
            string[] args = [Path.Combine(RepoRoot(), "agent-plugin", "setup", "athar.mjs"), "ingest", folder, "--project", QaSeed.PilotProject.ToString()];

            var (code, output) = await Node(home, args, ct);
            Assert.True(code == 0, output);
            Assert.Contains("3 of 3 documents sent", output);
            using var member = Bearer(QaSeed.MemberPat);
            using var outsider = Bearer(QaSeed.KnowledgeOwnerPat);
            foreach (var word in new[] { keep, edited, removed }) await Until(() => Finds(member, word, ct), true, ct);
            Assert.False(await Finds(member, ignored, ct)); // .gitignore'd
            Assert.False(await Finds(outsider, keep, ct));  // project members only

            // A re-run sends only what changed; a deleted file leaves search.
            Put("docs/onboarding.txt", $"Day one, revised: {edited} and more.");
            File.Delete(Path.Combine(folder, "docs", "old.md"));
            (code, output) = await Node(home, args, ct);
            Assert.True(code == 0, output);
            Assert.Contains("1 of 2 documents sent", output);
            await Until(() => Finds(member, removed, ct), false, ct);
            Assert.True(await Finds(member, keep, ct));
            var sources = await member.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{QaSeed.PilotProject}/sources", ct);
            var src = sources.EnumerateArray().Single(s => s.GetProperty("repo").GetString() == $"handbook-{tag}");
            Assert.Equal(("file", 2), (src.GetProperty("kind").GetString(), src.GetProperty("documents").GetInt32()));

            // The server takes text documents only.
            using var bad = await member.PostAsJsonAsync("/api/v1/sources/files", new
                { project_id = QaSeed.PilotProject, label = $"bad-{tag}", files = new[] { new { path = "x.pdf", content = "%PDF" } } }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static async Task<(int Code, string Output)> Node(string? home, string[] args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("node", ["--use-system-ca", .. args]) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (home is not null) { psi.Environment["HOME"] = home; psi.Environment["USERPROFILE"] = home; }
        psi.Environment.Remove("ATHAR_URL");
        psi.Environment.Remove("ATHAR_TOKEN");
        if (Environment.GetEnvironmentVariable("SSL_CERT_DIR") is { } certs) // CI: a colon-separated list, which Node reads as one folder
        {
            var first = certs.Split(':')[0];
            psi.Environment["SSL_CERT_DIR"] = first;
            if (Directory.Exists(first) && Directory.GetFiles(first, "*.pem").FirstOrDefault() is { } pem) psi.Environment["NODE_EXTRA_CA_CERTS"] = pem;
        }
        using var node = System.Diagnostics.Process.Start(psi)!;
        var output = await node.StandardOutput.ReadToEndAsync(ct) + await node.StandardError.ReadToEndAsync(ct);
        await node.WaitForExitAsync(ct);
        return (node.ExitCode, output);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "agent-plugin"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("agent-plugin");
    }

    private HttpClient Bearer(string pat)
    {
        var http = athar.Web();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        return http;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> AddUrl(HttpClient http, string url, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync("/api/v1/sources/url", new { project_id = QaSeed.PilotProject, url }, ct);
        return (res.StatusCode, JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone());
    }

    private static async Task<string?> Status(HttpClient http, Guid source, CancellationToken ct) =>
        (await http.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{QaSeed.PilotProject}/sources", ct)).EnumerateArray()
            .Where(s => s.GetProperty("source_id").GetGuid() == source).Select(s => s.GetProperty("status").GetString()).SingleOrDefault();

    private static async Task<JsonElement> Hit(HttpClient http, string word, CancellationToken ct) =>
        (await http.GetFromJsonAsync<JsonElement>($"/api/v1/search?q={word}&kinds=source", ct)).GetProperty("hits").EnumerateArray()
            .First(h => h.GetProperty("snippet").GetString()!.Contains(word) || h.GetProperty("title").GetString()!.Contains(word));

    private static async Task<bool> Finds(HttpClient http, string word, CancellationToken ct) =>
        (await http.GetFromJsonAsync<JsonElement>($"/api/v1/search?q={word}&kinds=source", ct)).GetProperty("hits").EnumerateArray()
            .Any(h => h.GetProperty("snippet").GetString()!.Contains(word) || h.GetProperty("title").GetString()!.Contains(word));

    private static async Task Until(Func<Task<bool>> check, bool expected, CancellationToken ct)
    {
        while (await check() != expected) await Task.Delay(1000, ct);
    }

    private static byte[] Pptx(string text)
    {
        using var ms = new MemoryStream();
        using (var deck = PresentationDocument.Create(ms, DocumentFormat.OpenXml.PresentationDocumentType.Presentation))
        {
            var part = deck.AddPresentationPart();
            var slide = part.AddNewPart<SlidePart>("rId1");
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(new P.Shape(new P.TextBody(new A.BodyProperties(), new A.Paragraph(new A.Run(new A.Text(text))))))));
            part.Presentation = new P.Presentation(new P.SlideIdList(new P.SlideId { Id = 256U, RelationshipId = "rId1" }));
        }
        return ms.ToArray();
    }

    private static byte[] Xlsx(string text)
    {
        using var ms = new MemoryStream();
        using (var book = SpreadsheetDocument.Create(ms, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var wb = book.AddWorkbookPart();
            var strings = wb.AddNewPart<SharedStringTablePart>();
            strings.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text(text)));
            var sheet = wb.AddNewPart<WorksheetPart>("rId1");
            sheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell { CellReference = "A1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") },
                new S.Cell { CellReference = "B1", CellValue = new S.CellValue("42") })));
            wb.Workbook = new S.Workbook(new S.Sheets(new S.Sheet { Id = "rId1", SheetId = 1U, Name = "Plan" }));
        }
        return ms.ToArray();
    }

    /// <summary>A tiny site on 127.0.0.1 (trusted for tests through Athar:Sources:TrustedHosts), with a page that redirects to the metadata address.</summary>
    private sealed class FakeSite : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string Url { get; private set; } = "";
        private FakeSite(WebApplication app) => _app = app;

        public static async Task<FakeSite> StartAsync(Func<string> body)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var site = new FakeSite(app);
            app.MapGet("/to-metadata", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
            app.MapGet("/{**path}", () => Results.Content(body(), "text/html; charset=utf-8"));
            await app.StartAsync();
            site.Url = app.Urls.First().TrimEnd('/');
            return site;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
