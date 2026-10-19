using System.Security.Cryptography;
using System.Text;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T020: the qa seed holds every state docs/04 §4.3 promises, so later tests can rely on it.</summary>
[Collection(AtharCollection.Name)]
public class QaSeedTests(AtharApp athar)
{
    [Fact]
    public async Task T020_qa_seed_covers_every_state()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(QaSeed.Org);

        static void Every<T>(IEnumerable<T> seen) where T : struct, Enum => Assert.Equal(Enum.GetValues<T>().Order(), seen.Distinct().Order());

        // Roles, teams as principals
        Every(await db.OrgMembers.Select(m => m.Role).ToListAsync(ct));
        Assert.True(await db.Users.AnyAsync(u => u.IsGuest, ct) && await db.Users.AnyAsync(u => u.DeactivatedAt != null, ct));
        Assert.True(await db.SpaceMembers.AnyAsync(m => m.TeamId == QaSeed.PlatformTeam, ct));

        // Pages: Arabic, mixed, restricted subtree, trash, confidential; nothing from the other org
        var pages = await db.Pages.ToDictionaryAsync(p => p.Id, ct);
        Assert.Matches(@"\p{IsArabic}", pages[QaSeed.OnCallPage].Title);
        Assert.Matches(@"\p{IsArabic}.*[A-Za-z]|[A-Za-z].*\p{IsArabic}", pages[QaSeed.ApiReferencePage].ContentMd!);
        Assert.Equal(QaSeed.IncidentReviewsPage, pages[QaSeed.OutageReviewPage].ParentId);
        Assert.Equal(2, await db.PageRestrictions.CountAsync(r => r.PageId == QaSeed.IncidentReviewsPage, ct));
        Assert.NotNull(pages[QaSeed.TrashedPage].DeletedAt);
        Assert.True(await db.Spaces.AnyAsync(s => s.Id == pages[QaSeed.CompensationPage].SpaceId && s.IsConfidential, ct));
        Assert.DoesNotContain(QaSeed.OtherOrgPage, pages.Keys);

        // Comments and share links
        Every(await db.CommentThreads.Select(t => t.Status).ToListAsync(ct));
        Assert.True(await db.Mentions.AnyAsync(ct) && await db.Notifications.AnyAsync(ct));
        var links = await db.ShareLinks.ToDictionaryAsync(l => l.Id, ct);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(QaSeed.ShareActiveToken))), links[QaSeed.ShareActive].TokenHash);
        Assert.True(links[QaSeed.ShareSpace].IncludeDescendants);
        Assert.True(links[QaSeed.ShareExpired].ExpiresAt < DateTimeOffset.UtcNow);
        Assert.NotNull(links[QaSeed.ShareRevoked].RevokedAt);
        Assert.NotNull(links[QaSeed.SharePassword].PasswordHash);

        // Direct shares, through the real access rule: they lift access on that exact page only
        Assert.True(await db.CanAsync(new Caller(QaSeed.Admin), QaSeed.OnCallPage, PageAction.Comment, ct));
        Assert.False(await db.CanAsync(new Caller(QaSeed.Admin), QaSeed.OnCallPage, PageAction.Edit, ct));
        Assert.True(await db.CanAsync(new Caller(QaSeed.KnowledgeOwner), QaSeed.DeployRunbookPage, PageAction.Edit, ct));
        Assert.False(await db.CanAsync(new Caller(QaSeed.KnowledgeOwner), QaSeed.HomePage, PageAction.Edit, ct));

        // Memories: every scope and status; non-overridable policy; shadow of an overridable company memory; a conflict
        var memories = await db.Memories.ToDictionaryAsync(m => m.Id, ct);
        Every(memories.Values.Select(m => m.Scope));
        Every(memories.Values.Select(m => m.Status));
        Assert.False(memories[QaSeed.MemoryCompanyPolicy].Overridable);
        var shadow = await db.MemoryShadows.SingleAsync(s => s.Id == QaSeed.ShadowAthar, ct);
        Assert.True(memories[shadow.CompanyMemoryId] is { Scope: Scope.Company, Overridable: true });
        Assert.Equal(memories.Count, await db.Chunks.CountAsync(c => c.OwnerKind == ChunkOwner.Memory, ct));
        Assert.Equal(ConflictStatus.Open, (await db.MemoryConflicts.SingleAsync(c => c.Id == QaSeed.ConflictOpen, ct)).Status);
        Every(await db.MemoryProposals.Select(p => p.Status).ToListAsync(ct));
        Assert.Equal(Enum.GetValues<ProposalKind>().Order(),
            (await db.MemoryProposals.Select(p => p.Kind).ToListAsync(ct)).Distinct().Order());

        // Capture, source, skills
        Assert.Equal(3, await db.SessionRecords.CountAsync(r => r.SessionId == QaSeed.Session, ct));
        Every(await db.CaptureCandidates.Select(c => c.Status).ToListAsync(ct));
        Assert.True(await db.CaptureCandidates.AnyAsync(c => c.SuggestedScope == Scope.Company, ct));
        Assert.Equal(QaSeed.MemoryPersonal, (await db.CaptureCandidates.SingleAsync(c => c.Id == QaSeed.CandidateAutoAccepted, ct)).MemoryId);
        Every(await db.AgentSessions.Select(s => s.Source).ToListAsync(ct));
        Assert.True(await db.SkillVersions.AnyAsync(v => v.SkillId == QaSeed.SkillProject && v.Revision == 1, ct));
        Assert.Equal(1, await db.SourceDocuments.CountAsync(d => d.SourceId == QaSeed.Source && d.TombstonedAt != null, ct));
        Assert.Equal([Scope.Personal, Scope.Project, Scope.Company], (await db.Skills.Select(s => s.Scope).ToListAsync(ct)).Distinct().Order());
    }
}
