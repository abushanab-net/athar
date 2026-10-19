using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Athar.Data.Seed;

namespace Athar.IntegrationTests;

/// <summary>T136 (FR-AG-04 amended): the real capture hook against the server's capture settings → AT-60 (hook side).</summary>
[Collection(AtharCollection.Name)]
public class HookTests(AtharApp athar)
{
    [Fact]
    public async Task AT_60_the_hook_follows_the_server_repos_and_pause_with_local_excludes_and_an_offline_cache()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var remote = $"github.com/acme/hook-{tag}";
        using var owner = Bearer(QaSeed.KnowledgeOwnerPat); // read + memory:write; its capture settings are restored at the end
        var saved = await owner.GetFromJsonAsync<JsonElement>("/api/v1/me/capture-settings", ct);
        var dir = Directory.CreateTempSubdirectory("athar-hook-");
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(dir.FullName, "home")).FullName;
            var repo = Directory.CreateDirectory(Path.Combine(dir.FullName, "repo")).FullName;
            Git(repo, "init", "-q");
            Git(repo, "remote", "add", "origin", $"git@github.com:Acme/hook-{tag}.git");
            var transcript = Path.Combine(dir.FullName, "transcript.jsonl");
            await File.WriteAllTextAsync(transcript, "{\"role\":\"user\",\"text\":\"we tag releases vX.Y.Z\"}\n", ct);
            var web = athar.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
            Directory.CreateDirectory(Path.Combine(home, ".athar"));
            async Task Config(object extra) => await File.WriteAllTextAsync(Path.Combine(home, ".athar", "config.json"),
                JsonSerializer.Serialize(new Dictionary<string, object> { ["url"] = web, ["token"] = QaSeed.KnowledgeOwnerPat }.Concat(
                    JsonSerializer.SerializeToElement(extra).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value)).ToDictionary()), ct);
            async Task Settings(string[] repos, bool paused)
            {
                using var put = await owner.PutAsJsonAsync("/api/v1/me/capture-settings", new { repos, paused, auto_accept = saved.GetProperty("auto_accept") }, ct);
                Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(ct));
            }
            async Task<bool> Captured(string session)
            {
                await Hook(home, "start", new { session_id = session, cwd = repo }, ct);
                await Hook(home, "commit", new { session_id = session, cwd = repo, transcript_path = transcript }, ct);
                var mine = await owner.GetFromJsonAsync<JsonElement>("/api/v1/sessions", ct);
                return mine.EnumerateArray().Any(s => s.GetProperty("external_session_id").GetString() == session);
            }

            // Opted in on the server only (no local list): captured, and the answer is cached for offline use.
            await Config(new { });
            await Settings([remote], paused: false);
            Assert.True(await Captured($"on-{tag}"));
            var cache = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(home, ".athar", "settings.json"), ct)).RootElement;
            Assert.Equal(remote, cache.GetProperty("repos")[0].GetString());

            // A local exclude wins over the server list.
            await Config(new { capture_exclude = new[] { $"https://github.com/acme/hook-{tag}" } });
            Assert.False(await Captured($"excluded-{tag}"));

            // Pause on the server stops everything, even a locally listed repo.
            await Config(new { capture_repos = new[] { remote } });
            await Settings([remote], paused: true);
            Assert.False(await Captured($"paused-{tag}"));

            // Offline (an unreachable server): the hook keeps the last list it fetched, here paused, and sends nothing.
            await File.WriteAllTextAsync(Path.Combine(home, ".athar", "config.json"), JsonSerializer.Serialize(new { url = "https://127.0.0.1:9", token = QaSeed.KnowledgeOwnerPat }), ct);
            await Hook(home, "start", new { session_id = $"offline-{tag}", cwd = repo }, ct);
            Assert.True(JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(home, ".athar", "settings.json"), ct)).RootElement.GetProperty("paused").GetBoolean());
        }
        finally
        {
            using var restore = await owner.PutAsJsonAsync("/api/v1/me/capture-settings", saved, CancellationToken.None);
            dir.Delete(recursive: true);
        }
    }

    private static async Task Hook(string home, string mode, object payload, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("node", ["--use-system-ca", Path.Combine(RepoRoot(), "agent-skills", "hooks", "athar-hook.mjs"), mode, "claude-code"])
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["HOME"] = home;
        psi.Environment["USERPROFILE"] = home;
        psi.Environment.Remove("ATHAR_URL");
        psi.Environment.Remove("ATHAR_TOKEN");
        if (Environment.GetEnvironmentVariable("SSL_CERT_DIR") is { } certs) // CI: a colon-separated list, which Node reads as one folder
        {
            var first = certs.Split(':')[0];
            psi.Environment["SSL_CERT_DIR"] = first;
            if (Directory.Exists(first) && Directory.GetFiles(first, "*.pem").FirstOrDefault() is { } pem) psi.Environment["NODE_EXTRA_CA_CERTS"] = pem;
        }
        using var node = Process.Start(psi)!;
        await node.StandardInput.WriteAsync(JsonSerializer.Serialize(payload));
        node.StandardInput.Close();
        var err = await node.StandardError.ReadToEndAsync(ct);
        await node.WaitForExitAsync(ct);
        Assert.True(node.ExitCode == 0, err);
    }

    private static void Git(string cwd, params string[] args)
    {
        using var git = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true })!;
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
    }

    private HttpClient Bearer(string pat)
    {
        var http = athar.Web();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        return http;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "agent-skills"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("agent-skills");
    }
}
