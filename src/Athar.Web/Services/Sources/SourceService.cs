using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Domain.Sources;
using Athar.Web.Api;
using Athar.Web.Mcp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

public sealed record SourceView(Guid SourceId, Guid ProjectId, string Repo, string Status, bool IsConfidential, int Documents,
    string Audience, string? LastCommitSha = null, string Kind = "github", string? Title = null, DateTimeOffset? FetchedAt = null);

/// <summary>
/// FR-KS-01/03 knowledge sources: project admins attach allow-listed GitHub repos with a read-only PAT, which is stored
/// encrypted (<see cref="AtharKeys"/>) and never returned or audited. Members list them. "Sync now", a new PAT (which
/// resumes a paused source) and purging the synced content are admin actions. The worker does the syncing.
/// </summary>
public sealed class SourceService(AtharDbContext db, IDataProtectionProvider keys, IConfiguration config, TimeProvider clock, AttachmentStore store)
{
    /// <summary>
    /// FR-KS-06 (T142): one web page as a project source. Project members add it; the worker snapshots it (SSRF-guarded) and
    /// indexes the text for the project's members. Adding the same URL again refreshes it instead of duplicating it.
    /// </summary>
    public async Task<SourceView> AddUrlAsync(Actor actor, Guid projectId, string url, bool confidential, CancellationToken ct)
    {
        await RequireContributorAsync(actor, projectId, ct);
        var uri = IngestRules.ParseUrl(url) ?? throw new AtharError("invalid_input", "url must be an absolute http or https address.");
        var repo = uri.ToString();
        if (await db.KnowledgeSources.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == SourceKind.Url && s.Repo == repo, ct) is { } existing)
            return await RefreshAsync(actor, existing.Id, ct);
        var source = new KnowledgeSource { ProjectId = projectId, Kind = SourceKind.Url, Repo = repo, Status = SourceStatus.Ok, IsConfidential = confidential, CreatedBy = actor.UserId };
        db.KnowledgeSources.Add(source);
        QueueIngest(source);
        db.Audit(actor.OrgId, actor.UserId, "source.add", "knowledge_source", source.Id, after: new { project_id = projectId, kind = "url", url = repo, confidential }, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return View(source, 0);
    }

    /// <summary>
    /// FR-KS-06 (T142): a document (MD, TXT, HTML, PDF, DOCX, PPTX or XLSX, up to 10 MB) as a project source. The file is kept
    /// in the attachment store (so the nightly backup covers it) and the worker extracts its text. The same file name in the
    /// same project replaces the earlier upload.
    /// </summary>
    public async Task<SourceView> AddDocumentAsync(Actor actor, Guid projectId, string fileName, byte[] bytes, bool confidential, CancellationToken ct)
    {
        await RequireContributorAsync(actor, projectId, ct);
        var name = Path.GetFileName((fileName ?? "").Trim());
        var media = IngestRules.MediaTypeOf(name) ?? throw new AtharError("invalid_input", "Documents must be MD, TXT, HTML, PDF, DOCX, PPTX or XLSX.");
        if (bytes.LongLength == 0) throw new AtharError("invalid_input", "The file is empty.");
        if (bytes.LongLength > IngestRules.MaxBytes) throw new AtharError("invalid_input", "Documents are limited to 10 MB.");

        var key = AttachmentStore.NewKey();
        await store.WriteAsync(key, bytes, ct);
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == SourceKind.Document && s.Repo == name, ct);
        var created = source is null;
        source ??= db.KnowledgeSources.Add(new KnowledgeSource
            { ProjectId = projectId, Kind = SourceKind.Document, Repo = name, Status = SourceStatus.Ok, IsConfidential = confidential, CreatedBy = actor.UserId }).Entity;
        var doc = created ? null : await db.SourceDocuments.FirstOrDefaultAsync(d => d.SourceId == source.Id, ct);
        doc ??= db.SourceDocuments.Add(new SourceDocument { SourceId = source.Id, Path = name, ContentMd = "", ContentHash = "" }).Entity;
        (doc.StoredFile, doc.MediaType, doc.SizeBytes) = (key, media, bytes.LongLength);
        source.Status = SourceStatus.Ok;
        QueueIngest(source);
        db.Audit(actor.OrgId, actor.UserId, created ? "source.add" : "source.replace", "knowledge_source", source.Id,
            after: new { project_id = projectId, kind = "document", file = name, bytes = bytes.LongLength, confidential = source.IsConfidential }, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return View(source, await DocsAsync(source.Id, ct));
    }

