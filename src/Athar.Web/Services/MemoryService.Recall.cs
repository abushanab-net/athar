using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

// Recall (FR-SR-03..04, docs/03 §4.3).
public sealed partial class MemoryService
{
    /// <summary>
    /// Recall, computed separately for each project group (one per requested member project, or one group):
    /// <list type="number">
    /// <item>Hybrid ranking over the group's visible memories (access and disclosure inside the query, T052), collapsed to
    /// memory level, best chunk wins.</item>
    /// <item>Replace in place: a company memory this group's project shadows is replaced at its rank by the shadow, or by a
    /// <c>withheld_override</c> marker when the caller can't see the shadow. The company version is never shown.</item>
    /// <item>A shadow found both by replacing and on its own rank appears once, at the higher rank; then top up to the limit.</item>
    /// <item>Annotations (FR-SR-04): override and stale, conflict (the other memory only if visible), pending (the caller's own
    /// proposals), binding (policies and conventions). The query and the result IDs are audited.</item>
    /// </list>
    /// </summary>
    public async Task<RecallResult> RecallAsync(Actor actor, RecallInput input, CancellationToken ct)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var who = await LoadActor(actor, ct);
        var limit = Math.Clamp(input.Limit, 1, 50);
        var scopes = input.Scopes?.Select(s => ParseEnum<Scope>(s, "scopes")).ToArray();
        var types = input.Types?.Select(t => ParseEnum<MemoryType>(t, "types")).ToArray();

        // The live memory rows decide visibility (a classification change applies before any reindex).
        var readable = db.Memories.Where(MemoryRules.Visible(who)).Where(m => m.Status == MemoryStatus.Active);
        var candidates = await readable.Where(m => (scopes == null || scopes.Contains(m.Scope)) && (types == null || types.Contains(m.Type)))
            .Select(m => new { m.Id, m.Scope, m.OwnerProjectId }).ToListAsync(ct);

        // One group per requested project the caller belongs to (others stay hidden), or one group.
        // Without project_ids, repo_remote picks the member projects linked to it (architecture §4.3 step 1).
        var requested = input.ProjectIds is null && input.RepoRemote is { } remote ? await projects.ResolveIdsAsync(actor.UserId, remote, ct) : input.ProjectIds;
        Guid?[] groupIds = requested is { Length: > 0 } ids && ids.Where(who.ProjectIds.Contains).Distinct().ToArray() is { Length: > 0 } member
            ? [.. member.Select(p => (Guid?)p)]
            : [null];

        // T092: one ranking over every group's memories (each group then keeps its own), not both queries per group.
        bool InGroup(Guid? project, Scope scope, Guid? owner) => scope != Scope.Project || project == null || owner == project;
        Guid[] GroupScope(Guid? project) => [.. candidates.Where(m => InGroup(project, m.Scope, m.OwnerProjectId)).Select(m => m.Id)];
        var snapshot = await search.SnapshotAsync(actor.OrgId, ct);
        var k = limit * 4 * groupIds.Length;
        var (shared, degraded) = await search.RankAsync(actor.OrgId, input.Query, [], [.. groupIds.SelectMany(GroupScope).Distinct()], k, ct, snapshot: snapshot);
        var byId = candidates.ToDictionary(m => m.Id);

        var slotsByGroup = new List<(Guid? Project, List<Slot> Slots)>();
        foreach (var project in groupIds)
        {
            var ranked = shared.Where(r => byId[r.OwnerId] is var m && InGroup(project, m.Scope, m.OwnerProjectId)).ToList();
            // The shared list was cut off before this group filled up (other groups' memories took the places): rank it alone.
            if (groupIds.Length > 1 && shared.Count >= k && ranked.Select(r => r.OwnerId).Distinct().Count() < limit)
                (ranked, _) = await search.RankAsync(actor.OrgId, input.Query, [], GroupScope(project), limit * 4, ct, snapshot: snapshot);
            var owners = ranked.GroupBy(r => r.OwnerId).Select(g => g.MaxBy(r => r.Score)!).OrderByDescending(r => r.Score).ThenBy(r => r.OwnerId);

            // This project's active shadows, and whether the caller may see each (unfiltered by scopes/types: it's the
            // project's override either way).
            var shadows = project is { } p
                ? await db.MemoryShadows.Where(s => s.ProjectId == p && s.DeletedAt == null)
                    .Select(s => new Shadow(s.CompanyMemoryId, s.ShadowMemoryId, s.Stale, readable.Any(m => m.Id == s.ShadowMemoryId)))
                    .ToListAsync(ct)
                : [];
            var byBase = shadows.ToDictionary(s => s.BaseId);

            var slots = new List<Slot>();
            var placed = new HashSet<Guid>();
            foreach (var r in owners)
            {
                if (slots.Count == limit) break;
                Slot slot = byBase.TryGetValue(r.OwnerId, out var over)
                    ? over.Visible ? new(over.ShadowId, null) : new(r.OwnerId, null, Withheld: true)
                    : new(r.OwnerId, r.ChunkId);
                if (placed.Add(slot.Id)) slots.Add(slot); // dedupe: the higher rank wins
            }
            slotsByGroup.Add((project, slots));
        }

