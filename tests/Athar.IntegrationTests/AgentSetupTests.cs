using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Athar.Data.Seed;

namespace Athar.IntegrationTests;

/// <summary>T132 (FR-AG-08): the setup scripts and the plugin bundle, served anonymously, and setup.mjs run for real.</summary>
[Collection(AtharCollection.Name)]
public class AgentSetupTests(AtharApp athar)
{
    [Fact]
    public async Task AT_58_setup_scripts_and_bundle_are_served_and_setup_configures_each_agent_without_leaking_the_token()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web(); // anonymous
        var sh = await http.GetStringAsync("/agent/setup.sh", ct);
        var ps1 = await http.GetStringAsync("/agent/setup.ps1", ct);
        var mcp = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/.well-known/oauth-protected-resource", ct)).GetProperty("resource").GetString()!;
        foreach (var script in new[] { sh, ps1 })
        {
            Assert.DoesNotContain("{{", script); // every address filled in
            Assert.Contains(mcp, script);
            Assert.DoesNotContain("athar_pat_", script);
        }
        Assert.Contains("read -rsp", sh);              // the token is never echoed or put on a command line
        Assert.DoesNotContain("\r", sh);               // bash refuses CRLF (.gitattributes keeps *.sh LF)
        Assert.Contains("-AsSecureString", ps1);

