using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImpersonationSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "impersonation_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorLevel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_impersonation_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_impersonation_sessions_ActorUserId_RevokedAt",
                table: "impersonation_sessions",
                columns: new[] { "ActorUserId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_impersonation_sessions_OrgId",
                table: "impersonation_sessions",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_impersonation_sessions_TokenHash",
                table: "impersonation_sessions",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "impersonation_sessions");
        }
    }
}
