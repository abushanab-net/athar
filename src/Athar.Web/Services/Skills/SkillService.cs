using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

public sealed record SkillFile(string Path, string Content);
public sealed record SkillSummary(Guid SkillId, string Name, string Description, string Scope, DateTimeOffset UpdatedAt, Guid? OwnerId = null);
public sealed record SkillList(IReadOnlyList<SkillSummary> Skills);
public sealed record SkillDetail(Guid SkillId, string Name, string Description, string Scope, string BodyMd, IReadOnlyList<SkillFile> Files, Guid? OwnerId = null);
/// <summary>What <c>publish_skill</c> answers: <c>published</c> with the skill, or <c>pending</c> with the proposal for a knowledge owner.</summary>
public sealed record SkillPublishResult(string Status,
    // Always written (null while pending): the MCP output schema lists skill_id as required.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] Guid? SkillId, string Name, string Description, string Scope, string BodyMd,
    IReadOnlyList<SkillFile> Files, Guid? OwnerId = null, Guid? ProposalId = null);
public sealed record PublishSkillInput(string Name, string Description, string BodyMd, string Scope, Guid? OwnerId, IReadOnlyList<SkillFile>? Files);

/// <summary>
/// FR-SK-01 skills library (T063): SKILL.md packages scoped like memories. Visibility and write rights are the memory rules
/// (personal: the owner; team and project: members; company: everyone, written by knowledge owners only, since skills have
/// no proposals); confidential skills are hidden from external agents. Publishing a name again in the same scope and owner
/// replaces that skill.
/// </summary>
public sealed partial class SkillService(AtharDbContext db)
{
    public const int MaxFiles = 20;
    public const int MaxPackageChars = 256 * 1024;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex SkillName();

    public async Task<SkillList> ListAsync(Actor actor, string? query, string? scope, CancellationToken ct)
    {
        var who = await BeginAsync(actor, "read", ct);
        var rows = db.Skills.AsNoTracking().Where(Visible(who)).Where(s => s.Status == SkillStatus.Active);
        if (scope is not null) { var sc = ParseScope(scope); rows = rows.Where(s => s.Scope == sc); }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = $"%{query.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
            rows = rows.Where(s => EF.Functions.ILike(s.Name, like, @"\") || EF.Functions.ILike(s.Description, like, @"\"));
        }
        var list = await rows.OrderBy(s => s.Name).Take(100).ToListAsync(ct);
        return new([.. list.Select(s => new SkillSummary(s.Id, s.Name, s.Description, Snake(s.Scope), s.UpdatedAt, Owner(s)))]);
    }

    public async Task<SkillDetail> GetAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var who = await BeginAsync(actor, "read", ct);
        var s = await db.Skills.AsNoTracking().Where(Visible(who)).FirstOrDefaultAsync(x => x.Id == id && x.Status == SkillStatus.Active, ct)
            ?? throw new AtharError("not_found", "No such skill.");
        return new(s.Id, s.Name, s.Description, Snake(s.Scope), s.BodyMd, Files(s), Owner(s));
    }

    /// <summary>Publishes where the caller may publish; a company skill from anyone but a knowledge owner is refused.</summary>
    public async Task<SkillDetail> PublishAsync(Actor actor, PublishSkillInput input, CancellationToken ct)
    {
        var r = await PublishOrProposeAsync(actor, input, propose: false, ct);
        return new(r.SkillId!.Value, r.Name, r.Description, r.Scope, r.BodyMd, r.Files, r.OwnerId);
    }

