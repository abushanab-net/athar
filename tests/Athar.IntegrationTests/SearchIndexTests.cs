using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Athar.IntegrationTests;

/// <summary>T051 (M-C2): normalization (AT-37), the chunking API, embed-page and embed-vectors jobs with revision checks.</summary>
[Collection(AtharCollection.Name)]
public class SearchIndexTests(AtharApp athar)
{
    [Theory]
    [InlineData("أحمد إبراهيم آمن", "احمد ابراهيم امن")]   // hamza and madda forms of alef
    [InlineData("مدرسة", "مدرسه")]                        // ta marbuta
    [InlineData("مـــرحبا", "مرحبا")]                     // tatweel
    [InlineData("إِدَارَةُ المَشْرُوعِ", "اداره المشروع")] // diacritics
    [InlineData("Use `أداة` and ```\nإعداد\n``` as is", "Use `أداة` and ```\nإعداد\n``` as is")] // code untouched
    public void AT_37_normalization(string input, string expected) => Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Chunker_follows_headings_and_keeps_code_whole()
    {
        var long1 = string.Join(" ", Enumerable.Repeat("Sentence one is here.", 60)); // ~1300 chars
        var code = "```\n" + string.Join("\n", Enumerable.Repeat("let x = 1;", 50)) + "\n```";
        var chunks = Chunker.Split("Doc", $"Intro.\n\n# Setup\n\n{long1}\n\n## Tools\n\n{code}\n\nAfter.");

        Assert.Equal("Doc", chunks[0].HeadingPath);
        Assert.Equal("Intro.", chunks[0].Body);
        Assert.All(chunks, c => Assert.True(c.Body.Length <= Chunker.Max));
        Assert.Contains(chunks, c => c.HeadingPath == "Doc › Setup");
        var tools = Assert.Single(chunks, c => c.Body.StartsWith("```"));
        Assert.Equal("Doc › Setup › Tools", tools.HeadingPath);
        Assert.Contains("let x = 1;\n```", tools.Body); // the fence closes in the same chunk
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Idx));
        Assert.Single(Chunker.Split("Title only", "")); // still findable by its title
    }

    [Fact]
    public async Task AT_37_arabic_recall_matches_any_spelling_and_keeps_the_original()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web();
        await using var owner = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.KnowledgeOwnerPat, ct);
        const string body = "تُراجَعُ إدارةُ المشروعِ كلَّ أسبوع.";
        var id = (await Mcp.Call(owner, "remember", new()
        {
            ["title"] = "مراجعة إدارة المشروع", ["body_md"] = body, ["type"] = "procedure", ["scope"] = "company", ["op_id"] = Guid.NewGuid().ToString(),
        }, ct)).Str("memory_id");

        foreach (var query in new[] { "اداره المشروع", "إدارة المشروع", "إِدَارَة" })
        {
            var item = (await Mcp.Call(owner, "recall", new() { ["query"] = query }, ct)).GetProperty("groups")[0].GetProperty("items")
                .EnumerateArray().Single(i => i.Str("id") == id);
            Assert.Equal(body, item.Str("snippet")); // original text, diacritics and all
        }
    }

    [Fact]
    public async Task Embed_page_chunks_the_current_revision_and_discards_a_stale_one()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(QaSeed.Org);
        await Enqueue(db, QaSeed.OnCallPage, 1, ct);
        await Enqueue(db, QaSeed.CompensationPage, 1, ct);
        await Enqueue(db, QaSeed.HandbookPage, 0, ct); // stale: the page is at revision 1

        var onCall = await WaitForChunks(QaSeed.OnCallPage, ct);
        Assert.All(onCall, c => Assert.Equal((QaSeed.OpsWiki, false, 1L), (c.SpaceId!.Value, c.IsConfidential, c.OwnerRevision)));
        Assert.Contains("اجراءات المناوبه", onCall[0].SearchText); // the search copy is normalized
        Assert.StartsWith("إجراءات المناوبة", onCall[0].HeadingPath);    // the heading keeps the original
        Assert.All(await WaitForChunks(QaSeed.CompensationPage, ct), c => Assert.True(c.IsConfidential)); // FR-CL-01 from the space

        await WaitForJob($"{QaSeed.HandbookPage}:0", ct);
        await using var check = athar.Db(QaSeed.Org);
        // Discarded: no chunks at the stale revision and no vectors job for it (other tests may index the current one).
        Assert.False(await check.Chunks.AnyAsync(c => c.OwnerId == QaSeed.HandbookPage && c.OwnerRevision == 0, ct));
        Assert.False(await check.Jobs.AnyAsync(j => j.Kind == IndexJobs.EmbedVectors && j.Key == $"page:{QaSeed.HandbookPage}:0", ct));
        Assert.True(await check.Jobs.AnyAsync(j => j.Kind == IndexJobs.EmbedVectors && j.Key == $"page:{QaSeed.OnCallPage}:1", ct));
    }

    [Fact]
    public async Task Embed_vectors_stores_bge_m3_vectors_for_a_memory()
    {
        // First run pulls bge-m3 into the AppHost's Ollama volume (~1.2 GB); CPU embedding is ~2.6 chunks/s (D8).
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        using var http = athar.Web();
        await using var member = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.MemberPat, ct);
        var id = Guid.Parse((await Mcp.Call(member, "remember", new()
        {
            ["title"] = "Vectors probe", ["body_md"] = "Embeddings are 1024-dimensional.", ["type"] = "fact", ["scope"] = "personal",
            ["op_id"] = Guid.NewGuid().ToString(),
        }, ct)).Str("memory_id"));

        while (true)
        {
            await using var db = athar.Db(QaSeed.Org);
            var vectors = await db.ChunkEmbeddings.Where(e => db.Chunks.Any(c => c.Id == e.ChunkId && c.OwnerId == id)).ToListAsync(ct);
            if (vectors.Count > 0)
            {
                Assert.All(vectors, v => Assert.Equal(("bge-m3", 1024), (v.ModelId, v.Embedding.Length)));
                return;
            }
            var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == IndexJobs.EmbedVectors && j.Key == $"memory:{id}:1", ct);
            Assert.True(job.Status != JobStatus.Dead, job.LastError);
            await Task.Delay(1000, ct);
        }
    }

    [Fact]
    public async Task An_edit_re_embeds_only_the_sections_it_changed()
    {
        // T100: unchanged chunks keep their rows and vectors, so editing doesn't re-embed the whole page every save.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        var admin = new Athar.Web.Mcp.Actor(QaSeed.Admin, QaSeed.Org, null, new HashSet<string> { "read", "write" }, DisclosureClass.TrustedLocal, null);
        static string Body(string last) => $"## Alpha\n\nDeploys need two approvals.\n\n## Beta\n\nBackups are kept for 35 days.\n\n## Gamma\n\n{last}";
        var ops = await athar.OpsAsync();
        var created = await ops.CreatePageAsync(admin, QaSeed.EngWiki, null, $"Incremental {Guid.NewGuid():N}", Body("The first text."), Guid.NewGuid().ToString(), ct);
        var before = await WaitForVectors(created.PageId, created.Revision, ct);

        var edited = await ops.RunAsync(admin, created.PageId, Guid.NewGuid().ToString(),
            new Athar.Web.CollabBridge.PageOp("replace", Body("The changed text."), ExpectedLiveToken: created.LiveToken), ct);
        var after = await WaitForVectors(created.PageId, edited.Revision, ct);

        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Where(v => !v.Body.Contains("first")), after.Where(v => !v.Body.Contains("changed"))); // same chunk and vector ids
        Assert.DoesNotContain(after, v => before.Any(b => b.Body.Contains("first") && (b.Chunk == v.Chunk || b.Vector == v.Vector)));
    }

    [Fact]
    public async Task A_page_indexed_moments_ago_waits_before_indexing_again()
    {
        // T100: on the hosts a page re-indexes at most every Athar:Index:PageEvery (tests run with 0).
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        var admin = new Athar.Web.Mcp.Actor(QaSeed.Admin, QaSeed.Org, null, new HashSet<string> { "read", "write" }, DisclosureClass.TrustedLocal, null);
        var created = await (await athar.OpsAsync()).CreatePageAsync(admin, QaSeed.EngWiki, null, $"Throttle {Guid.NewGuid():N}", "Indexed once.", Guid.NewGuid().ToString(), ct);
        await WaitForVectors(created.PageId, created.Revision, ct);

        var throttled = new Athar.Worker.Jobs.Embed.EmbedPageJob(TimeProvider.System,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Athar:Index:PageEvery"] = "00:10:00" }).Build());
        var job = new Job { Kind = IndexJobs.EmbedPage, Key = "throttle-test", Payload = JsonSerializer.Serialize(new IndexJobs.PagePayload(created.PageId, created.Revision), IndexJobs.Json) };
        await using var db = athar.Db(QaSeed.Org);
        var deferred = await Assert.ThrowsAsync<Athar.Worker.Jobs.JobDeferred>(() => throttled.RunAsync(job, db, ct));
        Assert.InRange(deferred.Delay, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }

    private sealed record Indexed(Guid Chunk, Guid Vector, string Body);

    /// <summary>Waits until every chunk of the page is at <paramref name="revision"/> and has a vector.</summary>
    private async Task<List<Indexed>> WaitForVectors(Guid page, long revision, CancellationToken ct)
    {
        while (true)
        {
            await using var db = athar.Db(QaSeed.Org);
            var rows = await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.Page && c.OwnerId == page).OrderBy(c => c.Idx)
                .Select(c => new { c.Id, c.OwnerRevision, c.Body, Vector = db.ChunkEmbeddings.Where(e => e.ChunkId == c.Id).Select(e => (Guid?)e.Id).FirstOrDefault() })
                .ToListAsync(ct);
            if (rows.Count > 0 && rows.All(r => r.OwnerRevision == revision && r.Vector != null))
                return [.. rows.Select(r => new Indexed(r.Id, r.Vector!.Value, r.Body))];
            await Task.Delay(500, ct);
        }
    }

    private static async Task Enqueue(Athar.Data.AtharDbContext db, Guid page, long revision, CancellationToken ct)
    {
        // What the collab persist enqueues with each revision (docs/03 §4.1).
        db.Jobs.Add(new Job
        {
            Kind = IndexJobs.EmbedPage, Key = $"{page}:{revision}", RunAfter = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.Serialize(new IndexJobs.PagePayload(page, revision), IndexJobs.Json),
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<Chunk>> WaitForChunks(Guid page, CancellationToken ct)
    {
        while (true)
        {
            await using var db = athar.Db(QaSeed.Org);
            var chunks = await db.Chunks.AsNoTracking().Where(c => c.OwnerKind == ChunkOwner.Page && c.OwnerId == page).OrderBy(c => c.Idx).ToListAsync(ct);
            if (chunks.Count > 0) return chunks;
            await Task.Delay(250, ct);
        }
    }

    private async Task WaitForJob(string key, CancellationToken ct)
    {
        while (true)
        {
            await using var db = athar.Db(QaSeed.Org);
            if (await db.Jobs.AnyAsync(j => j.Kind == IndexJobs.EmbedPage && j.Key == key && j.Status == JobStatus.Done, ct)) return;
            await Task.Delay(250, ct);
        }
    }
}
