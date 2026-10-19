using Athar.Data.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Athar.IntegrationTests;

[Collection(AtharCollection.Name)]
public sealed class AiSchemaTests(AtharApp app)
{
    [Fact]
    public async Task Additive_migration_preserves_existing_rows_through_upgrade_down_upgrade()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct); // seed with the current schema before testing older migration targets
        await using var db = data.CreateDbContext();
        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, m => m.EndsWith("_AdminAiSettings"));
        Assert.True(index > 0);
        await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);
        var pages = await db.Pages.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Title, p.ContentMd, p.Revision }).ToArrayAsync(ct);
        var users = await db.Users.AsNoTracking().Select(u => u.Id).ToArrayAsync(ct);
        Assert.Contains(QaSeed.Admin, users);
        Assert.NotEmpty(pages);
        await db.Database.MigrateAsync(ct);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.AiSettings.ToListAsync(ct));
        await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);
        Assert.Equal(pages, await db.Pages.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Title, p.ContentMd, p.Revision }).ToArrayAsync(ct));
        await db.Database.MigrateAsync(ct);
        Assert.Equal(pages, await db.Pages.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Title, p.ContentMd, p.Revision }).ToArrayAsync(ct));
        Assert.Equal(users.Order(), (await db.Users.AsNoTracking().Select(u => u.Id).ToArrayAsync(ct)).Order());
    }
}
