using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NpgsqlTypes;
using Pgvector;

namespace Athar.Data;

/// <summary>
/// The EF model is the schema. Change it here, then run
/// <c>dotnet ef migrations add &lt;Name&gt; -p src/Athar.Data -s src/Athar.Web</c>. Never hand-written SQL.
/// </summary>
public partial class AtharDbContext(DbContextOptions<AtharDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The organization this unit of work runs as. Every LINQ query on a tenant table filters on it (FR-AC-03),
    /// so an unset value matches nothing. Cross-org system work (seeding, retention) uses IgnoreQueryFilters.
    /// New rows with an empty OrgId get this value.
    /// </summary>
    public Guid CurrentOrgId { get; set; }

    /// <summary>Where this unit of work's changes come from, read at SaveChanges and stamped on every audit event it adds (FR-AU-02).</summary>
    public Func<AuditOrigin?>? OriginSource { get; set; }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<AiSettings> AiSettings => Set<AiSettings>();
    public DbSet<User> Users => Set<User>();
    public DbSet<OrgMember> OrgMembers => Set<OrgMember>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<SpaceMember> SpaceMembers => Set<SpaceMember>();
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<PageRevision> PageRevisions => Set<PageRevision>();
    public DbSet<PageRestriction> PageRestrictions => Set<PageRestriction>();
    public DbSet<PageVersion> PageVersions => Set<PageVersion>();
    public DbSet<PageShare> PageShares => Set<PageShare>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<PageWatch> PageWatches => Set<PageWatch>();
    public DbSet<CommentThread> CommentThreads => Set<CommentThread>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Mention> Mentions => Set<Mention>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectLink> ProjectLinks => Set<ProjectLink>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<MemoryVersion> MemoryVersions => Set<MemoryVersion>();
    public DbSet<MemoryShadow> MemoryShadows => Set<MemoryShadow>();
    public DbSet<MemoryProposal> MemoryProposals => Set<MemoryProposal>();
    public DbSet<MemoryConflict> MemoryConflicts => Set<MemoryConflict>();
    public DbSet<MemoryLink> MemoryLinks => Set<MemoryLink>();
    public DbSet<CaptureCandidate> CaptureCandidates => Set<CaptureCandidate>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<SessionCommit> SessionCommits => Set<SessionCommit>();
    public DbSet<SessionRecord> SessionRecords => Set<SessionRecord>();
    public DbSet<CaptureSettings> CaptureSettings => Set<CaptureSettings>();
    public DbSet<KnowledgeSource> KnowledgeSources => Set<KnowledgeSource>();
    public DbSet<SourceDocument> SourceDocuments => Set<SourceDocument>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<SkillVersion> SkillVersions => Set<SkillVersion>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<ChunkEmbedding> ChunkEmbeddings => Set<ChunkEmbedding>();
    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<McpCall> McpCalls => Set<McpCall>();
    public DbSet<ModelUsage> ModelUsage => Set<ModelUsage>();
    public DbSet<DailyAgentStat> DailyAgentStats => Set<DailyAgentStat>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasPostgresExtension("vector");
        OAuth(mb); // first: OpenIddict names its tables, the loop below renames them

        foreach (var set in GetType().GetProperties().Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>)))
            mb.Entity(set.PropertyType.GetGenericArguments()[0]).ToTable(Snake(set.Name));

        // Tenancy: PK (org_id, id), FK to organizations, org query filter (docs/04 conventions).
        foreach (var type in mb.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => t.IsAssignableTo(typeof(OrgEntity))))
            GetType().GetMethod(nameof(ConfigureTenant), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(type).Invoke(this, [mb]);

        Wiki(mb);
        Brain(mb);
        Platform(mb);
        mb.Entity<AiSettings>(b =>
        {
            b.HasIndex(e => e.OrgId).IsUnique();
            b.Property(e => e.Revision).IsConcurrencyToken();
            b.Property(e => e.BaseUrl).HasMaxLength(2048);
            b.Property(e => e.EmbeddingModel).HasMaxLength(200);
            b.Property(e => e.QueryModel).HasMaxLength(200);
            b.Property(e => e.VisionModel).HasMaxLength(200);
        });
        Identity(mb);

        // snake_case columns, enums stored as snake_case text.
        foreach (var entity in mb.Model.GetEntityTypes().Where(e => !e.IsMappedToJson()))
            foreach (var p in entity.GetProperties())
            {
                if (p.FindAnnotation(RelationalAnnotationNames.ColumnName) is null) p.SetColumnName(Snake(p.Name));
                var enumType = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
                if (enumType.IsEnum)
                    p.SetValueConverter((ValueConverter)Activator.CreateInstance(typeof(SnakeEnumConverter<>).MakeGenericType(enumType))!);
            }
    }

    private void ConfigureTenant<T>(ModelBuilder mb) where T : OrgEntity
    {
        var b = mb.Entity<T>();
        b.HasKey(e => new { e.OrgId, e.Id });
        b.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrgId);
        b.HasQueryFilter(e => e.OrgId == CurrentOrgId);
        // T166: deleted spaces are gone for every query (this replaces the tenant filter, so it repeats the org check).
        if (typeof(T) == typeof(Space)) mb.Entity<Space>().HasQueryFilter(e => e.OrgId == CurrentOrgId && e.DeletedAt == null);
    }

    /// <summary>Same-org foreign key: (org_id, column) → principal (org_id, id).</summary>
    private static void Fk<TDep, TPrin>(ModelBuilder mb, Expression<Func<TDep, object?>> columns)
        where TDep : OrgEntity where TPrin : OrgEntity =>
        mb.Entity<TDep>().HasOne<TPrin>().WithMany().HasForeignKey(columns);

    private const string OnePrincipal = "num_nonnulls(user_id, team_id) = 1";

    // The owner column matches the scope; all null for company.
    private const string ScopeOwner =
        "num_nonnulls(owner_user_id, owner_team_id, owner_project_id) = CASE scope WHEN 'company' THEN 0 ELSE 1 END " +
        "AND (scope <> 'personal' OR owner_user_id IS NOT NULL) " +
        "AND (scope <> 'team' OR owner_team_id IS NOT NULL) " +
        "AND (scope <> 'project' OR owner_project_id IS NOT NULL)";

    private static void Wiki(ModelBuilder mb)
    {
        mb.Entity<User>().HasIndex(e => new { e.OrgId, e.Email }).IsUnique();

        Fk<OrgMember, User>(mb, e => new { e.OrgId, e.UserId });
        mb.Entity<OrgMember>().HasIndex(e => new { e.OrgId, e.UserId }).IsUnique();

        mb.Entity<Invitation>().HasIndex(e => e.TokenHash).IsUnique();

        Fk<Team, User>(mb, e => new { e.OrgId, e.LeadUserId });
        Fk<TeamMember, Team>(mb, e => new { e.OrgId, e.TeamId });
        Fk<TeamMember, User>(mb, e => new { e.OrgId, e.UserId });
        mb.Entity<TeamMember>().HasIndex(e => new { e.OrgId, e.TeamId, e.UserId }).IsUnique();

        mb.Entity<Workspace>().HasIndex(e => new { e.OrgId, e.Slug }).IsUnique();
        Fk<WorkspaceMember, Workspace>(mb, e => new { e.OrgId, e.WorkspaceId });
        Fk<WorkspaceMember, User>(mb, e => new { e.OrgId, e.UserId });
        mb.Entity<WorkspaceMember>().HasIndex(e => new { e.OrgId, e.WorkspaceId, e.UserId }).IsUnique();

        Fk<Space, Workspace>(mb, e => new { e.OrgId, e.WorkspaceId });
        mb.Entity<Space>().HasIndex(e => new { e.OrgId, e.WorkspaceId, e.Slug }).IsUnique();
        // jsonb that queries can read (access predicates test sp.AiPolicy.Read): {"read": true, "write": false, "delete_to_trash": false}.
        mb.Entity<Space>().OwnsOne(e => e.AiPolicy, b =>
        {
            b.ToJson("ai_policy");
            b.Property(p => p.Read).HasJsonPropertyName("read");
            b.Property(p => p.Write).HasJsonPropertyName("write");
            b.Property(p => p.DeleteToTrash).HasJsonPropertyName("delete_to_trash");
        });

        Fk<SpaceMember, Space>(mb, e => new { e.OrgId, e.SpaceId });
        Fk<SpaceMember, User>(mb, e => new { e.OrgId, e.UserId });
        Fk<SpaceMember, Team>(mb, e => new { e.OrgId, e.TeamId });
        mb.Entity<SpaceMember>(b =>
        {
            b.ToTable(t => t.HasCheckConstraint("ck_space_members_one_principal", OnePrincipal));
            b.HasIndex(e => new { e.OrgId, e.SpaceId, e.UserId }).IsUnique();
            b.HasIndex(e => new { e.OrgId, e.SpaceId, e.TeamId }).IsUnique();
        });

        mb.Entity<Page>(b =>
        {
            Fk<Page, Space>(mb, e => new { e.OrgId, e.SpaceId });
            // The parent is in the same org and the same space.
            b.HasAlternateKey(e => new { e.OrgId, e.SpaceId, e.Id });
            b.HasOne<Page>().WithMany().HasForeignKey(e => new { e.OrgId, e.SpaceId, e.ParentId })
                .HasPrincipalKey(e => new { e.OrgId, e.SpaceId, e.Id });
            b.Property(e => e.ContentJson).HasColumnType("jsonb");
        });

        Fk<PageRevision, Page>(mb, e => new { e.OrgId, e.PageId });
        mb.Entity<PageRevision>().HasIndex(e => new { e.OrgId, e.PageId, e.Revision }).IsUnique();

        Fk<PageRestriction, Page>(mb, e => new { e.OrgId, e.PageId });
        Fk<PageRestriction, User>(mb, e => new { e.OrgId, e.UserId });
        Fk<PageRestriction, Team>(mb, e => new { e.OrgId, e.TeamId });
        mb.Entity<PageRestriction>(b =>
        {
            b.ToTable(t => t.HasCheckConstraint("ck_page_restrictions_one_principal", OnePrincipal));
            b.HasIndex(e => new { e.OrgId, e.PageId, e.UserId }).IsUnique();
            b.HasIndex(e => new { e.OrgId, e.PageId, e.TeamId }).IsUnique();
        });

        Fk<PageShare, Page>(mb, e => new { e.OrgId, e.PageId });
        Fk<PageShare, User>(mb, e => new { e.OrgId, e.UserId });
        Fk<PageShare, Team>(mb, e => new { e.OrgId, e.TeamId });
        Fk<PageShare, User>(mb, e => new { e.OrgId, e.CreatedBy });
        mb.Entity<PageShare>(b =>
        {
            b.ToTable(t => t.HasCheckConstraint("ck_page_shares_one_principal", OnePrincipal));
            b.HasIndex(e => new { e.OrgId, e.PageId, e.UserId }).IsUnique();
            b.HasIndex(e => new { e.OrgId, e.PageId, e.TeamId }).IsUnique();
        });

        // Phase U: page status (existing pages stay published), byline, watchers, avatars.
        mb.Entity<Page>().Property(e => e.Status).HasMaxLength(16).HasDefaultValue(PageStatus.Published);
        Fk<Page, User>(mb, e => new { e.OrgId, e.PublishedBy });
        Fk<Page, User>(mb, e => new { e.OrgId, e.CreatedBy });
        mb.Entity<User>().Property(e => e.AvatarFile).HasMaxLength(128);
        mb.Entity<PageWatch>(b =>
        {
            b.HasOne<Page>().WithMany().HasForeignKey(e => new { e.OrgId, e.PageId }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(e => new { e.OrgId, e.UserId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(e => new { e.OrgId, e.PageId, e.UserId }).IsUnique();
        });

        // Side-menu favorites: one per person and target; they go with the person, workspace or space.
        mb.Entity<Favorite>(b =>
        {
            b.HasOne<User>().WithMany().HasForeignKey(e => new { e.OrgId, e.UserId }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Workspace>().WithMany().HasForeignKey(e => new { e.OrgId, e.WorkspaceId }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Space>().WithMany().HasForeignKey(e => new { e.OrgId, e.SpaceId }).OnDelete(DeleteBehavior.Cascade);
            b.ToTable(t => t.HasCheckConstraint("ck_favorites_one_target", "num_nonnulls(workspace_id, space_id) = 1"));
            b.HasIndex(e => new { e.OrgId, e.UserId, e.WorkspaceId }).IsUnique();
            b.HasIndex(e => new { e.OrgId, e.UserId, e.SpaceId }).IsUnique();
        });
        foreach (var e in new[] { typeof(Workspace), typeof(Space) })
        {
            mb.Entity(e).Property(nameof(Workspace.Color)).HasMaxLength(16);
            mb.Entity(e).Property(nameof(Workspace.Icon)).HasMaxLength(32);
        }

        Fk<PageVersion, Page>(mb, e => new { e.OrgId, e.PageId });
        Fk<PageVersion, User>(mb, e => new { e.OrgId, e.CreatedBy });
        mb.Entity<PageVersion>().Property(e => e.ContentJson).HasColumnType("jsonb");

        Fk<CommentThread, Page>(mb, e => new { e.OrgId, e.PageId });
        Fk<CommentThread, User>(mb, e => new { e.OrgId, e.ResolvedBy });
        mb.Entity<CommentThread>().HasIndex(e => new { e.OrgId, e.AnchorId }).IsUnique();

        Fk<Comment, CommentThread>(mb, e => new { e.OrgId, e.ThreadId });
        Fk<Comment, User>(mb, e => new { e.OrgId, e.AuthorId });

        Fk<Mention, User>(mb, e => new { e.OrgId, e.UserId });
        mb.Entity<Mention>().HasIndex(e => new { e.OrgId, e.SourceKind, e.SourceId });

        Fk<ShareLink, Page>(mb, e => new { e.OrgId, e.PageId });
        Fk<ShareLink, Space>(mb, e => new { e.OrgId, e.SpaceId });
        Fk<ShareLink, User>(mb, e => new { e.OrgId, e.CreatedBy });
        mb.Entity<ShareLink>(b =>
        {
            b.ToTable(t => t.HasCheckConstraint("ck_share_links_one_target", "num_nonnulls(page_id, space_id) = 1"));
            b.HasIndex(e => e.TokenHash).IsUnique();
        });

        Fk<Notification, User>(mb, e => new { e.OrgId, e.UserId });
        Fk<Notification, User>(mb, e => new { e.OrgId, e.ActorUserId });
        Fk<Attachment, Page>(mb, e => new { e.OrgId, e.PageId });

        mb.Entity<AuditEvent>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.CreatedAt });
            b.Property(e => e.Before).HasColumnType("jsonb");
            b.Property(e => e.After).HasColumnType("jsonb");
        });
    }

    private static void Brain(ModelBuilder mb)
    {
        mb.Entity<Project>().HasIndex(e => new { e.OrgId, e.Slug }).IsUnique();
        Fk<ProjectMember, Project>(mb, e => new { e.OrgId, e.ProjectId });
        Fk<ProjectMember, User>(mb, e => new { e.OrgId, e.UserId });
        mb.Entity<ProjectMember>().HasIndex(e => new { e.OrgId, e.ProjectId, e.UserId }).IsUnique();

        Fk<ProjectLink, Project>(mb, e => new { e.OrgId, e.ProjectId });
        Fk<ProjectLink, Space>(mb, e => new { e.OrgId, e.SpaceId });
        Fk<ProjectLink, Team>(mb, e => new { e.OrgId, e.TeamId });
        mb.Entity<ProjectLink>().ToTable(t => t.HasCheckConstraint("ck_project_links_one_target", "num_nonnulls(repo_remote, space_id, team_id) = 1"));

        Fk<Memory, User>(mb, e => new { e.OrgId, e.OwnerUserId });
        Fk<Memory, Team>(mb, e => new { e.OrgId, e.OwnerTeamId });
        Fk<Memory, Project>(mb, e => new { e.OrgId, e.OwnerProjectId });
        Fk<Memory, User>(mb, e => new { e.OrgId, e.CreatedByUser });
        mb.Entity<Memory>().ToTable(t => t.HasCheckConstraint("ck_memories_scope_owner", ScopeOwner));

        Fk<MemoryVersion, Memory>(mb, e => new { e.OrgId, e.MemoryId });
        Fk<MemoryVersion, User>(mb, e => new { e.OrgId, e.ChangedBy });
        mb.Entity<MemoryVersion>().HasIndex(e => new { e.OrgId, e.MemoryId, e.Revision }).IsUnique();

        Fk<MemoryShadow, Project>(mb, e => new { e.OrgId, e.ProjectId });
        Fk<MemoryShadow, Memory>(mb, e => new { e.OrgId, e.CompanyMemoryId });
        Fk<MemoryShadow, Memory>(mb, e => new { e.OrgId, e.ShadowMemoryId });
        mb.Entity<MemoryShadow>().HasIndex(e => new { e.OrgId, e.ProjectId, e.CompanyMemoryId }).IsUnique().HasFilter("deleted_at IS NULL");

        Fk<MemoryProposal, Memory>(mb, e => new { e.OrgId, e.TargetMemoryId });
        Fk<MemoryProposal, User>(mb, e => new { e.OrgId, e.ProposedBy });
        Fk<MemoryProposal, User>(mb, e => new { e.OrgId, e.ReviewerId });
        Fk<MemoryProposal, MemoryProposal>(mb, e => new { e.OrgId, e.RebasedFrom });
        mb.Entity<MemoryProposal>(b =>
        {
            b.Property(e => e.Payload).HasColumnType("jsonb");
            b.Property(e => e.Evidence).HasColumnType("jsonb");
        });

        Fk<MemoryConflict, Memory>(mb, e => new { e.OrgId, e.MemoryA });
        Fk<MemoryConflict, Memory>(mb, e => new { e.OrgId, e.MemoryB });
        Fk<MemoryConflict, User>(mb, e => new { e.OrgId, e.OpenedBy });

        Fk<MemoryLink, Memory>(mb, e => new { e.OrgId, e.MemoryId });

        Fk<CaptureCandidate, AgentSession>(mb, e => new { e.OrgId, e.SessionId });
        Fk<CaptureCandidate, User>(mb, e => new { e.OrgId, e.OwnerUserId });
        mb.Entity<CaptureCandidate>().Property(e => e.EvidenceExcerpt).HasMaxLength(500);
        Fk<CaptureCandidate, Memory>(mb, e => new { e.OrgId, e.MemoryId });
        Fk<CaptureCandidate, MemoryProposal>(mb, e => new { e.OrgId, e.ProposalId });

        Fk<AgentSession, User>(mb, e => new { e.OrgId, e.UserId });
        Fk<AgentSession, Project>(mb, e => new { e.OrgId, e.ProjectId });
        mb.Entity<AgentSession>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.UserId, e.Client, e.ExternalSessionId }).IsUnique();
            b.Property(e => e.Title).HasMaxLength(200);
            b.Property(e => e.Source).HasMaxLength(16).HasDefaultValue(SessionSource.Hook);
        });

        // Phase G (FR-AG-04, FR-MM-06): one row per person, going with them.
        mb.Entity<CaptureSettings>(b =>
        {
            b.HasOne<User>().WithMany().HasForeignKey(e => new { e.OrgId, e.UserId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(e => new { e.OrgId, e.UserId }).IsUnique();
            // Sentinel true: EF sends an explicit false instead of letting the column default (true) win.
            b.Property(e => e.AutoAcceptPersonal).HasDefaultValue(true).HasSentinel(true);
        });

        Fk<SessionCommit, AgentSession>(mb, e => new { e.OrgId, e.SessionId });
        mb.Entity<SessionCommit>().HasIndex(e => new { e.OrgId, e.SessionId, e.CommitId }).IsUnique();

        Fk<SessionRecord, AgentSession>(mb, e => new { e.OrgId, e.SessionId });
        mb.Entity<SessionRecord>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.SessionId, e.RecordIndex }).IsUnique();
            b.HasIndex(e => new { e.OrgId, e.PurgeAfter });
            b.Property(e => e.RedactedRecord).HasColumnType("jsonb");
        });

        Fk<KnowledgeSource, Project>(mb, e => new { e.OrgId, e.ProjectId });
        Fk<SourceDocument, KnowledgeSource>(mb, e => new { e.OrgId, e.SourceId });
        Fk<KnowledgeSource, User>(mb, e => new { e.OrgId, e.CreatedBy });
        mb.Entity<KnowledgeSource>().Property(e => e.Title).HasMaxLength(300);
        mb.Entity<SourceDocument>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.SourceId, e.Path }).IsUnique();
            b.Property(e => e.MediaType).HasMaxLength(128);
            b.Property(e => e.StoredFile).HasMaxLength(128);
        });

        Fk<Skill, User>(mb, e => new { e.OrgId, e.OwnerUserId });
        Fk<Skill, Team>(mb, e => new { e.OrgId, e.OwnerTeamId });
        Fk<Skill, Project>(mb, e => new { e.OrgId, e.OwnerProjectId });
        mb.Entity<Skill>(b =>
        {
            b.ToTable(t => t.HasCheckConstraint("ck_skills_scope_owner", ScopeOwner));
            b.Property(e => e.Files).HasColumnType("jsonb");
            b.Property(e => e.Revision).HasDefaultValue(1);
        });
        Fk<SkillVersion, User>(mb, e => new { e.OrgId, e.PublishedBy });
        mb.Entity<SkillVersion>(b =>
        {
            b.HasOne<Skill>().WithMany().HasForeignKey(e => new { e.OrgId, e.SkillId }).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(e => new { e.OrgId, e.SkillId, e.Revision }).IsUnique();
            b.Property(e => e.Files).HasColumnType("jsonb");
        });

        mb.Entity<Chunk>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.OwnerKind, e.OwnerId });
            // Generated tsvector columns over the normalized search copy (FR-SR-05), one per language config.
            foreach (var (column, config) in new[] { ("TsvAr", "arabic"), ("TsvEn", "english"), ("TsvSimple", "simple") })
            {
                b.Property<NpgsqlTsVector>(column).IsGeneratedTsVectorColumn(config, nameof(Chunk.SearchText));
                b.HasIndex(column).HasMethod("gin");
            }
        });

        Fk<ChunkEmbedding, Chunk>(mb, e => new { e.OrgId, e.ChunkId });
        mb.Entity<ChunkEmbedding>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.ChunkId, e.ModelId }).IsUnique();
            b.Property(e => e.Embedding).HasColumnType("vector(1024)")
                .HasConversion(v => new Vector(v), v => v.ToArray());
            b.HasIndex(e => e.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
        });
    }

    private static void Platform(ModelBuilder mb)
    {
        Fk<AccessToken, User>(mb, e => new { e.OrgId, e.UserId });
        Fk<AccessToken, User>(mb, e => new { e.OrgId, e.ClassifiedBy });
        mb.Entity<AccessToken>().HasIndex(e => e.TokenHash).IsUnique();

        mb.Entity<Job>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.Kind, e.Key }).IsUnique();
            b.HasIndex(e => new { e.OrgId, e.Status, e.RunAfter });
            b.Property(e => e.Payload).HasColumnType("jsonb");
            b.Property(e => e.Version).IsRowVersion().HasColumnName("xmin");
        });

        Fk<Operation, User>(mb, e => new { e.OrgId, e.ActorUserId });
        mb.Entity<Operation>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.OpId }).IsUnique();
            b.Property(e => e.Result).HasColumnType("jsonb");
        });

        // FR-AG-12: no FKs, like audit events, so usage outlives what it describes; only retention deletes it.
        mb.Entity<McpCall>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.CreatedAt });
            b.Property(e => e.Tool).HasMaxLength(100);
            b.Property(e => e.Outcome).HasMaxLength(64);
        });
        mb.Entity<ModelUsage>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.CreatedAt });
            b.Property(e => e.ModelId).HasMaxLength(200);
        });
        mb.Entity<DailyAgentStat>(b =>
        {
            b.HasIndex(e => new { e.OrgId, e.Date, e.UserId, e.Client, e.Kind }).IsUnique().AreNullsDistinct(false);
            b.Property(e => e.Client).HasMaxLength(200);
            b.Property(e => e.Kind).HasMaxLength(64);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        MaintainAncestryAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        await MaintainAncestryAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Timestamps, the tenant of new rows, and the origin of new audit events. Explicit values (seeds) are kept.
    private void Stamp()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var e in ChangeTracker.Entries())
        {
            if (e.Entity is OrgEntity { OrgId: var org } tenant && e.State == EntityState.Added && org == Guid.Empty)
                tenant.OrgId = CurrentOrgId != Guid.Empty ? CurrentOrgId
                    : throw new InvalidOperationException($"New {e.Metadata.ClrType.Name} has no OrgId and CurrentOrgId is unset.");

            if (e.State == EntityState.Added) StampOAuth(e.Entity);

            if (e.Entity is AuditEvent { } audit && e.State == EntityState.Added && OriginSource?.Invoke() is { } o)
            {
                audit.Ip ??= o.Ip;
                audit.UserAgent ??= o.UserAgent;
                audit.CredentialId ??= o.CredentialId;
                audit.OauthClientId ??= o.OAuthClientId;
                audit.ClaimedClient ??= o.ClaimedClient;
            }

            if (e.Metadata.FindProperty(nameof(OrgEntity.CreatedAt)) is null) continue;
            var created = e.Property(nameof(OrgEntity.CreatedAt));
            var updated = e.Property(nameof(OrgEntity.UpdatedAt));
            if (e.State == EntityState.Added)
            {
                if ((DateTimeOffset)created.CurrentValue! == default) created.CurrentValue = now;
                if ((DateTimeOffset)updated.CurrentValue! == default) updated.CurrentValue = created.CurrentValue;
            }
            else if (e.State == EntityState.Modified) updated.CurrentValue = now;
        }
    }

    internal static string Snake(string name) => SnakeRegex().Replace(name, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex SnakeRegex();

    private sealed class SnakeEnumConverter<T>() : ValueConverter<T, string>(
        v => Snake(v.ToString()), s => Enum.Parse<T>(s.Replace("_", ""), true)) where T : struct, Enum;
}
