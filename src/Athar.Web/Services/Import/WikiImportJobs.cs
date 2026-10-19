using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Athar.Web.Mcp;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services.Import;

/// <summary>An import job's state, in its payload: the request, then progress and the outcome.</summary>
public sealed record ImportJobPayload(string Mode, Guid TargetId, Guid? ParentId, string FileName, string Path, Guid UserId, string OpId,
    int Done = 0, int Total = 0, ImportResult? Result = null);

/// <summary>What the import dialog shows: queued, running (done of total pages), done (the result) or failed (why).</summary>
public sealed record ImportStatus(string State, int Done, int Total, ImportResult? Result, string? Error);

/// <summary>
/// T164: imports up to 4 GB. The browser uploads the file to <c>POST /wiki/import</c>, which streams it to disk and queues a
/// <c>wiki-import</c> job; <see cref="WikiImportRunner"/> (in web: pages go through collab) runs it and notifies the
/// importer. Permission is checked before the upload is read, and again when the job runs.
/// </summary>
public sealed class WikiImportQueue(AtharDbContext db, WorkspaceAdminService admin, IConfiguration config, TimeProvider clock)
{
    public const string Kind = "wiki-import";

    /// <summary>Where uploads wait for their job: <c>Athar:Imports:Path</c>, else the temp folder (not the backed-up attachments volume).</summary>
    public string UploadDir => config["Athar:Imports:Path"] is { Length: > 0 } p ? p : Path.Combine(Path.GetTempPath(), "athar-imports");

    /// <summary><paramref name="mode"/> "space" (space managers) or "workspace" (workspace managers).</summary>
    public async Task RequireAsync(Actor actor, string mode, Guid targetId, CancellationToken ct = default)
    {
        if (actor.Caller.Agent) throw new AtharError("forbidden", "Importing pages is a wiki action.");
        var allowed = mode switch
        {
            "space" => await admin.CanManageSpaceAsync(actor.OrgId, actor.UserId, targetId, ct),
            "workspace" => (await admin.WorkspacesAsync(actor.OrgId, actor.UserId, ct)).Any(w => w.Id == targetId && w.CanManage),
            _ => throw new AtharError("invalid_input", "mode is space or workspace."),
        };
        if (!allowed) throw new AtharError("forbidden", mode == "space" ? "Only the space's admins can import pages." : "Only the workspace's admins can import spaces.");
    }

    public async Task<Guid> EnqueueAsync(Actor actor, string mode, Guid targetId, Guid? parentId, string fileName, string path, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var job = new Job
        {
            OrgId = actor.OrgId, Kind = Kind, Key = Guid.NewGuid().ToString(), Status = JobStatus.Ready, RunAfter = clock.GetUtcNow(),
            Payload = JsonSerializer.Serialize(new ImportJobPayload(mode, targetId, parentId, fileName, path, actor.UserId, Guid.NewGuid().ToString()), IndexJobs.Json),
        };
        db.Jobs.Add(job);
        db.Audit(actor.OrgId, actor.UserId, "page.import_queued", "job", job.Id, after: new { mode, target = targetId, parent = parentId, file_name = fileName });
        await db.SaveChangesAsync(ct);
        return job.Id;
    }

    /// <summary>The importer's own job, or null.</summary>
    public async Task<ImportStatus?> StatusAsync(Actor actor, Guid jobId, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId && j.Kind == Kind, ct);
        if (job is null || Read(job) is not { } p || p.UserId != actor.UserId) return null;
        return job.Status switch
        {
            JobStatus.Done => new("done", p.Total, p.Total, p.Result, null),
            JobStatus.Dead => new("failed", p.Done, p.Total, null, job.LastError),
            JobStatus.Running => new("running", p.Done, p.Total, null, null),
            _ => new(job.Attempts > 0 ? "running" : "queued", p.Done, p.Total, null, null),
        };
    }

    public static ImportJobPayload? Read(Job job) => JsonSerializer.Deserialize<ImportJobPayload>(job.Payload, IndexJobs.Json);
}

