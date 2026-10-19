using System.Text.Json;
using Athar.Data.Seed;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Athar.IntegrationTests.Perf;

/// <summary>Runs only with ATHAR_PERF_URL set: it drives a running Athar, not the test AppHost.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ATHAR_PERF_URL") is not { Length: > 0 }) Skip = "Set ATHAR_PERF_URL to run the edit load (T100, AT-41).";
    }
}

/// <summary>
/// T100 (AT-41, NFR-01/02): edit propagation under concurrent editors, measured in real browsers. Each editor inserts a unique
/// marker every couple of seconds; every other editor on the same page notes when it shows up in its own DOM. All browsers
/// run on this machine, so one clock measures keystroke to remote screen, through collab.
/// <code>
/// ATHAR_PERF_URL=http://192.168.1.20  ATHAR_PERF_EDITORS=20  ATHAR_PERF_PAGES=5  ATHAR_PERF_SECONDS=300 \
///   dotnet test tests/Athar.IntegrationTests --filter FullyQualifiedName~EditLoadTests
/// </code>
/// ATHAR_PERF_PAGES is a page count (the perf seed's first root pages) or a comma-separated list of page ids. The editors sign
/// in as ATHAR_PERF_USER (default: the qa member, an editor of the perf space) with ATHAR_PERF_PASSWORD (default: the qa
/// password). NFR-01 is 20 editors on 5 pages, NFR-02 20 on one.
/// </summary>
public class EditLoadTests(ITestOutputHelper output)
{
    private const double TargetP95Ms = 300; // NFR-01: edit propagation p95 on the LAN

