using System.Text.RegularExpressions;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Extract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

/// <summary>Runs only with ATHAR_TEST_AI=gateway, against the model gateway whose key is in the AppHost's user-secrets.</summary>
public sealed class GatewayFactAttribute : FactAttribute
{
    public GatewayFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ATHAR_TEST_AI") != "gateway") Skip = "Set ATHAR_TEST_AI=gateway to run against the model gateway (T090).";
    }
}

/// <summary>T090: the OpenAI-compatible model gateway, the 1024-value vectors, and re-embedding on a model change.</summary>
[Collection(AtharCollection.Name)]
public class AiGatewayTests(AtharApp athar)
{
    [Fact]
    public void Embeddings_are_cut_to_1024_values_and_renormalized()
    {
        var long1 = Enumerable.Range(1, 2560).Select(i => (float)Math.Sin(i)).ToArray();
        var fitted = AiModels.Fit(long1, 1024);
        Assert.Equal(1024, fitted.Length);
        Assert.Equal(1.0, Math.Sqrt(fitted.Sum(x => (double)x * x)), 5);
        Assert.Equal(Math.Sign(long1[0]), Math.Sign(fitted[0])); // the first values, scaled, not reordered
        Assert.Throws<InvalidOperationException>(() => AiModels.Fit(new float[512], 1024));

        Assert.Equal("bge-m3", AiModels.EmbeddingId(new AiOptions()));
        Assert.Equal("Qwen3-Embedding-4B@1024", AiModels.EmbeddingId(new AiOptions { BaseUrl = "http://gw/v1", ApiKey = "k" }));
        Assert.Throws<InvalidOperationException>(() => new AiOptions { BaseUrl = "http://gw/v1" }.Validated()); // no silent fallback
    }

    [Fact]
    public async Task A_model_change_queues_every_chunk_for_re_embedding()
    {
        var ct = AtharApp.Timeout();
        int before;
        await using (var db0 = athar.Db(QaSeed.Org))
            before = await db0.Chunks.Select(c => new { c.OwnerKind, c.OwnerId, c.OwnerRevision }).Distinct().CountAsync(ct);
        var sweep = new ReembedSweep(new Scopes(athar), new Model("another-model@1024"), TimeProvider.System, NullLogger<ReembedSweep>.Instance);
        var owners = await sweep.SweepAsync(ct);

        // Every chunk owner: no chunk has a vector from a model nobody used (more if other tests chunk meanwhile).
        Assert.True(owners >= before, $"{owners} < {before}");
        await using var db = athar.Db(QaSeed.Org);
        var key = $"memory:{QaSeed.MemoryProject}:1";
        var job = await db.Jobs.SingleAsync(j => j.Kind == IndexJobs.EmbedVectors && j.Key == key, ct);
        Assert.Contains(job.Status, new[] { JobStatus.Ready, JobStatus.Running, JobStatus.Done }); // queued (or already picked up) for the new model
    }

    [GatewayFact]
    public async Task Gateway_embeds_and_extracts_in_the_sessions_language()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var options = Options();
        var vectors = await new GatewayEmbedder(options).EmbedAsync(["Deploys need two approvals.", "يحتاج النشر إلى موافقتين."], cts.Token);
        Assert.All(vectors, v => Assert.Equal((1024, 1.0), (v.Length, Math.Round(Math.Sqrt(v.Sum(x => (double)x * x)), 4))));

        var extractor = new GatewayExtractor(options, NullLogger<GatewayExtractor>.Instance);
        var arabic = await extractor.ExtractAsync("user: قررنا أن تكون مدة الاحتفاظ بالنسخ الاحتياطية خمسة وثلاثين يومًا.", cts.Token);
        Assert.NotEmpty(arabic);
        Assert.All(arabic, c => Assert.Matches(@"\p{IsArabic}", c.Title + c.BodyMd));
        var english = await extractor.ExtractAsync("user: we decided that production deploys always need two approvals.", cts.Token);
        Assert.NotEmpty(english);
        Assert.All(english, c => Assert.DoesNotMatch(@"[\p{IsArabic}\p{IsCJKUnifiedIdeographs}]", c.Title + c.BodyMd));

        // T091: a mixed session keeps each decision in its own language, and nothing in a third script.
        var mixed = await extractor.ExtractAsync("""
            user: قررنا أن تكون مدة الاحتفاظ بالنسخ الاحتياطية خمسة وثلاثين يومًا.
            assistant: Noted. Anything else?
            user: Also, we agreed that every pull request needs a linked ticket before review.
            """, cts.Token);
        static bool Arabic(string? s) => s is not null && Regex.IsMatch(s, @"\p{IsArabic}");
        var shown = string.Join(" | ", mixed.Select(c => $"{c.Title}: {c.BodyMd} <- {c.Evidence}"));
        Assert.True(mixed.Any(c => Arabic(c.Title + c.BodyMd)) && mixed.Any(c => !Arabic(c.Title + c.BodyMd)), shown);
        Assert.True(mixed.All(c => Arabic(c.Title + c.BodyMd) == Arabic(c.Evidence)), shown); // never translated
        Assert.All(mixed, c => Assert.DoesNotMatch(@"[\p{IsCJKUnifiedIdeographs}\p{IsCyrillic}]", c.Title + c.BodyMd));
    }

    private static AiOptions Options()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Athar.AppHost", "appsettings.json"))
            .AddUserSecrets("ad94c0a7-5f8d-46da-bae9-35d202277ca2").Build();
        var o = config.GetSection("Athar:Ai").Get<AiOptions>()!;
        o.ApiKey = config["Parameters:ai-key"];
        Assert.True(o.Gateway, "No gateway key in the AppHost's user-secrets (Parameters:ai-key).");
        return o;
    }

    private sealed class Model(string id) : IEmbedder
    {
        public string ModelId => id;
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Scopes(AtharApp app) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(app.Db(QaSeed.Org));
        private sealed class Scope(Athar.Data.AtharDbContext db) : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type t) => t == typeof(Athar.Data.AtharDbContext) ? db : null;
            public void Dispose() => db.Dispose();
            public ValueTask DisposeAsync() => db.DisposeAsync();
        }
    }
}