    /// <summary>
    /// <c>publish_skill</c> (FR-SK-02): publishes where the caller may; with <paramref name="propose"/>, a company skill from anyone
    /// but a knowledge owner becomes a pending <see cref="ProposalKind.Skill"/> proposal that a knowledge owner approves.
    /// </summary>
    public async Task<SkillPublishResult> PublishOrProposeAsync(Actor actor, PublishSkillInput input, bool propose, CancellationToken ct)
    {
        var who = await BeginAsync(actor, "memory:write", ct);
        var scope = ParseScope(input.Scope);
        var files = Validate(input);
        var owner = scope == Scope.Personal ? actor.UserId : input.OwnerId;
        if (scope is Scope.Team or Scope.Project && owner is null) throw new AtharError("invalid_input", $"owner_id is required for {input.Scope} scope.");
        var decision = MemoryRules.CanWrite(who, scope, owner);
        if (decision == WriteDecision.Propose && propose)
        {
            var proposal = new MemoryProposal
            {
                OrgId = actor.OrgId, Kind = ProposalKind.Skill, ProposedBy = actor.UserId, Status = ProposalStatus.Pending,
                Payload = JsonSerializer.Serialize(new SkillProposal(input.Name, input.Description.Trim(), input.BodyMd, files), McpEndpoint.Json),
            };
            db.MemoryProposals.Add(proposal);
            db.Audit(actor.OrgId, actor.UserId, "proposal.create", "memory_proposal", proposal.Id,
                after: new { kind = "skill", input.Name, files = files.Count }, credentialId: actor.CredentialId);
            await db.SaveChangesAsync(ct);
            return new("pending", null, input.Name, input.Description.Trim(), "company", input.BodyMd, files, null, proposal.Id);
        }
        if (decision != WriteDecision.Publish)
            throw new AtharError("forbidden", scope == Scope.Company ? "Only knowledge owners publish company skills." : $"You can't publish {input.Scope} skills here.");

        var skill = await UpsertAsync(db, actor.OrgId, actor.UserId, actor.UserId, actor.CredentialId, scope, owner, input with { Files = files }, null, ct);
        await db.SaveChangesAsync(ct);
        return new("published", skill.Id, skill.Name, skill.Description, Snake(skill.Scope), skill.BodyMd, Files(skill), Owner(skill));
    }

    /// <summary>The package a skill proposal carries: the jsonb payload of a <see cref="ProposalKind.Skill"/> proposal.</summary>
    public sealed record SkillProposal(string Name, string Description, string BodyMd, IReadOnlyList<SkillFile> Files);

