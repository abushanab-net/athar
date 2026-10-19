using System.Text;
using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Athar.IntegrationTests;

/// <summary>
/// AT-40 (BRD G3): the bilingual evaluation set. 30 questions (Eval/athar-eval.json) over the qa evaluation corpus
/// (QaSeed.Eval), asked by Qa Member through an external-agent token, with hybrid ranking (bge-m3 + FTS). At least 80%
/// must have an expected item in the top 5, and no trap item may appear anywhere. The report is printed per question.
/// </summary>
[Collection(AtharCollection.Name)]
public class EvalTests(AtharApp athar, ITestOutputHelper output)
{
    private sealed record Question(string Id, string Lang, string Tool, string Q, string[] Expect, string[]? Never = null, string? Project = null, string? Note = null);
    private sealed record EvalSet(string Description, double PassRate, Question[] Questions);

    [Fact]
    public async Task AT_40_bilingual_evaluation()
    {
        // The corpus needs vectors; the first run may also be pulling bge-m3.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = cts.Token;
        var set = JsonSerializer.Deserialize<EvalSet>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Eval", "athar-eval.json"), ct),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        var names = QaSeed.Eval.ToDictionary(e => e.Value, e => e.Key);
        names[QaSeed.MemoryProject] = "MemoryProject";
        Guid Id(string name) => name == "MemoryProject" ? QaSeed.MemoryProject : QaSeed.Eval[name];
        await EmbedCorpusAsync(ct);

        using var http = athar.Web();
        await using var member = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.MemberPat, ct);
        var report = new StringBuilder("| Q | lang | rank | top 5 | traps | note |\n|---|---|---|---|---|---|\n");
        int hits = 0, violations = 0;
        foreach (var q in set.Questions)
        {
            List<Guid> ids;
            bool degraded;
            if (q.Tool == "search")
            {
                var r = await Mcp.Call(member, "search", new() { ["query"] = q.Q, ["kinds"] = new[] { "page" }, ["limit"] = 10 }, ct);
                degraded = r.GetProperty("degraded").GetBoolean();
                ids = [.. r.GetProperty("hits").EnumerateArray().Select(h => Guid.Parse(h.Str("id")))];
            }
            else
            {
                var args = new Dictionary<string, object?> { ["query"] = q.Q, ["limit"] = 10 };
                if (q.Project == "pilot") args["project_ids"] = new[] { QaSeed.PilotProject.ToString() };
                var r = await Mcp.Call(member, "recall", args, ct);
                degraded = r.GetProperty("degraded").GetBoolean();
                ids = [.. r.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("items").EnumerateArray())
                    .Where(i => i.Str("kind") == "memory").Select(i => Guid.Parse(i.Str("id")))];
            }
            Assert.False(degraded, $"{q.Id}: search is degraded (Ollama or bge-m3 unavailable); AT-40 measures hybrid ranking");

            var expected = q.Expect.Select(Id).ToHashSet();
            var rank = ids.FindIndex(expected.Contains) + 1;
            var traps = (q.Never ?? []).Where(n => ids.Contains(Id(n))).ToList();
            if (rank is > 0 and <= 5) hits++;
            violations += traps.Count;
            var top = string.Join(" ", ids.Take(5).Select(i => names.GetValueOrDefault(i, "·")));
            report.Append($"| {q.Id} | {q.Lang} | {(rank == 0 ? "–" : rank)} | {top} | {(traps.Count == 0 ? "ok" : "LEAK " + string.Join(",", traps))} | {q.Note} |\n");
        }

        var rate = (double)hits / set.Questions.Length;
        report.Append($"\nHit@5: {hits}/{set.Questions.Length} ({rate:P0}); required {set.PassRate:P0}. Trap violations: {violations}.\n");
        output.WriteLine(report.ToString());
        Assert.Equal(0, violations); // a trap is a permission or override leak, never tolerated
        Assert.True(rate >= set.PassRate, report.ToString());
    }

    /// <summary>Queues vectors for the corpus (the seed writes FTS chunks only) and waits until every chunk has one.</summary>
    private async Task EmbedCorpusAsync(CancellationToken ct)
    {
        Guid[] memories = [.. QaSeed.Eval.Where(e => e.Key.StartsWith('E')).Select(e => e.Value), QaSeed.MemoryProject];
        Guid[] pages = [.. QaSeed.Eval.Where(e => e.Key.StartsWith('P')).Select(e => e.Value)];
        await using (var db = athar.Db(QaSeed.Org))
        {
            var jobs = memories.Select(m => IndexJobs.Vectors(ChunkOwner.Memory, m, 1, DateTimeOffset.UtcNow))
                .Concat(pages.Select(p => IndexJobs.Vectors(ChunkOwner.Page, p, 1, DateTimeOffset.UtcNow))).ToList();
            var keys = jobs.Select(j => j.Key).ToArray();
            var queued = (await db.Jobs.Where(j => j.Kind == IndexJobs.EmbedVectors && keys.Contains(j.Key)).Select(j => j.Key).ToListAsync(ct)).ToHashSet();
            db.Jobs.AddRange(jobs.Where(j => !queued.Contains(j.Key)));
            // The worker's startup re-embed sweep queues the same jobs for unembedded chunks; if it got there first, they're queued.
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }) { }
        }
        Guid[] owners = [.. memories, .. pages];
        while (true)
        {
            await using var db = athar.Db(QaSeed.Org);
            var missing = await db.Chunks.CountAsync(c => owners.Contains(c.OwnerId) && !db.ChunkEmbeddings.Any(e => e.ChunkId == c.Id), ct);
            if (missing == 0) return;
            await Task.Delay(1000, ct);
        }
    }
}
