using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Athar.Domain.Model;

namespace Athar.Data.Seed;

/// <summary>
/// The <c>qa</c> profile: fixed-ID fixtures for integration tests, Playwright and QA (docs/04 §4.3).
/// This file: organizations, one user per role, workspaces, spaces, PATs and the AT-07 canary.
/// <c>QaSeed.Content.cs</c> (T020): teams, projects, pages, threads, share links, memories, proposals,
/// a capture session, a GitHub source and skills. Tests refer to rows by these IDs.
/// </summary>
public static partial class QaSeed
{
    private static Guid Id(int n) => new($"00000000-0000-7000-8000-{n:x12}");

    // Organizations. OtherOrg exists for cross-org tests (AT-07).
    public static readonly Guid Org = Id(0x001);
    public static readonly Guid OtherOrg = Id(0x002);

    // Users in Org, one per role. Deactivated and Guest are members.
    public static readonly Guid Owner = Id(0x101);
    public static readonly Guid Admin = Id(0x102);
    public static readonly Guid Member = Id(0x103);
    public static readonly Guid KnowledgeOwner = Id(0x104);
    public static readonly Guid Guest = Id(0x105);
    public static readonly Guid Deactivated = Id(0x106);
    public static readonly Guid OtherOrgOwner = Id(0x1f1);
    /// <summary>AT-02: password reset revokes this user's sessions and <see cref="ResetUserPat"/>.</summary>
    public static readonly Guid ResetUser = Id(0x107);
    /// <summary>AT-02: locked out after 5 failed sign-ins.</summary>
    public static readonly Guid LockoutUser = Id(0x108);

    /// <summary>AT-03: an org with exactly two owners, for the last-owner rule under concurrency.</summary>
    public static readonly Guid LastOwnerOrg = Id(0x003);
    public static readonly Guid OwnerA = Id(0x1e1);
    public static readonly Guid OwnerB = Id(0x1e2);

    /// <summary>Every qa user signs in with this password. Public on purpose: qa is a local test profile.</summary>
    public const string Password = "Qa-password-1!";
    public static string Email(string displayName) => $"{displayName.ToLowerInvariant().Replace(' ', '.')}@qa.athar.local";

    /// <summary>AT-02: an invitation that expired before the seed date.</summary>
    public const string ExpiredInviteToken = "qa_invite_expired";
    public static readonly Guid ExpiredInvite = Id(0x601);

    public static readonly Guid Engineering = Id(0x201);
    public static readonly Guid Operations = Id(0x202);
    public static readonly Guid OtherOrgWorkspace = Id(0x2f1);

    public static readonly Guid EngWiki = Id(0x301);
    /// <summary>The confidential space (FR-CL-01).</summary>
    public static readonly Guid EngConfidential = Id(0x302);
    public static readonly Guid OpsWiki = Id(0x303);
    public static readonly Guid OpsHandbook = Id(0x304);
    public static readonly Guid OtherOrgSpace = Id(0x3f1);

    // AT-07 canary: the same text and vector in both orgs, so a search that misses the org predicate returns
    // the OtherOrg row. Owner IDs are placeholders until T020 seeds pages (chunks.owner_id has no FK).
    public const string CanaryText = "Canary runbook: rotate the deploy key every quarter.";
    public static readonly Guid CanaryChunk = Id(0x901);
    public static readonly Guid CanaryEmbedding = Id(0x902);
    public static readonly Guid OtherOrgCanaryChunk = Id(0x9f1);
    public static readonly Guid OtherOrgCanaryEmbedding = Id(0x9f2);
    public static float[] CanaryVector() { var v = new float[1024]; v[0] = 1; return v; }
    // PATs (T005). The plaintext is public on purpose: qa is a local test profile. Only the SHA-256 hash is stored.
    public const string MemberPat = "athar_pat_qa_member";
    public const string MemberReadPat = "athar_pat_qa_member_read";
    public const string KnowledgeOwnerPat = "athar_pat_qa_knowledge_owner";
    public const string DeactivatedPat = "athar_pat_qa_deactivated";
    public static readonly Guid MemberPatId = Id(0x401);
    public static readonly Guid MemberReadPatId = Id(0x402);
    public static readonly Guid KnowledgeOwnerPatId = Id(0x403);
    public static readonly Guid DeactivatedPatId = Id(0x404);
    public const string AdminPat = "athar_pat_qa_admin";
    public static readonly Guid AdminPatId = Id(0x405);
    /// <summary>The member's trusted-local token (FR-ID-06): sees confidential items the member may read. Plain <see cref="MemberPat"/> is external-agent.</summary>
    public const string MemberTrustedPat = "athar_pat_qa_member_trusted";
    public static readonly Guid MemberTrustedPatId = Id(0x408);
    public const string KnowledgeOwnerTrustedPat = "athar_pat_qa_knowledge_owner_trusted";
    public static readonly Guid KnowledgeOwnerTrustedPatId = Id(0x409);
    /// <summary>A non-confidential company memory's link to the confidential Compensation page (FR-CL-01: external agents never get that ID).</summary>
    public static readonly Guid ConfidentialPageLink = Id(0x412);

