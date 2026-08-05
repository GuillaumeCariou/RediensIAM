using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RediensIAM.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropDragonflySharedStateToPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rate_counters",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_counters", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "shared_state",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    value = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_state", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "webhook_pending",
                columns: table => new
                {
                    job_json = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_pending", x => x.job_json);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rate_counters_window_end",
                table: "rate_counters",
                column: "window_end");

            migrationBuilder.CreateIndex(
                name: "ix_shared_state_expires_at",
                table: "shared_state",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_pending_score",
                table: "webhook_pending",
                column: "score");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "rate_counters");

            migrationBuilder.DropTable(
                name: "shared_state");

            migrationBuilder.DropTable(
                name: "webhook_pending");
        }
    }
}
