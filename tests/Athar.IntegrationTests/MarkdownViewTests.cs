using Athar.Web.Components.Shared;

namespace Athar.IntegrationTests;

/// <summary>Memory bodies render as Markdown (owner request), with no way for stored text to run script.</summary>
public class MarkdownViewTests
{
    [Fact]
    public void Renders_markdown()
    {
        var html = MarkdownView.Render("My name is **Samy**.\n\n## Details\n- one\n- [x] done\n\n[wiki](/wiki/p/1)");
        Assert.Contains("<strong>Samy</strong>", html);
        Assert.Contains("<h2", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("href=\"/wiki/p/1\"", html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JaVa\tScRiPt:alert(1))")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("![x](data:text/html,<script>alert(1)</script>)")]
    public void Never_emits_script(string markdown)
    {
        var html = MarkdownView.Render(markdown);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        // Text may say "javascript:"; no link or image target may.
        var plain = new string(html.Where(c => c > ' ').ToArray());
        Assert.DoesNotMatch("(?i)(href|src)=\"(javascript|data):", plain);
    }
}
