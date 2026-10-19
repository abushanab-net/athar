using System.Text.Json.Serialization;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athar.Web.Services;

public sealed record AutoAcceptSettings([property: JsonPropertyName("personal")] bool Personal, [property: JsonPropertyName("shared")] bool Shared);

/// <summary>Interface I-1 (docs/06 §6.3): what <c>GET|PUT /api/v1/me/capture-settings</c> send and take.</summary>
public sealed record CaptureSettingsView(
    [property: JsonPropertyName("repos")] IReadOnlyList<string> Repos,
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("auto_accept")] AutoAcceptSettings AutoAccept);

/// <summary>
/// A person's capture settings (FR-AG-04 and FR-MM-06 as amended by Phase G): the repos opted in on the server, Pause, and
/// the auto-accept switches. Only the person reads or changes their own; no row means the defaults (personal auto-accept on).
/// </summary>
public sealed class CaptureSettingsService(AtharDbContext db)
{
    public const int MaxRepos = 200;
    public static readonly CaptureSettingsView Defaults = new([], false, new(true, false));

    public async Task<CaptureSettingsView> GetAsync(Actor actor, CancellationToken ct)
    {
        Begin(actor, "read");
        var row = await db.CaptureSettings.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == actor.UserId, ct);
        return row is null ? Defaults : View(row);
    }

    /// <summary>Replaces the settings. Repos are normalized like <c>start_session</c>'s <c>repo_remote</c> and deduplicated.</summary>
    public async Task<CaptureSettingsView> PutAsync(Actor actor, CaptureSettingsView input, CancellationToken ct)
    {
        Begin(actor, "memory:write");
        if (input.AutoAccept is null) throw new AtharError("invalid_input", "auto_accept is required.");
        var repos = new List<string>();
        foreach (var r in input.Repos ?? [])
        {
            var normal = ProjectRules.NormalizeRemote(r) ?? throw new AtharError("invalid_input", $"'{r}' isn't a repository remote.");
            if (!repos.Contains(normal)) repos.Add(normal);
        }
        if (repos.Count > MaxRepos) throw new AtharError("invalid_input", $"At most {MaxRepos} repos.");

        var row = await db.CaptureSettings.SingleOrDefaultAsync(c => c.UserId == actor.UserId, ct);
        var before = row is null ? Defaults : View(row);
        if (row is null) db.CaptureSettings.Add(row = new CaptureSettings { OrgId = actor.OrgId, UserId = actor.UserId });
        row.Repos = [.. repos];
        row.Paused = input.Paused;
        row.AutoAcceptPersonal = input.AutoAccept.Personal;
        row.AutoAcceptShared = input.AutoAccept.Shared;
        var after = View(row);
        db.Audit(actor.OrgId, actor.UserId, "capture_settings.update", "user", actor.UserId, before: before, after: after, credentialId: actor.CredentialId);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent first save created the row; apply this one over it.
            db.ChangeTracker.Clear();
            return await PutAsync(actor, input, ct);
        }
        return after;
    }

    private static CaptureSettingsView View(CaptureSettings c) => new([.. c.Repos], c.Paused, new(c.AutoAcceptPersonal, c.AutoAcceptShared));

    private void Begin(Actor a, string scope)
    {
        if (!a.Scopes.Contains(scope)) throw new AtharError("forbidden", $"The token lacks the '{scope}' scope.");
        db.CurrentOrgId = a.OrgId;
    }
}
