using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athar.Domain.Model;

namespace Athar.Data.Seed;

/// <summary>
/// T020: the <c>qa</c> content (docs/04 §4.3). Every state a test or a QA walkthrough needs, with fixed IDs.
/// Direct shares here are the everyday cases; the access edge cases (non-member share, child not covered)
/// are in <c>QaSeed.Access.cs</c>.
/// </summary>
public static partial class QaSeed
{
    // Teams. Member and Admin are in Platform; Platform holds the edit restriction on the incident subtree.
    /// <summary>T064: the one space whose AI policy lets agents delete to trash (Member edits it); EngWiki allows writes only.</summary>
    public static readonly Guid AgentSandbox = Id(0x3a1);
    /// <summary>Member's agent token with <c>pages:delete</c> (AT-33).</summary>
    public const string AgentPat = "athar_pat_qa_member_agent";
    public static readonly Guid AgentPatId = Id(0x3a2);
    /// <summary>T062: Admin's trusted-local token, which may read the confidential space (promote keeps its classification).</summary>
    public const string AdminTrustedPat = "athar_pat_qa_admin_trusted";
    public static readonly Guid AdminTrustedPatId = Id(0x3a3);

    public static readonly Guid PlatformTeam = Id(0x551);
    public static readonly Guid DataTeam = Id(0x552);

    // Projects. PilotProject (QaSeed.cs, T017: Admin and Member) has the repo link; this adds EngWiki; KnowledgeOwner is in Billing.
    public static readonly Guid BillingProject = Id(0x602);

    // Pages. EngWiki: Home > (DeployRunbook, IncidentReviews > OutageReview, ApiReference, Trashed).
    public static readonly Guid HomePage = Id(0x701);
    public static readonly Guid DeployRunbookPage = Id(0x702);
    /// <summary>Restricted subtree root: view for Admin, edit for PlatformTeam. Its child inherits the restriction.</summary>
    public static readonly Guid IncidentReviewsPage = Id(0x703);
    public static readonly Guid OutageReviewPage = Id(0x704);
    /// <summary>Mixed Arabic and English.</summary>
    public static readonly Guid ApiReferencePage = Id(0x705);
    public static readonly Guid TrashedPage = Id(0x706);
    /// <summary>Arabic, in OpsWiki.</summary>
    public static readonly Guid OnCallPage = Id(0x707);
    public static readonly Guid HandbookPage = Id(0x708);
    /// <summary>In the confidential space.</summary>
    public static readonly Guid CompensationPage = Id(0x709);
    public static readonly Guid OtherOrgPage = Id(0x7f1);

    // Comment threads on DeployRunbook, one per status.
    public static readonly Guid ThreadPendingAnchor = Id(0x801);
    public static readonly Guid ThreadOpen = Id(0x802);
    public static readonly Guid ThreadResolved = Id(0x803);
    public static readonly Guid ThreadOrphaned = Id(0x804);
    public static readonly Guid ThreadAnchorFailed = Id(0x805);

    // Share links. The plaintext tokens are public on purpose (local test profile); only hashes are stored.
    public const string ShareActiveToken = "athar_share_qa_active";
    public const string ShareSpaceToken = "athar_share_qa_space";
    public const string ShareExpiredToken = "athar_share_qa_expired";
    public const string ShareRevokedToken = "athar_share_qa_revoked";
    public static readonly Guid ShareActive = Id(0xf01);
    public static readonly Guid ShareSpace = Id(0xf02);
    public static readonly Guid ShareExpired = Id(0xf03);
    public static readonly Guid ShareRevoked = Id(0xf04);
    /// <summary>Opens with <see cref="Password"/> (same deterministic hash as the qa users).</summary>
    public const string SharePasswordToken = "athar_share_qa_password";
    public static readonly Guid SharePassword = Id(0xf05);

    // Direct page shares (FR-AC-01): Admin (Operations member, no OpsWiki role) can comment on the on-call page;
    // the Data team can edit the deploy runbook, so KnowledgeOwner (EngWiki commenter) edits that page only.
    public static readonly Guid PageShareOnCallAdmin = Id(0xf11);
    public static readonly Guid PageShareRunbookData = Id(0xf12);

