using System.ComponentModel;
using Athar.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Athar.Web.Mcp;

/// <summary>docs/06 §6.2 <c>remember</c> and <c>recall</c>. Thin: the actor comes from the PAT, the work from <see cref="MemoryService"/>.</summary>
[McpServerToolType]
public sealed class MemoryTools(MemoryService memories, ProjectService projects, SearchService search, IHttpContextAccessor http)
{
    [McpServerTool(Name = "remember", UseStructuredContent = true, Idempotent = true)]
    [Description("Save a memory. Company scope creates a proposal unless you're a knowledge owner.")]
    public Task<RememberResult> Remember(
        string title, string body_md,
        [Description("fact, decision, convention, procedure, policy, preference, incident or glossary")] string type,
        [Description("personal, team, project or company")] string scope,
        [Description("Idempotency key: repeating it with the same payload returns the stored result")] string op_id,
        [Description("Team or project id, for team and project scope")] Guid? owner_id = null,
        string[]? links = null,
        CancellationToken ct = default) =>
        Run(a => memories.RememberAsync(a, new(title, body_md, type, scope, op_id, owner_id, links), ct));

    [McpServerTool(Name = "search", UseStructuredContent = true, ReadOnly = true)]
    [Description("Search pages and memories you can read (keywords and meaning, Arabic and English). Ranked hits.")]
    public Task<SearchResult> Search(
        string query,
        [Description("page and/or memory; both when omitted")] string[]? kinds = null,
        Guid[]? space_ids = null,
        Guid[]? project_ids = null,
        int limit = 10,
        CancellationToken ct = default) =>
        Run(a => search.SearchAsync(a, new(query, kinds, space_ids, project_ids, limit), ct));

    [McpServerTool(Name = "recall", UseStructuredContent = true, ReadOnly = true)]
    [Description("Find memories relevant to a query, one group per project.")]
    public Task<RecallResult> Recall(
        string query,
        [Description("The repo you're in; recall then groups results per project linked to it")] string? repo_remote = null,
        Guid[]? project_ids = null,
        string[]? scopes = null,
        string[]? types = null,
        int limit = 10,
        CancellationToken ct = default) =>
        Run(a => memories.RecallAsync(a, new(query, project_ids, scopes, types, limit, repo_remote), ct));

    [McpServerTool(Name = "resolve_project", UseStructuredContent = true, ReadOnly = true)]
    [Description("The projects you're a member of that are linked to this git remote.")]
    public Task<ResolvedProjects> ResolveProject(
        [Description("A git remote URL, e.g. the output of `git remote get-url origin`")] string repo_remote,
        CancellationToken ct = default) =>
        Run(async a => new ResolvedProjects(await projects.ResolveAsync(a, repo_remote, ct)));

    [McpServerTool(Name = "update_memory", UseStructuredContent = true, Idempotent = true)]
    [Description("Edit a memory you can publish to (a new revision), or propose the edit for a company memory. Fails with conflict_revision if expected_revision is stale.")]
    public Task<UpdateResult> UpdateMemory(
        Guid memory_id,
        [Description("The revision you read; the edit applies only on top of it")] int expected_revision,
        [Description("Idempotency key")] string op_id,
        string? title = null,
        string? body_md = null,
        CancellationToken ct = default) =>
        Run(a => memories.UpdateAsync(a, new(memory_id, title, body_md, expected_revision, op_id), ct));

    [McpServerTool(Name = "shadow_memory", UseStructuredContent = true, Idempotent = true)]
    [Description("Project admins only: override an overridable company memory for one project.")]
    public Task<ShadowResult> ShadowMemory(
        Guid company_memory_id, Guid project_id, string body_md,
        [Description("Why this project differs from the company memory")] string reason,
        [Description("Idempotency key")] string op_id,
        CancellationToken ct = default) =>
        Run(a => memories.ShadowAsync(a, new(company_memory_id, project_id, body_md, reason, op_id), ct));

    [McpServerTool(Name = "propose_company_memory", UseStructuredContent = true, Idempotent = true)]
    [Description("Propose a new company memory, or a replacement of one (target_memory_id), for a knowledge owner to review.")]
    public Task<ProposeResult> ProposeCompanyMemory(
        string title, string body_md,
        [Description("fact, decision, convention, procedure, policy, preference, incident or glossary")] string type,
        [Description("Idempotency key")] string op_id,
        Guid? target_memory_id = null,
        string[]? evidence_links = null,
        CancellationToken ct = default) =>
        Run(a => memories.ProposeAsync(a, new(title, body_md, type, target_memory_id, evidence_links, op_id), ct));

    // Domain errors become MCP tool errors carrying the §6.6 code.
    private async Task<T> Run<T>(Func<Actor, Task<T>> call)
    {
        try { return await call(Actor.From(http.HttpContext!)); }
        catch (AtharError e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }
}
