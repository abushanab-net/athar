using Athar.Data.Seed;
using Microsoft.Playwright;

namespace Athar.IntegrationTests.Smoke;

/// <summary>T080: one headless Chromium for the smoke tests, installed on first use, against the AppHost's web.</summary>
public static class Ui
{
    private static readonly Lazy<Task<IBrowser>> Browser = new(async () =>
    {
        // A CI runner also needs Chromium's system libraries; locally the browser alone is enough.
        string[] args = Environment.GetEnvironmentVariable("CI") == "true" ? ["install", "--with-deps", "chromium"] : ["install", "chromium"];
        if (Microsoft.Playwright.Program.Main(args) != 0) throw new InvalidOperationException("playwright install chromium failed");
        return await (await Playwright.CreateAsync()).Chromium.LaunchAsync();
    });

    /// <summary>The first-visit page tours (T152, PageTour); smoke tests start with them seen, except the tour test.</summary>
    public static readonly string[] Tours = ["home", "wiki-page", "review-inbox", "memories"];

    /// <summary>A fresh signed-out browser context on web.</summary>
    public static async Task<IPage> AnonymousAsync(this AtharApp athar, bool tours = false)
    {
        var context = await (await Browser.Value).NewContextAsync(new()
        {
            BaseURL = athar.App.GetEndpoint("web", "https").ToString().TrimEnd('/'),
            IgnoreHTTPSErrors = true, // the ASP.NET dev certificate
            ViewportSize = new() { Width = 1400, Height = 950 },
        });
        context.SetDefaultTimeout(20_000);
        // A tour's veil would catch the clicks a smoke test makes; mark them seen unless the test is about the tour.
        if (!tours) await context.AddInitScriptAsync($"try {{ for (const id of {System.Text.Json.JsonSerializer.Serialize(Tours)}) localStorage.setItem('athar-tour-' + id, '1'); }} catch {{ }}");
        return await context.NewPageAsync();
    }

    /// <summary>A browser context signed in as the qa user with this display name (e.g. "Qa Admin").</summary>
    public static async Task<IPage> SignInAsync(this AtharApp athar, string displayName, bool tours = false)
    {
        var page = await athar.AnonymousAsync(tours);
        await page.GotoAsync("/account/login");
        await page.FillAsync("#email", QaSeed.Email(displayName));
        await page.FillAsync("#password", QaSeed.Password);
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(u => !new Uri(u).AbsolutePath.StartsWith("/account"));
        return page;
    }

    /// <summary>
    /// Repeats <paramref name="act"/> until <paramref name="effect"/> shows: on a page without an editor there's no sign
    /// that the circuit is live, and a click or a keystroke before it is lost.
    /// </summary>
    public static async Task UntilAsync(Func<Task> act, ILocator effect, int tries = 10)
    {
        for (var i = 1; ; i++)
        {
            await act();
            try
            {
                await Assertions.Expect(effect.First).ToBeVisibleAsync(new() { Timeout = 2_000 });
                return;
            }
            catch (PlaywrightException) when (i < tries) { }
        }
    }

    /// <summary>As <see cref="UntilAsync"/>, for an action whose effect is that <paramref name="gone"/> disappears.</summary>
    public static async Task UntilGoneAsync(Func<Task> act, ILocator gone, int tries = 10)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                if (await gone.CountAsync() > 0) await act();
                await Assertions.Expect(gone).ToHaveCountAsync(0, new() { Timeout = 2_000 });
                return;
            }
            catch (PlaywrightException) when (i < tries) { }
        }
    }

    /// <summary>Presses Tab (or Shift+Tab with <paramref name="back"/>) until <paramref name="target"/> has focus, and checks the focus is visible (AT-46).</summary>
    public static async Task TabToAsync(this IPage page, ILocator target, bool back = false, int max = 120)
    {
        target = target.First;
        var seen = new List<string>();
        for (var i = 0; i < max; i++)
        {
            await page.Keyboard.PressAsync(back ? "Shift+Tab" : "Tab");
            var state = await target.EvaluateAsync<string>("""
                e => {
                  if (e !== document.activeElement) return "no";
                  const c = getComputedStyle(e);
                  return (c.outlineStyle !== "none" && c.outlineWidth !== "0px") || c.boxShadow !== "none" ? "visible" : "invisible";
                }
                """);
            if (state == "no")
            {
                seen.Add(await page.EvaluateAsync<string>("() => { const e = document.activeElement; return `${e?.tagName}.${e?.className}`.slice(0, 60); }"));
                continue;
            }
            if (state == "invisible")
            {
                // No ring on the element itself: MudBlazor fields show focus on their outline border or underline instead, and
                // the editor on the page card around it. Visible means it looks different focused than blurred (screenshots
                // hide the text caret).
                var field = target.Locator("xpath=ancestor-or-self::*[contains(@class,'mud-input-control') or contains(concat(' ', @class, ' '), ' wiki-page ')][1]");
                var focused = await field.ScreenshotAsync();
                await target.BlurAsync();
                var blurred = await field.ScreenshotAsync();
                Assert.False(focused.AsSpan().SequenceEqual(blurred), $"{target} has keyboard focus but nothing shows it");
                await target.FocusAsync();
            }
            return;
        }
        Assert.Fail($"{max} Tab presses never reached {target}; focus went to: {string.Join(" | ", seen.Distinct().Take(25))}");
    }
}
