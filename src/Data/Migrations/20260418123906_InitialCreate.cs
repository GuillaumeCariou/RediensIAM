using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RediensIAM.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TargetId = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    IpAddress = table.Column<string>(type: "text", nullable: true),
                    UserAgent = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SecretEnc = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Events = table.Column<string>(type: "jsonb", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhooks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    WebhookId = table.Column<Guid>(type: "uuid", nullable: false),
                    Event = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_webhooks_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "webhooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backup_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_codes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "email_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    NewEmail = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "org_roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    GrantedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "org_smtp_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false, defaultValue: 587),
                    StartTls = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Username = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PasswordEnc = table.Column<string>(type: "text", nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    FromName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_smtp_configs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "organisations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrgListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SuspendedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    AuditRetentionDays = table.Column<int>(type: "integer", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organisations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_lists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    Immovable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_lists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_lists_organisations_OrgId",
                        column: x => x.OrgId,
                        principalTable: "organisations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    HydraClientId = table.Column<string>(type: "text", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_accounts_user_lists_UserListId",
                        column: x => x.UserListId,
                        principalTable: "user_lists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Discriminator = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    EmailVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    PhoneVerified = table.Column<bool>(type: "boolean", nullable: false),
                    TotpEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TotpSecret = table.Column<string>(type: "text", nullable: true),
                    WebAuthnEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NewDeviceAlertsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_users_user_lists_UserListId",
                        column: x => x.UserListId,
                        principalTable: "user_lists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "personal_access_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ServiceAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_personal_access_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_personal_access_tokens_service_accounts_ServiceAccountId",
                        column: x => x.ServiceAccountId,
                        principalTable: "service_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_account_roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ServiceAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_account_roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_account_roles_service_accounts_ServiceAccountId",
                        column: x => x.ServiceAccountId,
                        principalTable: "service_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_social_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderUserId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_social_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_social_accounts_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webauthn_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<byte[]>(type: "bytea", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    SignCount = table.Column<long>(type: "bigint", nullable: false),
                    DeviceName = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webauthn_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_webauthn_credentials_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    HydraClientId = table.Column<string>(type: "text", nullable: true),
                    AssignedUserListId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoginTheme = table.Column<string>(type: "jsonb", nullable: false),
                    LoginTemplate = table.Column<string>(type: "text", nullable: true),
                    RequireRoleToLogin = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AllowSelfRegistration = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    RequireMfa = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedEmailDomains = table.Column<string[]>(type: "text[]", nullable: false),
                    EmailVerificationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SmsVerificationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DefaultRoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    MinPasswordLength = table.Column<int>(type: "integer", nullable: false),
                    PasswordRequireUppercase = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordRequireLowercase = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordRequireDigit = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordRequireSpecial = table.Column<bool>(type: "boolean", nullable: false),
                    EmailFromName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IpAllowlist = table.Column<string>(type: "jsonb", nullable: false),
                    CheckBreachedPasswords = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AllowedScopes = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_projects_organisations_OrgId",
                        column: x => x.OrgId,
                        principalTable: "organisations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_projects_user_lists_AssignedUserListId",
                        column: x => x.AssignedUserListId,
                        principalTable: "user_lists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Rank = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_roles_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saml_idp_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MetadataUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SsoUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CertificatePem = table.Column<string>(type: "text", nullable: true),
                    EmailAttributeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: "email"),
                    DisplayNameAttributeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    JitProvisioning = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    DefaultRoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saml_idp_configs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_saml_idp_configs_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_saml_idp_configs_roles_DefaultRoleId",
                        column: x => x.DefaultRoleId,
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "user_project_roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_project_roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_project_roles_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_project_roles_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_project_roles_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_Action_CreatedAt",
                table: "audit_log",
                columns: new[] { "Action", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_ActorId_CreatedAt",
                table: "audit_log",
                columns: new[] { "ActorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_OrgId_CreatedAt",
                table: "audit_log",
                columns: new[] { "OrgId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_ProjectId_CreatedAt",
                table: "audit_log",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_codes_UserId",
                table: "backup_codes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_email_tokens_TokenHash",
                table: "email_tokens",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_email_tokens_UserId",
                table: "email_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_org_roles_OrgId_UserId_Role_ScopeId",
                table: "org_roles",
                columns: new[] { "OrgId", "UserId", "Role", "ScopeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_org_roles_UserId",
                table: "org_roles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_org_smtp_configs_OrgId",
                table: "org_smtp_configs",
                column: "OrgId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organisations_OrgListId",
                table: "organisations",
                column: "OrgListId");

            migrationBuilder.CreateIndex(
                name: "IX_organisations_Slug",
                table: "organisations",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_personal_access_tokens_ServiceAccountId",
                table: "personal_access_tokens",
                column: "ServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_personal_access_tokens_TokenHash",
                table: "personal_access_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_AssignedUserListId",
                table: "projects",
                column: "AssignedUserListId");

            migrationBuilder.CreateIndex(
                name: "IX_projects_DefaultRoleId",
                table: "projects",
                column: "DefaultRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_projects_OrgId_Slug",
                table: "projects",
                columns: new[] { "OrgId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_roles_ProjectId_Name",
                table: "roles",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_saml_idp_configs_DefaultRoleId",
                table: "saml_idp_configs",
                column: "DefaultRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_saml_idp_configs_ProjectId",
                table: "saml_idp_configs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_service_account_roles_ServiceAccountId",
                table: "service_account_roles",
                column: "ServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_service_account_roles_ServiceAccountId_Role_OrgId_ProjectId",
                table: "service_account_roles",
                columns: new[] { "ServiceAccountId", "Role", "OrgId", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_accounts_UserListId",
                table: "service_accounts",
                column: "UserListId");

            migrationBuilder.CreateIndex(
                name: "IX_service_accounts_UserListId_Name",
                table: "service_accounts",
                columns: new[] { "UserListId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_lists_OrgId",
                table: "user_lists",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_user_project_roles_ProjectId",
                table: "user_project_roles",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_user_project_roles_RoleId",
                table: "user_project_roles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_user_project_roles_UserId",
                table: "user_project_roles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_project_roles_UserId_ProjectId_RoleId",
                table: "user_project_roles",
                columns: new[] { "UserId", "ProjectId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_social_accounts_Provider_ProviderUserId",
                table: "user_social_accounts",
                columns: new[] { "Provider", "ProviderUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_social_accounts_UserId",
                table: "user_social_accounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_users_UserListId_Active",
                table: "users",
                columns: new[] { "UserListId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_users_UserListId_Email",
                table: "users",
                columns: new[] { "UserListId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_UserListId_Username_Discriminator",
                table: "users",
                columns: new[] { "UserListId", "Username", "Discriminator" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webauthn_credentials_CredentialId",
                table: "webauthn_credentials",
                column: "CredentialId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webauthn_credentials_UserId",
                table: "webauthn_credentials",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_CreatedAt",
                table: "webhook_deliveries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_WebhookId",
                table: "webhook_deliveries",
                column: "WebhookId");

            migrationBuilder.CreateIndex(
                name: "IX_webhooks_OrgId",
                table: "webhooks",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_webhooks_ProjectId",
                table: "webhooks",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_backup_codes_users_UserId",
                table: "backup_codes",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_email_tokens_users_UserId",
                table: "email_tokens",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_org_roles_organisations_OrgId",
                table: "org_roles",
                column: "OrgId",
                principalTable: "organisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_org_roles_users_UserId",
                table: "org_roles",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_org_smtp_configs_organisations_OrgId",
                table: "org_smtp_configs",
                column: "OrgId",
                principalTable: "organisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_organisations_user_lists_OrgListId",
                table: "organisations",
                column: "OrgListId",
                principalTable: "user_lists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_projects_roles_DefaultRoleId",
                table: "projects",
                column: "DefaultRoleId",
                principalTable: "roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_projects_organisations_OrgId",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "FK_user_lists_organisations_OrgId",
                table: "user_lists");

            migrationBuilder.DropForeignKey(
                name: "FK_projects_user_lists_AssignedUserListId",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "FK_projects_roles_DefaultRoleId",
                table: "projects");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "backup_codes");

            migrationBuilder.DropTable(
                name: "email_tokens");

            migrationBuilder.DropTable(
                name: "org_roles");

            migrationBuilder.DropTable(
                name: "org_smtp_configs");

            migrationBuilder.DropTable(
                name: "personal_access_tokens");

            migrationBuilder.DropTable(
                name: "saml_idp_configs");

            migrationBuilder.DropTable(
                name: "service_account_roles");

            migrationBuilder.DropTable(
                name: "user_project_roles");

            migrationBuilder.DropTable(
                name: "user_social_accounts");

            migrationBuilder.DropTable(
                name: "webauthn_credentials");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "service_accounts");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "webhooks");

            migrationBuilder.DropTable(
                name: "organisations");

            migrationBuilder.DropTable(
                name: "user_lists");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