/// <summary>
/// Runs <c>wiki-import</c> jobs one at a time in web (the worker never claims this kind). The lease is renewed as pages are
/// written, so an import that outlives a restart is reclaimed and resumes (pages and threads are idempotent by op id).
/// A bad upload or a refusal fails at once; anything else is retried twice. The importer is told either way.
/// </summary>
public sealed class WikiImportRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<WikiImportRunner> log) : BackgroundService
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    public static TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(3);
    private const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!await RunOneAsync(stop)) await Task.Delay(Poll, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                log.LogWarning(e, "wiki-import runner");
                await Task.Delay(Poll, stop);
            }
        }
    }

    /// <summary>Claims and runs one due import; false when there's none.</summary>
    public async Task<bool> RunOneAsync(CancellationToken ct)
    {
        Guid jobId, leaseId = Guid.NewGuid();
        ImportJobPayload request;
        Guid orgId;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
            var now = clock.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var job = await db.Jobs.FromSql($"""
                    SELECT *, xmin FROM jobs
                    WHERE kind = {WikiImportQueue.Kind}
                      AND ((status = 'ready' AND run_after <= {now}) OR (status = 'running' AND lease_until < {now}))
                    ORDER BY run_after LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).IgnoreQueryFilters().ToListAsync(ct) is [var claimed] ? claimed : null;
            if (job is null) return false;
            db.CurrentOrgId = job.OrgId;
            (job.Status, job.LeaseId, job.LeaseUntil) = (JobStatus.Running, leaseId, now + Lease);
            job.Attempts++;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            (jobId, orgId, request) = (job.Id, job.OrgId, WikiImportQueue.Read(job)!);
        }

        var actor = new Actor(request.UserId, orgId, null, new HashSet<string> { "read", "write" }, DisclosureClass.TrustedLocal, null);
        try
        {
            ImportResult result;
            await using (var scope = scopes.CreateAsyncScope())
            {
                if (!File.Exists(request.Path)) throw new AtharError("invalid_input", "The uploaded file is gone (the server restarted before the import began). Upload it again.");
                var importer = scope.ServiceProvider.GetRequiredService<WikiImportService>();
                await using var file = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                Task Progress(int done, int total) => UpdateAsync(jobId, leaseId, j => j with { Done = done, Total = total }, ct);
                result = request.Mode == "workspace"
                    ? await importer.ImportSpacesAsync(actor, request.TargetId, request.FileName, file, request.OpId, ct, Progress)
                    : await importer.ImportAsync(actor, request.TargetId, request.ParentId, request.FileName, file, request.OpId, ct, Progress);
            }
            await FinishAsync(jobId, leaseId, request, JobStatus.Done, null, j => j with { Done = result.Pages, Total = result.Pages, Result = result },
                "import_done", $"page:{result.FirstPageId}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // shutting down: the lease runs out and it resumes
        catch (Exception e)
        {
            var final = e is AtharError || await AttemptsAsync(jobId, ct) >= MaxAttempts;
            log.LogWarning(e, "wiki-import {Job} failed{Final}", jobId, final ? "" : ", will retry");
            var message = e is AtharError ? e.Message : e.GetBaseException().Message;
            if (final) await FinishAsync(jobId, leaseId, request, JobStatus.Dead, message, j => j, "import_failed", $"import:{jobId}", ct);
            else await RetryAsync(jobId, leaseId, message, ct);
        }
        return true;
    }

    private async Task UpdateAsync(Guid jobId, Guid leaseId, Func<ImportJobPayload, ImportJobPayload> change, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
        var job = await db.Jobs.IgnoreQueryFilters().SingleAsync(j => j.Id == jobId, ct);
        if (job.LeaseId != leaseId) throw new InvalidOperationException("The import's lease was lost.");
        db.CurrentOrgId = job.OrgId;
        job.Payload = JsonSerializer.Serialize(change(WikiImportQueue.Read(job)!), IndexJobs.Json);
        job.LeaseUntil = clock.GetUtcNow() + Lease;
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> AttemptsAsync(Guid jobId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AtharDbContext>().Jobs.IgnoreQueryFilters().Where(j => j.Id == jobId).Select(j => j.Attempts).SingleAsync(ct);
    }

    private async Task RetryAsync(Guid jobId, Guid leaseId, string error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
        var job = await db.Jobs.IgnoreQueryFilters().SingleAsync(j => j.Id == jobId, ct);
        if (job.LeaseId != leaseId) return;
        db.CurrentOrgId = job.OrgId;
        (job.Status, job.LeaseId, job.LeaseUntil, job.RunAfter, job.LastError) = (JobStatus.Ready, null, null, clock.GetUtcNow() + TimeSpan.FromMinutes(1), Trim(error));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Done or failed: the outcome, the upload removed, and a notification for the importer.</summary>
    private async Task FinishAsync(Guid jobId, Guid leaseId, ImportJobPayload request, JobStatus status, string? error,
        Func<ImportJobPayload, ImportJobPayload> change, string kind, string reference, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
        var job = await db.Jobs.IgnoreQueryFilters().SingleAsync(j => j.Id == jobId, ct);
        if (job.LeaseId != leaseId) return;
        db.CurrentOrgId = job.OrgId;
        job.Payload = JsonSerializer.Serialize(change(WikiImportQueue.Read(job)!), IndexJobs.Json);
        (job.Status, job.LeaseId, job.LeaseUntil, job.LastError) = (status, null, null, error is null ? null : Trim(error));
        db.Notifications.Add(new Notification { OrgId = job.OrgId, UserId = request.UserId, Kind = kind, Ref = reference });
        await db.SaveChangesAsync(ct);
        try { File.Delete(request.Path); } catch (IOException e) { log.LogWarning(e, "Couldn't remove the upload {Path}", request.Path); }
    }

    private static string Trim(string s) => s.Length > 2000 ? s[..2000] : s;
}

/// <summary>The upload, with the person's cookie and an antiforgery token (not part of /api/v1).</summary>
public static class WikiImportEndpoints
{
    public static void MapWikiImport(this WebApplication app) =>
        app.MapPost("/wiki/import", async (HttpContext http, WikiImportQueue queue, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            try { await antiforgery.ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { return Results.Problem(title: "forbidden", detail: "Reload the page and try again.", statusCode: 400); }
            var q = http.Request.Query;
            var mode = q["mode"].ToString();
            if (!Guid.TryParse(q["target"], out var target) || string.IsNullOrWhiteSpace(q["name"]))
                return Results.Problem(title: "invalid_input", detail: "target and name are required.", statusCode: 400);
            Guid? parent = Guid.TryParse(q["parent"], out var p) ? p : null;
            var actor = Actor.ForUser(http.User);
            string? path = null;
            try
            {
                await queue.RequireAsync(actor, mode, target, ct);
                if (http.Request.ContentLength > WikiImport.MaxBytes) throw new AtharError("too_large", "Imports are limited to 4 GB.");
                Directory.CreateDirectory(queue.UploadDir);
                path = Path.Combine(queue.UploadDir, $"{Guid.NewGuid():N}.upload");
                long size = 0;
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var chunk = new byte[81920];
                    int read;
                    while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
                    {
                        size += read;
                        if (size > WikiImport.MaxBytes) throw new AtharError("too_large", "Imports are limited to 4 GB.");
                        await file.WriteAsync(chunk.AsMemory(0, read), ct);
                    }
                }
                if (size == 0) throw new AtharError("invalid_input", "The file is empty.");
                var job = await queue.EnqueueAsync(actor, mode, target, parent, Path.GetFileName(q["name"].ToString()), path, ct);
                return Results.Json(new { job_id = job });
            }
            catch (Exception e) when (e is AtharError or OperationCanceledException or IOException or BadHttpRequestException)
            {
                if (path is not null) try { File.Delete(path); } catch (IOException) { }
                return e is AtharError a
                    ? Results.Problem(title: a.Code, detail: a.Message, statusCode: a.Code switch { "forbidden" => 403, "too_large" => 413, _ => 400 })
                    : Results.Problem(title: "upload_failed", detail: "The upload didn't finish.", statusCode: 400);
            }
        })
        .RequireAuthorization()
        .DisableAntiforgery()   // validated above, from the header, so the raw body is never read as a form
        .WithMetadata(new RequestSizeLimitAttribute(WikiImport.MaxBytes + 1024 * 1024))
        .ExcludeFromDescription();
}
