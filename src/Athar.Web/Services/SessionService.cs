using Athar.Domain.Memory;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Athar.Data;
using Athar.Domain.Model;
using Athar.Domain.Sessions;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athar.Web.Services;

/// <summary><paramref name="Capture"/> is false while the person has paused capture (T135): the hook then sends nothing.</summary>
public sealed record SessionStarted(Guid SessionId, bool Capture = true);
public sealed record CommitRecord(int Index, JsonElement Data);
public sealed record CommitInput(string CommitId, int FromIndex, int ToIndex, int Skipped, IReadOnlyList<CommitRecord> Records);
public sealed record CommitAck(int AckedIndex);
public sealed record SavedChat(string Result, Guid SessionId);
/// <summary>A candidate on My sessions: <c>status</c> new, accepted, edited, discarded or auto_accepted.</summary>
public sealed record SessionCandidateView(Guid Id, string Title, string Status, string Scope, Guid? MemoryId, Guid? ProposalId);
/// <summary>A session on My sessions (T138). <c>source</c> is hook or chat.</summary>
public sealed record MySessionView(Guid SessionId, string? Title, string Client, string Source, string? RepoRemote, Guid? ProjectId, string? ProjectName,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, int Records, DateTimeOffset? PurgeAfter, IReadOnlyList<SessionCandidateView> Candidates);
public sealed record TranscriptDeleted(int Records, int Candidates);
public sealed record SessionSummary(Guid SessionId, string Client, string ExternalSessionId, string? RepoRemote, int AckedIndex, DateTimeOffset CreatedAt);
public sealed record SessionRecordView(int Index, JsonElement Data, DateTimeOffset PurgeAfter);
public sealed record SessionDetail(SessionSummary Session, IReadOnlyList<SessionRecordView> Records);

/// <summary>
/// FR-AG-04 session capture (docs/06 §6.2 <c>start_session</c>, <c>commit_session</c>). Starting is idempotent per
/// (user, client, external_session_id), a repeated <c>commit_id</c> is a no-op, and records the server already holds are
/// skipped, so the hook's outbox can re-send anything. Records are redacted again here and readable only by their owner.
/// </summary>
public sealed class SessionService(AtharDbContext db, TimeProvider clock)
{
    public const int MaxRecordsBytes = 2_000_000;

