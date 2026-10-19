using System.Text.RegularExpressions;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Athar.Web.Components.Wiki;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Athar.IntegrationTests.Smoke;

/// <summary>
/// T080 Playwright smoke tests: the E legs of AT-09, 13, 17, 20, 37 and 46 in a real browser. Each test makes its own pages
/// through the ops path, so it doesn't depend on what other tests did to the shared qa database.
/// </summary>
[Collection(AtharCollection.Name)]
public class SmokeTests(AtharApp athar) : IAsyncLifetime
{
    private static readonly Actor Admin = new(QaSeed.Admin, QaSeed.Org, null, new HashSet<string> { "read", "write" }, DisclosureClass.TrustedLocal, null);
    private readonly List<IBrowserContext> contexts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in contexts) await c.CloseAsync();
    }

    private async Task<IPage> As(string displayName) => Track(await athar.SignInAsync(displayName));
    private IPage Track(IPage page) { contexts.Add(page.Context); return page; }

    private async Task<Guid> PageWith(string title, string markdown) =>
        (await (await athar.OpsAsync()).CreatePageAsync(Admin, QaSeed.EngWiki, null, title, markdown, Guid.NewGuid().ToString(), AtharApp.Timeout())).PageId;

    private static async Task Open(IPage page, Guid id)
    {
        await page.GotoAsync($"/wiki/p/{id}");
        await Expect(page.Locator(".ProseMirror[contenteditable=true]")).ToBeVisibleAsync();
    }

    /// <summary>The side panel opens on Outline; comments are the second tab (its name carries the open count).</summary>
    private static Task OpenComments(IPage page) =>
        page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("^(Comments|التعليقات)") }).ClickAsync();

    [Fact]
    public async Task AT_09_two_browsers_co_edit_and_see_each_others_carets()
    {
        var id = await PageWith("AT-09 smoke", "Shared start.");
        var admin = await As("Qa Admin");
        var member = await As("Qa Member");
        await Open(admin, id);
        await Open(member, id);

        await member.Locator(".ProseMirror p").Last.ClickAsync();
        await member.Keyboard.TypeAsync(" مرحبا من العضو");
        await Expect(admin.Locator(".ProseMirror")).ToContainTextAsync("مرحبا من العضو");
        await Expect(admin.Locator(".collaboration-carets__label", new() { HasText = "Qa Member" })).ToBeVisibleAsync();

        await admin.Locator(".ProseMirror p").First.ClickAsync();
        await admin.Keyboard.TypeAsync("Admin here. ");
        await Expect(member.Locator(".ProseMirror")).ToContainTextAsync("Admin here.");
        await Expect(member.Locator(".collaboration-carets__label", new() { HasText = "Qa Admin" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AT_13_restoring_a_version_updates_the_open_editor()
    {
        var id = await PageWith("AT-13 smoke", "Version one text.");
        await (await athar.OpsAsync()).RunAsync(Admin, id, Guid.NewGuid().ToString(), new PageOp("append", Markdown: "Version two text."), AtharApp.Timeout());
        var admin = await As("Qa Admin");
        await Open(admin, id);
        await Expect(admin.Locator(".ProseMirror")).ToContainTextAsync("Version two text.");

        await admin.GetByRole(AriaRole.Tab, new() { Name = "Versions" }).ClickAsync();
        // Each version row has preview and restore icon actions; restore asks first in a dialog.
        await admin.GetByRole(AriaRole.Button, new() { Name = "Preview revision 1", Exact = true }).ClickAsync();
        await Expect(admin.Locator(".version-preview")).ToContainTextAsync("Version one text.");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Restore revision 1", Exact = true }).ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Restore this version" }).ClickAsync(); // confirm

        // The editor that was open all along shows the old content (FR-PG-05 through the ops path).
        await Expect(admin.Locator(".ProseMirror")).Not.ToContainTextAsync("Version two text.");
        await Expect(admin.Locator(".ProseMirror")).ToContainTextAsync("Version one text.");
    }

    [Fact]
    public async Task AT_17_a_mention_picked_in_a_comment_reaches_the_mentioned_users_bell()
    {
        var id = await PageWith("AT-17 smoke", "Mention target.");
        var member = await As("Qa Member"); // Arabic UI
        await Open(member, id);
        await OpenComments(member);

        var box = member.Locator(".comments-panel .composer textarea");
        await box.ClickAsync();
        await box.PressSequentiallyAsync("Look ");
        // The mention picker opens from the composer's @ button.
        await member.Locator(".comments-panel .composer .icon-action[aria-pressed]").ClickAsync();
        var picker = member.Locator(".comments-panel .mud-autocomplete input").First;
        await picker.ClickAsync();
        await picker.PressSequentiallyAsync("Qa Ad", new() { Delay = 60 });
        await member.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Qa Admin" }).First.ClickAsync();
        await member.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Comment on page|تعليق على الصفحة") }).ClickAsync();
        await Expect(member.Locator(".comments-panel .user-mention", new() { HasText = "Qa Admin" })).ToBeVisibleAsync();

        var admin = await As("Qa Admin");
        await Open(admin, id); // the editor is live once the circuit is, so the bell's click is handled
        await admin.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Notifications") }).ClickAsync();
        await admin.Locator($".notification-list a[href*='{id}']").First.ClickAsync();
        await admin.WaitForURLAsync($"**/wiki/p/{id}**");
    }

    [Fact]
    public async Task AT_20_a_password_share_link_opens_read_only_for_someone_signed_out()
    {
        var id = await PageWith("AT-20 smoke", "Shared **content** for outsiders.");
        var admin = await As("Qa Admin");
        await Open(admin, id);
        await admin.Locator(".page-bar .share-button").ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Add link", Exact = true }).ClickAsync(); // the link options are a disclosure
        await admin.GetByLabel("Password (optional)").FillAsync("letmein123");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Create link" }).ClickAsync();
        var url = await admin.Locator(".mud-alert a").First.InnerTextAsync();
        Assert.Contains("/s/", url);

        var outsider = Track(await athar.AnonymousAsync());
        var first = await outsider.GotoAsync(url);
        Assert.Equal("no-store", first!.Headers["cache-control"]);
        Assert.Equal("no-referrer", first.Headers["referrer-policy"]);

        await outsider.FillAsync("#password", "wrong");
        await outsider.GetByRole(AriaRole.Button, new() { Name = "Open" }).ClickAsync();
        await Expect(outsider.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();

        await outsider.FillAsync("#password", "letmein123");
        await outsider.GetByRole(AriaRole.Button, new() { Name = "Open" }).ClickAsync();
        await Expect(outsider.Locator("main h1")).ToContainTextAsync("AT-20 smoke");
        await Expect(outsider.Locator("main strong")).ToHaveTextAsync("content");
        await Expect(outsider.Locator("[contenteditable], main form, textarea")).ToHaveCountAsync(0); // read-only
    }

    [Fact]
    public async Task AT_20_an_uploaded_image_shows_in_the_editor_and_through_the_share_link()
    {
        var id = await PageWith("AT-20 upload smoke", "Picture below.");
        var admin = await As("Qa Admin");
        await Open(admin, id);
        await admin.Locator(".ProseMirror p").Last.ClickAsync();
        await admin.Keyboard.PressAsync("End");
        // The toolbar's attach button opens this input (MudFileUpload adds a fresh one after each upload: the last).
        await admin.Locator(".editor-toolbar input[type=file]").Last.SetInputFilesAsync(new FilePayload
        {
            Name = "dot.png", MimeType = "image/png",
            Buffer = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="),
        });
        var image = admin.Locator(".ProseMirror img[src^='/attachments/']");
        await Expect(image).ToBeVisibleAsync();
        await admin.WaitForFunctionAsync("() => document.querySelector(\".ProseMirror img\")?.naturalWidth > 0");
        // A file that isn't an image goes in as a link with its name.
        await admin.Locator(".editor-toolbar input[type=file]").Last.SetInputFilesAsync(new FilePayload
        {
            Name = "notes.pdf", MimeType = "application/pdf", Buffer = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"),
        });
        await Expect(admin.Locator(".ProseMirror a[href^='/attachments/']")).ToHaveTextAsync("notes.pdf");
        await Expect(admin.Locator(".wiki-root .save-state[role=status]")).ToContainTextAsync("Saved"); // empty states are role=status too

        await admin.Locator(".page-bar .share-button").ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Add link", Exact = true }).ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Create link" }).ClickAsync();
        var url = await admin.Locator(".mud-alert a").First.InnerTextAsync();
        var outsider = Track(await athar.AnonymousAsync());
        await outsider.GotoAsync(url);
        await Expect(outsider.Locator("main img[src*='/attachments/']")).ToHaveAttributeAsync("src", new Regex("^/s/.+/attachments/"));
        await outsider.WaitForFunctionAsync("() => document.querySelector(\"main img\")?.naturalWidth > 0");
        await Expect(outsider.Locator("main a", new() { HasText = "notes.pdf" })).ToHaveAttributeAsync("href", new Regex("^/s/.+/attachments/"));
        // The file's own address needs a signed-in reader: the outsider doesn't get the image there.
        var direct = await outsider.GotoAsync(await image.GetAttributeAsync("src") ?? "");
        Assert.DoesNotContain("image/png", direct?.Headers.GetValueOrDefault("content-type") ?? "");
    }

    [Fact]
    public async Task AT_58_agents_hub_makes_a_working_token_fills_in_each_client_and_shows_status()
    {
        var admin = await As("Qa Admin");
        await admin.GotoAsync("/connect"); // the old address redirects to the hub
        await Expect(admin).ToHaveURLAsync(new Regex("/agents$"));
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Agents", Exact = true })).ToBeVisibleAsync();
        ILocator Snippet(string caption) => admin.Locator("figure.snippet", new() { Has = admin.Locator("figcaption", new() { HasText = caption }) }).Locator("code");
        await Expect(Snippet("MCP address")).ToHaveTextAsync(new Regex("^https://.+/mcp$"));

        await Ui.UntilAsync(() => admin.GetByRole(AriaRole.Button, new() { Name = "Create token" }).ClickAsync(), Snippet("Your token"));
        var token = await Snippet("Your token").InnerTextAsync();
        Assert.StartsWith("athar_pat_", token);
        await admin.GetByLabel("Repositories to capture (optional)").FillAsync("github.com/acme/app");
        // The setup now carries the token, the opted-in repository and this host's MCP address.
        await Expect(Snippet(".athar/config.json").First).ToContainTextAsync(token);
        await Expect(Snippet(".athar/config.json").First).ToContainTextAsync("github.com/acme/app");
        await Expect(Snippet("Terminal")).ToContainTextAsync("--client-id claude-code --callback-port 3118"); // MCP signs in with OAuth (T133)
        await Expect(Snippet("macOS / Linux")).ToContainTextAsync("/agent/setup.sh | bash");
        // The hook-capable tabs share the capture settings; the others show their own setup.
        await admin.GetByRole(AriaRole.Tab, new() { Name = "Codex" }).ClickAsync();
        await Expect(Snippet("~/.codex/config.toml")).ToContainTextAsync("client_id = \"codex\"");
        await Expect(Snippet(".athar/config.json").First).ToContainTextAsync("github.com/acme/app");
        await admin.GetByRole(AriaRole.Tab, new() { Name = "Claude apps" }).ClickAsync();
        await Expect(Snippet("OAuth client ID")).ToHaveTextAsync("claude-ai");
        await admin.GetByRole(AriaRole.Tab, new() { Name = "VS Code" }).ClickAsync();
        await Expect(Snippet("mcp.json").First).ToContainTextAsync("\"clientId\": \"vscode\"");
        await Expect(Snippet("mcp.json (PAT)")).ToContainTextAsync("${input:athar-token}");
        await Expect(Snippet("mcp.json (PAT)")).Not.ToContainTextAsync(token); // VS Code keeps the token in its secret storage

        // The token works against the agent API, and a Codex session commit shows up in the status.
        using (var http = athar.Web())
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/inbox")).StatusCode);
            var ct = AtharApp.Timeout();
            var start = await Mcp.Post(http, "/api/v1/sessions", token, new { client = "codex", external_session_id = Guid.NewGuid().ToString(), repo_remote = (string?)null }, ct);
            var commit = await Mcp.Post(http, $"/api/v1/sessions/{start.Body.Str("session_id")}/commit", token, new
            {
                commit_id = Guid.NewGuid().ToString(), from_index = 0, to_index = 0, skipped = 0,
                records = new[] { new { index = 0, data = new { role = "user", text = "hello" } } },
            }, ct);
            Assert.Equal(HttpStatusCode.OK, commit.Status);
        }
        await admin.ReloadAsync();
        ILocator Status(string term) => admin.Locator("dl.status dt", new() { HasText = term }).Locator("xpath=following-sibling::dd[1]");
        await Expect(Status("Codex")).Not.ToContainTextAsync("Never");
        await Expect(Status("Access token last used")).Not.ToContainTextAsync("Never");

        // The downloads are the files in agent-skills/.
        var hook = await admin.Locator("a[download='athar-hook.mjs']").First.GetAttributeAsync("href");
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(RepoRoot(), "agent-skills", "hooks", "athar-hook.mjs")),
            Convert.FromBase64String(hook![(hook.IndexOf("base64,") + 7)..]));

        var member = await As("Qa Member"); // Arabic UI
        await member.GotoAsync("/agents");
        await Expect(member.GetByRole(AriaRole.Heading, new() { Name = "الوكلاء" })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Tab, new() { Name = "تطبيقات Claude" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AT_64_skills_page_searches_installs_and_publishes_by_upload()
    {
        var admin = await As("Qa Admin");
        await admin.GotoAsync("/skills");
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Skills", Exact = true })).ToBeVisibleAsync();
        await Expect(admin.GetByRole(AriaRole.Link, new() { Name = "athar-workflow" })).ToBeVisibleAsync();
        // A search from the URL applies on the first render: no race with the circuit becoming interactive (the typing version
        // flaked under load on #137 and develop).
        await admin.GotoAsync("/skills?q=incident");
        await Expect(admin.GetByLabel("Search skills")).ToHaveValueAsync("incident");
        await Expect(admin.GetByRole(AriaRole.Link, new() { Name = "athar-workflow" })).ToHaveCountAsync(0);
        await Expect(admin.GetByRole(AriaRole.Link, new() { Name = "incident-review" })).ToBeVisibleAsync();

        // Read it, and get the zip and the install command.
        await admin.GetByRole(AriaRole.Link, new() { Name = "incident-review" }).ClickAsync();
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "incident-review" }).First).ToBeVisibleAsync();
        await Expect(admin.GetByRole(AriaRole.Link, new() { Name = "Download zip" })).ToHaveAttributeAsync("href", new Regex("^data:application/zip;base64,"));
        await Expect(admin.Locator("figure.snippet code").First).ToHaveTextAsync("unzip -o ~/Downloads/incident-review.zip -d ~/.claude/skills");

        // Publish a personal skill by uploading its SKILL.md; it opens at revision 1.
        var name = $"smoke-{Guid.NewGuid():N}"[..14];
        await admin.GotoAsync("/skills");
        await Ui.UntilAsync(() => admin.GetByRole(AriaRole.Button, new() { Name = "Publish a skill" }).ClickAsync(), admin.Locator(".publish"));
        await admin.Locator(".publish input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "SKILL.md", MimeType = "text/markdown",
            Buffer = System.Text.Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: Smoke skill\n---\n\n# {name}\n\nDo the thing."),
        });
        await Expect(admin.Locator(".preview")).ToContainTextAsync(name);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Publish", Exact = true }).ClickAsync();
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = name }).First).ToBeVisibleAsync();
        await Expect(admin.Locator(".history summary").First).ToContainTextAsync("Revision 1");

        // T141: a company skill proposal shows in the knowledge owner's review queue as a skill, with its SKILL.md.
        var owner = await As("Qa Knowledge Owner");
        await owner.GotoAsync("/review/proposals");
        var proposal = owner.Locator("article", new() { Has = owner.GetByRole(AriaRole.Heading, new() { Name = "release-notes" }) });
        await Expect(proposal).ToContainTextAsync("Company skill");
        await Expect(proposal).ToContainTextAsync("List user-visible changes first");

        var member = await As("Qa Member"); // Arabic UI; the admin's personal skill isn't theirs to see
        await member.GotoAsync("/skills");
        await Expect(member.GetByRole(AriaRole.Heading, new() { Name = "المهارات" })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Link, new() { Name = name })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Row_action_that_opens_a_dialog_rerenders_its_page()
    {
        // A RowAction's OnClick runs inside RowActions; without Done the owning page never re-rendered and the dialog stayed shut.
        var admin = await As("Qa Admin");
        await admin.GotoAsync("/admin/users");
        var row = admin.GetByRole(AriaRole.Row).Filter(new() { HasText = "lab.viewer@qa.athar.local" });
        await Ui.UntilAsync(() => row.GetByRole(AriaRole.Button, new() { Name = "Change role…" }).ClickAsync(), admin.GetByRole(AriaRole.Dialog));
        await Expect(admin.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Lab Viewer");
        await admin.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(admin.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task FR_MM_04_a_proposed_memory_edit_stays_visible_and_a_long_body_folds()
    {
        var admin = await As("Qa Admin"); // not a knowledge owner: editing a company memory proposes
        try
        {
            await admin.GotoAsync($"/memories/{QaSeed.MemoryArabic}");
            await Ui.UntilAsync(() => admin.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync(), admin.GetByLabel("Body"));
            await admin.GetByLabel("Body").FillAsync("Smoke proposed body.");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await Expect(admin.GetByText("Sent for review. The memory changes")).ToBeVisibleAsync();
            await Expect(admin.Locator(".pending")).ToContainTextAsync("Your edit is waiting");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
            await Expect(admin.GetByLabel("Body")).ToHaveValueAsync("Smoke proposed body.");

            await admin.GotoAsync($"/memories/{QaSeed.MemoryLong}");
            await Expect(admin.Locator(".body-wrap.folded")).ToBeVisibleAsync();
            // Until the circuit is interactive the prerendered button does nothing: click until the body unfolds (flaked on T163's verify).
            await Ui.UntilGoneAsync(() => admin.GetByRole(AriaRole.Button, new() { Name = "Show more" }).ClickAsync(), admin.Locator(".body-wrap.folded"));
            await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Contacts" })).ToBeVisibleAsync();
        }
        finally
        {
            await using var db = athar.Db(QaSeed.Org);
            await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync(
                db.MemoryProposals.Where(p => p.TargetMemoryId == QaSeed.MemoryArabic && p.ProposedBy == QaSeed.Admin));
        }
    }

    [Fact]
    public async Task AT_70_dashboard_shows_own_activity_and_admins_switch_to_the_org()
    {
        // Today's calls for the member through Claude Code, so the bars and the tools table have something to show.
        await using (var db = athar.Db(QaSeed.Org))
        {
            db.McpCalls.AddRange(
                new McpCall { ActorUserId = QaSeed.Member, OauthClientId = Guid.Parse("00000000-0000-7000-8000-0000000c0de1"), Tool = "recall", DurationMs = 40, Outcome = "ok", ResultCount = 3 },
                new McpCall { ActorUserId = QaSeed.Member, ClaimedClient = "cursor", Tool = "remember", DurationMs = 90, Outcome = "ok" });
            await db.SaveChangesAsync();
        }

        var member = await As("Qa Member"); // Arabic UI
        await member.GotoAsync("/");
        await member.GetByRole(AriaRole.Link, new() { Name = "لوحة المتابعة" }).ClickAsync();
        await Expect(member).ToHaveURLAsync(new Regex("/dashboard$"));
        await Expect(member.GetByRole(AriaRole.Heading, new() { Name = "لوحة المتابعة", Level = 1 })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Article, new() { Name = "استدعاءات الوكلاء اليوم" })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Img, new() { Name = "استدعاءات الوكلاء يوميًا" })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Cell, new() { Name = "recall" })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Heading, new() { Name = "طابور المهام" })).ToHaveCountAsync(0); // admin-only
        await Expect(member.GetByText("الكل", new() { Exact = true })).ToHaveCountAsync(0); // no Mine/All for members
        await Capture(member, "member-ar");

        var admin = await As("Qa Admin");
        await admin.GotoAsync("/dashboard");
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Job queue" })).ToBeVisibleAsync();
        // Until the circuit is interactive, a prerendered click does nothing.
        await Ui.UntilAsync(() => admin.GetByText("All", new() { Exact = true }).ClickAsync(),
            admin.GetByText("What everyone's agents did"));
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Model tokens per day" })).ToBeVisibleAsync();
        await Expect(admin.Locator(".legend").GetByText("Claude Code")).ToBeVisibleAsync(); // the member's client, in the org view
        await Capture(admin, "admin-en");
        await admin.SetViewportSizeAsync(390, 844);
        await admin.ReloadAsync();
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Job queue" })).ToBeVisibleAsync();
        await Capture(admin, "admin-mobile");
    }

    private static async Task Capture(IPage page, string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "dashboard");
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png"), FullPage = true });
    }

    [Fact]
    public async Task AT_62_my_sessions_groups_by_project_and_saves_capture_settings()
    {
        var member = await As("Qa Member"); // Arabic UI; read only, so the seeded auto-accepted memory stays
        await member.GotoAsync("/sessions");
        await Expect(member.GetByRole(AriaRole.Heading, new() { Name = "جلساتي" })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Heading, new() { Name = "Athar pilot" })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Article, new() { Name = "Release checklist" })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Button, new() { Name = "تراجع" }).First).ToBeVisibleAsync(); // Undo on the auto-accepted memory
        await Expect(member.GetByRole(AriaRole.Link, new() { Name = "I prefer small PRs" }).First).ToHaveAttributeAsync("href", $"/memories/{QaSeed.MemoryPersonal}");

        // Settings: a repo added in any spelling is saved normalized.
        var admin = await As("Qa Admin");
        try
        {
            await admin.GotoAsync("/sessions");
            await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "My sessions" })).ToBeVisibleAsync();
            // Until the circuit is interactive, a prerendered fill is lost and Add stays disabled.
            await Ui.UntilAsync(() => admin.GetByLabel("Repository (for example github.com/org/repo)").FillAsync("git@github.com:Org/Smoke.git"),
                admin.Locator("button:not([disabled])", new() { HasText = "Add repository" }));
            await admin.GetByRole(AriaRole.Button, new() { Name = "Add repository" }).ClickAsync();
            await admin.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await Expect(admin.GetByText("Capture settings saved")).ToBeVisibleAsync();
            await Expect(admin.Locator(".repo code", new() { HasText = "github.com/org/smoke" })).ToBeVisibleAsync();
        }
        finally
        {
            await using var db = athar.Db(QaSeed.Org);
            await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync(db.CaptureSettings.Where(s => s.UserId == QaSeed.Admin));
        }
    }

    [Fact]
    public async Task AT_66_help_deep_links_and_an_admin_adds_the_getting_started_page()
    {
        var admin = await As("Qa Admin");
        await admin.GotoAsync("/help#capture");
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Help", Exact = true })).ToBeVisibleAsync();
        await Expect(admin.Locator("#capture")).ToBeVisibleAsync();
        await Expect(admin.Locator("#agents")).ToHaveTextAsync("Agents");
        // One guide shows at a time; a link into the admin guide opens it.
        await admin.GotoAsync("/help#admin-users");
        await Expect(admin.Locator("#admin-users")).ToBeVisibleAsync();

        // The admin action (how production gets the page): pick a space, add, and again returns the same pages.
        // Until the circuit is live a click on the select does nothing; once open, its overlay takes further clicks.
        var option = admin.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Engineering › Engineering wiki" });
        await Ui.UntilAsync(async () => { if (!await option.First.IsVisibleAsync()) await admin.GetByLabel("Space").ClickAsync(new() { Force = true }); }, option);
        await option.First.ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Add the getting-started page" }).ClickAsync();
        await Expect(admin.GetByText("The getting-started pages were added")).ToBeVisibleAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Add the getting-started page" }).ClickAsync();
        await using (var db = athar.Db(QaSeed.Org))
        {
            Guid[] added = [.. await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.Pages.Where(p => p.SpaceId == QaSeed.EngWiki && p.ParentId == null && (p.Title == "Getting started with agents" || p.Title == "البدء مع الوكلاء")).Select(p => p.Id))];
            Assert.Equal(2, added.Length);
            await Open(admin, added[0]);
        }

        var member = await As("Qa Member"); // Arabic UI; members see the guide but not the admin action
        await member.GotoAsync("/help");
        await Expect(member.GetByRole(AriaRole.Heading, new() { Name = "المساعدة", Exact = true })).ToBeVisibleAsync();
        await Expect(member.GetByRole(AriaRole.Button, new() { Name = "إضافة صفحة البدء" })).ToHaveCountAsync(0);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "agent-skills"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("agent-skills");
    }

    [Fact]
    public async Task Large_spaces_render_a_collapsed_paged_tree()
    {
        // T100: a space with thousands of pages ran web out of memory when the tree rendered fully expanded.
        var ct = AtharApp.Timeout();
        var pages = new PageService(athar.Db(QaSeed.Org), TimeProvider.System);
        var big = await pages.CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, null, "Tree big parent", ct);
        var closed = await pages.CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, null, "Tree closed parent", ct);
        await pages.CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, closed, "Tree hidden child", ct);
        var loose = await pages.CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, null, "Tree loose page", ct);
        var first = Guid.Empty;
        for (var i = 0; i < 150; i++)
        {
            var id = await pages.CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, big, $"Tree child {i:000}", ct);
            if (i == 0) first = id;
        }

        var admin = await As("Qa Admin");
        await Open(admin, first);
        var tree = admin.Locator(".page-tree");
        // The path to the open page is expanded, 100 siblings at a time; other parents stay collapsed.
        await Expect(tree.Locator("a.tree-title", new() { HasText = "Tree child " })).ToHaveCountAsync(100);
        // Render budget (retro 2026-10-06, action 4): the tree renders at most two steps of rows, however big the space.
        Assert.InRange(await tree.Locator(".tree-row").CountAsync(), 1, 2 * PageTreeItem.Step + 10);
        await Expect(tree.Locator("a.tree-title", new() { HasText = "Tree hidden child" })).ToHaveCountAsync(0);
        await Ui.UntilAsync(() => tree.GetByRole(AriaRole.Button, new() { Name = "Show 50 more" }).ClickAsync(),
            tree.Locator("a.tree-title", new() { HasText = "Tree child 149" }));
        await Expect(tree.Locator("a.tree-title", new() { HasText = "Tree child " })).ToHaveCountAsync(150);

        await tree.GetByRole(AriaRole.Button, new() { Name = "Expand: Tree closed parent" }).ClickAsync();
        await Expect(tree.Locator("a.tree-title", new() { HasText = "Tree hidden child" })).ToBeVisibleAsync();
        await tree.GetByRole(AriaRole.Button, new() { Name = "Collapse: Tree big parent" }).ClickAsync();
        await Expect(tree.Locator("a.tree-title", new() { HasText = "Tree child " })).ToHaveCountAsync(0);

        // Dropping a page in the middle of a collapsed parent nests it there and opens that parent (Lane A's drag and
        // drop). On a short list: with 150 rows open the tree scrolls under Playwright's mouse mid-drag.
        await tree.GetByRole(AriaRole.Button, new() { Name = "Collapse: Tree closed parent" }).ClickAsync();
        await tree.Locator(".tree-row", new() { HasText = "Tree loose page" }).DragToAsync(tree.Locator(".tree-row", new() { HasText = "Tree closed parent" }));
        await Expect(tree.Locator(".tree-row", new() { HasText = "Tree closed parent" }).Locator("xpath=..").Locator("a.tree-title", new() { HasText = "Tree loose page" })).ToBeVisibleAsync();
        var dropped = (await pages.TreeAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, ct: ct)).Single(p => p.Id == loose);
        Assert.Equal(closed, dropped.ParentId);
    }

    [Fact]
    public async Task Long_version_and_comment_lists_render_in_steps()
    {
        // T103: a busy page gains an automatic version every 5 minutes; neither panel may render every row.
        var ct = AtharApp.Timeout();
        var id = await PageWith("T103 long lists", "Busy page.");
        var db = athar.Db(QaSeed.Org);
        var ops = await athar.OpsAsync(db: db);
        var versions = new VersionService(db, ops);
        for (var i = 0; i < VersionsPanel.Step + 10; i++) await versions.SnapshotAsync(Admin, id, ct);
        var comments = new CommentService(db, ops, TimeProvider.System, new NotificationService(db));
        for (var i = 0; i < CommentsPanel.ThreadStep + 10; i++) await comments.AddPageThreadAsync(Admin, id, $"Comment {i:00}", ct);

        var admin = await As("Qa Admin");
        await Open(admin, id);
        await OpenComments(admin);
        var threads = admin.Locator("section.comments-panel article.thread");
        await Expect(threads).ToHaveCountAsync(CommentsPanel.ThreadStep);
        await Ui.UntilAsync(() => admin.GetByRole(AriaRole.Button, new() { Name = "Show 10 more" }).ClickAsync(), threads.Nth(CommentsPanel.ThreadStep));
        await Expect(threads).ToHaveCountAsync(CommentsPanel.ThreadStep + 10);

        await admin.GetByRole(AriaRole.Tab, new() { Name = "Versions" }).ClickAsync();
        var rows = admin.Locator("section.versions-panel li.row");
        await Expect(rows).ToHaveCountAsync(VersionsPanel.Step);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Show older versions" }).ClickAsync();
        await Expect(rows.Nth(VersionsPanel.Step)).ToBeVisibleAsync(); // the next step loaded
    }

    [Fact]
    public async Task Page_chrome_drafts_read_mode_outline_and_menu()
    {
        // T115 (U-1, U-2): a page made in the wiki starts as a draft; Read mode is remembered; outline and ⋯ menu work.
        var admin = await As("Qa Admin");
        await admin.GotoAsync($"/wiki/s/{QaSeed.EngWiki}");
        // Click until the circuit takes it, but only once it does: a slow first click must not make a second page.
        await Ui.UntilAsync(() => admin.Url.Contains("/wiki/p/") ? Task.CompletedTask : admin.GetByRole(AriaRole.Button, new() { Name = "New page" }).First.ClickAsync(),
            admin.Locator(".ProseMirror[contenteditable=true]"));
        await Expect(admin.Locator(".page-actions", new() { HasText = "Draft" })).ToBeVisibleAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Publish" }).ClickAsync();
        await Expect(admin.Locator(".page-actions", new() { HasText = "Draft" })).ToHaveCountAsync(0);
        // The page bar's breadcrumb starts at the space; the byline names the author.
        await Expect(admin.GetByRole(AriaRole.Navigation, new() { Name = "Breadcrumb" })).ToContainTextAsync("Engineering wiki");
        await Expect(admin.Locator(".byline")).ToContainTextAsync("By Qa Admin");

        await admin.Locator(".ProseMirror").ClickAsync();
        await admin.Keyboard.TypeAsync("# Plan\nThree words here");
        await Expect(admin.Locator(".save-state")).ToContainTextAsync("Saved", new() { Timeout = 15_000 });
        // The side panel opens on the outline.
        await Expect(admin.Locator("nav.outline .outline-item", new() { HasText = "Plan" })).ToBeVisibleAsync();

        await admin.GetByRole(AriaRole.Button, new() { Name = "More page actions" }).ClickAsync();
        await Expect(admin.Locator(".page-facts")).ToContainTextAsync("4 words");
        // Watching (T118) lives in the ⋯ menu. Creating the page already watched it.
        await admin.GetByText("Stop watching this page").ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "More page actions" }).ClickAsync();
        await Expect(admin.GetByText("Watch this page", new() { Exact = true })).ToBeVisibleAsync();
        await admin.GetByText("Back to draft").ClickAsync();
        await Expect(admin.Locator(".page-actions", new() { HasText = "Draft" })).ToBeVisibleAsync();

        // Read mode: no toolbar, not editable, and still Read after a reload (this browser's choice).
        await admin.GetByRole(AriaRole.Button, new() { Name = "Read", Exact = true }).ClickAsync();
        await Expect(admin.Locator(".editor-toolbar")).ToHaveCountAsync(0);
        await Expect(admin.Locator(".ProseMirror")).ToHaveAttributeAsync("contenteditable", "false");
        await admin.ReloadAsync();
        await Expect(admin.GetByRole(AriaRole.Button, new() { Name = "Read", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(admin.Locator(".ProseMirror")).ToHaveAttributeAsync("contenteditable", "false");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        await Expect(admin.Locator(".ProseMirror")).ToHaveAttributeAsync("contenteditable", "true");

        // The outline button closes and reopens the side panel (it opens on Outline after a reload). Closed, the panel stays
        // mounted but hidden, so the comment count and resolved highlights stay live.
        await admin.GetByRole(AriaRole.Button, new() { Name = "Outline", Exact = true }).ClickAsync();
        await Expect(admin.Locator("aside.wiki-side")).ToBeHiddenAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Outline", Exact = true }).ClickAsync();
        await Expect(admin.Locator("aside.wiki-side")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task FR_PG_09_import_here_from_the_tree_and_export_the_subtree()
    {
        // T158: "Import here…" on a tree row takes a zip, reports it, and opens the first page under that row; the page menu
        // exports it again (FR-PG-10).
        var tag = Guid.NewGuid().ToString("N")[..8];
        var parent = await PageWith($"Import target {tag}", "Imports land here.");
        var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text) { using var w = new StreamWriter(archive.CreateEntry(path).Open()); w.Write(text); }
            Add($"Guide {tag}.md", $"# Guide {tag}\n\nRead the [Child]({Uri.EscapeDataString($"Guide {tag}")}/Child.md) page.");
            Add($"Guide {tag}/Child.md", "# Child\n\nChild body.");
        }

        var admin = await As("Qa Admin");
        await Open(admin, parent);
        var tree = admin.Locator(".page-tree");
        await tree.GetByRole(AriaRole.Button, new() { Name = $"Page actions: Import target {tag}" }).ClickAsync();
        await admin.GetByText("Import here…").ClickAsync();
        var dialog = admin.Locator(".mud-dialog", new() { HasText = "Import pages…" });
        await dialog.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "guide.zip", MimeType = "application/zip", Buffer = zip.ToArray() });
        // T164: uploaded over HTTP, then imported by the background job while the dialog follows it.
        await Expect(dialog.GetByRole(AriaRole.Status)).ToContainTextAsync("Imported 2 pages and 0 files", new() { Timeout = 60_000 });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true }).ClickAsync();
        await Expect(admin.Locator(".title-input")).ToHaveValueAsync($"Guide {tag}");
        await Expect(admin.Locator(".ProseMirror")).ToContainTextAsync("Read the Child page.");
        await Expect(tree.Locator("a.tree-title", new() { HasText = $"Guide {tag}" })).ToBeVisibleAsync();

        // Export: the page menu links to the cookie download, which returns the subtree as a zip.
        await admin.GetByRole(AriaRole.Button, new() { Name = "More page actions" }).ClickAsync();
        var link = admin.Locator(".mud-popover-open .mud-menu-item", new() { HasText = "Export with subpages (.zip)" });
        var href = await link.GetAttributeAsync("href") ?? "";
        Assert.Contains("format=zip", href);
        var response = await admin.Context.APIRequest.GetAsync(href);
        Assert.Equal((200, "application/zip"), (response.Status, response.Headers["content-type"]));
        var names = new System.IO.Compression.ZipArchive(new MemoryStream(await response.BodyAsync())).Entries.Select(e => e.FullName).ToList();
        Assert.Contains($"Guide {tag}/Child.md", names);
    }

    [Fact]
    public async Task Page_tree_row_actions_duplicate_copy_and_quick_links()
    {
        // T116: row + adds a subpage, Duplicate lands right after the original, Copy to space copies, trash confirms.
        var ct = AtharApp.Timeout();
        var pages = new PageService(athar.Db(QaSeed.Org), TimeProvider.System);
        var parent = await pages.CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, null, "Tree actions parent", ct);
        var child = await pages.CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, parent, "Tree actions child", ct);
        await pages.CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, parent, "Tree actions sibling", ct);

        var admin = await As("Qa Admin");
        await Open(admin, child);
        var tree = admin.Locator(".page-tree");
        await Expect(admin.Locator(".quick-links", new() { HasText = "Overview" })).ToBeVisibleAsync();

        // Duplicate: a "(copy)" draft right after the original, then opened.
        await tree.GetByRole(AriaRole.Button, new() { Name = "Page actions: Tree actions child" }).ClickAsync();
        await admin.GetByText("Duplicate", new() { Exact = true }).ClickAsync();
        await Expect(admin.Locator(".title-input")).ToHaveValueAsync("Tree actions child (copy)");
        await Expect(admin.Locator(".page-actions", new() { HasText = "Draft" })).ToBeVisibleAsync();
        var order = await tree.Locator("a.tree-title", new() { HasText = "Tree actions " }).AllInnerTextsAsync();
        Assert.Equal(["Tree actions parent", "Tree actions child", "Tree actions child (copy)", "Tree actions sibling"], order);

        // Row +: a draft subpage under that row.
        await tree.GetByRole(AriaRole.Button, new() { Name = "Add a subpage: Tree actions sibling" }).ClickAsync();
        await admin.WaitForURLAsync(u => !u.Contains(child.ToString()) && u.Contains("/wiki/p/"));
        await Expect(admin.Locator(".page-actions", new() { HasText = "Draft" })).ToBeVisibleAsync();

        // Copy to space: the dialog shows the page count; the copy lands in the other space.
        await tree.GetByRole(AriaRole.Button, new() { Name = "Page actions: Tree actions parent" }).ClickAsync();
        await admin.GetByText("Copy to space…").ClickAsync();
        await Expect(admin.Locator(".copy-count")).ToContainTextAsync("5");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Copy", Exact = true }).ClickAsync();
        await Expect(admin.Locator(".mud-snackbar", new() { HasText = "Copied to" })).ToBeVisibleAsync();

        // Trash from a row asks first; cancelling keeps the page.
        await tree.GetByRole(AriaRole.Button, new() { Name = "Page actions: Tree actions sibling" }).ClickAsync();
        await admin.GetByText("Move to trash").ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(tree.Locator("a.tree-title", new() { HasText = "Tree actions sibling" })).ToBeVisibleAsync();

        // The Pages menu: collapse all keeps only the open page's path; show trash switches the list.
        await admin.GetByRole(AriaRole.Button, new() { Name = "Page tree options" }).ClickAsync();
        await admin.GetByText("Show trash").ClickAsync();
        await Expect(admin.Locator(".trash-list")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AT_37_arabic_renders_right_to_left_and_search_matches_any_spelling()
    {
        var id = await PageWith("AT-37 smoke", "English first.\n\nالأخطاء تتبع العقد.");
        var member = await As("Qa Member"); // locale ar
        await Open(member, id);
        await Expect(member.Locator("html")).ToHaveAttributeAsync("dir", "rtl");
        await Expect(member.Locator("html")).ToHaveAttributeAsync("lang", "ar");
        // dir="auto" per block: the Arabic paragraph is right-to-left, the English one left-to-right.
        Assert.Equal("rtl", await member.Locator(".ProseMirror p", new() { HasText = "الأخطاء" }).EvaluateAsync<string>("e => getComputedStyle(e).direction"));
        Assert.Equal("ltr", await member.Locator(".ProseMirror p", new() { HasText = "English first" }).EvaluateAsync<string>("e => getComputedStyle(e).direction"));

        // Search without the shadda finds the seeded on-call page (يصعّد) and shows the original spelling.
        var admin = await As("Qa Admin");
        await admin.GotoAsync("/search?q=" + Uri.EscapeDataString("يصعد"));
        await Expect(admin.GetByRole(AriaRole.Listitem).Filter(new() { HasText = "يصعّد" }).First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AT_46_tree_editor_comment_and_share_work_from_the_keyboard_with_visible_focus()
    {
        var start = await PageWith("AT-46 start", "Start page.");
        var target = await PageWith("AT-46 target", "Target page.");
        var admin = await As("Qa Admin");
        await Open(admin, start);

        // Tree: Tab to the other page's link and open it with Enter.
        await admin.TabToAsync(admin.GetByRole(AriaRole.Link, new() { Name = "AT-46 target" }));
        await admin.Keyboard.PressAsync("Enter");
        await admin.WaitForURLAsync($"**/wiki/p/{target}");
        await Expect(admin.Locator(".ProseMirror[contenteditable=true]")).ToContainTextAsync("Target page.");

        // Editor focus.
        await admin.TabToAsync(admin.Locator(".ProseMirror"));
        await admin.Keyboard.PressAsync("End");
        await admin.Keyboard.TypeAsync(" Typed by keyboard.");
        await Expect(admin.Locator(".ProseMirror")).ToContainTextAsync("Typed by keyboard.");

        // A page comment: the side tabs follow the ARIA tabs pattern (only the selected tab takes Tab focus, arrows move).
        await admin.TabToAsync(admin.GetByRole(AriaRole.Tab, new() { Selected = true }));
        await admin.Keyboard.PressAsync("ArrowRight");
        await Expect(admin.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("^Comments") })).ToHaveAttributeAsync("aria-selected", "true");
        await admin.TabToAsync(admin.GetByLabel("Add a comment"));
        await admin.Keyboard.TypeAsync("Keyboard comment");
        await admin.TabToAsync(admin.GetByRole(AriaRole.Button, new() { Name = "Comment on page" }));
        await admin.Keyboard.PressAsync("Enter");
        await Expect(admin.Locator(".comments-panel", new() { HasText = "Keyboard comment" })).ToBeVisibleAsync();

        // Share opens from the page bar: back up to it and press Enter.
        var share = admin.Locator(".page-bar .share-button");
        await admin.TabToAsync(share, back: true);
        await admin.Keyboard.PressAsync("Enter");
        await Expect(share).ToHaveAttributeAsync("aria-pressed", "true");
        // The link options open from the "Add link" disclosure.
        var addLink = admin.GetByRole(AriaRole.Button, new() { Name = "Add link", Exact = true });
        await admin.TabToAsync(addLink);
        await admin.Keyboard.PressAsync("Enter");
        await Expect(addLink).ToHaveAttributeAsync("aria-expanded", "true");
        await admin.TabToAsync(admin.GetByRole(AriaRole.Button, new() { Name = "Create link" }));
        await admin.Keyboard.PressAsync("Enter");
        await Expect(admin.Locator(".mud-alert a")).ToContainTextAsync("/s/");
    }

    [Fact]
    public async Task Slash_commands_insert_blocks_from_a_filtered_menu()
    {
        // An empty page: a late collab update can move the caret to the start of the document, which would put the
        // commands in front of any stored text (the test, not the menu, depended on the caret).
        var id = await PageWith("Slash smoke", "");
        var admin = await As("Qa Admin");
        await Open(admin, id);
        await Expect(admin.Locator(".save-state")).ToContainTextAsync(new Regex("Synced|Saved"));
        await admin.Locator(".ProseMirror").ClickAsync();

        // "/" opens the grouped menu; typing filters it; Enter turns the block into the pick.
        await admin.Keyboard.TypeAsync("/");
        var menu = admin.Locator(".slash-menu");
        await Expect(menu.GetByRole(AriaRole.Option, new() { NameRegex = new Regex("^Heading 2") })).ToBeVisibleAsync();
        await Expect(menu.Locator(".slash-group").First).ToHaveTextAsync("Basic");
        // T110: every row's icon starts at the same x (a centred row moved it with the text's width).
        var lefts = await menu.Locator(".slash-icon").EvaluateAllAsync<double[]>("els => els.map(e => Math.round(e.getBoundingClientRect().left))");
        Assert.Single(lefts.Distinct());
        await admin.Keyboard.TypeAsync("h2");
        await Expect(menu.GetByRole(AriaRole.Option)).ToHaveCountAsync(1);
        await admin.Keyboard.PressAsync("Enter");
        await admin.Keyboard.TypeAsync("Slash heading");
        await Expect(admin.Locator(".ProseMirror h2")).ToHaveTextAsync("Slash heading");
        await Expect(menu).ToHaveCountAsync(0);

        // English keywords work too; "/" and the query are replaced by the block.
        await admin.Keyboard.PressAsync("Enter");
        await admin.Keyboard.TypeAsync("/todo");
        await admin.Keyboard.PressAsync("Enter");
        await admin.Keyboard.TypeAsync("Ship it");
        await Expect(admin.Locator(".ProseMirror ul[data-type=taskList]")).ToContainTextAsync("Ship it");
        await Expect(admin.Locator(".ProseMirror")).Not.ToContainTextAsync("/todo");

        // Escape closes the menu and keeps the "/" as text; the image entry opens the page's URL dialog.
        await admin.Keyboard.PressAsync("Enter");
        await admin.Keyboard.PressAsync("Enter");
        await admin.Keyboard.TypeAsync("/");
        await Expect(menu).ToBeVisibleAsync();
        await admin.Keyboard.PressAsync("Escape");
        await Expect(menu).ToHaveCountAsync(0);
        await admin.Keyboard.PressAsync("Backspace");
        await admin.Keyboard.TypeAsync("/image");
        await admin.Keyboard.PressAsync("Enter");
        await Expect(admin.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Space_settings_open_from_the_wiki_as_tabs()
    {
        var admin = await As("Qa Admin");
        await admin.GotoAsync($"/wiki/s/{QaSeed.EngWiki}");
        var dialog = admin.GetByRole(AriaRole.Dialog);
        // The circuit may not be live at the first click (as for "Create token"): click until the dialog opens.
        await Ui.UntilAsync(() => admin.GetByRole(AriaRole.Button, new() { Name = "Space settings" }).ClickAsync(), dialog);
        await Expect(dialog.GetByRole(AriaRole.Tab, new() { Name = "Settings" })).ToBeVisibleAsync();
        await Expect(dialog.GetByLabel("Space name")).ToHaveValueAsync("Engineering wiki");

        await dialog.GetByRole(AriaRole.Tab, new() { Name = "Members" }).ClickAsync();
        await dialog.GetByLabel("Search members").FillAsync("Qa Guest");
        await Expect(dialog.GetByText("Qa Guest")).ToBeVisibleAsync();
        await Expect(dialog.GetByText("Qa Member")).ToHaveCountAsync(0);

        await dialog.GetByRole(AriaRole.Tab, new() { Name = "Security" }).ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Mark as confidential|Declassify") })).ToBeVisibleAsync();

        // Not for a viewer.
        var guest = await As("Qa Guest");
        await guest.GotoAsync($"/wiki/s/{QaSeed.EngWiki}");
        await Expect(guest.Locator(".wiki-tree h2")).ToBeVisibleAsync();
        await Expect(guest.GetByRole(AriaRole.Button, new() { Name = "Space settings" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_home_tour_starts_once_and_the_help_button_replays_it()
    {
        // T152: a first visit gets a short skippable tour; it doesn't come back by itself, and the top bar's ? replays it.
        var admin = Track(await athar.SignInAsync("Qa Admin", tours: true));
        var card = admin.Locator("#tour-card");
        await Expect(card).ToBeVisibleAsync();
        await Expect(card).ToContainTextAsync("1 of");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        await Expect(card).ToContainTextAsync("2 of");
        await admin.Keyboard.PressAsync("Escape");
        await Expect(card).ToBeHiddenAsync();

        await admin.ReloadAsync();
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync();
        await Expect(card).ToBeHiddenAsync();
        await Ui.UntilAsync(() => admin.Locator("#tour-help").ClickAsync(), card);
    }

    [Fact]
    public async Task A_profile_photo_shows_in_the_top_bar()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var page = await As("Lab Commenter");
        await page.GotoAsync("/account/profile");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Upload photo" })).ToBeVisibleAsync();
        // Choosing a file opens the crop panel; only the cropped square is uploaded.
        await Ui.UntilAsync(() => page.Locator("input[type=file]").First.SetInputFilesAsync(new FilePayload { Name = "me.png", MimeType = "image/png", Buffer = png }),
            page.GetByRole(AriaRole.Group, new() { Name = "Crop your photo" }));
        await Expect(page.GetByLabel("Photo framing")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save photo" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Remove photo" })).ToBeVisibleAsync();
        // The top bar's avatar now loads the photo, as the 512 px square the crop made.
        await page.ReloadAsync();
        await page.WaitForFunctionAsync("() => [...document.querySelectorAll('.person-avatar img')].some(i => i.complete && i.naturalWidth === 512 && i.naturalHeight === 512)");
        // After the reload the circuit may not be live at the first click.
        var remove = page.GetByRole(AriaRole.Button, new() { Name = "Remove photo" });
        await Ui.UntilGoneAsync(() => remove.ClickAsync(new() { Timeout = 2_000 }), remove);
    }
}
