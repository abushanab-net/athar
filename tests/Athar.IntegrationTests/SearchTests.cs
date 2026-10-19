using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Pgvector;

namespace Athar.IntegrationTests;

/// <summary>T052 hybrid search (FR-SR-01..02): FTS + bge-m3 with RRF, permissions inside the query, exact and HNSW paths, degraded mode (AT-39).</summary>
[Collection(AtharCollection.Name)]
public class SearchTests(AtharApp athar)
{
    private static readonly string[] All = ["read", "write", "memory:write", "pages:delete"];

    [Fact]
    public async Task FR_SR_02_hybrid_ranking_finds_meaning_across_languages()
    {
        // The first run may still be pulling bge-m3 (~1.2 GB) into the AppHost's Ollama volume.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        using var http = athar.Web();
        await using var owner = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.KnowledgeOwnerPat, ct);
        var cats = await RememberAndEmbed(owner, "Cats sleep most of the day", "Our office cats nap for hours every afternoon.", ct);

        // No Arabic word is in the memory, so only the vectors can find it.
        var result = await Mcp.Call(owner, "search", new() { ["query"] = "القطط تنام طوال اليوم", ["kinds"] = new[] { "memory" } }, ct);
        Assert.False(result.GetProperty("degraded").GetBoolean());
        // Found at all means found by meaning: FTS can't match it. (Its rank against Arabic memories that share words with
        // the query is the cross-language tuning AT-40 measures, T091.)
        Assert.Contains(cats.ToString(), result.GetProperty("hits").EnumerateArray().Select(h => h.Str("id")));
    }

    [Fact]
    public async Task FR_SR_01_page_hits_follow_access_ai_policy_and_disclosure()
    {
        var ct = AtharApp.Timeout();
        await using (var db = athar.Db(QaSeed.Org))
        {
            foreach (var page in new[] { QaSeed.DeployRunbookPage, QaSeed.HandbookPage, QaSeed.CompensationPage })
                db.Jobs.Add(new Job
                {
                    Kind = IndexJobs.EmbedPage, Key = $"{page}:1:search-test", RunAfter = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.Serialize(new IndexJobs.PagePayload(page, 1), IndexJobs.Json),
                });
            await db.SaveChangesAsync(ct);
        }
        await WaitFor(db => db.Chunks.Where(c => c.OwnerId == QaSeed.HandbookPage || c.OwnerId == QaSeed.DeployRunbookPage || c.OwnerId == QaSeed.CompensationPage)
            .Select(c => c.OwnerId).Distinct().CountAsync(ct), 3, ct);

        using var http = athar.Web();
        var endpoint = new Uri(http.BaseAddress!, "/mcp");
        await using var member = await Mcp.Connect(http, endpoint, QaSeed.MemberPat, ct);
        await using var admin = await Mcp.Connect(http, endpoint, QaSeed.AdminPat, ct);
        async Task<string> Ids(McpClient c, string q) =>
            string.Join(",", (await Mcp.Call(c, "search", new() { ["query"] = q, ["kinds"] = new[] { "page" } }, ct)).GetProperty("hits").EnumerateArray().Select(h => h.Str("id")));

        Assert.Contains(QaSeed.DeployRunbookPage.ToString(), await Ids(member, "roll back previous tag"));
        Assert.DoesNotContain(QaSeed.HandbookPage.ToString(), await Ids(member, "shifts escalation leave")); // not in Operations
        Assert.DoesNotContain(QaSeed.HandbookPage.ToString(), await Ids(admin, "shifts escalation leave"));  // agents: AI policy read is off
        Assert.DoesNotContain(QaSeed.CompensationPage.ToString(), await Ids(admin, "salary bands per level")); // external agent: confidential

        // The same admin as a person (no credential) sees both.
        await using var db2 = athar.Db(QaSeed.Org);
        var person = new Actor(QaSeed.Admin, QaSeed.Org, null, All.ToHashSet(), DisclosureClass.TrustedLocal, null);
        var search = new SearchService(db2, await Embedder(ct));
        Assert.Contains((await search.SearchAsync(person, new("shifts escalation leave", ["page"]), ct)).Hits, h => h.Id == QaSeed.HandbookPage);
        Assert.Contains((await search.SearchAsync(person, new("salary bands per level", ["page"]), ct)).Hits, h => h.Id == QaSeed.CompensationPage);
    }

    [Fact]
    public async Task FR_SR_02_exact_and_hnsw_paths_agree()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        using var http = athar.Web();
        await using var owner = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.KnowledgeOwnerPat, ct);
        var probe = await RememberAndEmbed(owner, "Lighthouse keepers log the weather", "Every four hours the keeper records wind and visibility.", ct);

        await using var db = athar.Db(QaSeed.Org);
        var vector = await db.ChunkEmbeddings.Where(e => db.Chunks.Any(c => c.Id == e.ChunkId && c.OwnerId == probe)).Select(e => e.Embedding).FirstAsync(ct);
        var person = new Actor(QaSeed.KnowledgeOwner, QaSeed.Org, null, All.ToHashSet(), DisclosureClass.TrustedLocal, null);
        var search = new SearchService(db, await Embedder(ct));
        var memories = await search.VisibleMemoriesAsync(person, null, ct);

        var exact = await search.NearestAsync(QaSeed.Org, new Vector(vector), [], memories, 5, exact: true, ct);
        var hnsw = await search.NearestAsync(QaSeed.Org, new Vector(vector), [], memories, 5, exact: false, ct);
        Assert.Equal(probe, exact[0].OwnerId);
        Assert.Equal(exact[0].ChunkId, hnsw[0].ChunkId);
        Assert.All(exact.Concat(hnsw), r => Assert.Contains(r.OwnerId, memories)); // only visible owners
    }

    [Fact]
    public async Task AT_39_with_ollama_stopped_search_and_recall_are_fts_and_degraded()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        using var http = athar.Web();
        await using var member = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.MemberPat, ct);
        var id = (await Mcp.Call(member, "remember", new()
        {
            ["title"] = "Degraded probe kiwi", ["body_md"] = "Kiwi fruit in the fridge.", ["type"] = "fact", ["scope"] = "personal", ["op_id"] = Guid.NewGuid().ToString(),
        }, ct)).Str("memory_id");

        await athar.App.ResourceCommands.ExecuteCommandAsync("ollama", KnownResourceCommands.StopCommand, ct);
        try
        {
            var search = await Mcp.Call(member, "search", new() { ["query"] = "kiwi fridge" }, ct);
            Assert.True(search.GetProperty("degraded").GetBoolean());
            Assert.Contains(search.GetProperty("hits").EnumerateArray(), h => h.Str("id") == id);
            var recall = await Mcp.Call(member, "recall", new() { ["query"] = "kiwi fridge" }, ct);
            Assert.True(recall.GetProperty("degraded").GetBoolean());
            Assert.Contains(recall.GetProperty("groups")[0].GetProperty("items").EnumerateArray(), i => i.Str("id") == id);
        }
        finally
        {
            await athar.App.ResourceCommands.ExecuteCommandAsync("ollama", KnownResourceCommands.StartCommand, CancellationToken.None);
            await athar.App.ResourceNotifications.WaitForResourceHealthyAsync("ollama", CancellationToken.None);
        }
    }

    private async Task<Guid> RememberAndEmbed(McpClient client, string title, string body, CancellationToken ct)
    {
        var id = Guid.Parse((await Mcp.Call(client, "remember", new()
        {
            ["title"] = title, ["body_md"] = body, ["type"] = "fact", ["scope"] = "company", ["op_id"] = Guid.NewGuid().ToString(),
        }, ct)).Str("memory_id"));
        await WaitFor(db => db.ChunkEmbeddings.CountAsync(e => db.Chunks.Any(c => c.Id == e.ChunkId && c.OwnerId == id), ct), 1, ct);
        return id;
    }

    private async Task WaitFor(Func<Athar.Data.AtharDbContext, Task<int>> count, int atLeast, CancellationToken ct)
    {
        while (true)
        {
            await using var db = athar.Db(QaSeed.Org);
            if (await count(db) >= atLeast) return;
            await Task.Delay(500, ct);
        }
    }

    private async Task<QueryEmbedder> Embedder(CancellationToken ct) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ollama"] = await athar.App.GetConnectionStringAsync("ollama", ct),
        }).Build(),
        NullLogger<QueryEmbedder>.Instance);
}