    // Projects (T017). Admin is the project admin, Member a plain member; KnowledgeOwner isn't in it.
    public static readonly Guid PilotProject = Id(0x501);
    /// <summary>PilotProject's repo, stored normalized (FR-PJ-01).</summary>
    public const string PilotRemote = "github.com/abushanab-net/athar";
    public const string ResetUserPat = "athar_pat_qa_reset";
    public static readonly Guid ResetUserPatId = Id(0x406);

    internal static void Add(AtharDbContext db, string embeddingModelId)
    {
        if (db.Organizations.Any(o => o.Id == Org)) return;

        db.Organizations.AddRange(
            new Organization { Id = Org, Name = "Athar QA", Slug = "athar-qa", BootstrapCompletedAt = Seeder.At },
            new Organization { Id = OtherOrg, Name = "Other Org", Slug = "other-org", BootstrapCompletedAt = Seeder.At },
            new Organization { Id = LastOwnerOrg, Name = "Two Owners", Slug = "two-owners", BootstrapCompletedAt = Seeder.At });

        var n = 0x1000; // member rows
        void User(Guid org, Guid id, string name, OrgRole role, bool knowledgeOwner = false, bool guest = false, bool deactivated = false, string locale = "en")
        {
            db.Users.Add(new User
            {
                Id = id, OrgId = org, Email = Email(name), DisplayName = name,
                Locale = locale, IsGuest = guest, DeactivatedAt = deactivated ? Seeder.At : null,
            });
            db.OrgMembers.Add(new OrgMember { Id = Id(n++), OrgId = org, UserId = id, Role = role, IsKnowledgeOwner = knowledgeOwner });
            var email = Email(name);
            db.IdentityUsers.Add(new AtharIdentityUser
            {
                Id = id, OrgId = org, UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email,
                NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true, PasswordHash = PasswordHash.Value,
                SecurityStamp = id.ToString("N"), ConcurrencyStamp = id.ToString("N"), LockoutEnabled = true,
            });
        }

        User(Org, Owner, "Qa Owner", OrgRole.Owner, knowledgeOwner: true);
        User(Org, Admin, "Qa Admin", OrgRole.Admin);
        User(Org, Member, "Qa Member", OrgRole.Member, locale: "ar");
        User(Org, KnowledgeOwner, "Qa Knowledge Owner", OrgRole.Member, knowledgeOwner: true);
        User(Org, Guest, "Qa Guest", OrgRole.Member, guest: true);
        User(Org, Deactivated, "Qa Deactivated", OrgRole.Member, knowledgeOwner: true, deactivated: true);
        User(Org, ResetUser, "Qa Reset User", OrgRole.Member);
        User(Org, LockoutUser, "Qa Lockout User", OrgRole.Member);
        User(Org, CaptureUser, "Qa Capture User", OrgRole.Member); // T060: the AT-53 test deactivates this one
        // T012 access lab users (QaSeed.Access.cs). LabOutsider is an org member outside the Engineering workspace.
        User(Org, LabViewer, "Lab Viewer", OrgRole.Member);
        User(Org, LabCommenter, "Lab Commenter", OrgRole.Member);
        User(Org, LabEditor, "Lab Editor", OrgRole.Member);
        User(Org, LabAdmin, "Lab Admin", OrgRole.Member);
        User(Org, LabTeamViewer, "Lab Team Viewer", OrgRole.Member);
        User(Org, LabTeamEditor, "Lab Team Editor", OrgRole.Member);
        User(Org, LabOutsider, "Lab Outsider", OrgRole.Member);
        User(Org, LabShareUser, "Lab Share User", OrgRole.Member);
        User(OtherOrg, OtherOrgOwner, "Other Owner", OrgRole.Owner, knowledgeOwner: true);
        User(LastOwnerOrg, OwnerA, "Owner A", OrgRole.Owner);
        User(LastOwnerOrg, OwnerB, "Owner B", OrgRole.Owner);

        db.Invitations.Add(new Invitation
        {
            Id = ExpiredInvite, OrgId = Org, Email = "expired.invitee@qa.athar.local", Role = OrgRole.Member,
            TokenHash = Sha256(ExpiredInviteToken), ExpiresAt = Seeder.At.AddDays(-23),
        });

        db.Workspaces.AddRange(
            new Workspace { Id = Engineering, OrgId = Org, Name = "Engineering", Slug = "engineering" },
            new Workspace { Id = Operations, OrgId = Org, Name = "Operations", Slug = "operations" },
            new Workspace { Id = OtherOrgWorkspace, OrgId = OtherOrg, Name = "Other", Slug = "other" });

        void WsMember(Guid org, Guid ws, Guid user, WorkspaceRole role) =>
            db.WorkspaceMembers.Add(new WorkspaceMember { Id = Id(n++), OrgId = org, WorkspaceId = ws, UserId = user, Role = role });

        WsMember(Org, Engineering, Owner, WorkspaceRole.Admin);
        WsMember(Org, Engineering, Admin, WorkspaceRole.Admin);
        WsMember(Org, Engineering, Member, WorkspaceRole.Member);
        WsMember(Org, Engineering, KnowledgeOwner, WorkspaceRole.Member);
        WsMember(Org, Engineering, Guest, WorkspaceRole.Member);
        WsMember(Org, Engineering, Deactivated, WorkspaceRole.Member);
        WsMember(Org, Operations, Owner, WorkspaceRole.Admin);
        WsMember(Org, Operations, Admin, WorkspaceRole.Member);
        WsMember(OtherOrg, OtherOrgWorkspace, OtherOrgOwner, WorkspaceRole.Admin);

        db.Spaces.AddRange(
            // Agents may read and write the wiki (collab agent tests, T064); the handbook has AI off (AT-44).
            new Space { Id = EngWiki, OrgId = Org, WorkspaceId = Engineering, Name = "Engineering wiki", Slug = "wiki", AiPolicy = new(Read: true, Write: true) },
            new Space { Id = EngConfidential, OrgId = Org, WorkspaceId = Engineering, Name = "People (confidential)", Slug = "people", IsConfidential = true },
            new Space { Id = OpsWiki, OrgId = Org, WorkspaceId = Operations, Name = "دليل العمليات", Slug = "ops-wiki" },
            new Space { Id = OpsHandbook, OrgId = Org, WorkspaceId = Operations, Name = "Handbook", Slug = "handbook", AiPolicy = new(Read: false) },
            new Space { Id = OtherOrgSpace, OrgId = OtherOrg, WorkspaceId = OtherOrgWorkspace, Name = "Other wiki", Slug = "wiki" });

        void SpMember(Guid org, Guid space, Guid user, SpaceRole role) =>
            db.SpaceMembers.Add(new SpaceMember { Id = Id(n++), OrgId = org, SpaceId = space, UserId = user, Role = role });

        SpMember(Org, EngWiki, Admin, SpaceRole.Admin);
        SpMember(Org, EngWiki, Member, SpaceRole.Editor);
        SpMember(Org, EngWiki, KnowledgeOwner, SpaceRole.Commenter);
        SpMember(Org, EngWiki, Guest, SpaceRole.Viewer);
        SpMember(Org, EngConfidential, Owner, SpaceRole.Admin);
        SpMember(Org, EngConfidential, Admin, SpaceRole.Editor);
        SpMember(Org, OpsWiki, Owner, SpaceRole.Admin);
        SpMember(Org, OpsHandbook, Admin, SpaceRole.Admin);
        SpMember(OtherOrg, OtherOrgSpace, OtherOrgOwner, SpaceRole.Admin);

        void Canary(Guid org, Guid space, Guid chunk, Guid embedding)
        {
            db.Chunks.Add(new Chunk
            {
                Id = chunk, OrgId = org, OwnerKind = ChunkOwner.Page, OwnerId = chunk, OwnerRevision = 1, Idx = 0,
                Body = CanaryText, SearchText = CanaryText.ToLowerInvariant(), ContentHash = "canary", SpaceId = space,
            });
            db.ChunkEmbeddings.Add(new ChunkEmbedding { Id = embedding, OrgId = org, ChunkId = chunk, ModelId = embeddingModelId, Embedding = CanaryVector() });
        }
        Canary(Org, EngWiki, CanaryChunk, CanaryEmbedding);
        Canary(OtherOrg, OtherOrgSpace, OtherOrgCanaryChunk, OtherOrgCanaryEmbedding);
        void Pat(Guid id, Guid user, string token, params string[] scopes) => db.AccessTokens.Add(new AccessToken
        {
            Id = id, OrgId = Org, UserId = user, Name = token, Scopes = scopes,
            TokenHash = Sha256(token),
        });
        Pat(MemberPatId, Member, MemberPat, "read", "write", "memory:write");
        void Trusted(Guid id, Guid user, string token) => db.AccessTokens.Add(new AccessToken
        {
            Id = id, OrgId = Org, UserId = user, Name = token, Scopes = ["read", "memory:write"], TokenHash = Sha256(token),
            DisclosureClass = DisclosureClass.TrustedLocal, ClassifiedBy = Owner, ClassificationReason = "qa: trusted local agent",
        });
        Trusted(MemberTrustedPatId, Member, MemberTrustedPat);
        Trusted(KnowledgeOwnerTrustedPatId, KnowledgeOwner, KnowledgeOwnerTrustedPat);
        db.MemoryLinks.Add(new MemoryLink { Id = ConfidentialPageLink, OrgId = Org, MemoryId = MemoryCompanyFact, TargetKind = "page", TargetId = CompensationPage.ToString() });
        Pat(MemberReadPatId, Member, MemberReadPat, "read");
        Pat(KnowledgeOwnerPatId, KnowledgeOwner, KnowledgeOwnerPat, "read", "memory:write");
        Pat(DeactivatedPatId, Deactivated, DeactivatedPat, "read", "memory:write");
        Pat(AdminPatId, Admin, AdminPat, "read", "write", "memory:write");

        db.Projects.Add(new Project { Id = PilotProject, OrgId = Org, Name = "Athar pilot", Slug = "pilot" });
        db.ProjectMembers.AddRange(
            new ProjectMember { Id = Id(n++), OrgId = Org, ProjectId = PilotProject, UserId = Admin, Role = ProjectRole.Admin },
            new ProjectMember { Id = Id(n++), OrgId = Org, ProjectId = PilotProject, UserId = Member, Role = ProjectRole.Member });
        db.ProjectLinks.Add(new ProjectLink { Id = Id(0x511), OrgId = Org, ProjectId = PilotProject, RepoRemote = PilotRemote });
        Pat(ResetUserPatId, ResetUser, ResetUserPat, "read");
        Pat(CaptureUserPatId, CaptureUser, CaptureUserPat, "read", "memory:write");

        AddContent(db);
        AddAccessLab(db);
        AddEval(db); // T082: AT-40 corpus
        OAuthClients.Add(db, Org, qa: true);
    }

    private static string Sha256(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ASP.NET Identity's V3 password hash format (PBKDF2-HMACSHA512, 100k iterations) with a fixed salt, so a reset
    // reproduces the dataset exactly (AT-55). Only for the public qa password; real hashes come from PasswordHasher.
    private static readonly Lazy<string> PasswordHash = new(() =>
    {
        byte[] salt = new byte[16];
        var subkey = Rfc2898DeriveBytes.Pbkdf2(Password, salt, 100_000, HashAlgorithmName.SHA512, 32);
        var hash = new byte[13 + salt.Length + subkey.Length];
        hash[0] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(hash.AsSpan(1), 2); // KeyDerivationPrf.HMACSHA512
        BinaryPrimitives.WriteUInt32BigEndian(hash.AsSpan(5), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(hash.AsSpan(9), (uint)salt.Length);
        salt.CopyTo(hash, 13);
        subkey.CopyTo(hash, 13 + salt.Length);
        return Convert.ToBase64String(hash);
    });
}