    // Memories: every scope and status, a non-overridable policy, an overridable convention with a project shadow.
    public static readonly Guid MemoryPersonal = Id(0xa01);
    public static readonly Guid MemoryTeam = Id(0xa02);
    public static readonly Guid MemoryProject = Id(0xa03);
    public static readonly Guid MemoryCompanyFact = Id(0xa04);
    /// <summary>type=policy, non-overridable (FR-MM-03).</summary>
    public static readonly Guid MemoryCompanyPolicy = Id(0xa05);
    /// <summary>Overridable, revision 2; shadowed in PilotProject by <see cref="MemoryShadowOverride"/>.</summary>
    public static readonly Guid MemoryCompanyConvention = Id(0xa06);
    public static readonly Guid MemoryShadowOverride = Id(0xa07);
    public static readonly Guid MemoryArchived = Id(0xa08);
    public static readonly Guid MemorySuperseded = Id(0xa09);
    public static readonly Guid MemoryConfidential = Id(0xa0a);
    public static readonly Guid MemoryArabic = Id(0xa0b);
    /// <summary>Contradicts <see cref="MemoryCompanyFact"/>; the pair is <see cref="ConflictOpen"/>.</summary>
    public static readonly Guid MemoryConflicting = Id(0xa0c);
    /// <summary>A long Markdown body (headings, table, code, long lines) for how the detail page shows a big memory.</summary>
    public static readonly Guid MemoryLong = Id(0xa0d);
    public static readonly Guid ShadowAthar = Id(0xa21);
    public static readonly Guid ConflictOpen = Id(0xa31);

    // Proposals, one per status (and every kind).
    public static readonly Guid ProposalPending = Id(0xb01);
    public static readonly Guid ProposalApproved = Id(0xb02);
    public static readonly Guid ProposalRejected = Id(0xb03);
    /// <summary>A replace whose base moved (the convention is now revision 2).</summary>
    public static readonly Guid ProposalNeedsRereview = Id(0xb04);
    public static readonly Guid ProposalEvidence = Id(0xb05);
    /// <summary>T141: a member's pending company skill (a SKILL.md package) awaiting a knowledge owner.</summary>
    public static readonly Guid ProposalSkill = Id(0xb10);

    // A committed capture session of Member, with one candidate per status.
    public static readonly Guid Session = Id(0xc01);
    /// <summary>A chat Member saved from claude.ai with save_chat (FR-AG-09).</summary>
    public static readonly Guid ChatSession = Id(0xc02);
    /// <summary>An ordinary member whose sessions the AT-53 test creates and who it then deactivates.</summary>
    public static readonly Guid CaptureUser = Id(0xc21);
    public static readonly Guid CaptureUserPatId = Id(0xc22);
    public const string CaptureUserPat = "athar_pat_qa_capture_user";
    public const string SessionExternalId = "qa-session-1";
    public static readonly Guid CandidateNew = Id(0xc11);
    public static readonly Guid CandidateAccepted = Id(0xc12);
    public static readonly Guid CandidateEdited = Id(0xc13);
    public static readonly Guid CandidateDiscarded = Id(0xc14);
    /// <summary>Suggests company scope: accepting it must create a proposal (FR-MM-06).</summary>
    public static readonly Guid CandidateCompany = Id(0xc15);
    /// <summary>Auto-accepted into <see cref="MemoryPersonal"/> (FR-MM-06 amended); Undo archives that memory.</summary>
    public static readonly Guid CandidateAutoAccepted = Id(0xc16);

    // GitHub source fixture (no PAT: the sync job pauses it as paused_auth until one is set).
    public static readonly Guid Source = Id(0xd01);
    public const string SourceRepo = "abushanab-net/athar-qa-fixture";
    public const string SourceCommit = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    public static readonly Guid SourceReadme = Id(0xd11);
    public static readonly Guid SourceAdr = Id(0xd12);
    public static readonly Guid SourceTombstoned = Id(0xd13);

