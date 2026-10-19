using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;
using Pgvector;

#nullable disable

namespace Athar.Data.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    bootstrap_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    oauth_client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_client = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    resource_type = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<string>(type: "text", nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true),
                    ip = table.Column<string>(type: "text", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_audit_events_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_kind = table.Column<string>(type: "text", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_revision = table.Column<long>(type: "bigint", nullable: false),
                    idx = table.Column<int>(type: "integer", nullable: false),
                    heading_path = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "text", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_confidential = table.Column<bool>(type: "boolean", nullable: false),
                    tsv_ar = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true)
                        .Annotation("Npgsql:TsVectorConfig", "arabic")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "search_text" }),
                    tsv_en = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true)
                        .Annotation("Npgsql:TsVectorConfig", "english")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "search_text" }),
                    tsv_simple = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true)
                        .Annotation("Npgsql:TsVectorConfig", "simple")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "search_text" }),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunks", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_chunks_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_invitations_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_jobs_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    client_secret = table.Column<string>(type: "text", nullable: true),
                    client_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    consent_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    json_web_key_set = table.Column<string>(type: "text", nullable: true),
                    permissions = table.Column<string>(type: "text", nullable: true),
                    post_logout_redirect_uris = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redirect_uris = table.Column<string>(type: "text", nullable: true),
                    requirements = table.Column<string>(type: "text", nullable: true),
                    settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_applications", x => x.id);
                    table.ForeignKey(
                        name: "FK_oauth_applications_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_scopes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    descriptions = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    resources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_scopes", x => x.id);
                    table.ForeignKey(
                        name: "FK_oauth_scopes_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_projects_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    locale = table.Column<string>(type: "text", nullable: false),
                    is_guest = table.Column<bool>(type: "boolean", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_users_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspaces", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_workspaces_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chunk_embeddings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunk_embeddings", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_chunk_embeddings_chunks_org_id_chunk_id",
                        columns: x => new { x.org_id, x.chunk_id },
                        principalTable: "chunks",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chunk_embeddings_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_authorizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_authorizations", x => x.id);
                    table.ForeignKey(
                        name: "FK_oauth_authorizations_oauth_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "oauth_applications",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_oauth_authorizations_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    repo = table.Column<string>(type: "text", nullable: false),
                    encrypted_pat = table.Column<byte[]>(type: "bytea", nullable: true),
                    last_commit_sha = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    is_confidential = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_sources", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_knowledge_sources_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_knowledge_sources_projects_org_id_project_id",
                        columns: x => new { x.org_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "access_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    disclosure_class = table.Column<string>(type: "text", nullable: false),
                    classified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    classification_reason = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_tokens", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_access_tokens_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_access_tokens_users_org_id_classified_by",
                        columns: x => new { x.org_id, x.classified_by },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_access_tokens_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client = table.Column<string>(type: "text", nullable: false),
                    external_session_id = table.Column<string>(type: "text", nullable: false),
                    repo_remote = table.Column<string>(type: "text", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acked_index = table.Column<int>(type: "integer", nullable: false),
                    extracted_upto = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_sessions", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_agent_sessions_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_sessions_projects_org_id_project_id",
                        columns: x => new { x.org_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_agent_sessions_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    normalized_user_name = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    normalized_email = table.Column<string>(type: "text", nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_users", x => x.id);
                    table.ForeignKey(
                        name: "FK_identity_users_users_org_id_id",
                        columns: x => new { x.org_id, x.id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mentions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mentions", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_mentions_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_mentions_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "text", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_notifications_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notifications_users_org_id_actor_user_id",
                        columns: x => new { x.org_id, x.actor_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_notifications_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    op_id = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operations", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_operations_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_operations_users_org_id_actor_user_id",
                        columns: x => new { x.org_id, x.actor_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "org_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    is_knowledge_owner = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_members", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_org_members_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_org_members_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_members", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_project_members_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_members_projects_org_id_project_id",
                        columns: x => new { x.org_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_members_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    lead_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_teams_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_teams_users_org_id_lead_user_id",
                        columns: x => new { x.org_id, x.lead_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "spaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    is_confidential = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ai_policy = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spaces", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_spaces_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_spaces_workspaces_org_id_workspace_id",
                        columns: x => new { x.org_id, x.workspace_id },
                        principalTable: "workspaces",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspace_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_members", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_workspace_members_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workspace_members_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workspace_members_workspaces_org_id_workspace_id",
                        columns: x => new { x.org_id, x.workspace_id },
                        principalTable: "workspaces",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authorization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expiration_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redemption_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reference_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_oauth_tokens_oauth_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "oauth_applications",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_oauth_tokens_oauth_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalTable: "oauth_authorizations",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_oauth_tokens_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    content_md = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: false),
                    tombstoned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_documents", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_source_documents_knowledge_sources_org_id_source_id",
                        columns: x => new { x.org_id, x.source_id },
                        principalTable: "knowledge_sources",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_source_documents_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "capture_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    suggested_scope = table.Column<string>(type: "text", nullable: false),
                    suggested_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body_md = table.Column<string>(type: "text", nullable: false),
                    evidence_excerpt = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    similar_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    is_confidential = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capture_candidates", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_capture_candidates_agent_sessions_org_id_session_id",
                        columns: x => new { x.org_id, x.session_id },
                        principalTable: "agent_sessions",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_capture_candidates_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_capture_candidates_users_org_id_owner_user_id",
                        columns: x => new { x.org_id, x.owner_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_commits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commit_id = table.Column<string>(type: "text", nullable: false),
                    from_index = table.Column<int>(type: "integer", nullable: false),
                    to_index = table.Column<int>(type: "integer", nullable: false),
                    skipped = table.Column<bool>(type: "boolean", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_commits", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_session_commits_agent_sessions_org_id_session_id",
                        columns: x => new { x.org_id, x.session_id },
                        principalTable: "agent_sessions",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_session_commits_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_index = table.Column<int>(type: "integer", nullable: false),
                    redacted_record = table.Column<string>(type: "jsonb", nullable: false),
                    purge_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_records", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_session_records_agent_sessions_org_id_session_id",
                        columns: x => new { x.org_id, x.session_id },
                        principalTable: "agent_sessions",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_session_records_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "FK_identity_user_claims_identity_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_logins",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "FK_identity_user_logins_identity_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_tokens",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "FK_identity_user_tokens_identity_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body_md = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    overridable = table.Column<bool>(type: "boolean", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    created_by_user = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_credential = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    is_confidential = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memories", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_memories_scope_owner", "num_nonnulls(owner_user_id, owner_team_id, owner_project_id) = CASE scope WHEN 'company' THEN 0 ELSE 1 END AND (scope <> 'personal' OR owner_user_id IS NOT NULL) AND (scope <> 'team' OR owner_team_id IS NOT NULL) AND (scope <> 'project' OR owner_project_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_memories_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memories_projects_org_id_owner_project_id",
                        columns: x => new { x.org_id, x.owner_project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_memories_teams_org_id_owner_team_id",
                        columns: x => new { x.org_id, x.owner_team_id },
                        principalTable: "teams",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_memories_users_org_id_created_by_user",
                        columns: x => new { x.org_id, x.created_by_user },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_memories_users_org_id_owner_user_id",
                        columns: x => new { x.org_id, x.owner_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "skills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    body_md = table.Column<string>(type: "text", nullable: false),
                    files = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    is_confidential = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_skills_scope_owner", "num_nonnulls(owner_user_id, owner_team_id, owner_project_id) = CASE scope WHEN 'company' THEN 0 ELSE 1 END AND (scope <> 'personal' OR owner_user_id IS NOT NULL) AND (scope <> 'team' OR owner_team_id IS NOT NULL) AND (scope <> 'project' OR owner_project_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_skills_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_skills_projects_org_id_owner_project_id",
                        columns: x => new { x.org_id, x.owner_project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_skills_teams_org_id_owner_team_id",
                        columns: x => new { x.org_id, x.owner_team_id },
                        principalTable: "teams",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_skills_users_org_id_owner_user_id",
                        columns: x => new { x.org_id, x.owner_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_members", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_team_members_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_members_teams_org_id_team_id",
                        columns: x => new { x.org_id, x.team_id },
                        principalTable: "teams",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_members_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position = table.Column<double>(type: "double precision", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: true),
                    content_md = table.Column<string>(type: "text", nullable: true),
                    ydoc = table.Column<byte[]>(type: "bytea", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    live_epoch = table.Column<long>(type: "bigint", nullable: false),
                    live_seq = table.Column<long>(type: "bigint", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ancestor_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pages", x => new { x.org_id, x.id });
                    table.UniqueConstraint("AK_pages_org_id_space_id_id", x => new { x.org_id, x.space_id, x.id });
                    table.ForeignKey(
                        name: "FK_pages_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_pages_pages_org_id_space_id_parent_id",
                        columns: x => new { x.org_id, x.space_id, x.parent_id },
                        principalTable: "pages",
                        principalColumns: new[] { "org_id", "space_id", "id" });
                    table.ForeignKey(
                        name: "FK_pages_spaces_org_id_space_id",
                        columns: x => new { x.org_id, x.space_id },
                        principalTable: "spaces",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo_remote = table.Column<string>(type: "text", nullable: true),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_links", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_project_links_one_target", "num_nonnulls(repo_remote, space_id, team_id) = 1");
                    table.ForeignKey(
                        name: "FK_project_links_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_links_projects_org_id_project_id",
                        columns: x => new { x.org_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_links_spaces_org_id_space_id",
                        columns: x => new { x.org_id, x.space_id },
                        principalTable: "spaces",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_project_links_teams_org_id_team_id",
                        columns: x => new { x.org_id, x.team_id },
                        principalTable: "teams",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "space_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_space_members", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_space_members_one_principal", "num_nonnulls(user_id, team_id) = 1");
                    table.ForeignKey(
                        name: "FK_space_members_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_space_members_spaces_org_id_space_id",
                        columns: x => new { x.org_id, x.space_id },
                        principalTable: "spaces",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_space_members_teams_org_id_team_id",
                        columns: x => new { x.org_id, x.team_id },
                        principalTable: "teams",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_space_members_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "memory_conflicts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    memory_a = table.Column<Guid>(type: "uuid", nullable: false),
                    memory_b = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_conflicts", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_memory_conflicts_memories_org_id_memory_a",
                        columns: x => new { x.org_id, x.memory_a },
                        principalTable: "memories",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_conflicts_memories_org_id_memory_b",
                        columns: x => new { x.org_id, x.memory_b },
                        principalTable: "memories",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_conflicts_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_conflicts_users_org_id_opened_by",
                        columns: x => new { x.org_id, x.opened_by },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memory_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    memory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: false),
                    anchor = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_links", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_memory_links_memories_org_id_memory_id",
                        columns: x => new { x.org_id, x.memory_id },
                        principalTable: "memories",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_links_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memory_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_memory_id = table.Column<Guid>(type: "uuid", nullable: true),
                    base_revision = table.Column<int>(type: "integer", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    proposed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    rebased_from = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    is_confidential = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_proposals", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_memory_proposals_memories_org_id_target_memory_id",
                        columns: x => new { x.org_id, x.target_memory_id },
                        principalTable: "memories",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_memory_proposals_memory_proposals_org_id_rebased_from",
                        columns: x => new { x.org_id, x.rebased_from },
                        principalTable: "memory_proposals",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_memory_proposals_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_proposals_users_org_id_proposed_by",
                        columns: x => new { x.org_id, x.proposed_by },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_proposals_users_org_id_reviewer_id",
                        columns: x => new { x.org_id, x.reviewer_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "memory_shadows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_memory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shadow_memory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_revision = table.Column<int>(type: "integer", nullable: false),
                    stale = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_shadows", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_memory_shadows_memories_org_id_company_memory_id",
                        columns: x => new { x.org_id, x.company_memory_id },
                        principalTable: "memories",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_shadows_memories_org_id_shadow_memory_id",
                        columns: x => new { x.org_id, x.shadow_memory_id },
                        principalTable: "memories",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_shadows_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_shadows_projects_org_id_project_id",
                        columns: x => new { x.org_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memory_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    memory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body_md = table.Column<string>(type: "text", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_versions", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_memory_versions_memories_org_id_memory_id",
                        columns: x => new { x.org_id, x.memory_id },
                        principalTable: "memories",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_versions_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_versions_users_org_id_changed_by",
                        columns: x => new { x.org_id, x.changed_by },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    mime_sniffed = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    text_content = table.Column<string>(type: "text", nullable: true),
                    extract_status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attachments", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_attachments_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_attachments_pages_org_id_page_id",
                        columns: x => new { x.org_id, x.page_id },
                        principalTable: "pages",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comment_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anchor_id = table.Column<string>(type: "text", nullable: true),
                    quote = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    op_id = table.Column<string>(type: "text", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comment_threads", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_comment_threads_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_comment_threads_pages_org_id_page_id",
                        columns: x => new { x.org_id, x.page_id },
                        principalTable: "pages",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_comment_threads_users_org_id_resolved_by",
                        columns: x => new { x.org_id, x.resolved_by },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "page_restrictions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    level = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_restrictions", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_page_restrictions_one_principal", "num_nonnulls(user_id, team_id) = 1");
                    table.ForeignKey(
                        name: "FK_page_restrictions_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_restrictions_pages_org_id_page_id",
                        columns: x => new { x.org_id, x.page_id },
                        principalTable: "pages",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_restrictions_teams_org_id_team_id",
                        columns: x => new { x.org_id, x.team_id },
                        principalTable: "teams",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_page_restrictions_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "page_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    live_seq = table.Column<long>(type: "bigint", nullable: false),
                    contributors = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    agent_credentials = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    batch_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    op_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_revisions", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_page_revisions_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_revisions_pages_org_id_page_id",
                        columns: x => new { x.org_id, x.page_id },
                        principalTable: "pages",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "page_shares",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    level = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_shares", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_page_shares_one_principal", "num_nonnulls(user_id, team_id) = 1");
                    table.ForeignKey(
                        name: "FK_page_shares_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_shares_pages_org_id_page_id",
                        columns: x => new { x.org_id, x.page_id },
                        principalTable: "pages",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_shares_teams_org_id_team_id",
                        columns: x => new { x.org_id, x.team_id },
                        principalTable: "teams",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_page_shares_users_org_id_created_by",
                        columns: x => new { x.org_id, x.created_by },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_page_shares_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "page_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: true),
                    content_md = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_versions", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_page_versions_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_versions_pages_org_id_page_id",
                        columns: x => new { x.org_id, x.page_id },
                        principalTable: "pages",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_versions_users_org_id_created_by",
                        columns: x => new { x.org_id, x.created_by },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "share_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: true),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    include_descendants = table.Column<bool>(type: "boolean", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_share_links", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_share_links_one_target", "num_nonnulls(page_id, space_id) = 1");
                    table.ForeignKey(
                        name: "FK_share_links_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_share_links_pages_org_id_page_id",
                        columns: x => new { x.org_id, x.page_id },
                        principalTable: "pages",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_share_links_spaces_org_id_space_id",
                        columns: x => new { x.org_id, x.space_id },
                        principalTable: "spaces",
                        principalColumns: new[] { "org_id", "id" });
                    table.ForeignKey(
                        name: "FK_share_links_users_org_id_created_by",
                        columns: x => new { x.org_id, x.created_by },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body_md = table.Column<string>(type: "text", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comments", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_comments_comment_threads_org_id_thread_id",
                        columns: x => new { x.org_id, x.thread_id },
                        principalTable: "comment_threads",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_comments_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_comments_users_org_id_author_id",
                        columns: x => new { x.org_id, x.author_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_org_id_classified_by",
                table: "access_tokens",
                columns: new[] { "org_id", "classified_by" });

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_org_id_user_id",
                table: "access_tokens",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_token_hash",
                table: "access_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_sessions_org_id_project_id",
                table: "agent_sessions",
                columns: new[] { "org_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_sessions_org_id_user_id_client_external_session_id",
                table: "agent_sessions",
                columns: new[] { "org_id", "user_id", "client", "external_session_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attachments_org_id_page_id",
                table: "attachments",
                columns: new[] { "org_id", "page_id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_org_id_created_at",
                table: "audit_events",
                columns: new[] { "org_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_capture_candidates_org_id_owner_user_id",
                table: "capture_candidates",
                columns: new[] { "org_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_capture_candidates_org_id_session_id",
                table: "capture_candidates",
                columns: new[] { "org_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chunk_embeddings_embedding",
                table: "chunk_embeddings",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_chunk_embeddings_org_id_chunk_id_model_id",
                table: "chunk_embeddings",
                columns: new[] { "org_id", "chunk_id", "model_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chunks_org_id_owner_kind_owner_id",
                table: "chunks",
                columns: new[] { "org_id", "owner_kind", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chunks_tsv_ar",
                table: "chunks",
                column: "tsv_ar")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_chunks_tsv_en",
                table: "chunks",
                column: "tsv_en")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_chunks_tsv_simple",
                table: "chunks",
                column: "tsv_simple")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_comment_threads_org_id_anchor_id",
                table: "comment_threads",
                columns: new[] { "org_id", "anchor_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_comment_threads_org_id_page_id",
                table: "comment_threads",
                columns: new[] { "org_id", "page_id" });

            migrationBuilder.CreateIndex(
                name: "IX_comment_threads_org_id_resolved_by",
                table: "comment_threads",
                columns: new[] { "org_id", "resolved_by" });

            migrationBuilder.CreateIndex(
                name: "IX_comments_org_id_author_id",
                table: "comments",
                columns: new[] { "org_id", "author_id" });

            migrationBuilder.CreateIndex(
                name: "IX_comments_org_id_thread_id",
                table: "comments",
                columns: new[] { "org_id", "thread_id" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_user_claims_user_id",
                table: "identity_user_claims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_identity_user_logins_user_id",
                table: "identity_user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_identity_users_normalized_email",
                table: "identity_users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "IX_identity_users_normalized_user_name",
                table: "identity_users",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_identity_users_org_id_id",
                table: "identity_users",
                columns: new[] { "org_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invitations_token_hash",
                table: "invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_org_id_kind_key",
                table: "jobs",
                columns: new[] { "org_id", "kind", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_org_id_status_run_after",
                table: "jobs",
                columns: new[] { "org_id", "status", "run_after" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_sources_org_id_project_id",
                table: "knowledge_sources",
                columns: new[] { "org_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_memories_org_id_created_by_user",
                table: "memories",
                columns: new[] { "org_id", "created_by_user" });

            migrationBuilder.CreateIndex(
                name: "IX_memories_org_id_owner_project_id",
                table: "memories",
                columns: new[] { "org_id", "owner_project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_memories_org_id_owner_team_id",
                table: "memories",
                columns: new[] { "org_id", "owner_team_id" });

            migrationBuilder.CreateIndex(
                name: "IX_memories_org_id_owner_user_id",
                table: "memories",
                columns: new[] { "org_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_conflicts_org_id_memory_a",
                table: "memory_conflicts",
                columns: new[] { "org_id", "memory_a" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_conflicts_org_id_memory_b",
                table: "memory_conflicts",
                columns: new[] { "org_id", "memory_b" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_conflicts_org_id_opened_by",
                table: "memory_conflicts",
                columns: new[] { "org_id", "opened_by" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_links_org_id_memory_id",
                table: "memory_links",
                columns: new[] { "org_id", "memory_id" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_proposals_org_id_proposed_by",
                table: "memory_proposals",
                columns: new[] { "org_id", "proposed_by" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_proposals_org_id_rebased_from",
                table: "memory_proposals",
                columns: new[] { "org_id", "rebased_from" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_proposals_org_id_reviewer_id",
                table: "memory_proposals",
                columns: new[] { "org_id", "reviewer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_proposals_org_id_target_memory_id",
                table: "memory_proposals",
                columns: new[] { "org_id", "target_memory_id" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_shadows_org_id_company_memory_id",
                table: "memory_shadows",
                columns: new[] { "org_id", "company_memory_id" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_shadows_org_id_project_id_company_memory_id",
                table: "memory_shadows",
                columns: new[] { "org_id", "project_id", "company_memory_id" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_memory_shadows_org_id_shadow_memory_id",
                table: "memory_shadows",
                columns: new[] { "org_id", "shadow_memory_id" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_versions_org_id_changed_by",
                table: "memory_versions",
                columns: new[] { "org_id", "changed_by" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_versions_org_id_memory_id_revision",
                table: "memory_versions",
                columns: new[] { "org_id", "memory_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mentions_org_id_source_kind_source_id",
                table: "mentions",
                columns: new[] { "org_id", "source_kind", "source_id" });

            migrationBuilder.CreateIndex(
                name: "IX_mentions_org_id_user_id",
                table: "mentions",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_org_id_actor_user_id",
                table: "notifications",
                columns: new[] { "org_id", "actor_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_org_id_user_id",
                table: "notifications",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_oauth_applications_client_id",
                table: "oauth_applications",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_applications_org_id",
                table: "oauth_applications",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_authorizations_application_id_status_subject_type",
                table: "oauth_authorizations",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_oauth_authorizations_org_id_subject",
                table: "oauth_authorizations",
                columns: new[] { "org_id", "subject" });

            migrationBuilder.CreateIndex(
                name: "IX_oauth_scopes_name",
                table: "oauth_scopes",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_scopes_org_id",
                table: "oauth_scopes",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_tokens_application_id_status_subject_type",
                table: "oauth_tokens",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_oauth_tokens_authorization_id",
                table: "oauth_tokens",
                column: "authorization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_tokens_org_id",
                table: "oauth_tokens",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_tokens_reference_id",
                table: "oauth_tokens",
                column: "reference_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operations_org_id_actor_user_id",
                table: "operations",
                columns: new[] { "org_id", "actor_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_operations_org_id_op_id",
                table: "operations",
                columns: new[] { "org_id", "op_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_org_members_org_id_user_id",
                table: "org_members",
                columns: new[] { "org_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_restrictions_org_id_page_id_team_id",
                table: "page_restrictions",
                columns: new[] { "org_id", "page_id", "team_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_restrictions_org_id_page_id_user_id",
                table: "page_restrictions",
                columns: new[] { "org_id", "page_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_restrictions_org_id_team_id",
                table: "page_restrictions",
                columns: new[] { "org_id", "team_id" });

            migrationBuilder.CreateIndex(
                name: "IX_page_restrictions_org_id_user_id",
                table: "page_restrictions",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_page_revisions_org_id_page_id_revision",
                table: "page_revisions",
                columns: new[] { "org_id", "page_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_shares_org_id_created_by",
                table: "page_shares",
                columns: new[] { "org_id", "created_by" });

            migrationBuilder.CreateIndex(
                name: "IX_page_shares_org_id_page_id_team_id",
                table: "page_shares",
                columns: new[] { "org_id", "page_id", "team_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_shares_org_id_page_id_user_id",
                table: "page_shares",
                columns: new[] { "org_id", "page_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_shares_org_id_team_id",
                table: "page_shares",
                columns: new[] { "org_id", "team_id" });

            migrationBuilder.CreateIndex(
                name: "IX_page_shares_org_id_user_id",
                table: "page_shares",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_page_versions_org_id_created_by",
                table: "page_versions",
                columns: new[] { "org_id", "created_by" });

            migrationBuilder.CreateIndex(
                name: "IX_page_versions_org_id_page_id",
                table: "page_versions",
                columns: new[] { "org_id", "page_id" });

            migrationBuilder.CreateIndex(
                name: "IX_pages_org_id_space_id_parent_id",
                table: "pages",
                columns: new[] { "org_id", "space_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_links_org_id_project_id",
                table: "project_links",
                columns: new[] { "org_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_links_org_id_space_id",
                table: "project_links",
                columns: new[] { "org_id", "space_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_links_org_id_team_id",
                table: "project_links",
                columns: new[] { "org_id", "team_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_members_org_id_project_id_user_id",
                table: "project_members",
                columns: new[] { "org_id", "project_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_members_org_id_user_id",
                table: "project_members",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_projects_org_id_slug",
                table: "projects",
                columns: new[] { "org_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_commits_org_id_session_id_commit_id",
                table: "session_commits",
                columns: new[] { "org_id", "session_id", "commit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_records_org_id_purge_after",
                table: "session_records",
                columns: new[] { "org_id", "purge_after" });

            migrationBuilder.CreateIndex(
                name: "IX_session_records_org_id_session_id_record_index",
                table: "session_records",
                columns: new[] { "org_id", "session_id", "record_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_share_links_org_id_created_by",
                table: "share_links",
                columns: new[] { "org_id", "created_by" });

            migrationBuilder.CreateIndex(
                name: "IX_share_links_org_id_page_id",
                table: "share_links",
                columns: new[] { "org_id", "page_id" });

            migrationBuilder.CreateIndex(
                name: "IX_share_links_org_id_space_id",
                table: "share_links",
                columns: new[] { "org_id", "space_id" });

            migrationBuilder.CreateIndex(
                name: "IX_share_links_token_hash",
                table: "share_links",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_skills_org_id_owner_project_id",
                table: "skills",
                columns: new[] { "org_id", "owner_project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_skills_org_id_owner_team_id",
                table: "skills",
                columns: new[] { "org_id", "owner_team_id" });

            migrationBuilder.CreateIndex(
                name: "IX_skills_org_id_owner_user_id",
                table: "skills",
                columns: new[] { "org_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_org_id_source_id_path",
                table: "source_documents",
                columns: new[] { "org_id", "source_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_space_members_org_id_space_id_team_id",
                table: "space_members",
                columns: new[] { "org_id", "space_id", "team_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_space_members_org_id_space_id_user_id",
                table: "space_members",
                columns: new[] { "org_id", "space_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_space_members_org_id_team_id",
                table: "space_members",
                columns: new[] { "org_id", "team_id" });

            migrationBuilder.CreateIndex(
                name: "IX_space_members_org_id_user_id",
                table: "space_members",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_spaces_org_id_workspace_id_slug",
                table: "spaces",
                columns: new[] { "org_id", "workspace_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_members_org_id_team_id_user_id",
                table: "team_members",
                columns: new[] { "org_id", "team_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_members_org_id_user_id",
                table: "team_members",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_teams_org_id_lead_user_id",
                table: "teams",
                columns: new[] { "org_id", "lead_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_users_org_id_email",
                table: "users",
                columns: new[] { "org_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspace_members_org_id_user_id",
                table: "workspace_members",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_members_org_id_workspace_id_user_id",
                table: "workspace_members",
                columns: new[] { "org_id", "workspace_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_org_id_slug",
                table: "workspaces",
                columns: new[] { "org_id", "slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_tokens");

            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "capture_candidates");

            migrationBuilder.DropTable(
                name: "chunk_embeddings");

            migrationBuilder.DropTable(
                name: "comments");

            migrationBuilder.DropTable(
                name: "identity_user_claims");

            migrationBuilder.DropTable(
                name: "identity_user_logins");

            migrationBuilder.DropTable(
                name: "identity_user_tokens");

            migrationBuilder.DropTable(
                name: "invitations");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "memory_conflicts");

            migrationBuilder.DropTable(
                name: "memory_links");

            migrationBuilder.DropTable(
                name: "memory_proposals");

            migrationBuilder.DropTable(
                name: "memory_shadows");

            migrationBuilder.DropTable(
                name: "memory_versions");

            migrationBuilder.DropTable(
                name: "mentions");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "oauth_scopes");

            migrationBuilder.DropTable(
                name: "oauth_tokens");

            migrationBuilder.DropTable(
                name: "operations");

            migrationBuilder.DropTable(
                name: "org_members");

            migrationBuilder.DropTable(
                name: "page_restrictions");

            migrationBuilder.DropTable(
                name: "page_revisions");

            migrationBuilder.DropTable(
                name: "page_shares");

            migrationBuilder.DropTable(
                name: "page_versions");

            migrationBuilder.DropTable(
                name: "project_links");

            migrationBuilder.DropTable(
                name: "project_members");

            migrationBuilder.DropTable(
                name: "session_commits");

            migrationBuilder.DropTable(
                name: "session_records");

            migrationBuilder.DropTable(
                name: "share_links");

            migrationBuilder.DropTable(
                name: "skills");

            migrationBuilder.DropTable(
                name: "source_documents");

            migrationBuilder.DropTable(
                name: "space_members");

            migrationBuilder.DropTable(
                name: "team_members");

            migrationBuilder.DropTable(
                name: "workspace_members");

            migrationBuilder.DropTable(
                name: "chunks");

            migrationBuilder.DropTable(
                name: "comment_threads");

            migrationBuilder.DropTable(
                name: "identity_users");

            migrationBuilder.DropTable(
                name: "memories");

            migrationBuilder.DropTable(
                name: "oauth_authorizations");

            migrationBuilder.DropTable(
                name: "agent_sessions");

            migrationBuilder.DropTable(
                name: "knowledge_sources");

            migrationBuilder.DropTable(
                name: "pages");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropTable(
                name: "oauth_applications");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "spaces");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "workspaces");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
