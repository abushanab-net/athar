using System.IO.Compression;
using System.Text;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Athar.Web.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Athar.IntegrationTests;

/// <summary>T155 (FR-PG-09): Confluence HTML and Markdown/HTML zip imports make the page tree, files, links and index chunks. T157 (FR-PG-10): exports import back.</summary>
[Collection(AtharCollection.Name)]
public class WikiImportTests(AtharApp athar)
{
    private static Actor Person(Guid user) => new(user, QaSeed.Org, null, new HashSet<string> { "read", "write" }, DisclosureClass.TrustedLocal, null);
    private static readonly Actor Member = Person(QaSeed.Member);   // EngWiki editor: can't import (T161)
    private static readonly Actor Admin = Person(QaSeed.Admin);     // EngWiki admin, Engineering workspace admin
    private static readonly AttachmentStore Store = new(new ConfigurationBuilder().Build());
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private async Task<WikiImportService> Importer(AtharDbContext db) =>
        new(db, await athar.OpsAsync(db: db), new PageService(db, TimeProvider.System), new AttachmentService(db, Store, TimeProvider.System),
            new Athar.Web.Auth.WorkspaceAdminService(new Factory(athar)));

    private static MemoryStream Zip(params (string Path, object Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(content as byte[] ?? Encoding.UTF8.GetBytes((string)content));
            }
        ms.Position = 0;
        return ms;
    }

    private static string ConfluencePage(string title, string body) => $"""
        <html><head><title>Engineering : {title}</title></head><body>
        <div id="main-header"><div id="breadcrumb-section"><ol id="breadcrumbs"><li><a href="index.html">Engineering</a></li></ol></div>
        <h1 id="title-heading" class="pagetitle"><span id="title-text"> Engineering : {title} </span></h1></div>
        <div id="content" class="view"><div class="page-metadata">Created by Samy</div>
        <div id="main-content" class="wiki-content group">{body}</div></div></body></html>
        """;

    [Fact]
    public async Task AT_PG_09_confluence_html_export_becomes_a_page_tree_with_files_links_and_chunks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = athar.Db(QaSeed.Org);
        var ops = await athar.OpsAsync(db: db);
        var under = (await ops.CreatePageAsync(Admin, QaSeed.EngWiki, null, $"Import target {tag}", "Here.", Guid.NewGuid().ToString(), ct)).PageId;

        var zip = Zip(
            ("ENG/index.html", """
                <html><body><div class="pageSection"><h2 class="pageSectionTitle">Available Pages:</h2>
                <ul><li><a href="Home_100.html">Home</a>
                  <ul><li><a href="Deploy-runbook_200.html">Deploy runbook</a></li><li><a href="Notes_300.html">Notes</a></li></ul>
                </li></ul></div></body></html>
                """),
            ("ENG/Home_100.html", ConfluencePage("Home", $"""
                <p>Welcome {tag}. See <a href="Deploy-runbook_200.html">Deploy runbook</a> and <a href="Notes_300.html">the notes</a>.</p>
                <p><img class="confluence-embedded-image" src="attachments/100/1.png" alt="dot"></p>
                <p><img src="attachments/100/logo.svg" alt="logo"></p>
                <div class="confluence-information-macro confluence-information-macro-note"><div class="confluence-information-macro-body"><p>Mind the gap.</p></div></div>
                <div class="code panel"><div class="codeContent"><pre class="syntaxhighlighter-pre" data-syntaxhighlighter-params="brush: java; gutter: false">int x = 1;</pre></div></div>
                """)),
            ("ENG/Deploy-runbook_200.html", ConfluencePage("Deploy runbook", $"<h2>Rollback</h2><p>Run the rollback script {tag} before paging anyone.</p>")),
            ("ENG/Notes_300.html", ConfluencePage("Notes", "<p>Short notes.</p>")),
            ("ENG/attachments/100/1.png", Png),
            ("ENG/attachments/100/logo.svg", "<svg xmlns='http://www.w3.org/2000/svg'/>"),
            ("ENG/styles/site.css", "body{}"));

        var importer = await Importer(db);
        var op = Guid.NewGuid().ToString();
        var result = await importer.ImportAsync(Admin, QaSeed.EngWiki, under, "ENG.zip", zip, op, ct);
        Assert.Equal((3, 1), (result.Pages, result.Files));
        Assert.Contains(result.Skipped, s => s.Contains("logo.svg"));

