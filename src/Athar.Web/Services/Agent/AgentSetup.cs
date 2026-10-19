using System.IO.Compression;
using System.Text;

namespace Athar.Web.Services;

/// <summary>
/// T132 (FR-AG-08): one-command agent setup, so nobody needs GitHub access. <c>/agent/bundle.zip</c> is the Claude Code
/// plugin from <c>agent-plugin/</c> (with the athar-brain skill and the capture hook from <c>agent-skills/</c>) plus
/// <c>setup.mjs</c>; <c>/agent/setup.sh</c> and <c>/agent/setup.ps1</c> ask for the PAT and run it. All are anonymous:
/// they hold no secret, only this server's addresses, filled in per request.
/// </summary>
public static class AgentSetup
{
    private const string Plugin = "agent-plugin/";

    public static void MapAgentSetup(this WebApplication app)
    {
        var group = app.MapGroup("/agent").AllowAnonymous();
        group.MapGet("/bundle.zip", (HttpContext http, IConfiguration config) => Results.File(Bundle(Urls(http, config)), "application/zip", "athar-agent.zip"));
        group.MapGet("/setup.sh", (HttpContext http, IConfiguration config) => Results.Text(Fill(Read(Plugin + "setup/setup.sh"), Urls(http, config)), "text/x-shellscript; charset=utf-8"));
        group.MapGet("/setup.ps1", (HttpContext http, IConfiguration config) => Results.Text(Fill(Read(Plugin + "setup/setup.ps1"), Urls(http, config)), "text/plain; charset=utf-8"));
    }

    public sealed record Addresses(string Base, string Server, string Mcp);

    /// <summary>Base: the host the person downloaded from (portal, LAN or agent host). MCP and the hook's REST calls use the agent host.</summary>
    public static Addresses Urls(HttpContext http, IConfiguration config)
    {
        var mcp = Athar.Web.Auth.OAuthSetup.Resource(config);
        return new($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}", mcp.EndsWith("/mcp") ? mcp[..^4] : mcp, mcp);
    }

    public static byte[] Bundle(Addresses urls)
    {
        var files = typeof(AgentSetup).Assembly.GetManifestResourceNames()
            .Select(n => (Name: n, Path: n.Replace('\\', '/')))
            .Where(r => r.Path.StartsWith(Plugin) && !r.Path.EndsWith("/setup.sh") && !r.Path.EndsWith("/setup.ps1") && r.Path != Plugin + "README.md")
            // setup/*.mjs (setup.mjs, and the athar CLI from T143) sit at the bundle's root; the rest is the marketplace.
            .Select(r => (Path: r.Path.StartsWith(Plugin + "setup/") ? r.Path[(Plugin.Length + "setup/".Length)..] : "athar-plugin/" + r.Path[Plugin.Length..],
                Text: r.Path.EndsWith("/athar.mjs") ? Read(r.Path) : Fill(Read(r.Path), urls)))
            .Append(("athar-plugin/athar/skills/athar-brain/SKILL.md", Read("agent/athar-brain/SKILL.md")))
            .Append(("athar-plugin/athar/hooks/athar-hook.mjs", Read("agent/athar-hook.mjs")));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    private static string Fill(string text, Addresses u) =>
        text.Replace("{{BASE_URL}}", u.Base).Replace("{{SERVER_URL}}", u.Server).Replace("{{MCP_URL}}", u.Mcp);

    /// <summary>By path with '/' (RecursiveDir puts '\' in resource names on a Windows build).</summary>
    private static string Read(string path)
    {
        var asm = typeof(AgentSetup).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == path) ?? throw new InvalidOperationException($"{path} isn't embedded");
        using var s = asm.GetManifestResourceStream(name)!;
        return new StreamReader(s).ReadToEnd();
    }
}