    public async Task<SessionStarted> StartAsync(Actor actor, string client, string externalSessionId, string? repoRemote, CancellationToken ct)
    {
        Begin(actor, "memory:write");
        if (string.IsNullOrWhiteSpace(client) || client.Length > 64) throw new AtharError("invalid_input", "client is required (at most 64 characters).");
        if (string.IsNullOrWhiteSpace(externalSessionId) || externalSessionId.Length > 200)
            throw new AtharError("invalid_input", "external_session_id is required (at most 200 characters).");

        // Pause (T135) still answers with a session, so an older hook without the capture flag keeps a valid id; its commits are dropped.
        var capture = !await PausedAsync(actor, ct);
        if (await FindAsync(actor, client, externalSessionId, ct) is { } existing) return new(existing, capture);
        var session = new AgentSession
        {
            OrgId = actor.OrgId, UserId = actor.UserId, CredentialId = actor.CredentialId, Client = client,
            ExternalSessionId = externalSessionId, RepoRemote = string.IsNullOrWhiteSpace(repoRemote) ? null : repoRemote.Trim(),
        };
        db.AgentSessions.Add(session);
        db.Audit(actor.OrgId, actor.UserId, "session.start", "session", session.Id, after: new { client, external_session_id = externalSessionId }, credentialId: actor.CredentialId);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent start with the same external ID won; return its session.
            db.ChangeTracker.Clear();
            return new((await FindAsync(actor, client, externalSessionId, ct))!.Value, capture);
        }
        return new(session.Id, capture);
    }

    public async Task<CommitAck> CommitAsync(Actor actor, Guid sessionId, CommitInput input, CancellationToken ct)
    {
        Begin(actor, "memory:write");
        var session = await db.AgentSessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == actor.UserId, ct)
            ?? throw new AtharError("not_found", "No such session.");
        Validate(input);
        // Paused (T135): nothing is stored or extracted. The ack still moves so the hook's outbox drains instead of re-sending.
        if (await PausedAsync(actor, ct)) return new(Math.Max(session.AckedIndex, input.ToIndex));
        if (await db.SessionCommits.AnyAsync(c => c.SessionId == sessionId && c.CommitId == input.CommitId, ct))
            return new(session.AckedIndex);

        var indexes = input.Records.Select(r => r.Index).ToList();
        var held = (await db.SessionRecords.Where(r => r.SessionId == sessionId && indexes.Contains(r.RecordIndex))
            .Select(r => r.RecordIndex).ToListAsync(ct)).ToHashSet();
        var now = clock.GetUtcNow();
        var added = 0;
        foreach (var r in input.Records.Where(r => !held.Contains(r.Index)))
        {
            db.SessionRecords.Add(new SessionRecord
            {
                OrgId = actor.OrgId, SessionId = sessionId, RecordIndex = r.Index, PurgeAfter = now + SessionRetention.Keep,
                RedactedRecord = TranscriptRedactor.Redact(JsonNode.Parse(r.Data.GetRawText()))!.ToJsonString(),
            });
            added++;
        }
        db.SessionCommits.Add(new SessionCommit
        {
            OrgId = actor.OrgId, SessionId = sessionId, CommitId = input.CommitId, FromIndex = input.FromIndex,
            ToIndex = input.ToIndex, Skipped = input.Skipped > 0, ReceivedAt = now,
        });
        // T061: new records queue their extraction into the owner's capture inbox, in this transaction (docs/03 §7).
        if (input.ToIndex > session.AckedIndex) db.Jobs.Add(IndexJobs.Extract(sessionId, input.ToIndex, now));
        session.AckedIndex = Math.Max(session.AckedIndex, input.ToIndex);
        db.Audit(actor.OrgId, actor.UserId, "session.commit", "session", sessionId,
            after: new { commit_id = input.CommitId, records = added, skipped = input.Skipped }, credentialId: actor.CredentialId);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same commit (or overlapping records) arrived concurrently; the winner's ack stands.
            db.ChangeTracker.Clear();
            return new((await db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, ct)).AckedIndex);
        }
        return new(session.AckedIndex);
    }

    public const int MaxKeyPoints = 50;

    /// <summary>
    /// <c>save_chat</c> (FR-AG-09, T139): a chat from a Claude app or VS Code, summarized by the agent, stored as a chat session
    /// (record 0 the summary, then one record per key point) and extracted like a hook session. Idempotent per
    /// <paramref name="opId"/>: a replay gets the same session, another payload under that op_id gets <c>op_id_reused</c>.
    /// </summary>
    public async Task<SavedChat> SaveChatAsync(Actor actor, string title, string summaryMd, IReadOnlyList<string>? keyPoints, Guid? projectId, string opId, CancellationToken ct)
    {
        Begin(actor, "memory:write");
        if (string.IsNullOrWhiteSpace(opId) || opId.Length > 190) throw new AtharError("invalid_input", "op_id is required (at most 190 characters).");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) throw new AtharError("invalid_input", "title is required (at most 200 characters).");
        if (string.IsNullOrWhiteSpace(summaryMd)) throw new AtharError("invalid_input", "summary_md is required.");
        var points = (keyPoints ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (points.Count > MaxKeyPoints) throw new AtharError("invalid_input", $"At most {MaxKeyPoints} key points.");
        if (Encoding.UTF8.GetByteCount(summaryMd) + points.Sum(Encoding.UTF8.GetByteCount) > MaxRecordsBytes)
            throw new AtharError("invalid_input", "The chat exceeds 2 MB; summarize it further.");

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { title = title.Trim(), summaryMd, points, projectId })));
        var external = "chat:" + opId;
        if (await ReplayAsync() is { } replay) return replay;

        if (await PausedAsync(actor, ct)) throw new AtharError("forbidden", "Capture is paused. Resume it on My sessions to save chats.");
        if (projectId is { } p && !await db.ProjectMembers.AnyAsync(m => m.ProjectId == p && m.UserId == actor.UserId, ct))
            throw new AtharError("not_found", "No such project.");

        var now = clock.GetUtcNow();
        var client = string.IsNullOrWhiteSpace(actor.ClaimedClient) ? "chat" : actor.ClaimedClient.Trim()[..Math.Min(64, actor.ClaimedClient.Trim().Length)];
        var session = new AgentSession
        {
            OrgId = actor.OrgId, UserId = actor.UserId, CredentialId = actor.CredentialId, Client = client, ExternalSessionId = external,
            Source = SessionSource.Chat, Title = title.Trim(), ProjectId = projectId, AckedIndex = points.Count,
        };
        db.AgentSessions.Add(session);
        string[] texts = [summaryMd, .. points];
        for (var i = 0; i < texts.Length; i++)
            db.SessionRecords.Add(new SessionRecord
            {
                OrgId = actor.OrgId, SessionId = session.Id, RecordIndex = i, PurgeAfter = now + SessionRetention.Keep,
                RedactedRecord = TranscriptRedactor.Redact(new JsonObject { ["role"] = i == 0 ? "summary" : "key_point", ["text"] = texts[i] })!.ToJsonString(),
            });
        // The payload hash as the commit id: how a replay tells the same chat from another one under the same op_id.
        db.SessionCommits.Add(new SessionCommit { OrgId = actor.OrgId, SessionId = session.Id, CommitId = hash, FromIndex = 0, ToIndex = points.Count, ReceivedAt = now });
        db.Jobs.Add(IndexJobs.Extract(session.Id, points.Count, now));
        db.Audit(actor.OrgId, actor.UserId, "session.save_chat", "session", session.Id,
            after: new { title = session.Title, project_id = projectId, key_points = points.Count }, credentialId: actor.CredentialId);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return await ReplayAsync() ?? throw new AtharError("pending", "The operation is still in progress.");
        }
        return new("saved", session.Id);

        async Task<SavedChat?> ReplayAsync()
        {
            var prior = await db.AgentSessions.AsNoTracking().Where(s => s.UserId == actor.UserId && s.Source == SessionSource.Chat && s.ExternalSessionId == external)
                .Select(s => new { s.Id, Hash = db.SessionCommits.Where(c => c.SessionId == s.Id).Select(c => c.CommitId).FirstOrDefault() }).FirstOrDefaultAsync(ct);
            if (prior is null) return null;
            return prior.Hash == hash ? new("saved", prior.Id) : throw new AtharError("op_id_reused", "This op_id was already used with a different chat.");
        }
    }

    public const int MySessionsMax = 100;

    /// <summary>
    /// My sessions (T138): the caller's latest sessions with their project, transcript size and what extraction made of them.
    /// The project is the session's, else the caller's one member project linked to its repo (as extraction resolves it).
    /// </summary>
    public async Task<IReadOnlyList<MySessionView>> MySessionsAsync(Actor actor, CancellationToken ct)
    {
        Begin(actor, "read");
        var sessions = await db.AgentSessions.AsNoTracking().Where(s => s.UserId == actor.UserId).OrderByDescending(s => s.CreatedAt).Take(MySessionsMax)
            .Select(s => new
            {
                s.Id, s.Title, s.Client, s.Source, s.RepoRemote, s.ProjectId, s.CreatedAt,
                Records = db.SessionRecords.Count(r => r.SessionId == s.Id),
                PurgeAfter = db.SessionRecords.Where(r => r.SessionId == s.Id).Min(r => (DateTimeOffset?)r.PurgeAfter),
                LastCommit = db.SessionCommits.Where(c => c.SessionId == s.Id).Max(c => (DateTimeOffset?)c.ReceivedAt),
            }).ToListAsync(ct);
        var ids = sessions.Select(s => s.Id).ToList();
        var candidates = (await db.CaptureCandidates.AsNoTracking().Where(c => ids.Contains(c.SessionId) && c.OwnerUserId == actor.UserId)
                .OrderBy(c => c.CreatedAt)
                .Select(c => new { c.SessionId, c.Id, c.Title, c.Status, c.SuggestedScope, c.MemoryId, c.ProposalId }).ToListAsync(ct))
            .ToLookup(c => c.SessionId);
        var mine = await (from m in db.ProjectMembers where m.UserId == actor.UserId join p in db.Projects on m.ProjectId equals p.Id select new { p.Id, p.Name }).ToListAsync(ct);
        var names = mine.ToDictionary(p => p.Id, p => p.Name);
        var links = (await db.ProjectLinks.AsNoTracking().Where(l => l.RepoRemote != null && names.Keys.Contains(l.ProjectId)).Select(l => new { l.RepoRemote, l.ProjectId }).ToListAsync(ct))
            .ToLookup(l => l.RepoRemote!, l => l.ProjectId);

        return [.. sessions.Select(s =>
        {
            var project = s.ProjectId ?? (ProjectRules.NormalizeRemote(s.RepoRemote) is { } r && links[r].Distinct().ToList() is [var only] ? only : null);
            return new MySessionView(s.Id, s.Title, s.Client, s.Source == SessionSource.Chat ? "chat" : "hook", s.RepoRemote, project,
                project is { } pid && names.TryGetValue(pid, out var n) ? n : null, s.CreatedAt, s.LastCommit ?? s.CreatedAt, s.Records, s.PurgeAfter,
                [.. candidates[s.Id].Select(c => new SessionCandidateView(c.Id, c.Title, Snake(c.Status.ToString()), Snake(c.SuggestedScope.ToString()), c.MemoryId, c.ProposalId))]);
        })];
    }

    /// <summary>
    /// Delete now (T138): the session's transcript records and its unreviewed candidates (which quote it) go at once, like the
    /// 30-day purge. Decided candidates and the memories they became stay. Owner only; audited.
    /// </summary>
    public async Task<TranscriptDeleted> DeleteTranscriptAsync(Actor actor, Guid sessionId, CancellationToken ct)
    {
        Begin(actor, "memory:write");
        if (!await db.AgentSessions.AnyAsync(s => s.Id == sessionId && s.UserId == actor.UserId, ct)) throw new AtharError("not_found", "No such session.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var records = await db.SessionRecords.Where(r => r.SessionId == sessionId).ExecuteDeleteAsync(ct);
        var candidates = await db.CaptureCandidates.Where(c => c.SessionId == sessionId && c.Status == CandidateStatus.New).ExecuteDeleteAsync(ct);
        db.Audit(actor.OrgId, actor.UserId, "session.delete_transcript", "session", sessionId, after: new { records, candidates }, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(records, candidates);
    }

    private static string Snake(string s) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(s);

    /// <summary>The caller's own sessions. Nobody else's, admins included (FR-AG-04).</summary>
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(Actor actor, CancellationToken ct)
    {
        Begin(actor, "read");
        return await db.AgentSessions.AsNoTracking().Where(s => s.UserId == actor.UserId).OrderByDescending(s => s.CreatedAt)
            .Select(s => new SessionSummary(s.Id, s.Client, s.ExternalSessionId, s.RepoRemote, s.AckedIndex, s.CreatedAt)).ToListAsync(ct);
    }

    /// <summary>The caller's latest session commit per client (FR-AG-08 connection status on the Agents hub).</summary>
    public async Task<Dictionary<string, DateTimeOffset>> LastCommitsAsync(Actor actor, CancellationToken ct)
    {
        Begin(actor, "read");
        return await db.SessionCommits.AsNoTracking()
            .Join(db.AgentSessions.Where(s => s.UserId == actor.UserId), c => c.SessionId, s => s.Id, (c, s) => new { s.Client, c.ReceivedAt })
            .GroupBy(x => x.Client).Select(g => new { g.Key, Last = g.Max(x => x.ReceivedAt) })
            .ToDictionaryAsync(x => x.Key, x => x.Last, ct);
    }

    /// <summary>One session with its records, for its owner only; anyone else gets <c>not_found</c>.</summary>
    public async Task<SessionDetail> GetAsync(Actor actor, Guid sessionId, CancellationToken ct)
    {
        Begin(actor, "read");
        var s = await db.AgentSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == actor.UserId, ct)
            ?? throw new AtharError("not_found", "No such session.");
        var records = await db.SessionRecords.AsNoTracking().Where(r => r.SessionId == sessionId).OrderBy(r => r.RecordIndex)
            .Select(r => new { r.RecordIndex, r.RedactedRecord, r.PurgeAfter }).ToListAsync(ct);
        return new(new(s.Id, s.Client, s.ExternalSessionId, s.RepoRemote, s.AckedIndex, s.CreatedAt),
            [.. records.Select(r => new SessionRecordView(r.RecordIndex, JsonDocument.Parse(r.RedactedRecord).RootElement.Clone(), r.PurgeAfter))]);
    }

    private Task<bool> PausedAsync(Actor a, CancellationToken ct) => db.CaptureSettings.AnyAsync(c => c.UserId == a.UserId && c.Paused, ct);

    private async Task<Guid?> FindAsync(Actor a, string client, string externalId, CancellationToken ct) =>
        await db.AgentSessions.Where(s => s.UserId == a.UserId && s.Client == client && s.ExternalSessionId == externalId)
            .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct);

    private static void Validate(CommitInput input)
    {
        if (string.IsNullOrWhiteSpace(input.CommitId) || input.CommitId.Length > 100) throw new AtharError("invalid_input", "commit_id is required (at most 100 characters).");
        if (input.FromIndex < 0 || input.ToIndex < input.FromIndex) throw new AtharError("invalid_input", "Need 0 <= from_index <= to_index.");
        if (input.Skipped < 0) throw new AtharError("invalid_input", "skipped can't be negative.");
        if (input.Records.Any(r => r.Index < input.FromIndex || r.Index > input.ToIndex)) throw new AtharError("invalid_input", "Every record index must be within from_index..to_index.");
        if (input.Records.Select(r => r.Index).Distinct().Count() != input.Records.Count) throw new AtharError("invalid_input", "Record indexes must be unique.");
        if (input.Records.Any(r => r.Data.ValueKind != JsonValueKind.Object)) throw new AtharError("invalid_input", "Each record's data must be a JSON object.");
        if (input.Records.Sum(r => Encoding.UTF8.GetByteCount(r.Data.GetRawText())) > MaxRecordsBytes)
            throw new AtharError("invalid_input", "records exceed 2 MB; split the commit.");
    }

    /// <summary>Every entry point: check the scope and scope the unit of work to the caller's org.</summary>
    private void Begin(Actor a, string scope)
    {
        if (!a.Scopes.Contains(scope)) throw new AtharError("forbidden", $"The token lacks the '{scope}' scope.");
        db.CurrentOrgId = a.OrgId;
    }
}