    // In-page probes: when this page sent a marker, and when each marker first appeared here.
    private const string Probe = """
        () => {
          window.__sent = {}; window.__seen = {};
          const editor = document.querySelector('.ProseMirror');
          const scan = (text, now) => {
            for (const m of text.matchAll(/ath[0-9a-f]{4}e\d+n\d+/g)) if (!(m[0] in window.__seen)) window.__seen[m[0]] = now;
          };
          scan(editor.textContent, Date.now()); // markers already there (an earlier run) are never in anyone's __sent
          // Only the changed blocks: rescanning the whole page on every change made a long page's browsers the bottleneck.
          new MutationObserver(records => {
            const now = Date.now();
            for (const r of records) scan(((r.target.nodeType === 3 ? r.target.parentElement : r.target) ?? editor).textContent, now);
          }).observe(editor, { subtree: true, childList: true, characterData: true });
        }
        """;

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    [PerfFact]
    public async Task AT_41_edit_propagation_under_concurrent_editors()
    {
        var url = Env("ATHAR_PERF_URL", "").TrimEnd('/');
        var editors = int.Parse(Env("ATHAR_PERF_EDITORS", "20"));
        var seconds = int.Parse(Env("ATHAR_PERF_SECONDS", "120"));
        var pagesSetting = Env("ATHAR_PERF_PAGES", "5");
        // A count picks perf root pages from a random start, so a run doesn't type into pages earlier runs filled with markers.
        var first = Random.Shared.Next(0, 100 - Math.Min(int.TryParse(pagesSetting, out var n) ? n : 0, 100) + 1);
        Guid[] pages = n > 0 ? [.. Enumerable.Range(first, n).Select(r => PerfSeed.Page(r, 0))] : [.. pagesSetting.Split(',').Select(Guid.Parse)];
        output.WriteLine($"pages: {string.Join(", ", pages)}");
        var run = Convert.ToHexString(BitConverter.GetBytes(Random.Shared.Next()))[..4].ToLowerInvariant();

        if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0) throw new InvalidOperationException("playwright install chromium failed");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var options = new BrowserNewContextOptions { BaseURL = url, IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1200, Height = 900 } };

        // One sign-in; every editor reuses its cookies (a browser each, as far as the server can tell: one circuit and one
        // collab socket per page).
        string state;
        await using (var login = await browser.NewContextAsync(options))
        {
            var p = await login.NewPageAsync();
            await p.GotoAsync("/account/login");
            await p.FillAsync("#email", Env("ATHAR_PERF_USER", QaSeed.Email("Qa Member")));
            await p.FillAsync("#password", Env("ATHAR_PERF_PASSWORD", QaSeed.Password));
            await p.Keyboard.PressAsync("Enter");
            await p.WaitForURLAsync(u => !new Uri(u).AbsolutePath.StartsWith("/account"), new() { Timeout = 30_000 });
            state = await login.StorageStateAsync();
        }

        var tabs = new List<(IPage Page, Guid Doc)>();
        for (var i = 0; i < editors; i++)
        {
            var context = await browser.NewContextAsync(new BrowserNewContextOptions(options) { StorageState = state });
            context.SetDefaultTimeout(30_000);
            var page = await context.NewPageAsync();
            var doc = pages[i % pages.Length];
            await page.GotoAsync($"/wiki/p/{doc}");
            var editor = page.Locator(".ProseMirror[contenteditable=true]");
            await Assertions.Expect(editor).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await page.EvaluateAsync(Probe);
            await editor.Locator("p").Last.ClickAsync();
            await page.Keyboard.PressAsync("End");
            tabs.Add((page, doc));
        }

        // Every editor types a marker every 1-3 s until the deadline.
        var until = DateTime.UtcNow.AddSeconds(seconds);
        await Task.WhenAll(tabs.Select(async (t, i) =>
        {
            var rng = new Random(i);
            for (var seq = 0; DateTime.UtcNow < until; seq++)
            {
                await Task.Delay(rng.Next(1000, 3000));
                var marker = $"ath{run}e{i}n{seq}";
                await t.Page.EvaluateAsync("m => window.__sent[m] = Date.now()", marker);
                await t.Page.Keyboard.InsertTextAsync($" {marker} ");
            }
        }));
        await Task.Delay(5000); // let the last markers arrive

        var sent = new List<Dictionary<string, long>>();
        var seen = new List<Dictionary<string, long>>();
        foreach (var (page, _) in tabs)
        {
            sent.Add(JsonSerializer.Deserialize<Dictionary<string, long>>(await page.EvaluateAsync<string>("() => JSON.stringify(window.__sent)"))!);
            seen.Add(JsonSerializer.Deserialize<Dictionary<string, long>>(await page.EvaluateAsync<string>("() => JSON.stringify(window.__seen)"))!);
        }

        var timed = new List<(long At, double Ms)>();
        var lost = 0;
        for (var s = 0; s < tabs.Count; s++)
        for (var o = 0; o < tabs.Count; o++)
        {
            if (o == s || tabs[o].Doc != tabs[s].Doc) continue;
            foreach (var (marker, at) in sent[s])
                if (seen[o].TryGetValue(marker, out var arrived)) timed.Add((at, arrived - at));
                else lost++;
        }
        static double Pct(List<double> xs, double q) => xs.Count == 0 ? double.NaN : xs[Math.Min(xs.Count - 1, (int)Math.Ceiling(q * xs.Count) - 1)];
        var latencies = timed.Select(t => t.Ms).Order().ToList();
        double P(double q) => Pct(latencies, q);
        var markers = sent.Sum(d => d.Count);
        output.WriteLine($"{editors} editors on {pages.Length} page(s), {seconds} s: {markers} edits, {latencies.Count} deliveries, {lost} lost");
        output.WriteLine($"| Edit propagation | p50 {P(0.50):F0} ms | p95 {P(0.95):F0} ms | max {P(1):F0} ms | NFR-01 p95 target < {TargetP95Ms} ms |");
        // Per minute of the run: steady (a fixed cost) or getting worse (something growing)?
        if (timed.Count > 0)
        {
            var start = timed.Min(t => t.At);
            foreach (var minute in timed.GroupBy(t => (t.At - start) / 60_000).OrderBy(g => g.Key))
            {
                var xs = minute.Select(t => t.Ms).Order().ToList();
                output.WriteLine($"  minute {minute.Key + 1}: {xs.Count} deliveries, p50 {Pct(xs, 0.5):F0} ms, p95 {Pct(xs, 0.95):F0} ms");
            }
        }

        Assert.True(markers > 0, "no edits were made");
        Assert.Equal(0, lost);
        Assert.True(P(0.95) < TargetP95Ms, $"edit propagation p95 {P(0.95):F0} ms");
    }
}