        // The bundle: a Claude Code marketplace with the plugin (skill, hooks, OAuth MCP entry) and setup.mjs.
        var bytes = await http.GetByteArrayAsync("/agent/bundle.zip", ct);
        var dir = Directory.CreateTempSubdirectory("athar-setup-");
        try
        {
            var bundle = Path.Combine(dir.FullName, "bundle");
            ZipFile.ExtractToDirectory(new MemoryStream(bytes), bundle);
            string Read(string p) => File.ReadAllText(Path.Combine(bundle, p));
            Assert.Equal("athar", JsonDocument.Parse(Read("athar-plugin/.claude-plugin/marketplace.json")).RootElement.GetProperty("name").GetString());
            var server = JsonDocument.Parse(Read("athar-plugin/athar/.mcp.json")).RootElement.GetProperty("mcpServers").GetProperty("athar");
            Assert.Equal(mcp, server.GetProperty("url").GetString());
            Assert.Equal("claude-code", server.GetProperty("oauth").GetProperty("clientId").GetString());
            Assert.Contains("${CLAUDE_PLUGIN_ROOT}/hooks/athar-hook.mjs", Read("athar-plugin/athar/hooks/hooks.json"));
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "agent-skills", "hooks", "athar-hook.mjs"), ct), Read("athar-plugin/athar/hooks/athar-hook.mjs"));
            Assert.True(File.Exists(Path.Combine(bundle, "athar-plugin/athar/skills/athar-brain/SKILL.md")));
            Assert.True(File.Exists(Path.Combine(bundle, "athar.mjs")));
            Assert.False(Directory.Exists(Path.Combine(bundle, "athar-plugin", "setup"))); // setup files sit at the root, not in the marketplace

            // Run setup.mjs as setup.sh would, in a scratch home with Codex and VS Code present. PATH holds only node,
            // so the real claude and codex CLIs on this machine are never touched.
            var home = Directory.CreateDirectory(Path.Combine(dir.FullName, "home")).FullName;
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
            var appData = Path.Combine(home, "AppData", "Roaming");
            var vscodeUser = OperatingSystem.IsWindows() ? Path.Combine(appData, "Code", "User")
                : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "Code", "User") : Path.Combine(home, ".config", "Code", "User");
            Directory.CreateDirectory(vscodeUser);
            await File.WriteAllTextAsync(Path.Combine(vscodeUser, "mcp.json"), """{ "servers": { "other": { "type": "http", "url": "https://example.test/mcp" } } }""", ct);
            await File.WriteAllTextAsync(Path.Combine(home, ".codex", "config.toml"), "model = \"o4\"\n", ct);
            Directory.CreateDirectory(Path.Combine(home, ".athar"));
            await File.WriteAllTextAsync(Path.Combine(home, ".athar", "config.json"), """{ "capture_repos": ["github.com/acme/app"] }""", ct);

            var web = athar.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
            async Task<(int Code, string Out)> Setup(string token)
            {
                var psi = new ProcessStartInfo("node", ["--use-system-ca", Path.Combine(bundle, "setup.mjs"), bundle, web, mcp])
                    { RedirectStandardOutput = true, RedirectStandardError = true };
                psi.Environment["ATHAR_TOKEN"] = token;
                psi.Environment["HOME"] = home;
                psi.Environment["USERPROFILE"] = home;
                psi.Environment["APPDATA"] = appData;
                psi.Environment["PATH"] = Path.GetDirectoryName(NodePath())!;
                // CI trusts the dev certificate through a colon-separated SSL_CERT_DIR, which Node reads as one folder: give it the first.
                if (Environment.GetEnvironmentVariable("SSL_CERT_DIR") is { } certs)
                {
                    var dir = certs.Split(':')[0];
                    psi.Environment["SSL_CERT_DIR"] = dir;
                    if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.pem").FirstOrDefault() is { } pem) psi.Environment["NODE_EXTRA_CA_CERTS"] = pem;
                }
                psi.Environment.Remove("CODEX_HOME");
                using var node = Process.Start(psi)!;
                var output = await node.StandardOutput.ReadToEndAsync(ct) + await node.StandardError.ReadToEndAsync(ct);
                await node.WaitForExitAsync(ct);
                return (node.ExitCode, output);
            }

            var bad = await Setup("athar_pat_not_a_real_token");
            Assert.True(bad.Code != 0, bad.Out);
            Assert.Contains("didn't work", bad.Out);
            Assert.False(File.Exists(Path.Combine(home, ".athar", "athar-hook.mjs"))); // nothing written for a bad token

            for (var run = 0; run < 2; run++) // a second run changes nothing: it merges, never duplicates
            {
                var ok = await Setup(QaSeed.MemberPat);
                Assert.True(ok.Code == 0, ok.Out);
                Assert.DoesNotContain(QaSeed.MemberPat, ok.Out);
            }

            var cfg = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(home, ".athar", "config.json"), ct)).RootElement;
            Assert.Equal((web, QaSeed.MemberPat, "github.com/acme/app"),
                (cfg.GetProperty("url").GetString(), cfg.GetProperty("token").GetString(), cfg.GetProperty("capture_repos")[0].GetString()));
            var toml = await File.ReadAllTextAsync(Path.Combine(home, ".codex", "config.toml"), ct);
            Assert.StartsWith("model = \"o4\"", toml);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(toml, @"^\[mcp_servers\.athar\]", System.Text.RegularExpressions.RegexOptions.Multiline));
            Assert.Contains("client_id = \"codex\"", toml);
            Assert.Contains("callback_port = 3119", toml);
            Assert.DoesNotContain(QaSeed.MemberPat, toml);
            var codexHooks = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(home, ".codex", "hooks.json"), ct)).RootElement.GetProperty("hooks");
            Assert.Single(codexHooks.GetProperty("SessionStart").EnumerateArray());
            Assert.Contains("athar-hook.mjs commit codex", codexHooks.GetProperty("Stop").GetRawText());
            Assert.True(File.Exists(Path.Combine(home, ".agents", "skills", "athar-brain", "SKILL.md")));
            // T143: the athar CLI, with shims for sh and cmd.
            foreach (var f in new[] { "athar.mjs", Path.Combine("bin", "athar"), Path.Combine("bin", "athar.cmd") }) Assert.True(File.Exists(Path.Combine(home, ".athar", f)), f);
            var vscode = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(vscodeUser, "mcp.json"), ct)).RootElement.GetProperty("servers");
            Assert.Equal("https://example.test/mcp", vscode.GetProperty("other").GetProperty("url").GetString()); // other servers kept
            Assert.Equal("vscode", vscode.GetProperty("athar").GetProperty("oauth").GetProperty("clientId").GetString());
            Assert.DoesNotContain(QaSeed.MemberPat, vscode.GetRawText());
        }
        finally { dir.Delete(recursive: true); }
    }

    private static string NodePath()
    {
        var psi = new ProcessStartInfo("node", ["-p", "process.execPath"]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        return p.StandardOutput.ReadToEnd().Trim();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "agent-plugin"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("agent-plugin");
    }
}