    /// <summary>
    /// Creates or revises the skill named in the package, with a version row and an audit entry, in <paramref name="db"/>'s unit
    /// of work (the caller saves). Approving a company skill proposal calls it inside the approval transaction.
    /// </summary>
    internal static async Task<Skill> UpsertAsync(AtharDbContext db, Guid orgId, Guid auditUser, Guid publishedBy, Guid? credentialId,
        Scope scope, Guid? owner, PublishSkillInput input, Guid? proposalId, CancellationToken ct)
    {
        var files = input.Files ?? [];
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Scope == scope && s.Name == input.Name && s.Status == SkillStatus.Active
            && s.OwnerUserId == (scope == Scope.Personal ? owner : null) && s.OwnerTeamId == (scope == Scope.Team ? owner : null)
            && s.OwnerProjectId == (scope == Scope.Project ? owner : null), ct);
        var created = skill is null;
        skill ??= db.Skills.Add(new Skill
        {
            OrgId = orgId, Scope = scope, Name = input.Name, Description = "", BodyMd = "",
            OwnerUserId = scope == Scope.Personal ? owner : null, OwnerTeamId = scope == Scope.Team ? owner : null, OwnerProjectId = scope == Scope.Project ? owner : null,
        }).Entity;
        (skill.Description, skill.BodyMd) = (input.Description.Trim(), input.BodyMd);
        skill.Files = JsonSerializer.Serialize(files.Select(f => new SkillFile(f.Path.Trim(), f.Content ?? "")), McpEndpoint.Json);
        // FR-SK-02: every publish is a revision, kept for history. Skills from before T130 start their history here.
        if (!created) skill.Revision++;
        db.SkillVersions.Add(new SkillVersion
        {
            OrgId = orgId, SkillId = skill.Id, Revision = skill.Revision, Description = skill.Description, BodyMd = skill.BodyMd,
            Files = skill.Files, PublishedBy = publishedBy,
        });
        db.Audit(orgId, auditUser, created ? "skill.publish" : "skill.update", "skill", skill.Id,
            after: new { skill.Name, scope = Snake(scope), files = files.Count, proposal_id = proposalId }, credentialId: credentialId);
        return skill;
    }

    private static IReadOnlyList<SkillFile> Validate(PublishSkillInput input)
    {
        if (!SkillName().IsMatch(input.Name ?? "")) throw new AtharError("invalid_input", "name: lowercase letters, digits and hyphens, at most 64 characters.");
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 1024) throw new AtharError("invalid_input", "description is required (at most 1,024 characters).");
        if (string.IsNullOrWhiteSpace(input.BodyMd)) throw new AtharError("invalid_input", "body_md is required.");
        var files = input.Files ?? [];
        if (files.Count > MaxFiles) throw new AtharError("invalid_input", $"At most {MaxFiles} files.");
        if (files.Any(f => string.IsNullOrWhiteSpace(f.Path) || f.Path.StartsWith('/') || f.Path.Contains('\\') || f.Path.Split('/').Contains("..")))
            throw new AtharError("invalid_input", "File paths must be relative, with no '..'.");
        if (input.BodyMd.Length + files.Sum(f => f.Path.Length + (f.Content?.Length ?? 0)) > MaxPackageChars)
            throw new AtharError("invalid_input", "A skill package is limited to 256 KB.");
        return files;
    }

    public sealed record Version(int Revision, string Description, string BodyMd, string PublishedBy, DateTimeOffset PublishedAt);

    /// <summary>The skill's published revisions, newest first, for anyone who can see the skill (FR-SK-02).</summary>
    public async Task<IReadOnlyList<Version>> HistoryAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var who = await BeginAsync(actor, "read", ct);
        if (!await db.Skills.AsNoTracking().Where(Visible(who)).AnyAsync(s => s.Id == id, ct)) throw new AtharError("not_found", "No such skill.");
        var rows = await db.SkillVersions.AsNoTracking().Where(v => v.SkillId == id).OrderByDescending(v => v.Revision)
            .Select(v => new { v.Revision, v.Description, v.BodyMd, By = db.Users.Where(u => u.Id == v.PublishedBy).Select(u => u.DisplayName).FirstOrDefault(), v.CreatedAt })
            .ToListAsync(ct);
        return [.. rows.Select(v => new Version(v.Revision, v.Description, v.BodyMd, v.By ?? "", v.CreatedAt))];
    }

    public sealed record Target(string Scope, Guid Id, string Name);

    /// <summary>The caller's teams and projects (where they publish team and project skills) for the Skills page.</summary>
    public async Task<IReadOnlyList<Target>> TargetsAsync(Actor actor, CancellationToken ct)
    {
        var who = await BeginAsync(actor, "read", ct);
        Guid[] teams = [.. who.TeamIds], projects = [.. who.ProjectIds];
        var t = await db.Teams.AsNoTracking().Where(x => teams.Contains(x.Id)).Select(x => new Target("team", x.Id, x.Name)).ToListAsync(ct);
        var p = await db.Projects.AsNoTracking().Where(x => projects.Contains(x.Id)).Select(x => new Target("project", x.Id, x.Name)).ToListAsync(ct);
        return [.. t.Concat(p).OrderBy(x => x.Name)];
    }

    /// <summary>The same rule as <see cref="MemoryRules.Visible"/>, over skills.</summary>
    internal static Expression<Func<Skill, bool>> Visible(MemoryActor a)
    {
        Guid[] teams = [.. a.TeamIds], projects = [.. a.ProjectIds];
        var (uid, external) = (a.UserId, a.ExternalAgent);
        return s => (s.Scope == Scope.Company
                || (s.Scope == Scope.Personal && s.OwnerUserId == uid)
                || (s.Scope == Scope.Team && teams.Contains(s.OwnerTeamId!.Value))
                || (s.Scope == Scope.Project && projects.Contains(s.OwnerProjectId!.Value)))
            && (!external || !s.IsConfidential);
    }

    /// <summary>The skill as one Markdown document (the <c>athar://skill/{id}</c> resource).</summary>
    public static string AsMarkdown(SkillDetail s)
    {
        var md = new StringBuilder($"---\nname: {s.Name}\ndescription: {s.Description}\n---\n\n{s.BodyMd}\n");
        foreach (var f in s.Files) md.Append($"\n## {f.Path}\n\n```\n{f.Content}\n```\n");
        return md.ToString();
    }

    private async Task<MemoryActor> BeginAsync(Actor actor, string scope, CancellationToken ct)
    {
        if (!actor.Scopes.Contains(scope)) throw new AtharError("forbidden", $"The token lacks the '{scope}' scope.");
        db.CurrentOrgId = actor.OrgId;
        return await db.MemoryActorAsync(actor, ct);
    }

    private static Scope ParseScope(string scope) =>
        Enum.TryParse<Scope>(scope, ignoreCase: true, out var s) ? s : throw new AtharError("invalid_input", "scope must be personal, team, project or company.");

    private static IReadOnlyList<SkillFile> Files(Skill s) => JsonSerializer.Deserialize<List<SkillFile>>(s.Files, McpEndpoint.Json) ?? [];
    private static Guid? Owner(Skill s) => s.OwnerUserId ?? s.OwnerTeamId ?? s.OwnerProjectId;
    private static string Snake(Scope s) => s.ToString().ToLowerInvariant();
}