        // Hydrate every item at once: memories, best-chunk snippets, links, conflicts, the caller's pending proposals.
        Guid[] itemIds = [.. slotsByGroup.SelectMany(g => g.Slots).Where(s => !s.Withheld).Select(s => s.Id).Distinct()];
        Guid[] chunkIds = [.. slotsByGroup.SelectMany(g => g.Slots).Select(s => s.ChunkId).OfType<Guid>()];
        var memories = await db.Memories.AsNoTracking().Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var snippets = await db.Chunks.Where(c => chunkIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Body, ct);
        var links = await LinksFor(actor.Caller, itemIds, ct);
        var conflicts = await db.MemoryConflicts.Where(c => c.Status == ConflictStatus.Open && (itemIds.Contains(c.MemoryA) || itemIds.Contains(c.MemoryB)))
            .Select(c => new { c.MemoryA, c.MemoryB }).ToListAsync(ct);
        Guid[] others = [.. conflicts.SelectMany(c => new[] { c.MemoryA, c.MemoryB }).Distinct()];
        var visibleOthers = (await readable.Where(m => others.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct)).ToHashSet();
        // Every shown shadow is annotated with its base (FR-SR-04), whichever group it appears in.
        var overrides = await db.MemoryShadows.Where(s => s.DeletedAt == null && itemIds.Contains(s.ShadowMemoryId))
            .ToDictionaryAsync(s => s.ShadowMemoryId, s => new OverrideAnnotation(s.CompanyMemoryId, s.Stale), ct);
        Guid[] credentials = [.. memories.Values.Select(m => m.CreatedByCredential).OfType<Guid>().Distinct()];
        var pats = (await db.AccessTokens.Where(t => credentials.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        var pending = (await db.MemoryProposals
            .Where(p => p.ProposedBy == actor.UserId && p.Status == ProposalStatus.Pending && p.TargetMemoryId != null && itemIds.Contains(p.TargetMemoryId.Value))
            .Select(p => p.TargetMemoryId!.Value).ToListAsync(ct)).ToHashSet();

        RecallItem Item(Slot s)
        {
            if (s.Withheld)
                return new("withheld_override", s.Id, "", "", "company", "active", new(WithheldOverride: true), new("withheld", []), "");
            var m = memories[s.Id];
            ConflictAnnotation? conflict = null;
            if (conflicts.FirstOrDefault(c => c.MemoryA == m.Id || c.MemoryB == m.Id) is { } c)
            {
                var other = c.MemoryA == m.Id ? c.MemoryB : c.MemoryA;
                conflict = new(visibleOthers.Contains(other) ? other : null); // the other side only if the caller may see it
            }
            var body = s.ChunkId is { } chunk && snippets.TryGetValue(chunk, out var b) ? b : m.BodyMd;
            return new("memory", m.Id, m.Title, body.Length <= 200 ? body : body[..200], Snake(m.Scope), Snake(m.Status),
                new RecallAnnotations(
                    Override: overrides.GetValueOrDefault(m.Id),
                    Conflict: conflict,
                    Pending: pending.Contains(m.Id) ? true : null,
                    Binding: m.Type is MemoryType.Policy or MemoryType.Convention ? true : null),
                new Provenance(m.Origin, [.. links[m.Id]], m.CreatedByUser,
                    m.CreatedByCredential is not { } cred ? null : pats.Contains(cred) ? "pat" : "oauth"),
                $"/memories/{m.Id}");
        }

        var groups = slotsByGroup.Select(g => new RecallGroup([.. g.Slots.Select(Item)], g.Project)).ToList();
        Audit(actor, "memory.recall", "memory", "*", null, new
        {
            query = input.Query.Length <= 500 ? input.Query : input.Query[..500],
            degraded,
            groups = groups.Select(g => new { project_id = g.ProjectId, ids = g.Items.Select(i => i.Id) }),
        });
        await db.SaveChangesAsync(ct);
        return new(degraded, groups);
    }

    private sealed record Shadow(Guid BaseId, Guid ShadowId, bool Stale, bool Visible);

    /// <summary>One recall position: the memory shown (or the withheld base), and its best chunk if it ranked itself.</summary>
    private sealed record Slot(Guid Id, Guid? ChunkId, bool Withheld = false);
}
