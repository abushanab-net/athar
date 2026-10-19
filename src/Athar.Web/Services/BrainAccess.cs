using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

/// <summary>Memory-side facts about an actor, loaded fresh per request (FR-AC-02).</summary>
public static class BrainAccess
{
    public static async Task<MemoryActor> MemoryActorAsync(this AtharDbContext db, Actor a, CancellationToken ct) => new(
        a.UserId,
        await db.OrgMembers.AnyAsync(m => m.UserId == a.UserId && m.IsKnowledgeOwner, ct),
        a.Disclosure == DisclosureClass.ExternalAgent,
        await db.TeamMembers.Where(m => m.UserId == a.UserId).Select(m => m.TeamId).ToListAsync(ct),
        await db.ProjectMembers.Where(m => m.UserId == a.UserId).Select(m => m.ProjectId).ToListAsync(ct),
        await db.ProjectMembers.Where(m => m.UserId == a.UserId && m.Role == ProjectRole.Admin).Select(m => m.ProjectId).ToListAsync(ct));
}