    public static readonly Guid SkillPersonal = Id(0xe01);
    public static readonly Guid SkillProject = Id(0xe02);
    public static readonly Guid SkillCompany = Id(0xe03);
    public static readonly Guid SkillArchived = Id(0xe04);

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static void AddContent(AtharDbContext db)
    {
        var n = 0x2000; // rows tests don't refer to

        // Teams and projects
        db.Spaces.Add(new Space { Id = AgentSandbox, OrgId = Org, WorkspaceId = Engineering, Name = "Agent sandbox", Slug = "agent-sandbox", AiPolicy = new(Read: true, Write: true, DeleteToTrash: true) });
        db.SpaceMembers.Add(new SpaceMember { Id = Id(n++), OrgId = Org, SpaceId = AgentSandbox, UserId = Member, Role = SpaceRole.Editor });
        db.AccessTokens.Add(new AccessToken { Id = AgentPatId, OrgId = Org, UserId = Member, Name = AgentPat, Scopes = ["read", "write", "pages:delete"], TokenHash = Hash(AgentPat) });
        db.AccessTokens.Add(new AccessToken
        {
            Id = AdminTrustedPatId, OrgId = Org, UserId = Admin, Name = AdminTrustedPat, Scopes = ["read", "memory:write"], TokenHash = Hash(AdminTrustedPat),
            DisclosureClass = DisclosureClass.TrustedLocal, ClassifiedBy = Owner, ClassificationReason = "qa: trusted local agent",
        });

        db.Teams.AddRange(
            new Team { Id = PlatformTeam, OrgId = Org, Name = "Platform", LeadUserId = Admin },
            new Team { Id = DataTeam, OrgId = Org, Name = "Data", LeadUserId = KnowledgeOwner });
        foreach (var (team, user) in new[] { (PlatformTeam, Admin), (PlatformTeam, Member), (DataTeam, KnowledgeOwner) })
            db.TeamMembers.Add(new TeamMember { Id = Id(n++), OrgId = Org, TeamId = team, UserId = user });
        db.SpaceMembers.Add(new SpaceMember { Id = Id(n++), OrgId = Org, SpaceId = OpsHandbook, TeamId = PlatformTeam, Role = SpaceRole.Viewer });

        db.Projects.Add(new Project { Id = BillingProject, OrgId = Org, Name = "Billing", Slug = "billing" });
        db.ProjectMembers.Add(new ProjectMember { Id = Id(n++), OrgId = Org, ProjectId = BillingProject, UserId = KnowledgeOwner, Role = ProjectRole.Admin });
        db.ProjectLinks.AddRange(
            new ProjectLink { Id = Id(n++), OrgId = Org, ProjectId = PilotProject, SpaceId = EngWiki },
            new ProjectLink { Id = Id(n++), OrgId = Org, ProjectId = BillingProject, TeamId = DataTeam });

        // Pages: English, Arabic, mixed, a restricted subtree, a trashed page, the confidential space
        void Page(Guid id, Guid org, Guid space, Guid? parent, double position, string title, string text, bool trashed = false)
        {
            var json = JsonSerializer.Serialize(new
            {
                type = "doc",
                content = text.Split("\n\n").Select(p => new { type = "paragraph", content = new[] { new { type = "text", text = p } } }),
            });
            db.Pages.Add(new Page
            {
                Id = id, OrgId = org, SpaceId = space, ParentId = parent, Position = position, Title = title,
                // Relative to Seeder.Today: the worker's 30-day trash purge (T087) compares with the real clock.
                ContentJson = json, ContentMd = text, Revision = 1, DeletedAt = trashed ? Seeder.Today.AddDays(-1) : null,
            });
            db.PageRevisions.Add(new PageRevision { Id = Id(n++), OrgId = org, PageId = id, Revision = 1, Contributors = org == Org ? [Admin] : [OtherOrgOwner] });
            // FTS chunks as the worker's embed-page job builds them, so seeded pages are searchable without an edit first (T047).
            // Like the memories below, no vectors: search ranks them by keywords until the page is next saved.
            db.Chunks.AddRange(Athar.Domain.Memory.Chunker.Build(ChunkOwner.Page, id, 1, title, text, c =>
            {
                c.Id = Id(n++);
                c.OrgId = org;
                c.SpaceId = space;
                c.IsConfidential = db.Spaces.Local.Single(s => s.Id == space).IsConfidential;
            }));
        }
        Page(HomePage, Org, EngWiki, null, 1, "Engineering home", "Start here. The deploy runbook and the incident reviews live below.");
        Page(DeployRunbookPage, Org, EngWiki, HomePage, 1, "Deploy runbook", "Deploys run from develop through CI.\n\nRoll back by redeploying the previous tag.");
        Page(IncidentReviewsPage, Org, EngWiki, HomePage, 2, "Incident reviews", "Reviews of production incidents. Restricted.");
        Page(OutageReviewPage, Org, EngWiki, IncidentReviewsPage, 1, "2026-09 disk outage review", "The disk filled with logs. We added an alert at 80%.");
        Page(ApiReferencePage, Org, EngWiki, HomePage, 3, "مرجع الـ API reference", "كل طلب يحمل Authorization: Bearer token.\n\nEvery request carries a bearer token; الأخطاء تتبع docs/06.");
        Page(TrashedPage, Org, EngWiki, HomePage, 4, "Old onboarding notes", "Superseded by the handbook.", trashed: true);
        Page(OnCallPage, Org, OpsWiki, null, 1, "إجراءات المناوبة", "يتسلم المناوب التنبيهات خارج أوقات العمل.\n\nيصعّد إلى قائد الفريق بعد ثلاثين دقيقة.");
        Page(HandbookPage, Org, OpsHandbook, null, 1, "Operations handbook", "Shifts, escalation and leave.");
        // T144: "Getting started with agents" in English and Arabic, beside the on-call page.
        var gs = 0xc40;
        GettingStarted.AddPages(db, Org, OpsWiki, GettingStarted.QaPages, () => Id(gs++), Owner);
        Page(CompensationPage, Org, EngConfidential, null, 1, "Compensation bands", "Confidential: salary bands per level.");
        Page(OtherOrgPage, OtherOrg, OtherOrgSpace, null, 1, "Other org home", "Not visible from Athar QA.");

        db.PageRestrictions.AddRange(
            new PageRestriction { Id = Id(n++), OrgId = Org, PageId = IncidentReviewsPage, UserId = Admin, Level = RestrictionLevel.View },
            new PageRestriction { Id = Id(n++), OrgId = Org, PageId = IncidentReviewsPage, TeamId = PlatformTeam, Level = RestrictionLevel.Edit });
        db.PageVersions.Add(new PageVersion
        {
            Id = Id(n++), OrgId = Org, PageId = DeployRunbookPage, Revision = 1, ContentMd = "Deploys run from develop through CI.",
            Reason = VersionReason.Manual, CreatedBy = Admin,
        });

        // Comment threads, one per status, with a mention and its notification
        void Thread(Guid id, ThreadStatus status, string? anchor, string? quote, Guid author, string body, Guid? resolvedBy = null)
        {
            db.CommentThreads.Add(new CommentThread { Id = id, OrgId = Org, PageId = DeployRunbookPage, AnchorId = anchor, Quote = quote, Status = status, ResolvedBy = resolvedBy });
            db.Comments.Add(new Comment { Id = Id(n++), OrgId = Org, ThreadId = id, AuthorId = author, BodyMd = body });
        }
        Thread(ThreadPendingAnchor, ThreadStatus.PendingAnchor, "qa-anchor-pending", "previous tag", Member, "Which tag exactly?");
        Thread(ThreadOpen, ThreadStatus.Open, "qa-anchor-open", "through CI", Member, "@Qa Admin should this mention the approval step?");
        Thread(ThreadResolved, ThreadStatus.Resolved, "qa-anchor-resolved", "Deploys run", Admin, "Fixed the wording.", resolvedBy: Admin);
        Thread(ThreadOrphaned, ThreadStatus.Orphaned, "qa-anchor-orphaned", "manual deploy", KnowledgeOwner, "This section was deleted.");
        Thread(ThreadAnchorFailed, ThreadStatus.AnchorFailed, "qa-anchor-failed", "Roll back", Member, "تم حذف النص المقتبس.");
        db.Comments.Add(new Comment { Id = Id(n++), OrgId = Org, ThreadId = ThreadOpen, AuthorId = Admin, BodyMd = "Yes, adding it.", EditedAt = Seeder.At });
        db.Mentions.Add(new Mention { Id = Id(n++), OrgId = Org, SourceKind = MentionSource.Comment, SourceId = ThreadOpen, UserId = Admin });
        db.Notifications.Add(new Notification { Id = Id(n++), OrgId = Org, UserId = Admin, Kind = "mention", Ref = $"thread:{ThreadOpen}" });

        // Share links: active page, space with descendants, expired, revoked
        void Share(Guid id, string token, Guid? page = null, Guid? space = null, DateTimeOffset? expires = null, DateTimeOffset? revoked = null, string? passwordHash = null) =>
            db.ShareLinks.Add(new ShareLink
            {
                Id = id, OrgId = Org, PageId = page, SpaceId = space, IncludeDescendants = space is not null,
                TokenHash = Hash(token), PasswordHash = passwordHash, ExpiresAt = expires, RevokedAt = revoked, CreatedBy = Admin,
            });
        Share(ShareActive, ShareActiveToken, page: DeployRunbookPage);
        Share(ShareSpace, ShareSpaceToken, space: OpsHandbook);
        Share(ShareExpired, ShareExpiredToken, page: HomePage, expires: Seeder.At.AddDays(-1));
        Share(ShareRevoked, ShareRevokedToken, page: HomePage, revoked: Seeder.At);
        Share(SharePassword, SharePasswordToken, page: HandbookPage, passwordHash: PasswordHash.Value);

        db.PageShares.AddRange(
            new PageShare { Id = PageShareOnCallAdmin, OrgId = Org, PageId = OnCallPage, UserId = Admin, Level = ShareLevel.Comment, CreatedBy = Owner },
            new PageShare { Id = PageShareRunbookData, OrgId = Org, PageId = DeployRunbookPage, TeamId = DataTeam, Level = ShareLevel.Edit, CreatedBy = Admin });

        // Memories. Like MemoryService: a version per revision and one FTS chunk of the current text.
        void Memory(Guid id, Scope scope, Guid? owner, MemoryType type, string title, string body, Guid by,
            MemoryStatus status = MemoryStatus.Active, bool confidential = false, int revision = 1, string origin = "seed")
        {
            var m = new Memory
            {
                Id = id, OrgId = Org, Scope = scope, Type = type, Title = title, BodyMd = body, Status = status,
                OwnerUserId = scope == Scope.Personal ? owner : null,
                OwnerTeamId = scope == Scope.Team ? owner : null,
                OwnerProjectId = scope == Scope.Project ? owner : null,
                Overridable = type != MemoryType.Policy, Origin = origin, CreatedByUser = by, Revision = revision, IsConfidential = confidential,
            };
            db.Memories.Add(m);
            for (var r = 1; r <= revision; r++)
                db.MemoryVersions.Add(new MemoryVersion
                {
                    Id = Id(n++), OrgId = Org, MemoryId = id, Revision = r, ChangedBy = by,
                    Title = title, BodyMd = r == revision ? body : $"{body} (revision {r})",
                });
            var text = title + "\n\n" + body;
            db.Chunks.Add(new Chunk
            {
                Id = Id(n++), OrgId = Org, OwnerKind = ChunkOwner.Memory, OwnerId = id, OwnerRevision = revision,
                Body = text, SearchText = text, ContentHash = Hash(text),
                Scope = scope, OwnerUserId = m.OwnerUserId, OwnerTeamId = m.OwnerTeamId, OwnerProjectId = m.OwnerProjectId, IsConfidential = confidential,
            });
        }
        Memory(MemoryPersonal, Scope.Personal, Member, MemoryType.Preference, "I prefer small PRs", "One PR per task, against develop.", Member);
        Memory(MemoryTeam, Scope.Team, PlatformTeam, MemoryType.Procedure, "Platform on-call handover", "Hand over open incidents in the channel before 09:00.", Admin);
        Memory(MemoryProject, Scope.Project, PilotProject, MemoryType.Decision, "Athar uses MudBlazor", "MudBlazor (MIT) over Fluent UI for the larger community.", Admin);
        Memory(MemoryCompanyFact, Scope.Company, null, MemoryType.Fact, "Session transcripts are kept 30 days", "Transcripts are deleted 30 days after receipt.", KnowledgeOwner);
        Memory(MemoryCompanyPolicy, Scope.Company, null, MemoryType.Policy, "PATs expire after 90 days", "Personal access tokens expire after 90 days by default. Security policy.", Owner);
        Memory(MemoryCompanyConvention, Scope.Company, null, MemoryType.Convention, "Use pnpm for Node packages", "Node projects use pnpm.", KnowledgeOwner, revision: 2);
        Memory(MemoryShadowOverride, Scope.Project, PilotProject, MemoryType.Convention, "Use npm for Node packages", "Athar's collab and editor packages use npm (Aspire runs npm install).", Admin);
        Memory(MemoryArchived, Scope.Company, null, MemoryType.Glossary, "WP means work package", "A WP is a planned slice of lane work.", KnowledgeOwner, status: MemoryStatus.Archived);
        Memory(MemorySuperseded, Scope.Company, null, MemoryType.Convention, "Use yarn for Node packages", "Node projects use yarn.", KnowledgeOwner, status: MemoryStatus.Superseded);
        Memory(MemoryConfidential, Scope.Company, null, MemoryType.Fact, "Salary review is in March", "Compensation bands are reviewed every March.", Owner, confidential: true);
        Memory(MemoryArabic, Scope.Company, null, MemoryType.Procedure, "التصعيد بعد ثلاثين دقيقة", "يصعّد المناوب إلى قائد الفريق إذا لم يُحل التنبيه خلال ثلاثين دقيقة.", KnowledgeOwner);
        Memory(MemoryLong, Scope.Team, PlatformTeam, MemoryType.Procedure, "Incident severity runbook", """
# Incident severity runbook

How the platform team grades an incident and who acts on each grade.

## Grades

| Grade | Example | First response | Updates |
|---|---|---|---|
| SEV1 | The whole wiki is down for everyone | 5 minutes | Every 15 minutes in the incident channel |
| SEV2 | Search or recall fails for one workspace | 15 minutes | Every 30 minutes |
| SEV3 | One page renders badly | Next working day | When fixed |

## First fifteen minutes

1. Acknowledge the alert so the next person on the rota isn't paged.
2. Open an incident channel named `inc-<date>-<short-name>`.
3. Write one line in it: what is broken, for whom, since when.
4. Decide the grade. If unsure, pick the higher one; lowering it later is cheap.

> A wrong guess at the grade costs less than a late one.

## Checks

- [x] Health endpoint: `curl -fsS https://athar.example.internal/health`
- [ ] Database connections below the pool limit
- [ ] Worker queue draining

```bash
docker compose -p athar ps
docker compose -p athar logs --since 15m web worker | grep -iE "error|fail" | tail -n 50
```

## Roles

### Incident lead

Owns the decision to roll back, keeps the timeline and calls the all-clear. Doesn't debug.

### Investigator

Finds the cause. Writes every command and its result in the channel, so the next shift can pick up without asking.

### Communicator

Posts the status updates on the grade's schedule, even when the update is "no change".

## After the incident

Write the review within five working days: timeline, cause, what went well, and at most three actions, each with an owner and a date. A very long reference line follows so wrapping is tested: https://athar.example.internal/wiki/p/00000000-0000-0000-0000-000000000000/incident-reviews/2026/a-deliberately-long-path-segment-that-has-no-spaces-at-all

## Contacts

| Who | When |
|---|---|
| Platform on-call | Always first |
| Data team lead | Anything touching backups or restores |
| Security | Any suspected leak of confidential content |
""", Admin);
        Memory(MemoryConflicting, Scope.Company, null, MemoryType.Fact, "Session transcripts are kept 90 days", "Transcripts are deleted 90 days after receipt.", Admin);

        db.MemoryShadows.Add(new MemoryShadow { Id = ShadowAthar, OrgId = Org, ProjectId = PilotProject, CompanyMemoryId = MemoryCompanyConvention, ShadowMemoryId = MemoryShadowOverride, BaseRevision = 2 });
        db.MemoryConflicts.Add(new MemoryConflict { Id = ConflictOpen, OrgId = Org, MemoryA = MemoryCompanyFact, MemoryB = MemoryConflicting, OpenedBy = KnowledgeOwner, Status = ConflictStatus.Open });
        db.MemoryLinks.Add(new MemoryLink { Id = Id(n++), OrgId = Org, MemoryId = MemoryProject, TargetKind = "page", TargetId = ApiReferencePage.ToString() });

        // Proposals: every status and kind
        string Payload(string title, string body, MemoryType type) => JsonSerializer.Serialize(new { title, body, type = type.ToString().ToLowerInvariant() });
        db.MemoryProposals.AddRange(
            new MemoryProposal { Id = ProposalPending, OrgId = Org, Kind = ProposalKind.New, ProposedBy = Member, Status = ProposalStatus.Pending, Payload = Payload("Releases are tagged vX.Y.Z", "Tag every release vX.Y.Z on main.", MemoryType.Convention) },
            new MemoryProposal { Id = ProposalApproved, OrgId = Org, Kind = ProposalKind.New, ProposedBy = Member, Status = ProposalStatus.Approved, ReviewerId = KnowledgeOwner, DecidedAt = Seeder.At, Payload = Payload("Session transcripts are kept 30 days", "Transcripts are deleted 30 days after receipt.", MemoryType.Fact) },
            new MemoryProposal { Id = ProposalRejected, OrgId = Org, Kind = ProposalKind.New, ProposedBy = Guest, Status = ProposalStatus.Rejected, ReviewerId = KnowledgeOwner, DecidedAt = Seeder.At, Note = "Not a company rule.", Payload = Payload("Fridays are deploy-free", "No deploys on Fridays.", MemoryType.Policy) },
            new MemoryProposal { Id = ProposalNeedsRereview, OrgId = Org, Kind = ProposalKind.Replace, TargetMemoryId = MemoryCompanyConvention, BaseRevision = 1, ProposedBy = Member, Status = ProposalStatus.NeedsRereview, Payload = Payload("Use pnpm 9 for Node packages", "Node projects use pnpm 9.", MemoryType.Convention) },
            new MemoryProposal { Id = ProposalEvidence, OrgId = Org, Kind = ProposalKind.Evidence, TargetMemoryId = MemoryCompanyFact, BaseRevision = 1, ProposedBy = Member, Status = ProposalStatus.Pending, Payload = Payload("Session transcripts are kept 30 days", "Transcripts are deleted 30 days after receipt.", MemoryType.Fact), Evidence = JsonSerializer.Serialize(new[] { new { kind = "page", id = DeployRunbookPage } }) },
            // The payload is SkillService.SkillProposal in the MCP JSON shape (snake_case).
            new MemoryProposal { Id = ProposalSkill, OrgId = Org, Kind = ProposalKind.Skill, ProposedBy = Member, Status = ProposalStatus.Pending,
                Payload = JsonSerializer.Serialize(new { name = "release-notes", description = "How we write release notes.",
                    body_md = "# release-notes\n\nList user-visible changes first, then fixes.", files = new[] { new { path = "template.md", content = "## Changes\n\n## Fixes" } } }) });

        // Capture: a committed session with redacted records and one candidate per status
        db.AgentSessions.Add(new AgentSession
        {
            Id = Session, OrgId = Org, UserId = Member, CredentialId = MemberPatId, Client = "claude-code", ExternalSessionId = SessionExternalId,
            RepoRemote = PilotRemote, ProjectId = PilotProject, AckedIndex = 2, ExtractedUpto = 2,
        });
        db.SessionCommits.Add(new SessionCommit { Id = Id(n++), OrgId = Org, SessionId = Session, CommitId = "qa-commit-1", FromIndex = 0, ToIndex = 2, ReceivedAt = Seeder.At });
        string[] records = ["we always use dotnet ef migrations add, never hand-written SQL", "token=[REDACTED] for the deploy", "قررنا أن تكون مدة الاحتفاظ ثلاثين يوماً"];
        for (var i = 0; i < records.Length; i++)
            db.SessionRecords.Add(new SessionRecord
            {
                Id = Id(n++), OrgId = Org, SessionId = Session, RecordIndex = i, PurgeAfter = Seeder.Today + SessionRetention.Keep,
                RedactedRecord = JsonSerializer.Serialize(new { role = i == 1 ? "assistant" : "user", text = records[i] }),
            });
        void Candidate(Guid id, CandidateStatus status, Scope scope, Guid? owner, MemoryType type, string title, string evidence) =>
            db.CaptureCandidates.Add(new CaptureCandidate
            {
                Id = id, OrgId = Org, SessionId = Session, OwnerUserId = Member, SuggestedScope = scope, SuggestedOwner = owner,
                Type = type, Title = title, BodyMd = title + ".", EvidenceExcerpt = evidence, Status = status,
            });
        Candidate(CandidateNew, CandidateStatus.New, Scope.Project, PilotProject, MemoryType.Convention, "Migrations only through dotnet ef", records[0]);
        Candidate(CandidateAccepted, CandidateStatus.Accepted, Scope.Personal, Member, MemoryType.Preference, "I review my own diffs before a PR", records[0]);
        Candidate(CandidateEdited, CandidateStatus.Edited, Scope.Team, PlatformTeam, MemoryType.Procedure, "Redact tokens before sharing logs", records[1]);
        Candidate(CandidateDiscarded, CandidateStatus.Discarded, Scope.Personal, Member, MemoryType.Fact, "The deploy uses a token", records[1]);
        Candidate(CandidateCompany, CandidateStatus.New, Scope.Company, null, MemoryType.Policy, "مدة الاحتفاظ بالجلسات ثلاثون يوماً", records[2]);
        Candidate(CandidateAutoAccepted, CandidateStatus.AutoAccepted, Scope.Personal, Member, MemoryType.Preference, "I prefer small PRs", records[0]);
        db.CaptureCandidates.Local.Single(c => c.Id == CandidateAutoAccepted).MemoryId = MemoryPersonal;

        // A chat saved from claude.ai: a session with no transcript records, grouped under the pilot project
        db.AgentSessions.Add(new AgentSession
        {
            Id = ChatSession, OrgId = Org, UserId = Member, Client = "claude-ai", ExternalSessionId = "qa-chat-1", ProjectId = PilotProject,
            Title = "Release checklist", Source = SessionSource.Chat,
        });

        // GitHub source fixture
        db.KnowledgeSources.Add(new KnowledgeSource { Id = Source, OrgId = Org, ProjectId = PilotProject, Kind = SourceKind.Github, Repo = SourceRepo, LastCommitSha = SourceCommit, Status = SourceStatus.Ok });
        void Doc(Guid id, string path, string title, string md, bool tombstoned = false) => db.SourceDocuments.Add(new SourceDocument
        {
            Id = id, OrgId = Org, SourceId = Source, Path = path, Title = title, ContentMd = md, ContentHash = Hash(md),
            CommitSha = SourceCommit, TombstonedAt = tombstoned ? Seeder.At : null,
        });
        Doc(SourceReadme, "README.md", "Athar QA fixture", "# Athar QA fixture\n\nA repository used by the GitHub sync tests.");
        Doc(SourceAdr, "docs/adr/0001-postgres.md", "ADR 1: one Postgres", "# ADR 1\n\nAll data lives in one Postgres database.");
        Doc(SourceTombstoned, "docs/old.md", "Old notes", "# Old notes", tombstoned: true);

        // Skills: personal, project, company, archived
        void Skill(Guid id, Scope scope, Guid? owner, string name, string description, SkillStatus status = SkillStatus.Active) => db.Skills.Add(new Skill
        {
            Id = id, OrgId = Org, Scope = scope, Name = name, Description = description, BodyMd = $"# {name}\n\n{description}", Status = status,
            OwnerUserId = scope == Scope.Personal ? owner : null, OwnerProjectId = scope == Scope.Project ? owner : null,
            Files = JsonSerializer.Serialize(new[] { new { path = "reference.md", content = $"Reference notes for {name}." } }),
        });
        Skill(SkillPersonal, Scope.Personal, Member, "my-review-checklist", "My checklist before opening a PR.");
        Skill(SkillProject, Scope.Project, PilotProject, "athar-workflow", "How an agent picks up and finishes a task in Athar.");
        db.SkillVersions.Add(new SkillVersion
        {
            Id = Id(n++), OrgId = Org, SkillId = SkillProject, Revision = 1, PublishedBy = Member, Description = "How an agent picks up and finishes a task in Athar.",
            BodyMd = "# athar-workflow\n\nHow an agent picks up and finishes a task in Athar.",
        });
        Skill(SkillCompany, Scope.Company, null, "incident-review", "How to write an incident review.");
        Skill(SkillArchived, Scope.Company, null, "old-deploy", "The deploy steps before CI.", SkillStatus.Archived);
    }
}
