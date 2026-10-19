using System.Collections.Concurrent;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Athar.IntegrationTests.Smoke;

/// <summary>
/// T167 (FR-AI-01..05) in a real browser: Admin › AI models for admins only, a key field that is always blank, a narrow
/// viewport, and in Arabic the whole save, probe, overlapping-search, answer and reset flow against the fake gateway. The flow runs in OtherOrg,
/// so the qa org keeps its deployment defaults for every other test.
/// </summary>
[Collection(AtharCollection.Name)]
public class AiSettingsSmokeTests(AtharApp athar, Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<IBrowserContext> contexts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in contexts) await c.CloseAsync();
    }

    private async Task<IPage> As(string displayName) => Track(await athar.SignInAsync(displayName));
    private IPage Track(IPage page)
    {
        contexts.Add(page.Context);
        page.Console += (_, message) => { if (message.Type == "error") output.WriteLine(message.Text); };
        page.PageError += (_, error) => output.WriteLine(error);
        return page;
    }

    [Fact]
    public async Task Admins_reach_AI_models_from_the_nav_and_the_key_field_starts_blank()
    {
        var admin = await As("Qa Admin");
        await admin.GotoAsync("/");
        await Ui.UntilAsync(() => admin.GetByRole(AriaRole.Link, new() { Name = "AI models", Exact = true }).ClickAsync(),
            admin.GetByRole(AriaRole.Heading, new() { Name = "AI models", Exact = true }));
        Assert.EndsWith("/admin/ai-models", admin.Url);
        await Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Search index" })).ToBeVisibleAsync();

        // A custom endpoint on the qa org is only drafted here, never saved: other tests rely on its deployment defaults.
        var custom = admin.GetByRole(AriaRole.Radio, new() { Name = "Custom endpoint" });
        await Ui.UntilAsync(() => custom.CheckAsync(new() { Force = true }), admin.GetByLabel("Endpoint URL"));
        await admin.GetByRole(AriaRole.Radio, new() { Name = "Set a key" }).Or(admin.GetByRole(AriaRole.Radio, new() { Name = "Replace the key" })).First.CheckAsync(new() { Force = true });
        var key = admin.GetByLabel("New API key");
        await Expect(key).ToBeVisibleAsync();
        await Expect(key).ToHaveAttributeAsync("type", "password");
        await Expect(key).ToHaveAttributeAsync("autocomplete", "new-password");
        await Expect(key).ToHaveValueAsync("");
        await CaptureAsync(admin, "en-desktop");

        // Narrow screens: the form stacks, nothing scrolls sideways, and Save stays reachable.
        await admin.SetViewportSizeAsync(390, 844);
        await Expect(admin.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeVisibleAsync();
        await admin.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= window.innerWidth + 1", options: new() { Timeout = 5_000 });
        Assert.True(await admin.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "the AI models page scrolls sideways at 390 px");
        await CaptureAsync(admin, "en-mobile");
    }

    [Fact]
    public async Task Members_get_no_AI_models_link_or_page()
    {
        var member = await As("Qa Member"); // Arabic UI
        await member.GotoAsync("/");
        await Expect(member.GetByRole(AriaRole.Link, new() { Name = "نماذج الذكاء الاصطناعي" })).ToHaveCountAsync(0);
        await member.GotoAsync("/admin/ai-models");
        await Expect(member.GetByText("لا يدير هذا إلا مسؤولو المؤسسة.")).ToBeVisibleAsync();
        await Expect(member.GetByLabel("مفتاح API الجديد")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task An_owner_saves_tests_answers_and_resets_in_Arabic_without_the_key_coming_back()
    {
        var ct = AtharApp.Timeout();
        const string secret = "sk-smoke-SECRET-5c1e";
        await using var fake = await FakeAiGateway.StartAsync();
        await SetLocaleAsync("ar", ct);
        var owner = await As("Other Owner");
        try
        {
            await owner.GotoAsync("/admin/ai-models");
            await Expect(owner.Locator("html")).ToHaveAttributeAsync("lang", "ar");
            await Expect(owner.GetByRole(AriaRole.Heading, new() { Name = "نماذج الذكاء الاصطناعي", Exact = true })).ToBeVisibleAsync();
            await ResetIfSavedAsync(owner);

            var custom = owner.GetByRole(AriaRole.Radio, new() { Name = "خادم مخصص" });
            await Ui.UntilAsync(() => custom.CheckAsync(new() { Force = true }), owner.GetByLabel("عنوان الخادم"));
            await owner.GetByLabel("عنوان الخادم").FillAsync(fake.BaseUrl);
            await owner.GetByRole(AriaRole.Radio, new() { Name = "تعيين مفتاح" }).Or(owner.GetByRole(AriaRole.Radio, new() { Name = "استبدال المفتاح" })).First.CheckAsync(new() { Force = true });
            var key = owner.GetByLabel("مفتاح API الجديد");
            await Expect(key).ToHaveValueAsync("");
            await key.FillAsync(secret);
            await FillModelAsync(owner, "نموذج التضمين", fake.EmbeddingModel);
            await FillModelAsync(owner, "نموذج الاستعلام", fake.QueryModel);

            // Load models lists the endpoint's models; Test probes each assigned role with made-up content.
            await owner.GetByRole(AriaRole.Button, new() { Name = "تحميل النماذج" }).ClickAsync();
            await Expect(owner.GetByText("نموذج متاح")).ToBeVisibleAsync();
            await owner.GetByRole(AriaRole.Button, new() { Name = "اختبار", Exact = true }).ClickAsync();
            var results = owner.GetByRole(AriaRole.List, new() { Name = "نتائج الاختبار" });
            await Expect(results.GetByRole(AriaRole.Listitem)).ToHaveCountAsync(2);
            await Expect(results.GetByText("يعمل")).ToHaveCountAsync(2);

            // A new embedding model re-indexes everything, so Save asks first.
            await owner.GetByRole(AriaRole.Button, new() { Name = "حفظ", Exact = true }).ClickAsync();
            await owner.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "حفظ", Exact = true }).ClickAsync();
            await Expect(owner.GetByText("تم الحفظ.")).ToBeVisibleAsync();

            // After a reload the key is stored but never sent back: the field is empty and the page doesn't carry it.
            await owner.ReloadAsync();
            await Expect(owner.GetByText("يوجد مفتاح محفوظ، ولا يُعرض أبدًا.")).ToBeVisibleAsync();
            await Expect(owner.GetByRole(AriaRole.Radio, new() { Name = "خادم مخصص" })).ToBeCheckedAsync();
            Assert.DoesNotContain(secret, await owner.ContentAsync());
            await Ui.UntilAsync(() => owner.GetByRole(AriaRole.Radio, new() { Name = "استبدال المفتاح" }).CheckAsync(new() { Force = true }),
                owner.GetByLabel("مفتاح API الجديد"));
            await Expect(owner.GetByLabel("مفتاح API الجديد")).ToHaveValueAsync("");
            await Expect(owner.GetByText("مقطع مفهرس بالنموذج الحالي").Or(owner.GetByText("مفهرسة بالنموذج الحالي"))).ToBeVisibleAsync();
            await CaptureAsync(owner, "ar-desktop");
            await owner.SetViewportSizeAsync(390, 844);
            await owner.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= window.innerWidth + 1", options: new() { Timeout = 5_000 });
            Assert.True(await owner.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "the Arabic AI models page scrolls sideways at 390 px");
            await CaptureAsync(owner, "ar-mobile");
            await owner.SetViewportSizeAsync(1400, 950);

            await owner.GetByRole(AriaRole.Button, new() { Name = "إعادة بناء الفهرس", Exact = true }).ClickAsync();
            await owner.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "إعادة بناء الفهرس", Exact = true }).ClickAsync();
            await Expect(owner.GetByText("بدأت إعادة البناء. يظهر التقدم هنا.", new() { Exact = true })).ToBeVisibleAsync();
            await Ui.UntilAsync(() => owner.GetByRole(AriaRole.Radio, new() { Name = "استبدال المفتاح" }).CheckAsync(new() { Force = true }), key);

            // The query model answers on the search page, from results this owner can read, citing them.
            var word = "zephyrine" + Random.Shared.Next(1000, 9999);
            // Automatic progress must reflect new content without a click, and must leave the unsaved key draft alone.
            await key.FillAsync("draft-preserved-during-indexing");
            var actor = new Actor(QaSeed.OtherOrgOwner, QaSeed.OtherOrg, null, new HashSet<string> { "read", "write" }, DisclosureClass.TrustedLocal, null);
            var pageId = (await (await athar.OpsAsync()).CreatePageAsync(actor, QaSeed.OtherOrgSpace, null, $"Rollout notes {word}",
                $"The {word} rollout needs two approvals before production.", Guid.NewGuid().ToString(), ct)).PageId;
            await WaitForChunksAsync(pageId, ct);
            await WaitForVectorsAsync(pageId, ct);
            await using (var progress = athar.Db(QaSeed.OtherOrg))
            {
                var total = await progress.Chunks.LongCountAsync(ct);
                await Expect(owner.GetByText($"جميع المقاطع ({total}) مفهرسة بالنموذج الحالي.", new() { Exact = true }))
                    .ToBeVisibleAsync(new() { Timeout = 45_000 });
            }
            await Expect(key).ToHaveValueAsync("draft-preserved-during-indexing");
            await Expect(owner.GetByText("الفهرس محدّث. يتحدث التقدم تلقائيًا كل 3 ثوانٍ.", new() { Exact = true })).ToBeVisibleAsync();
            await key.FillAsync("");
            Assert.Contains(fake.Requests, r => r.Path == "embeddings" && r.Authorization == $"Bearer {secret}"
                && r.Body.GetProperty("input").EnumerateArray().Any(x => x.GetString() is { } text && text.Contains(word) && text.Contains("two approvals")));
            // Overlapping searches (review finding): query A is held at the gateway, then query B is typed into the same search
            // page. B must cancel A (its gateway request is aborted well before the 5 s query-embedding timeout would end it),
            // and the results and the answer on screen must stay B's after A is released.
            var stale = "staleprobe" + Random.Shared.Next(100000, 999999); // in no page, so only the search page embeds it
            var attempts = new ConcurrentDictionary<string, (TaskCompletionSource<DateTimeOffset> Held, TaskCompletionSource<DateTimeOffset> Aborted)>();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.BeforeResponse = async r =>
            {
                if (r.Path != "embeddings") return;
                var text = r.Body.GetProperty("input").EnumerateArray().Select(x => x.GetString())
                    .FirstOrDefault(x => x is not null && attempts.ContainsKey(x));
                if (text is null) return;
                var gate = attempts[text];
                gate.Held.TrySetResult(DateTimeOffset.UtcNow);
                try { await release.Task.WaitAsync(r.Aborted); }
                catch (OperationCanceledException) { gate.Aborted.TrySetResult(DateTimeOffset.UtcNow); }
            };
            try
            {
                await owner.GotoAsync("/search");
                var box = owner.GetByLabel("ابحث في الصفحات والذاكرة");
                var active = (Held: new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously),
                    Aborted: new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously));
                // A cold circuit can lose a submit. Each retry owns its gate, so an older A's abort cannot satisfy B's check.
                for (var i = 1; ; i++)
                {
                    active = (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
                    var attempt = $"{stale}x{i:D2}";
                    attempts[attempt] = active;
                    await box.FillAsync(attempt);
                    await box.PressAsync("Enter");
                    try { await active.Held.Task.WaitAsync(TimeSpan.FromSeconds(2), ct); break; }
                    catch (TimeoutException) when (i < 10) { }
                }
                var heldAt = await active.Held.Task;
                Assert.False(active.Aborted.Task.IsCompleted, "the current query A must still be held when B is submitted");
                await box.FillAsync(word);
                await box.PressAsync("Enter");
                await Expect(owner.GetByRole(AriaRole.Link, new() { Name = $"Rollout notes {word}" }).First).ToBeVisibleAsync();
                var abortedAt = await active.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                Assert.True(abortedAt - heldAt < TimeSpan.FromSeconds(4.5), $"query A was aborted {abortedAt - heldAt} after it started: by its own timeout, not by query B");
            }
            finally
            {
                release.TrySetResult();
                fake.BeforeResponse = null;
            }

            // A is gone: B's results stay, and the answer is about B and cites B.
            await owner.GetByRole(AriaRole.Button, new() { Name = "أجب من هذه النتائج" }).ClickAsync();
            var answer = owner.Locator("section.answer");
            await Expect(answer.Locator(".answer-text")).Not.ToBeEmptyAsync();
            await Expect(answer.GetByRole(AriaRole.Link, new() { Name = $"Rollout notes {word}" })).ToBeVisibleAsync();
            await Expect(owner.GetByRole(AriaRole.Link, new() { Name = $"Rollout notes {word}" }).First).ToBeVisibleAsync();
            await Expect(owner.GetByText("لا شيء مما يمكنك رؤيته يطابق هذا البحث.")).ToHaveCountAsync(0);
            await Expect(owner.GetByLabel("ابحث في الصفحات والذاكرة")).ToHaveValueAsync(word);
            Assert.Contains(fake.Requests, r => r.Path == "chat/completions" && r.Body.ToString().Contains(word));
            Assert.DoesNotContain(fake.Requests, r => r.Path == "chat/completions" && r.Body.ToString().Contains(stale));

            await owner.GotoAsync("/admin/ai-models");
            await Ui.UntilAsync(async () =>
            {
                await owner.GetByRole(AriaRole.Radio, new() { Name = "بدون مفتاح", Exact = true }).CheckAsync(new() { Force = true });
                await owner.GetByRole(AriaRole.Button, new() { Name = "حفظ", Exact = true }).ClickAsync();
            }, owner.GetByText("لا يوجد مفتاح محفوظ.", new() { Exact = true }));
            await owner.ReloadAsync();
            await Expect(owner.GetByRole(AriaRole.Radio, new() { Name = "بدون مفتاح", Exact = true })).ToBeCheckedAsync();
            await Expect(owner.GetByLabel("مفتاح API الجديد")).ToHaveCountAsync(0);

            // Reset goes back to the deployment defaults.
            await owner.GotoAsync("/admin/ai-models");
            await ResetIfSavedAsync(owner);
            await Expect(owner.GetByText("تُستخدم الإعدادات الافتراضية للنشر.")).ToBeVisibleAsync();
        }
        finally
        {
            try { await ResetIfSavedAsync(owner); } catch (PlaywrightException) { }
            finally { await SetLocaleAsync("en", CancellationToken.None); }
        }
    }

    // ponytail: the owner's locale is set in the database before sign-in (the claim outranks the cookie), then put back.
    private async Task SetLocaleAsync(string locale, CancellationToken ct)
    {
        await using var db = athar.Db(QaSeed.OtherOrg);
        await db.Users.Where(u => u.Id == QaSeed.OtherOrgOwner).ExecuteUpdateAsync(s => s.SetProperty(u => u.Locale, locale), ct);
    }

    private static async Task FillModelAsync(IPage page, string label, string model)
    {
        var field = page.GetByLabel(label);
        await field.FillAsync(model);
        await field.PressAsync("Tab");
        await Expect(field).ToHaveValueAsync(model);
        await page.Keyboard.PressAsync("Escape");
    }

    /// <summary>Resets OtherOrg to the deployment defaults through the page when it has saved settings.</summary>
    private static async Task ResetIfSavedAsync(IPage page)
    {
        if (!page.Url.Contains("/admin/ai-models")) await page.GotoAsync("/admin/ai-models");
        var reset = page.GetByRole(AriaRole.Button, new() { Name = "إعادة الإعدادات الافتراضية للنشر" });
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "فهرس البحث" })).ToBeVisibleAsync();
        if (await reset.CountAsync() == 0) return;
        await Ui.UntilAsync(() => reset.First.ClickAsync(), page.GetByRole(AriaRole.Dialog));
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "إعادة الإعدادات الافتراضية للنشر" }).ClickAsync();
        await Expect(page.GetByText("تمت العودة إلى الإعدادات الافتراضية للنشر.")).ToBeVisibleAsync();
    }

    private async Task WaitForChunksAsync(Guid pageId, CancellationToken ct)
    {
        while (true)
        {
            await using (var db = athar.Db(QaSeed.OtherOrg))
                if (await db.Chunks.AnyAsync(c => c.OwnerKind == ChunkOwner.Page && c.OwnerId == pageId, ct)) return;
            await Task.Delay(250, ct);
        }
    }

    private async Task WaitForVectorsAsync(Guid pageId, CancellationToken ct)
    {
        while (true)
        {
            await using (var db = athar.Db(QaSeed.OtherOrg))
                if (await db.ChunkEmbeddings.AnyAsync(e => e.ModelId.StartsWith("ai:")
                    && db.Chunks.Any(c => c.Id == e.ChunkId && c.OwnerKind == ChunkOwner.Page && c.OwnerId == pageId), ct)) return;
            await Task.Delay(250, ct);
        }
    }

    private static async Task CaptureAsync(IPage page, string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "admin-ai-models");
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png"), FullPage = true });
    }
}
