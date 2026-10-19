using Athar.Data.Seed;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T130: the Phase G schema (capture settings, session source, candidate results, skill versions, snapshot sources) on real Postgres.</summary>
[Collection(AtharCollection.Name)]
public class PhaseGSchemaTests(AtharApp athar)
{
    [Fact]
    public async Task Capture_settings_default_to_auto_accept_personal_and_an_explicit_off_stays_off()
    {
        var ct = AtharApp.Timeout();
        await using (var db = athar.Db(QaSeed.Org))
        {
            db.CaptureSettings.Add(new CaptureSettings { OrgId = QaSeed.Org, UserId = QaSeed.Member, Repos = ["github.com/org/repo"] });
            // false is the CLR default, so without the sentinel EF would skip it and the column default (true) would win.
            db.CaptureSettings.Add(new CaptureSettings { OrgId = QaSeed.Org, UserId = QaSeed.Admin, AutoAcceptPersonal = false, Paused = true });
            await db.SaveChangesAsync(ct);
        }
        try
        {
            await using var check = athar.Db(QaSeed.Org);
            var rows = await check.CaptureSettings.Where(s => s.UserId == QaSeed.Member || s.UserId == QaSeed.Admin).ToDictionaryAsync(s => s.UserId, ct);
            Assert.True(rows[QaSeed.Member].AutoAcceptPersonal);
            Assert.False(rows[QaSeed.Member].AutoAcceptShared);
            Assert.Equal(["github.com/org/repo"], rows[QaSeed.Member].Repos);
            Assert.False(rows[QaSeed.Admin].AutoAcceptPersonal);
            Assert.True(rows[QaSeed.Admin].Paused);

            // One row per person.
            check.CaptureSettings.Add(new CaptureSettings { OrgId = QaSeed.Org, UserId = QaSeed.Member });
            await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync(ct));
        }
        finally
        {
            await using var cleanup = athar.Db(QaSeed.Org);
            await cleanup.CaptureSettings.Where(s => s.UserId == QaSeed.Member || s.UserId == QaSeed.Admin).ExecuteDeleteAsync(ct);
        }
    }

    [Fact]
    public async Task Sessions_default_to_hook_and_a_chat_session_stays_a_chat()
    {
        var ct = AtharApp.Timeout();
        var chat = Guid.CreateVersion7();
        await using (var db = athar.Db(QaSeed.Org))
        {
            db.AgentSessions.Add(new AgentSession
            {
                Id = chat, OrgId = QaSeed.Org, UserId = QaSeed.Member, Client = "claude-ai", ExternalSessionId = $"t130-{chat}",
                ProjectId = QaSeed.PilotProject, Title = "Release checklist chat", Source = SessionSource.Chat,
            });
            await db.SaveChangesAsync(ct);
        }
        await using var check = athar.Db(QaSeed.Org);
        Assert.Equal(SessionSource.Hook, (await check.AgentSessions.SingleAsync(s => s.Id == QaSeed.Session, ct)).Source);
        var saved = await check.AgentSessions.SingleAsync(s => s.Id == chat, ct);
        Assert.Equal(SessionSource.Chat, saved.Source);
        Assert.Equal("Release checklist chat", saved.Title);
    }

    [Fact]
    public async Task Skill_versions_are_unique_per_revision_and_snapshot_sources_need_no_commit()
    {
        var ct = AtharApp.Timeout();
        Guid source = Guid.CreateVersion7();
        await using (var db = athar.Db(QaSeed.Org))
        {
            Assert.Equal(1, (await db.Skills.SingleAsync(s => s.Id == QaSeed.SkillProject, ct)).Revision);
            Assert.True(await db.SkillVersions.AnyAsync(v => v.SkillId == QaSeed.SkillProject && v.Revision == 1, ct)); // seeded
            db.KnowledgeSources.Add(new KnowledgeSource { Id = source, OrgId = QaSeed.Org, ProjectId = QaSeed.PilotProject, Kind = SourceKind.Url, Repo = "https://example.com/handbook", Title = "Handbook", CreatedBy = QaSeed.Member });
            db.SourceDocuments.Add(new SourceDocument
            {
                OrgId = QaSeed.Org, SourceId = source, Path = "https://example.com/handbook", ContentMd = "# Handbook", ContentHash = "h1",
                MediaType = "text/html", SizeBytes = 1234, FetchedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        await using var check = athar.Db(QaSeed.Org);
        var doc = await check.SourceDocuments.SingleAsync(d => d.SourceId == source, ct);
        Assert.Null(doc.CommitSha);
        Assert.Equal(SourceKind.Url, (await check.KnowledgeSources.SingleAsync(s => s.Id == source, ct)).Kind);

        check.SkillVersions.Add(new SkillVersion { OrgId = QaSeed.Org, SkillId = QaSeed.SkillProject, Revision = 1, Description = "dup", BodyMd = "# dup", PublishedBy = QaSeed.Member });
        await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync(ct));
    }
}
