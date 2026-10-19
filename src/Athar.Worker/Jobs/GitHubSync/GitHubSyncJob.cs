using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Domain.Sources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Athar.Worker.Jobs.GitHubSync;

/// <summary>
/// <c>github-sync</c> (FR-KS-02/03): snapshots the default branch at its commit SHA; re-indexes docs whose blob changed,
/// tombstones docs that are gone, and rebuilds the structure document (tree summary plus manifest dependencies). The chunks
/// are project-scoped to the source's project and carry its classification, so search shows them to members only. A PAT
/// that no longer works pauses the source and notifies the project's admins. Each run queues the next hourly sweep.
/// </summary>
public sealed class GitHubSyncJob(GitHubClient github, IDataProtectionProvider keys, TimeProvider clock, ILogger<GitHubSyncJob> log) : IJob
{
    public string Kind => GitHubRules.SyncKind;

    public async Task RunAsync(Job job, AtharDbContext db, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<GitHubRules.SyncPayload>(job.Payload, IndexJobs.Json)!;
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(s => s.Id == p.SourceId, ct);
        if (source is null || source.Status == SourceStatus.PausedAuth) return; // gone, or waiting for a new PAT

        try
        {
            if (source.EncryptedPat is null) throw new GitHubAuthException("The source has no PAT.");
            var pat = keys.CreateProtector(AtharKeys.GitHubPatPurpose).Unprotect(Encoding.UTF8.GetString(source.EncryptedPat));
            await SyncAsync(db, source, pat, ct);
            source.Status = SourceStatus.Ok;
        }
        catch (GitHubAuthException e)
        {
            log.LogWarning("Source {Id} ({Repo}) paused: {Reason}", source.Id, source.Repo, e.Message);
            source.Status = SourceStatus.PausedAuth;
            var admins = await db.ProjectMembers.Where(m => m.ProjectId == source.ProjectId && m.Role == ProjectRole.Admin).Select(m => m.UserId).ToListAsync(ct);
            foreach (var admin in admins) db.Notifications.Add(new Notification { UserId = admin, Kind = "source_paused", Ref = source.Id.ToString() });
            db.Audit(source.OrgId, null, "source.pause", "knowledge_source", source.Id, after: new { source.Repo, reason = e.Message });
            return; // no sweep until an admin sets a new PAT
        }
        QueueSweep(db, source);
    }

    private async Task SyncAsync(AtharDbContext db, KnowledgeSource source, string pat, CancellationToken ct)
    {
        var branch = await github.DefaultBranchAsync(source.Repo, pat, ct);
        var sha = await github.HeadAsync(source.Repo, branch, pat, ct);
        if (sha == source.LastCommitSha) return;

        var tree = await github.TreeAsync(source.Repo, sha, pat, ct);
        var docs = await db.SourceDocuments.Where(d => d.SourceId == source.Id).ToDictionaryAsync(d => d.Path, ct);
        var wanted = tree.Where(t => GitHubRules.IsDoc(t.Path, t.Size)).ToList();
        var revision = clock.GetUtcNow().ToUnixTimeMilliseconds();

        foreach (var item in wanted)
        {
            if (docs.TryGetValue(item.Path, out var doc) && doc.ContentHash == item.Sha && doc.TombstonedAt is null) { doc.CommitSha = sha; continue; }
            var content = await github.BlobAsync(source.Repo, item.Sha, pat, ct);
            await IndexAsync(db, source, docs, item.Path, content, item.Sha, sha, revision, ct);
        }

        // Gone from the repo (or no longer a doc): tombstoned, out of search.
        var present = wanted.Select(w => w.Path).Append(GitHubRules.StructurePath).ToHashSet();
        foreach (var gone in docs.Values.Where(d => d.TombstonedAt is null && !present.Contains(d.Path)))
        {
            gone.TombstonedAt = clock.GetUtcNow();
            await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.SourceDocument && c.OwnerId == gone.Id).ExecuteDeleteAsync(ct);
        }

        var manifests = new List<(string, IReadOnlyList<string>)>();
        foreach (var m in tree.Where(t => GitHubRules.IsManifest(t.Path) && t.Size <= GitHubRules.MaxDocBytes))
            manifests.Add((m.Path, GitHubRules.Dependencies(m.Path, await github.BlobAsync(source.Repo, m.Sha, pat, ct))));
        var structure = GitHubRules.Structure(source.Repo, sha, [.. tree.Select(t => t.Path)], manifests);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(structure)));
        if (!docs.TryGetValue(GitHubRules.StructurePath, out var current) || current.ContentHash != hash)
            await IndexAsync(db, source, docs, GitHubRules.StructurePath, structure, hash, sha, revision, ct);

        source.LastCommitSha = sha;
        db.Audit(source.OrgId, null, "source.sync", "knowledge_source", source.Id, after: new { source.Repo, commit = sha, docs = wanted.Count });
    }

    /// <summary>Upserts the document and replaces its chunks (project-scoped, the source's classification) and their vectors job.</summary>
    private async Task IndexAsync(AtharDbContext db, KnowledgeSource source, Dictionary<string, SourceDocument> docs, string path, string content,
        string contentHash, string commit, long revision, CancellationToken ct)
    {
        var title = content.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? Path.GetFileName(path);
        if (!docs.TryGetValue(path, out var doc))
        {
            doc = new SourceDocument { SourceId = source.Id, Path = path, ContentMd = "", ContentHash = "", CommitSha = commit };
            db.SourceDocuments.Add(doc);
            docs[path] = doc;
        }
        (doc.Title, doc.ContentMd, doc.ContentHash, doc.CommitSha, doc.TombstonedAt) = (title, content, contentHash, commit, null);

        await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.SourceDocument && c.OwnerId == doc.Id).ExecuteDeleteAsync(ct);
        db.Chunks.AddRange(Chunker.Build(ChunkOwner.SourceDocument, doc.Id, revision, $"{source.Repo}: {title}", content, c =>
        {
            (c.Scope, c.OwnerProjectId, c.IsConfidential) = (Scope.Project, source.ProjectId, source.IsConfidential);
        }));
        db.Jobs.Add(IndexJobs.Vectors(ChunkOwner.SourceDocument, doc.Id, revision, clock.GetUtcNow()));
    }

    /// <summary>The next hourly sweep (FR-KS-03), one per source per hour.</summary>
    private void QueueSweep(AtharDbContext db, KnowledgeSource source)
    {
        var at = clock.GetUtcNow() + GitHubRules.SweepEvery;
        var key = $"{source.Id}:{at:yyyyMMddHH}";
        if (db.Jobs.Local.Any(j => j.Kind == GitHubRules.SyncKind && j.Key == key) || db.Jobs.Any(j => j.Kind == GitHubRules.SyncKind && j.Key == key)) return;
        db.Jobs.Add(new Job
        {
            OrgId = source.OrgId, Kind = GitHubRules.SyncKind, Key = key, Status = JobStatus.Ready, RunAfter = at,
            Payload = JsonSerializer.Serialize(new GitHubRules.SyncPayload(source.Id), IndexJobs.Json),
        });
    }
}
