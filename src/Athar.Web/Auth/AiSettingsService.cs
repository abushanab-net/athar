using System.Text.Json;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Auth;

public sealed record AiSettingsView(long Revision, bool UsesEnvironment, string? BaseUrl, string EmbeddingModel, string QueryModel,
    string? VisionModel, bool HasApiKey, bool KeyUnreadable);
public sealed record AiIndexStatus(string EmbeddingIdentity, long TotalChunks, long EmbeddedChunks, long ReadyJobs, long RunningJobs, long FailedJobs);
public sealed record AiRoleTest(string Role, bool Success, string Code);
public sealed record AiTestResult(IReadOnlyList<AiRoleTest> Roles);

public sealed class AiSettingsService(IDbContextFactory<AtharDbContext> dbs, AiSettingsResolver resolver)
{
    public async Task<AiSettingsView> ReadAsync(Actor actor, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(actor, ct);
        var row = await db.AiSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        var s = resolver.Resolve(actor.OrgId, row);
        return new(row?.Revision ?? 0, s.UsesEnvironment, s.BaseUrl, s.EmbeddingModel, s.QueryModel, s.VisionModel,
            s.ApiKey is { Length: > 0 } || s.KeyUnreadable, s.KeyUnreadable);
    }

    public Task SaveAsync(Actor actor, AiSettingsWrite input, CancellationToken ct = default) =>
        MutateAsync(actor, input.ExpectedRevision, "ai_settings.save", row =>
        {
            var s = Draft(actor.OrgId, row, input);
            (row.UsesEnvironment, row.BaseUrl, row.EmbeddingModel, row.QueryModel, row.VisionModel) =
                (false, s.BaseUrl, s.EmbeddingModel, s.QueryModel, s.VisionModel);
            row.ProtectedApiKey = s.ApiKey is null ? null : resolver.Protect(actor.OrgId, s.ApiKey);
        }, ct);

    public Task ResetAsync(Actor actor, long expectedRevision, CancellationToken ct = default) =>
        MutateAsync(actor, expectedRevision, "ai_settings.reset", row =>
        {
            row.UsesEnvironment = true;
            row.BaseUrl = row.EmbeddingModel = row.QueryModel = row.VisionModel = row.ProtectedApiKey = null;
        }, ct);

    public Task ReindexAsync(Actor actor, long expectedRevision, CancellationToken ct = default) =>
        MutateAsync(actor, expectedRevision, "ai_settings.rebuild", row => row.EmbeddingGeneration = checked(row.EmbeddingGeneration + 1), ct);

    public async Task<IReadOnlyList<string>> ListModelsAsync(Actor actor, AiSettingsWrite draft, CancellationToken ct = default)
    {
        var snapshot = await DraftAsync(actor, draft, ct, requireModels: false);
        try { return await new AiGatewayClient(snapshot).ModelsAsync(ct); }
        catch (AiProviderException) { throw new AtharError("provider_unavailable", "Model discovery is unavailable."); }
    }

