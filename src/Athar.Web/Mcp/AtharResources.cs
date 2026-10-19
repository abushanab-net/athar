using System.ComponentModel;
using System.Text;
using Athar.Web.CollabBridge;
using Athar.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Athar.Web.Mcp;

/// <summary>
/// Read-only MCP resources (docs/06 §6.2), authorized exactly like the tools: the same services and access checks, so a
/// hidden or confidential item reads as <c>not_found</c>. <c>athar://skill/{id}</c> is the skills library's (T063).
/// </summary>
[McpServerResourceType]
public sealed class AtharResources(MemoryService memories, PageOpsService pages, SkillService skills, IHttpContextAccessor http)
{
    [McpServerResource(UriTemplate = "athar://memory/{id}", Name = "memory", MimeType = "text/markdown")]
    [Description("A memory you can read, as Markdown with its scope, type and revision.")]
    public Task<string> Memory(Guid id, CancellationToken ct = default) => Run(async a =>
    {
        var d = await memories.GetAsync(a, id, ct);
        var m = d.Memory;
        return new StringBuilder($"# {m.Title}\n\n_{m.Scope} · {m.Type} · {m.Status} · revision {m.Revision}_\n\n{d.BodyMd}\n").ToString();
    });

    [McpServerResource(UriTemplate = "athar://page/{id}", Name = "page", MimeType = "text/markdown")]
    [Description("A wiki page you can read, as Markdown (the live document when it's open).")]
    public Task<string> Page(Guid id, CancellationToken ct = default) => Run(async a =>
    {
        var p = await pages.GetPageAsync(a, id, "markdown", ct);
        return $"# {p.Title}\n\n{p.Content}\n";
    });

    [McpServerResource(UriTemplate = "athar://skill/{id}", Name = "skill", MimeType = "text/markdown")]
    [Description("A skill you can use: its SKILL.md (with front matter) followed by its other files.")]
    public Task<string> Skill(Guid id, CancellationToken ct = default) => Run(async a => SkillService.AsMarkdown(await skills.GetAsync(a, id, ct)));

    private async Task<string> Run(Func<Actor, Task<string>> read)
    {
        try { return await read(Actor.From(http.HttpContext!)); }
        catch (AtharError e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }
}

/// <summary>MCP prompts (docs/06 §6.2), authorized like the tools.</summary>
[McpServerPromptType]
public sealed class AtharPrompts(MemoryService memories, ProjectService projects, IHttpContextAccessor http)
{
    [McpServerPrompt(Name = "save-chat")]
    [Description("Save this chat to Athar: summarize it, pick its project, and store it with save_chat (FR-AG-09).")]
    public async Task<string> SaveChat(
        [Description("The git remote of the repository the chat is about, if any")] string? repo_remote = null,
        CancellationToken ct = default)
    {
        try
        {
            var actor = Actor.From(http.HttpContext!);
            var mine = (await projects.ListAsync(actor, ct)).Where(p => p.Role is not null).ToList();
            var resolved = Athar.Domain.Memory.ProjectRules.NormalizeRemote(repo_remote) is null ? [] : await projects.ResolveAsync(actor, repo_remote!, ct);
            var sb = new StringBuilder("""
                Save this conversation to Athar.
                1. Write a short title, a Markdown summary of what was discussed and decided, and the key points worth remembering
                   (decisions, facts, conventions, preferences), one per item. Leave out secrets, tokens and personal data.
                2. Pick the project the chat is about:
                """);
            sb.Append('\n');
            if (resolved.Count == 1) sb.Append($"   - It's **{resolved[0].Name}** (project_id `{resolved[0].ProjectId}`), from the repository.\n");
            else
            {
                if (mine.Count == 0) sb.Append("   - The user isn't in any project: leave project_id out.\n");
                else
                {
                    sb.Append("   - Ask the user which one, or none:\n");
                    foreach (var p in mine) sb.Append($"     - {p.Name}: `{p.Id}`\n");
                }
            }
            sb.Append("""
                3. Show the user the title, summary and key points, and the project. Save only once they agree.
                4. Call `save_chat` with title, summary_md, key_points, project_id (if any) and a new op_id (a UUID). On a network
                   error, retry with the same op_id.
                """);
            return sb.ToString();
        }
        catch (AtharError e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }

    [McpServerPrompt(Name = "athar-context")]
    [Description("A short brief for the repository you're in: the conventions, decisions and policies recall finds for its projects.")]
    public async Task<string> Context(
        [Description("The git remote of the current repository")] string repo_remote,
        CancellationToken ct = default)
    {
        try
        {
            // ponytail: the brief is recall with a broad query over the repo's projects; a curated per-project brief can replace it.
            var repo = repo_remote.TrimEnd('/').Split('/', ':').Last().Replace(".git", "");
            var recall = await memories.RecallAsync(Actor.From(http.HttpContext!), new($"{repo} convention decision policy procedure", null, null, null, 10, repo_remote), ct);
            var sb = new StringBuilder($"# Athar context for {repo}\n");
            foreach (var group in recall.Groups)
            {
                sb.Append(group.ProjectId is { } p ? $"\n## Project {p}\n" : "\n## Memories\n");
                foreach (var i in group.Items.Where(i => i.Kind == "memory"))
                    sb.Append($"- **{i.Title}** ({i.Scope}{(i.Annotations.Binding == true ? ", binding" : "")}): {i.Snippet}\n");
            }
            if (recall.Degraded) sb.Append("\n_Search is degraded (keyword only)._\n");
            return sb.ToString();
        }
        catch (AtharError e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }
}