    public sealed record IngestFile(string Path, string Content);

    /// <summary>
    /// FR-KS-06 (T143): a laptop folder as one project source (<c>athar ingest</c>). Each request carries the changed text
    /// documents (indexed at once, project audience); the last one also lists every file still <paramref name="present"/>,
    /// and documents not in it are tombstoned and leave search. The same label in the same project is the same source.
    /// </summary>
    public async Task<SourceView> AddFilesAsync(Actor actor, Guid projectId, string label, bool confidential, IReadOnlyList<IngestFile> files,
        IReadOnlyCollection<string>? present, CancellationToken ct)
    {
        await RequireContributorAsync(actor, projectId, ct);
        label = (label ?? "").Trim();
        if (label.Length is 0 or > 200) throw new AtharError("invalid_input", "label is required (at most 200 characters).");
        if (files.Count > 500) throw new AtharError("invalid_input", "At most 500 files per request.");
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f.Path) || f.Path.Length > 1024 || f.Path.StartsWith('/') || f.Path.Contains('\\') || f.Path.Split('/').Contains(".."))
                throw new AtharError("invalid_input", "File paths must be relative, with '/' and no '..'.");
            if (!IngestRules.FolderTypes.Contains(Path.GetExtension(f.Path).ToLowerInvariant()))
                throw new AtharError("invalid_input", $"{f.Path}: folders send text documents only (Markdown, text, HTML, reStructuredText, AsciiDoc).");
            if (Encoding.UTF8.GetByteCount(f.Content ?? "") > IngestRules.MaxFolderFileBytes) throw new AtharError("invalid_input", $"{f.Path} is larger than 1 MB.");
        }

        var source = await db.KnowledgeSources.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == SourceKind.File && s.Repo == label, ct);
        var created = source is null;
        source ??= db.KnowledgeSources.Add(new KnowledgeSource
            { ProjectId = projectId, Kind = SourceKind.File, Repo = label, Title = label, Status = SourceStatus.Ok, IsConfidential = confidential, CreatedBy = actor.UserId }).Entity;
        var docs = created ? [] : await db.SourceDocuments.Where(d => d.SourceId == source.Id).ToDictionaryAsync(d => d.Path, ct);
        var now = clock.GetUtcNow();
        var revision = now.ToUnixTimeMilliseconds();
        foreach (var f in files)
        {
            var text = f.Content ?? "";
            var media = IngestRules.MediaTypeOf(f.Path) ?? "text/plain";
            var (title, body) = media == "text/html" ? IngestRules.HtmlToText(text) : (null, text.Trim());
            title ??= body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? Path.GetFileName(f.Path);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            if (!docs.TryGetValue(f.Path, out var doc))
                docs[f.Path] = doc = db.SourceDocuments.Add(new SourceDocument { SourceId = source.Id, Path = f.Path, ContentMd = "", ContentHash = "" }).Entity;
            (doc.MediaType, doc.SizeBytes, doc.FetchedAt, doc.TombstonedAt) = (media, Encoding.UTF8.GetByteCount(text), now, null);
            if (doc.ContentHash == hash) continue;
            (doc.Title, doc.ContentMd, doc.ContentHash) = (title, body, hash);
            if (!created) await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.SourceDocument && c.OwnerId == doc.Id).ExecuteDeleteAsync(ct);
            db.Chunks.AddRange(Chunker.Build(ChunkOwner.SourceDocument, doc.Id, revision, $"{label}: {title}", body, c =>
            {
                (c.Scope, c.OwnerProjectId, c.IsConfidential) = (Scope.Project, projectId, source.IsConfidential);
            }));
            db.Jobs.Add(IndexJobs.Vectors(ChunkOwner.SourceDocument, doc.Id, revision, now));
        }
        var removed = 0;
        if (present is not null)
        {
            var keep = present.ToHashSet(StringComparer.Ordinal);
            foreach (var gone in docs.Values.Where(d => d.TombstonedAt is null && !keep.Contains(d.Path)))
            {
                gone.TombstonedAt = now;
                await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.SourceDocument && c.OwnerId == gone.Id).ExecuteDeleteAsync(ct);
                removed++;
            }
        }
        source.Status = SourceStatus.Ok;
        db.Audit(actor.OrgId, actor.UserId, created ? "source.add" : "source.ingest", "knowledge_source", source.Id,
            after: new { project_id = projectId, kind = "file", label, files = files.Count, removed, confidential = source.IsConfidential }, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return View(source, docs.Values.Count(d => d.TombstonedAt is null && d.ContentHash != ""), now);
    }

    /// <summary>A new snapshot of a URL source (or a re-read of a document), by any project member.</summary>
    public async Task<SourceView> RefreshAsync(Actor actor, Guid sourceId, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct) ?? throw new AtharError("not_found", "No such source.");
        await RequireContributorAsync(actor, source.ProjectId, ct);
        if (source.Kind is not (SourceKind.Url or SourceKind.Document))
            throw new AtharError("invalid_input", source.Kind == SourceKind.File ? "Run athar ingest again to refresh a folder." : "Use sync for GitHub sources.");
        QueueIngest(source);
        db.Audit(actor.OrgId, actor.UserId, "source.refresh", "knowledge_source", source.Id, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return View(source, await DocsAsync(source.Id, ct));
    }

    public async Task<SourceView> AttachAsync(Actor actor, Guid projectId, string repoInput, string pat, bool confidential, CancellationToken ct)
    {
        await RequireManagerAsync(actor, projectId, ct);
        var repo = GitHubRules.NormalizeRepo(repoInput) ?? throw new AtharError("invalid_input", "repo must be owner/name or a GitHub URL.");
        if (!GitHubRules.IsAllowed(repo, config.GetSection("Athar:GitHub:AllowedRepos").Get<string[]>() ?? []))
            throw new AtharError("forbidden", $"{repo} isn't on the repository allow-list.");
        if (string.IsNullOrWhiteSpace(pat)) throw new AtharError("invalid_input", "pat is required (a fine-grained, read-only token).");
        if (await db.KnowledgeSources.AnyAsync(s => s.ProjectId == projectId && s.Repo == repo, ct))
            throw new AtharError("invalid_input", $"{repo} is already attached to this project.");

        var source = new KnowledgeSource { ProjectId = projectId, Kind = SourceKind.Github, Repo = repo, Status = SourceStatus.Ok, IsConfidential = confidential, EncryptedPat = Protect(pat) };
        db.KnowledgeSources.Add(source);
        QueueSync(source);
        db.Audit(actor.OrgId, actor.UserId, "source.attach", "knowledge_source", source.Id, after: new { project_id = projectId, repo, confidential }, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return View(source, 0);
    }

    public async Task<IReadOnlyList<SourceView>> ListAsync(Actor actor, Guid projectId, CancellationToken ct)
    {
        await RequireMemberAsync(actor, projectId, ct);
        var rows = await db.KnowledgeSources.AsNoTracking().Where(s => s.ProjectId == projectId).OrderBy(s => s.Kind).ThenBy(s => s.Repo)
            .Select(s => new
            {
                Source = s, Docs = db.SourceDocuments.Count(d => d.SourceId == s.Id && d.TombstonedAt == null && d.ContentHash != ""),
                Fetched = db.SourceDocuments.Where(d => d.SourceId == s.Id).Max(d => d.FetchedAt),
            }).ToListAsync(ct);
        return [.. rows.Select(r => View(r.Source, r.Docs, r.Fetched))];
    }

    public async Task<SourceView> SyncNowAsync(Actor actor, Guid sourceId, CancellationToken ct)
    {
        var source = await ManagedAsync(actor, sourceId, ct);
        if (source.Status == SourceStatus.PausedAuth) throw new AtharError("invalid_input", "The source is paused: set a new PAT first.");
        QueueSync(source);
        db.Audit(actor.OrgId, actor.UserId, "source.sync_now", "knowledge_source", source.Id, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return View(source, await DocsAsync(source.Id, ct));
    }

    /// <summary>A new PAT; a paused source resumes and syncs at once (FR-KS-03).</summary>
    public async Task<SourceView> SetPatAsync(Actor actor, Guid sourceId, string pat, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pat)) throw new AtharError("invalid_input", "pat is required.");
        var source = await ManagedAsync(actor, sourceId, ct);
        (source.EncryptedPat, source.Status) = (Protect(pat), SourceStatus.Ok);
        QueueSync(source);
        db.Audit(actor.OrgId, actor.UserId, "source.pat", "knowledge_source", source.Id, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return View(source, await DocsAsync(source.Id, ct));
    }

    /// <summary>Deletes what was synced (documents and their chunks). The source stays, so a later sync starts over.</summary>
    public async Task<SourceView> PurgeAsync(Actor actor, Guid sourceId, CancellationToken ct)
    {
        var source = await ManagedAsync(actor, sourceId, ct);
        var docs = db.SourceDocuments.Where(d => d.SourceId == sourceId);
        await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.SourceDocument && docs.Any(d => d.Id == c.OwnerId)).ExecuteDeleteAsync(ct);
        var purged = await docs.ExecuteDeleteAsync(ct);
        source.LastCommitSha = null;
        db.Audit(actor.OrgId, actor.UserId, "source.purge", "knowledge_source", source.Id, after: new { documents = purged }, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return View(source, 0);
    }

    private byte[] Protect(string pat) => Encoding.UTF8.GetBytes(keys.CreateProtector(AtharKeys.GitHubPatPurpose).Protect(pat.Trim()));

    private void QueueSync(KnowledgeSource s) => db.Jobs.Add(new Job
    {
        OrgId = s.OrgId == Guid.Empty ? db.CurrentOrgId : s.OrgId, Kind = GitHubRules.SyncKind, Key = $"{s.Id}:now:{Guid.NewGuid():N}",
        Status = JobStatus.Ready, RunAfter = clock.GetUtcNow(),
        Payload = JsonSerializer.Serialize(new GitHubRules.SyncPayload(s.Id), IndexJobs.Json),
    });

    private void QueueIngest(KnowledgeSource s) => db.Jobs.Add(new Job
    {
        OrgId = s.OrgId == Guid.Empty ? db.CurrentOrgId : s.OrgId, Kind = IngestRules.IngestKind, Key = $"{s.Id}:{Guid.NewGuid():N}",
        Status = JobStatus.Ready, RunAfter = clock.GetUtcNow(),
        Payload = JsonSerializer.Serialize(new IngestRules.IngestPayload(s.Id), IndexJobs.Json),
    });

    private Task<int> DocsAsync(Guid sourceId, CancellationToken ct) => db.SourceDocuments.CountAsync(d => d.SourceId == sourceId && d.TombstonedAt == null, ct);

    private static SourceView View(KnowledgeSource s, int docs, DateTimeOffset? fetchedAt = null) =>
        new(s.Id, s.ProjectId, s.Repo, s.Status == SourceStatus.PausedAuth ? "paused_auth" : s.Status.ToString().ToLowerInvariant(), s.IsConfidential, docs,
            "project members", s.LastCommitSha, s.Kind.ToString().ToLowerInvariant(), s.Title, fetchedAt);

    private async Task<KnowledgeSource> ManagedAsync(Actor actor, Guid sourceId, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct) ?? throw new AtharError("not_found", "No such source.");
        await RequireManagerAsync(actor, source.ProjectId, ct);
        return source;
    }

    /// <summary>Project admins, and org owners and admins (ProjectRules.CanManage). Non-members learn nothing.</summary>
    private async Task RequireManagerAsync(Actor actor, Guid projectId, CancellationToken ct)
    {
        if (!actor.Scopes.Contains("write")) throw new AtharError("forbidden", "The token lacks the 'write' scope.");
        var (orgRole, projectRole) = await RolesAsync(actor, projectId, ct);
        if (projectRole is null && !ProjectRules.CanCreate(orgRole)) throw new AtharError("not_found", "No such project.");
        if (!ProjectRules.CanManage(orgRole, projectRole)) throw new AtharError("forbidden", "Only project admins manage sources.");
    }

    /// <summary>Project members add URL and document sources (G-4); it needs the <c>write</c> scope, like attaching a repo.</summary>
    private async Task RequireContributorAsync(Actor actor, Guid projectId, CancellationToken ct)
    {
        if (!actor.Scopes.Contains("write")) throw new AtharError("forbidden", "The token lacks the 'write' scope.");
        var (orgRole, projectRole) = await RolesAsync(actor, projectId, ct);
        if (projectRole is null && !ProjectRules.CanManage(orgRole, null)) throw new AtharError("not_found", "No such project.");
    }

    private async Task RequireMemberAsync(Actor actor, Guid projectId, CancellationToken ct)
    {
        if (!actor.Scopes.Contains("read")) throw new AtharError("forbidden", "The token lacks the 'read' scope.");
        var (orgRole, projectRole) = await RolesAsync(actor, projectId, ct);
        if (projectRole is null && !ProjectRules.CanCreate(orgRole)) throw new AtharError("not_found", "No such project.");
    }

    private async Task<(OrgRole Org, ProjectRole? Project)> RolesAsync(Actor actor, Guid projectId, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) throw new AtharError("not_found", "No such project.");
        var org = await db.OrgMembers.Where(m => m.UserId == actor.UserId).Select(m => m.Role).FirstAsync(ct);
        var project = await db.ProjectMembers.Where(m => m.ProjectId == projectId && m.UserId == actor.UserId).Select(m => (ProjectRole?)m.Role).FirstOrDefaultAsync(ct);
        return (org, project);
    }
}