    public async Task<AiTestResult> TestAsync(Actor actor, AiSettingsWrite draft, CancellationToken ct = default)
    {
        var s = await DraftAsync(actor, draft, ct);
        var client = new AiGatewayClient(s);
        var roles = new List<AiRoleTest>();
        async Task Probe(string role, Func<Task> run)
        {
            try { await run(); roles.Add(new(role, true, "ok")); }
            catch (AiProviderException e) { roles.Add(new(role, false, e.Code)); }
            catch (JsonException) { roles.Add(new(role, false, "invalid_response")); }
        }
        await Probe("embedding", async () => await client.EmbedAsync(["Athar synthetic embedding probe"], true, ct));
        await Probe("query", async () =>
        {
            var schema = new { type = "object", additionalProperties = false, required = new[] { "ok" }, properties = new { ok = new { type = "boolean" } } };
            var text = await client.ChatAsync(s.QueryModel, [new { role = "user", content = "Return the JSON object with ok set to true." }], schema, 128, ct);
            using var result = JsonDocument.Parse(text);
            if (result.RootElement.ValueKind != JsonValueKind.Object || result.RootElement.EnumerateObject().Count() != 1
                || !result.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new AiProviderException("invalid_response");
        });
        if (s.VisionModel is not null)
            await Probe("vision", async () => await client.TranscribeAsync(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAHAAAAAsCAIAAAAyzquQAAAAtklEQVR4nO3QMRaEMAzEUN//0lDgkiLBMoE8qWad2R+HocXqAbslKJygcILCCQonKJygcEOgMVDlt7M3K3XvERTeIyi8R1B4zxDE7NCO71clKJygcILCCQpU2SboTYLCCQrXDjpb9+iOqD2CZoLCCQr3KmjHoC+AdmwQVFAuQeEEhRMUTlA4QeF+CRqFqn/u6ebSTeoxQfMm9ZigeZN6TNC8SR2yK0HhBIUTFE5QOEHhBIUTFO4EnqZRYD1jbn8AAAAASUVORK5CYII="), "image/png", ct));
        return new(roles);
    }

    public async Task<AiIndexStatus> IndexStatusAsync(Actor actor, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(actor, ct);
        var s = resolver.Resolve(actor.OrgId, await db.AiSettings.AsNoTracking().SingleOrDefaultAsync(ct));
        var total = await db.Chunks.LongCountAsync(ct);
        var embedded = await db.Chunks.LongCountAsync(c => db.ChunkEmbeddings.Any(e => e.ChunkId == c.Id && e.ModelId == s.EmbeddingIdentity), ct);
        // Job payloads are text, so inspect the bounded projection rather than invent a second status store.
        var jobs = await db.Jobs.Where(j => j.Kind == IndexJobs.EmbedVectors && j.Status != JobStatus.Done).Select(j => new { j.Payload, j.Status }).ToListAsync(ct);
        // Untargeted jobs predate managed rebuilds; their historical failures do not belong to a new generation.
        var current = jobs.Where(j => JsonSerializer.Deserialize<IndexJobs.VectorsPayload>(j.Payload, IndexJobs.Json)?.EmbeddingIdentity is { } id
            ? id == s.EmbeddingIdentity : !s.EmbeddingIdentity.StartsWith("ai:", StringComparison.Ordinal)).ToArray();
        return new(s.EmbeddingIdentity, total, embedded, current.LongCount(j => j.Status == JobStatus.Ready),
            current.LongCount(j => j.Status == JobStatus.Running), current.LongCount(j => j.Status == JobStatus.Dead));
    }

    private async Task<AiSnapshot> DraftAsync(Actor actor, AiSettingsWrite input, CancellationToken ct, bool requireModels = true)
    {
        await using var db = await OpenAsync(actor, ct);
        var row = await db.AiSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        CheckRevision(row, input.ExpectedRevision);
        return Draft(actor.OrgId, row, input, requireModels);
    }

    private AiSnapshot Draft(Guid org, AiSettings? row, AiSettingsWrite input, bool requireModels = true)
    {
        try
        {
            var url = AiGatewayClient.NormalizeEndpoint(input.BaseUrl);
            static string Model(string? value) => value?.Trim() is { Length: > 0 and <= 200 } name && !name.Any(char.IsControl)
                ? name : throw new AtharError("invalid_input", "A model identifier is required and must be at most 200 characters.");
            var embedding = !requireModels && string.IsNullOrWhiteSpace(input.EmbeddingModel) ? "" : Model(input.EmbeddingModel);
            var query = !requireModels && string.IsNullOrWhiteSpace(input.QueryModel) ? "" : Model(input.QueryModel);
            var vision = string.IsNullOrWhiteSpace(input.VisionModel) ? null : Model(input.VisionModel);
            var previous = resolver.Resolve(org, row);
            string? key;
            switch (input.KeyChange)
            {
                case ApiKeyChange.Keep:
                    if (previous.KeyUnreadable) throw new AtharError("invalid_input", "Replace or clear the unreadable key.");
                    if (string.IsNullOrWhiteSpace(previous.BaseUrl) || AiGatewayClient.NormalizeEndpoint(previous.BaseUrl) != url)
                        throw new AtharError("invalid_input", "Changing the endpoint requires replacing or clearing the key.");
                    key = previous.ApiKey;
                    break;
                case ApiKeyChange.Clear: key = null; break;
                case ApiKeyChange.Replace:
                    key = input.NewApiKey?.Trim();
                    if (string.IsNullOrEmpty(key) || key.Length > 4096 || key.Any(char.IsControl)) throw new AtharError("invalid_input", "A valid replacement key is required.");
                    break;
                default: throw new AtharError("invalid_input", "Invalid key action.");
            }
            return new(url, embedding, query, vision, key, false, false,
                AiSettingsResolver.Identity(url, embedding, previous.QueryInstruction, row?.EmbeddingGeneration ?? 0), previous.QueryInstruction);
        }
        catch (AiProviderException) { throw new AtharError("invalid_input", "A private HTTP or HTTPS inference endpoint is required."); }
    }

    private async Task MutateAsync(Actor actor, long revision, string action, Action<AiSettings> change, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = actor.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Organizations.FromSql($"SELECT * FROM organizations WHERE id = {actor.OrgId} FOR UPDATE").ToListAsync(ct);
        await RequireAsync(db, actor, ct);
        var row = await db.AiSettings.SingleOrDefaultAsync(ct);
        CheckRevision(row, revision);
        if (row is null) { row = new AiSettings { OrgId = actor.OrgId }; db.AiSettings.Add(row); }
        var before = AuditView(row);
        change(row);
        row.Revision = checked(row.Revision + 1);
        db.Audit(actor.OrgId, actor.UserId, action, "ai_settings", row.Id, before: before, after: AuditView(row));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // Explicitly exclude both plaintext and protected keys from the audit payload.
    private static object AuditView(AiSettings s) => new { s.Revision, s.UsesEnvironment, s.BaseUrl, s.EmbeddingModel, s.QueryModel, s.VisionModel,
        s.EmbeddingGeneration, HasApiKey = s.ProtectedApiKey != null };
    private static void CheckRevision(AiSettings? row, long revision)
    {
        if ((row?.Revision ?? 0) != revision) throw new AtharError("conflict", "AI settings changed. Reload before trying again.");
    }
    private async Task<AtharDbContext> OpenAsync(Actor actor, CancellationToken ct)
    {
        var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = actor.OrgId;
        try { await RequireAsync(db, actor, ct); return db; }
        catch { await db.DisposeAsync(); throw; }
    }
    private static async Task RequireAsync(AtharDbContext db, Actor actor, CancellationToken ct)
    {
        if (!OrgRules.ManageAi(await OrgAdminService.ActorRoleAsync(db, actor.UserId, ct), actor.CredentialId))
            throw new AtharError("forbidden", "Only active organization administrators can manage AI settings.");
    }
}