        var home = await db.Pages.AsNoTracking().SingleAsync(p => p.Id == result.FirstPageId, ct);
        Assert.Equal(("Home", (Guid?)under, PageStatus.Published), (home.Title, home.ParentId, home.Status));
        var children = await db.Pages.AsNoTracking().Where(p => p.ParentId == home.Id).OrderBy(p => p.Position).ToListAsync(ct);
        Assert.Equal(["Deploy runbook", "Notes"], children.Select(c => c.Title));

        var content = (await ops.GetPageAsync(Admin, home.Id, ct: ct)).Content;
        Assert.Contains($"Welcome {tag}", content);
        Assert.Contains($"(page:{children[0].Id})", content);           // link text = title: a page mention
        Assert.Contains($"[the notes](/wiki/p/{children[1].Id})", content);
        var file = await db.Attachments.AsNoTracking().SingleAsync(a => a.PageId == home.Id, ct);
        Assert.Contains($"/attachments/{file.Id}", content);
        Assert.DoesNotContain("logo.svg", content);
        Assert.Contains(":::callout{type=warning}", content);
        Assert.Contains("```java", content);
        Assert.DoesNotContain("Created by Samy", content);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "page.import" && e.ResourceId == home.Id.ToString(), ct));

        // A retry with the same op id lands on the same pages and writes nothing twice.
        var again = await importer.ImportAsync(Admin, QaSeed.EngWiki, under, "ENG.zip", Zip(
            ("ENG/index.html", "<div class=\"pageSection\">Available Pages:<ul><li><a href=\"Home_100.html\">Home</a></li></ul></div>"),
            ("ENG/Home_100.html", ConfluencePage("Home", "<p>x</p>"))), op, ct);
        Assert.Equal(home.Id, again.FirstPageId);
        Assert.Equal(1, await db.Pages.CountAsync(p => p.ParentId == under, ct));

        // Imported content is indexed for search and recall like any edit.
        var deploy = children[0].Id;
        while (!await db.Chunks.AnyAsync(c => c.OwnerKind == ChunkOwner.Page && c.OwnerId == deploy && c.Body.Contains(tag), ct))
            await Task.Delay(250, ct);
    }

    [Fact]
    public async Task AT_PG_09_markdown_zip_uses_folders_for_subpages()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(QaSeed.Org);
        var importer = await Importer(db);
        var tag = Guid.NewGuid().ToString("N")[..8];

        var result = await importer.ImportAsync(Admin, QaSeed.EngWiki, null, "export.zip", Zip(
            ($"export/Guide {tag}.md", $"# Guide {tag}\n\nRead [Child](Guide%20{tag}/Child.md) first."),
            ($"export/Guide {tag}/Child.md", "Child body."),
            ($"export/Loose {tag}/Orphan.html", "<html><head><title>Orphan</title></head><body><h1>Orphan</h1><p>Alone.</p></body></html>")), Guid.NewGuid().ToString(), ct);
        Assert.Equal(4, result.Pages);

        var guide = await db.Pages.AsNoTracking().SingleAsync(p => p.Title == $"Guide {tag}", ct);
        var child = await db.Pages.AsNoTracking().SingleAsync(p => p.ParentId == guide.Id, ct);
        var loose = await db.Pages.AsNoTracking().SingleAsync(p => p.Title == $"Loose {tag}", ct);
        Assert.Equal(("Child", (Guid?)null, (Guid?)null), (child.Title, guide.ParentId, loose.ParentId));
        Assert.Equal("Orphan", (await db.Pages.AsNoTracking().SingleAsync(p => p.ParentId == loose.Id, ct)).Title);
        var ops = await athar.OpsAsync(db: db);
        var content = (await ops.GetPageAsync(Admin, guide.Id, ct: ct)).Content;
        Assert.Contains($"(page:{child.Id})", content);
        Assert.DoesNotContain($"# Guide {tag}", content);
    }

    [Fact]
    public async Task AT_PG_09_refuses_empty_exports_unknown_files_viewers_and_editors()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(QaSeed.Org);
        var importer = await Importer(db);
        async Task<string> Code(Actor a, string name, Stream s) =>
            (await Assert.ThrowsAsync<AtharError>(() => importer.ImportAsync(a, QaSeed.EngWiki, null, name, s, Guid.NewGuid().ToString(), ct))).Code;

        Assert.Equal("invalid_input", await Code(Admin, "space.zip", Zip(("entities.xml", "<hibernate-generic/>"))));
        Assert.Equal("invalid_input", await Code(Admin, "notes.txt", new MemoryStream("hi"u8.ToArray())));
        Assert.Equal("invalid_input", await Code(Admin, "bad.zip", new MemoryStream("not a zip"u8.ToArray())));
        Assert.NotEqual("invalid_input", await Code(Person(QaSeed.Guest), "page.md", new MemoryStream("# Hi"u8.ToArray())));
        // T161: importing is for the space's admins; an editor is refused.
        Assert.Equal("forbidden", await Code(Member, "page.md", new MemoryStream("# Hi"u8.ToArray())));
    }

    [Fact]
    public async Task AT_PG_10_export_zip_imports_back_with_subpages_files_and_links()
    {
        var ct = AtharApp.Timeout();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = athar.Db(QaSeed.Org);
        var ops = await athar.OpsAsync(db: db);
        var root = (await ops.CreatePageAsync(Admin, QaSeed.EngWiki, null, $"Export {tag}", "Draft.", Guid.NewGuid().ToString(), ct)).PageId;
        var child = (await ops.CreatePageAsync(Admin, QaSeed.EngWiki, root, "Child: one", "Child text.", Guid.NewGuid().ToString(), ct)).PageId;
        var file = await new AttachmentService(db, Store, TimeProvider.System).UploadAsync(Admin, root, "dot.png", "image/png", new MemoryStream(Png), ct);
        var token = (await ops.GetPageAsync(Admin, root, ct: ct)).LiveToken;
        await ops.RunAsync(Admin, root, Guid.NewGuid().ToString(),
            new PageOp("replace", $"Root {tag}. See [[Child: one]](page:{child}).\n\n![dot](/attachments/{file.AttachmentId})", ExpectedLiveToken: token), ct);

        var export = new WikiExportService(db, Store, new Athar.Web.Auth.Mailer(new ConfigurationBuilder().Build()));
        // T161: one page with files is a zip of the page and its files; links to other pages go to Athar.
        var single = await export.PageAsync(Admin, root, "md", ct);
        Assert.Equal(($"Export {tag}.zip", "application/zip"), (single.Name, single.Mime));
        using (var zip = new ZipArchive(new MemoryStream(single.Bytes)))
        {
            Assert.Equal([$"Export {tag}.md", $"files/{file.AttachmentId:N}/dot.png"], zip.Entries.Select(e => e.FullName));
            var md = new StreamReader(zip.Entries[0].Open()).ReadToEnd();
            Assert.StartsWith($"# Export {tag}\n", md);
            Assert.Contains($"](files/{file.AttachmentId:N}/dot.png)", md);
            Assert.Contains($"[Child: one](https://localhost:7100/wiki/p/{child})", md);
        }
        using (var zip = new ZipArchive(new MemoryStream((await export.PageAsync(Admin, root, "html", ct)).Bytes)))
            Assert.Contains($"src=\"files/{file.AttachmentId:N}/dot.png\"", new StreamReader(zip.GetEntry($"Export {tag}.html")!.Open()).ReadToEnd());
        var plain = await export.PageAsync(Admin, child, "md", ct);   // no files: just the Markdown
        Assert.Equal(("Child_ one.md", "text/markdown"), (plain.Name, plain.Mime));

        var zipped = await export.ZipAsync(Admin, QaSeed.EngWiki, root, ct);
        Assert.Equal($"Export {tag}.zip", zipped.Name);
        using (var zip = new ZipArchive(new MemoryStream(zipped.Bytes)))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains($"Export {tag}.md", names);
            Assert.Contains($"Export {tag}/Child_ one.md", names);
            Assert.Contains($"files/{file.AttachmentId:N}/dot.png", names);
        }

        var result = await (await Importer(db)).ImportAsync(Admin, QaSeed.EngWiki, null, zipped.Name, new MemoryStream(zipped.Bytes), Guid.NewGuid().ToString(), ct);
        Assert.Equal((2, 1), (result.Pages, result.Files));
        var copy = await db.Pages.AsNoTracking().SingleAsync(p => p.Id == result.FirstPageId, ct);
        var copyChild = await db.Pages.AsNoTracking().SingleAsync(p => p.ParentId == copy.Id, ct);
        Assert.Equal(($"Export {tag}", "Child: one"), (copy.Title, copyChild.Title));   // titles come from each file's "# Title"
        var content = (await ops.GetPageAsync(Admin, copy.Id, ct: ct)).Content;
        Assert.Contains($"Root {tag}", content);
        Assert.Contains($"(page:{copyChild.Id})", content);     // the mention survives the round trip
        Assert.Contains($"/attachments/{(await db.Attachments.AsNoTracking().SingleAsync(a => a.PageId == copy.Id, ct)).Id}", content);
    }

    private static string Entities(params string[] objects) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<hibernate-generic datetime=\"2026-10-15\">" + string.Concat(objects) + "</hibernate-generic>";
    private static string Obj(string cls, string id, string props) => $"<object class=\"{cls}\" package=\"com.atlassian.confluence\"><id name=\"id\">{id}</id>{props}</object>";
    private static string P(string name, string value) => $"<property name=\"{name}\">{System.Security.SecurityElement.Escape(value)}</property>";
    private static string R(string name, string cls, string id) => $"<property name=\"{name}\" class=\"{cls}\"><id name=\"id\">{id}</id></property>";

    /// <summary>A Confluence XML export: spaces, a page tree, storage-format bodies with macros, links and files, plus history.</summary>
    private static MemoryStream ConfluenceXml(string tag, params (string Id, string Key, string Name)[] spaces)
    {
        var objects = new List<string>();
        var files = new List<(string, object)>();
        foreach (var (sid, key, name) in spaces)
        {
            objects.Add(Obj("Space", sid, P("key", key) + P("name", name)));
            string home = sid + "1", child = sid + "2", old = sid + "9", body = sid + "5", childBody = sid + "6", att = sid + "7", unused = sid + "8";
            objects.Add(Obj("Page", home, P("title", $"{name} home {tag}") + R("space", "Space", sid) + P("position", "0") + P("contentStatus", "current")));
            objects.Add(Obj("Page", child, P("title", "Runbook") + R("space", "Space", sid) + R("parent", "Page", home) + P("contentStatus", "current")));
            // An old version of the home page: never imported.
            objects.Add(Obj("Page", old, P("title", $"{name} home {tag} (v1)") + R("space", "Space", sid) + R("originalVersion", "Page", home)));
            objects.Add(Obj("BodyContent", body, R("content", "Page", home) + P("body",
                $"<p>Welcome {tag}. Read the <ac:link><ri:page ri:content-title=\"Runbook\" /></ac:link> or <ac:link><ri:page ri:content-title=\"Runbook\" /><ac:plain-text-link-body><![CDATA[the steps]]></ac:plain-text-link-body></ac:link>.</p>" +
                "<ac:structured-macro ac:name=\"info\"><ac:rich-text-body><p>Imported from Confluence.</p></ac:rich-text-body></ac:structured-macro>" +
                "<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">python</ac:parameter><ac:plain-text-body><![CDATA[print(\"<hi>\")]]></ac:plain-text-body></ac:structured-macro>" +
                "<p><ac:image><ri:attachment ri:filename=\"diagram.png\" /></ac:image></p>" +
                "<ac:structured-macro ac:name=\"toc\" />")));
            objects.Add(Obj("BodyContent", childBody, R("content", "Page", child) + P("body", $"<p>Roll back with care {tag}.</p>")));
            objects.Add(Obj("Attachment", att, P("title", "diagram.png") + R("containerContent", "Page", home) + P("version", "1")));
            objects.Add(Obj("Attachment", unused, P("title", "notes.pdf") + R("containerContent", "Page", home) + P("version", "2")));
            // T163: a comment with a reply, by a Confluence user, plus a reply's old version that isn't imported.
            objects.Add(Obj("ConfluenceUserImpl", $"u{sid}", P("name", "jdoe")));
            objects.Add(Obj("Comment", sid + "31", R("containerContent", "Page", home) + R("creator", "ConfluenceUserImpl", $"u{sid}") + P("creationDate", "2023-10-03 12:00:00.000")));
            objects.Add(Obj("Comment", sid + "32", R("containerContent", "Page", home) + R("parent", "Comment", sid + "31") + R("creator", "ConfluenceUserImpl", $"u{sid}") + P("creationDate", "2023-10-04 09:30:00.000")));
            objects.Add(Obj("Comment", sid + "33", R("containerContent", "Page", home) + R("originalVersion", "Comment", sid + "32") + P("creationDate", "2023-10-04 09:00:00.000")));
            objects.Add(Obj("BodyContent", sid + "41", R("content", "Comment", sid + "31") + P("body", $"<p>Is the runbook current {tag}?</p>")));
            objects.Add(Obj("BodyContent", sid + "42", R("content", "Comment", sid + "32") + P("body", "<p>Yes, see <ac:link><ri:page ri:content-title=\"Runbook\" /></ac:link>.</p>")));
            objects.Add(Obj("BodyContent", sid + "43", R("content", "Comment", sid + "33") + P("body", "<p>old draft</p>")));
            files.Add(($"attachments/{home}/{att}/1", Png));
            files.Add(($"attachments/{home}/{unused}/2", "%PDF-1.4\n%%EOF"u8.ToArray()));
        }
        files.Insert(0, ("entities.xml", Entities([.. objects])));
        return Zip([.. files]);
    }

    [Fact]
    public async Task AT_PG_09_confluence_xml_space_export_imports_tree_macros_links_and_files()
    {
        var ct = AtharApp.Timeout();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = athar.Db(QaSeed.Org);
        var ops = await athar.OpsAsync(db: db);
        var under = (await ops.CreatePageAsync(Admin, QaSeed.EngWiki, null, $"XML target {tag}", "Here.", Guid.NewGuid().ToString(), ct)).PageId;

        var result = await (await Importer(db)).ImportAsync(Admin, QaSeed.EngWiki, under, "space.zip", ConfluenceXml(tag, ("98001", "OPS", "Ops")), Guid.NewGuid().ToString(), ct);
        Assert.Equal((2, 2), (result.Pages, result.Files));
        var home = await db.Pages.AsNoTracking().SingleAsync(p => p.ParentId == under, ct);
        var child = await db.Pages.AsNoTracking().SingleAsync(p => p.ParentId == home.Id, ct);
        Assert.Equal(($"Ops home {tag}", "Runbook"), (home.Title, child.Title));

        var content = (await ops.GetPageAsync(Admin, home.Id, ct: ct)).Content;
        Assert.Contains($"Welcome {tag}", content);
        Assert.Contains($"[[Runbook]](page:{child.Id})", content);
        Assert.Contains($"[the steps](/wiki/p/{child.Id})", content);
        Assert.Contains(":::callout{type=info}", content);
        Assert.Contains("```python", content);
        Assert.Contains("print(\"<hi>\")", content);
        // Files keep their Confluence names; the one no link uses is listed under Attachments.
        var files = await db.Attachments.AsNoTracking().Where(a => a.PageId == home.Id).ToListAsync(ct);
        Assert.Equal(["diagram.png", "notes.pdf"], files.Select(f => f.FileName).Order());
        Assert.Contains($"/attachments/{files.Single(f => f.FileName == "diagram.png").Id}", content);
        Assert.Contains($"## Attachments", content);
        Assert.Contains($"[notes.pdf](/attachments/{files.Single(f => f.FileName == "notes.pdf").Id})", content);

        // T163: the comment and its reply are one page-level thread, by the importer, saying who wrote them and when.
        var thread = await db.CommentThreads.AsNoTracking().SingleAsync(t => t.PageId == home.Id, ct);
        Assert.Null(thread.AnchorId);
        var comments = await db.Comments.AsNoTracking().Where(c => c.ThreadId == thread.Id).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        Assert.Equal(2, comments.Count);
        Assert.All(comments, c => Assert.Equal(QaSeed.Admin, c.AuthorId));
        Assert.StartsWith("*Originally by jdoe, 3 Oct 2023:*", comments[0].BodyMd);
        Assert.Contains($"Is the runbook current {tag}?", comments[0].BodyMd);
        Assert.Contains($"[[Runbook]](page:{child.Id})", comments[1].BodyMd);
        Assert.Equal(new DateTimeOffset(2023, 10, 3, 12, 0, 0, TimeSpan.Zero), comments[0].CreatedAt);
    }

    [Fact]
    public async Task AT_PG_09_workspace_import_makes_a_space_per_confluence_space()
    {
        var ct = AtharApp.Timeout();
        var tag = Guid.NewGuid().ToString("N")[..6];
        await using var db = athar.Db(QaSeed.Org);
        var importer = await Importer(db);
        var op = Guid.NewGuid().ToString();
        MemoryStream Site() => ConfluenceXml(tag, ("97001", $"FIN{tag}", "Finance"), ("97002", $"HR{tag}", "People"));

        // A workspace member who isn't its admin can't.
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<AtharError>(() => importer.ImportSpacesAsync(Member, QaSeed.Engineering, "site.zip", Site(), op, ct))).Code);

        var result = await importer.ImportSpacesAsync(Admin, QaSeed.Engineering, "site.zip", Site(), op, ct);
        Assert.Equal((2, 4), (result.Spaces, result.Pages));
        var spaces = await db.Spaces.AsNoTracking().Where(s => s.WorkspaceId == QaSeed.Engineering && s.Slug.EndsWith(tag)).OrderBy(s => s.Slug).ToListAsync(ct);
        Assert.Equal([("Finance", $"fin{tag}"), ("People", $"hr{tag}")], spaces.Select(s => (s.Name, s.Slug)));
        foreach (var space in spaces)
        {
            Assert.True(await db.SpaceMembers.AnyAsync(m => m.SpaceId == space.Id && m.UserId == QaSeed.Admin && m.Role == SpaceRole.Admin, ct));
            Assert.Equal(2, await db.Pages.CountAsync(p => p.SpaceId == space.Id, ct));
        }
        Assert.Contains(spaces, s => s.Id == result.SpaceId);

        // The same op again lands in the same spaces and pages.
        var again = await importer.ImportSpacesAsync(Admin, QaSeed.Engineering, "site.zip", Site(), op, ct);
        Assert.Equal(result.FirstPageId, again.FirstPageId);
        Assert.Equal(2, await db.Spaces.CountAsync(s => s.WorkspaceId == QaSeed.Engineering && s.Slug.EndsWith(tag), ct));
        Assert.Equal(4, await db.Pages.CountAsync(p => spaces.Select(s => s.Id).Contains(p.SpaceId), ct));
        var spaceIds = spaces.Select(s => s.Id).ToList();
        Assert.Equal(2, await db.CommentThreads.CountAsync(t => db.Pages.Any(p => p.Id == t.PageId && spaceIds.Contains(p.SpaceId)), ct));   // not doubled by the retry
    }

    [Fact]
    public async Task AT_PG_09_confluence_html_attachments_keep_their_names_and_unlinked_ones_are_listed()
    {
        var ct = AtharApp.Timeout();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = athar.Db(QaSeed.Org);
        var page = ConfluencePage("Files", $"<p>See <a href=\"attachments/100/201.pdf\" data-linked-resource-default-alias=\"guide.pdf\">the guide</a> {tag}.</p>")
            .Replace("</div></div></body>", "</div></div><div class=\"pageSection group\"><div class=\"pageSectionHeader\"><h2 id=\"attachments\" class=\"pageSectionTitle\">Attachments:</h2></div>" +
                "<div class=\"greybox\"><a href=\"attachments/100/201.pdf\">guide.pdf</a><br/><a href=\"attachments/100/202.png\">photo.png</a></div></div></body>");
        var result = await (await Importer(db)).ImportAsync(Admin, QaSeed.EngWiki, null, "files.zip", Zip(
            ("F/index.html", "<div class=\"pageSection\">Available Pages:<ul><li><a href=\"Files_100.html\">Files " + tag + "</a></li></ul></div>"),
            ("F/Files_100.html", page),
            ("F/attachments/100/201.pdf", "%PDF-1.4\n%%EOF"u8.ToArray()),
            ("F/attachments/100/202.png", Png)), Guid.NewGuid().ToString(), ct);
        Assert.Equal(2, result.Files);
        var files = await db.Attachments.AsNoTracking().Where(a => a.PageId == result.FirstPageId).ToListAsync(ct);
        Assert.Equal(["guide.pdf", "photo.png"], files.Select(f => f.FileName).Order());
        var content = (await (await athar.OpsAsync(db: db)).GetPageAsync(Admin, result.FirstPageId, ct: ct)).Content;
        Assert.Contains($"[the guide](/attachments/{files.Single(f => f.FileName == "guide.pdf").Id})", content);
        Assert.Contains($"[photo.png](/attachments/{files.Single(f => f.FileName == "photo.png").Id})", content);
    }

    [Fact]
    public async Task AT_PG_09_confluence_html_space_name_and_tables_with_lists()
    {
        // T162: the space's name and key come from index.html's Space Details table (its title is just "Space Details"),
        // and a table cell's lists become lines, not raw HTML.
        var ct = AtharApp.Timeout();
        var tag = Guid.NewGuid().ToString("N")[..6];
        await using var db = athar.Db(QaSeed.Org);
        var result = await (await Importer(db)).ImportSpacesAsync(Admin, QaSeed.Engineering, "export.zip", Zip(
            ("Export/index.html", $"<html><head><title>Space Details</title></head><body><div id=\"title-text\">Space Details</div>" +
                $"<table class=\"confluenceTable\"><tr><th>Key</th><td>WP{tag}</td></tr><tr><th>Name</th><td>Wallet Passes {tag}</td></tr></table>" +
                "<div class=\"pageSection\">Available Pages:<ul><li><a href=\"Releases_100.html\">Releases</a></li></ul></div></body></html>"),
            ("Export/Releases_100.html", ConfluencePage("Releases",
                "<table><tr><th>Version</th><th>Fields</th></tr><tr><td>v. 0.1</td><td><p>Three divisions:</p><ol><li><p>a <strong>CTA</strong> div</p><ul><li><p>title</p></li></ul></li><li><p>cards</p></li></ol></td></tr></table>"))),
            Guid.NewGuid().ToString(), ct);
        var space = await db.Spaces.AsNoTracking().SingleAsync(s => s.Id == result.SpaceId, ct);
        Assert.Equal(($"Wallet Passes {tag}", $"wp{tag}"), (space.Name, space.Slug));
        var content = (await (await athar.OpsAsync(db: db)).GetPageAsync(Admin, result.FirstPageId, ct: ct)).Content;
        Assert.DoesNotContain("<ol>", content);
        Assert.DoesNotContain("<p>", content);
        Assert.Contains("Three divisions:", content);
        Assert.Contains("1. a **CTA** div", content);
        Assert.Contains("• title", content);
        Assert.Contains("2. cards", content);
    }

    [Fact]
    public async Task AT_PG_09_large_imports_run_as_a_background_job_and_notify_the_importer()
    {
        // T164: the upload waits on disk, web's runner imports it, the importer is told; imported files have no 25 MB cap.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = athar.Db(QaSeed.Org);
        var queue = new WikiImportQueue(db, new Athar.Web.Auth.WorkspaceAdminService(new Factory(athar)), new ConfigurationBuilder().Build(), TimeProvider.System);
        var big = new byte[26 * 1024 * 1024];
        "%PDF-1.4\n"u8.CopyTo(big);
        var path = Path.Combine(Path.GetTempPath(), $"athar-import-test-{tag}.zip");
        await File.WriteAllBytesAsync(path, Zip(($"Big {tag}.md", $"# Big {tag}\n\nSee [the manual](manual.pdf)."), ("manual.pdf", big)).ToArray(), ct);

        // Permission is checked before the upload is taken.
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<AtharError>(() => queue.RequireAsync(Member, "space", QaSeed.EngWiki, ct))).Code);
        await queue.RequireAsync(Admin, "space", QaSeed.EngWiki, ct);

        var job = await queue.EnqueueAsync(Admin, "space", QaSeed.EngWiki, null, "big.zip", path, ct);
        ImportStatus? status;
        do { await Task.Delay(500, ct); status = await queue.StatusAsync(Admin, job, ct); } while (status?.State is "queued" or "running");
        Assert.Equal("done", status?.State);
        Assert.Equal((1, 1), (status!.Result!.Pages, status.Result.Files));
        var file = await db.Attachments.AsNoTracking().SingleAsync(a => a.PageId == status.Result.FirstPageId, ct);
        Assert.Equal(("manual.pdf", (long)big.Length), (file.FileName, file.Size));
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == QaSeed.Admin && n.Kind == "import_done" && n.Ref == $"page:{status.Result.FirstPageId}", ct));
        Assert.False(File.Exists(path));                                   // the upload is removed once imported
        Assert.Null(await queue.StatusAsync(Member, job, ct));             // only the importer sees the job

        // A bad upload fails at once and says why.
        var bad = Path.Combine(Path.GetTempPath(), $"athar-import-test-{tag}-bad.zip");
        await File.WriteAllTextAsync(bad, "not a zip", ct);
        var failed = await queue.EnqueueAsync(Admin, "space", QaSeed.EngWiki, null, "bad.zip", bad, ct);
        do { await Task.Delay(500, ct); status = await queue.StatusAsync(Admin, failed, ct); } while (status?.State is "queued" or "running");
        Assert.Equal(("failed", "The file isn't a valid zip."), (status!.State, status.Error));
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == QaSeed.Admin && n.Kind == "import_failed" && n.Ref == $"import:{failed}", ct));

        // People and agents uploading by hand keep the 25 MB limit.
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<AtharError>(() => new AttachmentService(db, Store, TimeProvider.System)
            .UploadAsync(Admin, status.Result?.FirstPageId ?? file.PageId, "big.pdf", null, new MemoryStream(big), ct))).Code);
    }

    [Fact]
    public async Task AT_PG_09_confluence_tree_with_a_list_per_child_keeps_every_page_in_place()
    {
        // T165: real exports give each child (and each root) its own <ul>; all of them are walked, in order.
        var ct = AtharApp.Timeout();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = athar.Db(QaSeed.Org);
        var ops = await athar.OpsAsync(db: db);
        var under = (await ops.CreatePageAsync(Admin, QaSeed.EngWiki, null, $"Tree target {tag}", "Here.", Guid.NewGuid().ToString(), ct)).PageId;
        string Page(string title) => ConfluencePage(title, $"<p>{title} {tag}</p>");
        var result = await (await Importer(db)).ImportAsync(Admin, QaSeed.EngWiki, under, "dev.zip", Zip(
            ("GD/index.html", """
                <div class="pageSection"><div class="pageSectionHeader"><h2 class="pageSectionTitle">Available Pages:</h2></div>
                <ul><li><a href="Glee-Dev_1.html">Glee Dev</a>
                    <ul><li><a href="Backend_2.html">Backend</a>
                        <ul><li><a href="API_4.html">API</a></li></ul>
                        <ul><li><a href="Jobs_5.html">Jobs</a></li></ul>
                    </li></ul>
                    <ul><li><a href="Frontend_3.html">Frontend</a></li></ul>
                </li></ul>
                <ul><li><a href="Archive_6.html">Archive</a></li></ul>
                </div>
                """),
            ("GD/Glee-Dev_1.html", Page("Glee Dev")), ("GD/Backend_2.html", Page("Backend")), ("GD/Frontend_3.html", Page("Frontend")),
            ("GD/API_4.html", Page("API")), ("GD/Jobs_5.html", Page("Jobs")), ("GD/Archive_6.html", Page("Archive"))), Guid.NewGuid().ToString(), ct);
        Assert.Equal(6, result.Pages);

        var pages = await db.Pages.AsNoTracking().Where(p => p.Id == under || p.AncestorIds.Contains(under)).ToListAsync(ct);
        Guid Id(string title) => pages.Single(p => p.Title == title).Id;
        List<string> Kids(Guid parent) => [.. pages.Where(p => p.ParentId == parent).OrderBy(p => p.Position).Select(p => p.Title)];
        Assert.Equal(["Glee Dev", "Archive"], Kids(under));
        Assert.Equal(["Backend", "Frontend"], Kids(Id("Glee Dev")));
        Assert.Equal(["API", "Jobs"], Kids(Id("Backend")));
    }

    [Fact]
    public async Task T166_deleting_a_space_trashes_its_pages_hides_it_and_frees_its_slug_for_a_fresh_import()
    {
        var ct = AtharApp.Timeout();
        var tag = Guid.NewGuid().ToString("N")[..6];
        await using var db = athar.Db(QaSeed.Org);
        var admin = new Athar.Web.Auth.WorkspaceAdminService(new Factory(athar));
        MemoryStream Site() => ConfluenceXml(tag, ("96001", $"DEL{tag}", "Doomed"));
        var first = await (await Importer(db)).ImportSpacesAsync(Admin, QaSeed.Engineering, "site.zip", Site(), Guid.NewGuid().ToString(), ct);
        var pages = await db.Pages.AsNoTracking().Where(p => p.SpaceId == first.SpaceId).Select(p => p.Id).ToListAsync(ct);
        Assert.Equal(2, pages.Count);

        // Workspace managers only: a member (even the space's own admin would be refused if not a workspace admin) can't.
        Assert.Equal(OrgDecision.Forbidden, await admin.DeleteSpaceAsync(QaSeed.Org, QaSeed.Member, first.SpaceId, ct));
        Assert.Equal(OrgDecision.Allow, await admin.DeleteSpaceAsync(QaSeed.Org, QaSeed.Admin, first.SpaceId, ct));

        await using var check = athar.Db(QaSeed.Org);
        Assert.False(await check.Spaces.AnyAsync(s => s.Id == first.SpaceId, ct));                       // hidden everywhere
        Assert.DoesNotContain((await admin.WorkspacesAsync(QaSeed.Org, QaSeed.Admin, ct)).SelectMany(w => w.Spaces), s => s.Id == first.SpaceId);
        Assert.All(await check.Pages.AsNoTracking().Where(p => pages.Contains(p.Id)).ToListAsync(ct), p => Assert.NotNull(p.DeletedAt));
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == "space.delete" && e.ResourceId == first.SpaceId.ToString(), ct));

        // The slug is free again: importing the same export makes a new space with it, and the right tree.
        var again = await (await Importer(check)).ImportSpacesAsync(Admin, QaSeed.Engineering, "site.zip", Site(), Guid.NewGuid().ToString(), ct);
        Assert.NotEqual(first.SpaceId, again.SpaceId);
        var fresh = await check.Spaces.AsNoTracking().SingleAsync(s => s.Id == again.SpaceId, ct);
        Assert.Equal(($"del{tag}", "Doomed"), (fresh.Slug, fresh.Name));
        Assert.Equal(2, await check.Pages.CountAsync(p => p.SpaceId == again.SpaceId && p.DeletedAt == null, ct));
    }

    [Fact]
    public void Search_snippets_read_as_plain_text()
    {
        var body = "## Steps\n\n1. Merge to **main**\n\n```bash\nkubectl rollout\n```\n\n:::callout{type=danger}\nNever on Fridays.\n:::\n\nSee [[Rollback]](page:637b5aef-fb36-892b-84a9-fba5ff8746ba) and ![dot](/attachments/x) [pdf](/attachments/y).";
        Assert.Equal("Steps Merge to main kubectl rollout Never on Fridays. See Rollback and dot pdf.", Athar.Web.Components.Pages.Search.Plain(body));
    }
}