public sealed record AttachSourceBody([property: JsonPropertyName("repo")] string Repo, [property: JsonPropertyName("pat")] string Pat,
    [property: JsonPropertyName("confidential")] bool Confidential = false);
public sealed record PatBody([property: JsonPropertyName("pat")] string Pat);
public sealed record IngestFileBody([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("content")] string Content);
public sealed record AddFilesBody([property: JsonPropertyName("project_id")] Guid ProjectId, [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("files")] IngestFileBody[]? Files = null, [property: JsonPropertyName("present")] string[]? Present = null,
    [property: JsonPropertyName("confidential")] bool Confidential = false);
public sealed record AddUrlBody([property: JsonPropertyName("project_id")] Guid ProjectId, [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("confidential")] bool Confidential = false);

/// <summary>docs/06 §6.2 <c>add_source</c> (FR-KS-06, T142): a web page as a project source, snapshotted by the worker.</summary>
[ModelContextProtocol.Server.McpServerToolType]
public sealed class SourceTools(SourceService sources, IHttpContextAccessor http)
{
    [ModelContextProtocol.Server.McpServerTool(Name = "add_source", UseStructuredContent = true, Idempotent = true)]
    [System.ComponentModel.Description("Add a web page to a project's knowledge: Athar snapshots that one page (public addresses only), indexes its text for the project's members, and refreshes it when you add the same URL again. Use resolve_project for the project id. Only add what the user asked for.")]
    public async Task<SourceView> AddSource(Guid project_id, [System.ComponentModel.Description("An absolute http or https URL")] string url,
        [System.ComponentModel.Description("Hide it from external agents (classification)")] bool confidential = false, CancellationToken ct = default)
    {
        try { return await sources.AddUrlAsync(Actor.From(http.HttpContext!), project_id, url, confidential, ct); }
        catch (AtharError e) { throw new ModelContextProtocol.McpException($"{e.Code}: {e.Message}"); }
    }
}

