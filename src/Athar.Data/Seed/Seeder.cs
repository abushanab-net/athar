using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athar.Data.Seed;

/// <summary>
/// Seed profiles (docs/04 §4.3). Fixed IDs and a fixed timestamp, so a reset reproduces the dataset exactly,
/// and a profile that's already there is skipped (AT-55). Test data goes in <see cref="QaSeed"/>, nowhere else.
/// </summary>
public static class Seeder
{
    public static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Today 00:00 UTC, only for timestamps a retention job compares with the real clock (trash, transcripts), so seeded
    /// rows never age out. Resets on the same day stay identical; everything else uses the fixed <see cref="At"/>.
    /// </summary>
    public static DateTimeOffset Today => new(DateTime.UtcNow.Date, TimeSpan.Zero);

    public static async Task SeedAsync(AtharDbContext db, SeedOptions options, CancellationToken ct)
    {
        switch (options.Profile)
        {
            case "minimal": Minimal(db); break;
            case "qa" or "perf": QaSeed.Add(db, options.EmbeddingModelId); break;
            default: throw new InvalidOperationException($"Unknown seed profile '{options.Profile}' (expected minimal, qa or perf).");
        }

        foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added && e.Metadata.FindProperty(nameof(OrgEntity.CreatedAt)) is not null))
        {
            e.Property(nameof(OrgEntity.CreatedAt)).CurrentValue = At;
            e.Property(nameof(OrgEntity.UpdatedAt)).CurrentValue = At;
        }

        // The migration just created the `vector` extension (and a reset gives it a new OID), so Npgsql's
        // cached type list predates it; without a reload, writing a Vector fails.
        await db.Database.OpenConnectionAsync(ct);
        try { await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync(ct); }
        finally { await db.Database.CloseConnectionAsync(); }
        await db.SaveChangesAsync(ct);
        if (options.Profile == "perf") await PerfSeed.AddAsync(db, options.EmbeddingModelId, ct); // NFR-01: qa plus the bulk, in batches
    }

    /// <summary>
    /// The organization, the OAuth clients and, for a new organization only, a Help workspace with the "Getting started with
    /// agents" page (T144). The first owner comes from the bootstrap CLI (FR-ID-01). An existing organization (the production
    /// server runs this at every start) gets nothing new but OAuth clients: OD-5, no seeding there; an admin adds the page instead.
    /// </summary>
    private static void Minimal(AtharDbContext db)
    {
        if (!db.Organizations.Any(o => o.Id == QaSeed.Org))
        {
            db.Organizations.Add(new Organization { Id = QaSeed.Org, Name = "Athar", Slug = "athar" });
            db.Workspaces.Add(new Workspace { Id = GettingStarted.HelpWorkspace, OrgId = QaSeed.Org, Name = "Help", Slug = "help" });
            db.Spaces.Add(new Space { Id = GettingStarted.HelpSpace, OrgId = QaSeed.Org, WorkspaceId = GettingStarted.HelpWorkspace, Name = "Help", Slug = "help" });
            var n = 0xc60;
            GettingStarted.AddPages(db, QaSeed.Org, GettingStarted.HelpSpace, GettingStarted.MinimalPages, () => GettingStarted.Id(n++));
        }
        OAuthClients.Add(db, QaSeed.Org, qa: false);
    }
}
