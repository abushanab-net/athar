using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>AT-35 (FR-KS-01..03): GitHub sync against the fixture's fake GitHub, run by the AppHost's real worker.</summary>
[Collection(AtharCollection.Name)]
public class GitHubSyncTests(AtharApp athar)
{
    [Fact]
    public async Task AT_35_members_search_synced_docs_removed_files_tombstone_revoked_pats_pause_confidential_is_withheld()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token; // the worker syncs, chunks and re-syncs
        var tag = Guid.NewGuid().ToString("N")[..8];
        var repo = $"{AtharApp.GitHubOrg}/handbook-{tag}";
        var token = $"ghp_valid_{tag}";
        var (adr, old, dep) = ($"zebra{tag}", $"rhino{tag}", $"leftpad-{tag}");
        var pkg = JsonSerializer.Serialize(new { dependencies = new Dictionary<string, string> { [dep] = "1.0.0" } });
        athar.GitHub.SetRepo(repo, new()
        {
            ["README.md"] = $"# Handbook\n\nStart here.",
            ["docs/adr/0001-postgres.md"] = $"# ADR 1: one Postgres\n\nWe keep everything in Postgres {adr}.",
            ["docs/old.md"] = $"# Old notes\n\nObsolete {old}.",
            ["package.json"] = pkg,
            ["src/app.cs"] = "class App {}",
        });
        athar.GitHub.Grant(token, repo);

        using var admin = Bearer(QaSeed.AdminPat);   // PilotProject admin
        using var member = Bearer(QaSeed.MemberPat); // PilotProject member, external agent
        using var outsider = Bearer(QaSeed.KnowledgeOwnerPat); // not in PilotProject

        // Attach: allow-listed repos only, by project admins only; the PAT never comes back
        Assert.Equal(HttpStatusCode.Forbidden, (await Attach(admin, "evil-org/repo", token, false, ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Attach(member, $"https://github.com/{repo}", token, false, ct)).Status);
        var attached = await Attach(admin, $"https://github.com/{repo}", token, false, ct);
        Assert.Equal(HttpStatusCode.OK, attached.Status);
        Assert.DoesNotContain(token, attached.Body.GetRawText());
        var source = attached.Body.GetProperty("source_id").GetGuid();

        // Docs and manifest dependencies become searchable for project members only
        await Until(() => Finds(member, adr, ct), true, ct);
        await Until(() => Finds(member, dep, ct), true, ct); // the structure document lists package.json's dependencies
        Assert.False(await Finds(outsider, adr, ct));

        // A removed file is tombstoned and unsearchable after the next sync
        Assert.True(await Finds(member, old, ct));
        athar.GitHub.SetRepo(repo, new()
        {
            ["README.md"] = "# Handbook\n\nStart here.",
            ["docs/adr/0001-postgres.md"] = $"# ADR 1: one Postgres\n\nWe keep everything in Postgres {adr}.",
            ["package.json"] = pkg,
        });
        await Sync(admin, source, HttpStatusCode.OK, ct);
        await Until(() => Finds(member, old, ct), false, ct);
        await using (var db = athar.Db(QaSeed.Org))
        {
            Assert.NotNull((await db.SourceDocuments.SingleAsync(d => d.SourceId == source && d.Path == "docs/old.md", ct)).TombstonedAt);
            var stored = await db.KnowledgeSources.SingleAsync(s => s.Id == source, ct);
            Assert.DoesNotContain(token, Encoding.UTF8.GetString(stored.EncryptedPat!)); // encrypted at rest
        }

        // A revoked PAT pauses the source and notifies the project's admins; a new PAT resumes it
        athar.GitHub.Revoke(token);
        athar.GitHub.SetRepo(repo, new() { ["README.md"] = "# Handbook\n\nChanged." });
        await Sync(admin, source, HttpStatusCode.OK, ct);
        await Until(async () => await Status(admin, source, ct) == "paused_auth", true, ct);
        await using (var db = athar.Db(QaSeed.Org))
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == QaSeed.Admin && n.Kind == "source_paused" && n.Ref == source.ToString(), ct));
        Assert.True(await Finds(member, adr, ct)); // indexed content stays until an admin purges it
        await Sync(admin, source, HttpStatusCode.BadRequest, ct); // paused: set a new PAT first
        athar.GitHub.Grant($"{token}-2", repo);
        using (var res = await admin.PutAsJsonAsync($"/api/v1/sources/{source}/pat", new { pat = $"{token}-2" }, ct)) Assert.True(res.IsSuccessStatusCode);
        await Until(async () => await Status(admin, source, ct) == "ok", true, ct);

        // A confidential source is withheld from external agents, not from a trusted-local one
        var secretRepo = $"{AtharApp.GitHubOrg}/secrets-{tag}";
        var secret = $"walrus{tag}";
        athar.GitHub.SetRepo(secretRepo, new() { ["README.md"] = $"# Secrets\n\nRotation schedule {secret}." });
        athar.GitHub.Grant(token + "-2", secretRepo);
        Assert.Equal(HttpStatusCode.OK, (await Attach(admin, secretRepo, token + "-2", true, ct)).Status);
        using var trusted = Bearer(QaSeed.MemberTrustedPat);
        await Until(() => Finds(trusted, secret, ct), true, ct);
        Assert.False(await Finds(member, secret, ct));
    }

    private HttpClient Bearer(string pat)
    {
        var http = athar.Web();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        return http;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Attach(HttpClient http, string repo, string pat, bool confidential, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/api/v1/projects/{QaSeed.PilotProject}/sources", new { repo, pat, confidential }, ct);
        return (res.StatusCode, JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone());
    }

    private static async Task Sync(HttpClient http, Guid source, HttpStatusCode expected, CancellationToken ct)
    {
        using var res = await http.PostAsync($"/api/v1/sources/{source}/sync", null, ct);
        Assert.True(res.StatusCode == expected, $"{res.StatusCode}: {await res.Content.ReadAsStringAsync(ct)}");
    }

    private static async Task<string?> Status(HttpClient http, Guid source, CancellationToken ct) =>
        (await http.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{QaSeed.PilotProject}/sources", ct)).EnumerateArray()
            .Where(s => s.GetProperty("source_id").GetGuid() == source).Select(s => s.GetProperty("status").GetString()).SingleOrDefault();

    /// <summary>Whether search (sources only) returns a hit containing <paramref name="word"/>.</summary>
    private static async Task<bool> Finds(HttpClient http, string word, CancellationToken ct)
    {
        var result = await http.GetFromJsonAsync<JsonElement>($"/api/v1/search?q={word}&kinds=source", ct);
        return result.GetProperty("hits").EnumerateArray().Any(h => h.GetProperty("snippet").GetString()!.Contains(word) || h.GetProperty("title").GetString()!.Contains(word));
    }

    private static async Task Until(Func<Task<bool>> check, bool expected, CancellationToken ct)
    {
        while (await check() != expected) await Task.Delay(1000, ct);
    }
}