/// <summary>docs/06 §6.3: <c>/api/v1/projects/{id}/sources</c>, <c>/api/v1/sources/{id}/sync</c>; plus a new PAT and purge (FR-KS-03).</summary>
public static class SourceEndpoints
{
    public static void MapSourceEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/projects/{id:guid}/sources", (Guid id, SourceService s, HttpContext http, CancellationToken ct) => Rest.Run(() => s.ListAsync(Actor.From(http), id, ct)))
            .WithName("list_sources").Produces<IReadOnlyList<SourceView>>();
        v1.MapPost("/projects/{id:guid}/sources", (Guid id, AttachSourceBody b, SourceService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.AttachAsync(Actor.From(http), id, b.Repo, b.Pat, b.Confidential, ct)))
            .WithName("attach_source").Produces<SourceView>();
        v1.MapPost("/sources/{id:guid}/sync", (Guid id, SourceService s, HttpContext http, CancellationToken ct) => Rest.Run(() => s.SyncNowAsync(Actor.From(http), id, ct)))
            .WithName("sync_source").Produces<SourceView>();
        v1.MapPut("/sources/{id:guid}/pat", (Guid id, PatBody b, SourceService s, HttpContext http, CancellationToken ct) => Rest.Run(() => s.SetPatAsync(Actor.From(http), id, b.Pat, ct)))
            .WithName("set_source_pat").Produces<SourceView>();
        // T142 (FR-KS-06): URL and document sources, added by project members; refresh takes a new snapshot.
        v1.MapPost("/sources/url", (AddUrlBody b, SourceService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.AddUrlAsync(Actor.From(http), b.ProjectId, b.Url, b.Confidential, ct)))
            .WithName("add_source").Produces<SourceView>();
        v1.MapPost("/sources/documents", async (HttpContext http, SourceService s, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Results.BadRequest(new { error = "invalid_input", message = "Send multipart/form-data with project_id and file." });
                var form = await http.Request.ReadFormAsync(ct);
                if (form.Files.GetFile("file") is not { } file || !Guid.TryParse(form["project_id"], out var projectId))
                    return Results.BadRequest(new { error = "invalid_input", message = "project_id and file are required." });
                if (file.Length > IngestRules.MaxBytes) return Results.BadRequest(new { error = "invalid_input", message = "Documents are limited to 10 MB." });
                using var bytes = new MemoryStream();
                await file.CopyToAsync(bytes, ct);
                return await Rest.Run(() => s.AddDocumentAsync(Actor.From(http), projectId, file.FileName, bytes.ToArray(), bool.TryParse(form["confidential"], out var c) && c, ct));
            })
            .WithName("add_document_source").Produces<SourceView>().DisableAntiforgery();
        v1.MapPost("/sources/files", (AddFilesBody b, SourceService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.AddFilesAsync(Actor.From(http), b.ProjectId, b.Label, b.Confidential,
                    [.. (b.Files ?? []).Select(f => new SourceService.IngestFile(f.Path, f.Content))], b.Present, ct)))
            .WithName("add_folder_source").Produces<SourceView>();
        v1.MapPost("/sources/{id:guid}/refresh", (Guid id, SourceService s, HttpContext http, CancellationToken ct) => Rest.Run(() => s.RefreshAsync(Actor.From(http), id, ct)))
            .WithName("refresh_source").Produces<SourceView>();
        v1.MapDelete("/sources/{id:guid}/documents", (Guid id, SourceService s, HttpContext http, CancellationToken ct) => Rest.Run(() => s.PurgeAsync(Actor.From(http), id, ct)))
            .WithName("purge_source").Produces<SourceView>();
    }
}
