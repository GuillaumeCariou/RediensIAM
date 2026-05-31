using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Instances",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PublicUrl = table.Column<string>(type: "text", nullable: false),
                    AdminSpaOrigin = table.Column<string>(type: "text", nullable: false),
                    Domain = table.Column<string>(type: "text", nullable: false),
                    AdminPath = table.Column<string>(type: "text", nullable: false),
                    TrustedProxies = table.Column<string>(type: "text", nullable: false),
                    PublicPort = table.Column<int>(type: "integer", nullable: false),
                    AdminPort = table.Column<int>(type: "integer", nullable: false),
                    HydraAdminUrl = table.Column<string>(type: "text", nullable: false),
                    HydraPublicUrl = table.Column<string>(type: "text", nullable: false),
                    KetoReadUrl = table.Column<string>(type: "text", nullable: false),
                    KetoWriteUrl = table.Column<string>(type: "text", nullable: false),
                    SmtpHost = table.Column<string>(type: "text", nullable: false),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpStartTls = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpUsername = table.Column<string>(type: "text", nullable: false),
                    SmtpFromAddress = table.Column<string>(type: "text", nullable: false),
                    SmtpFromName = table.Column<string>(type: "text", nullable: false),
                    CacheInstanceName = table.Column<string>(type: "text", nullable: false),
                    PatCacheTtlMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxLoginAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockoutMinutes = table.Column<int>(type: "integer", nullable: false),
                    OtpTtlSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxSmsPerWindow = table.Column<int>(type: "integer", nullable: false),
                    SmsWindowMinutes = table.Column<int>(type: "integer", nullable: false),
                    ArgonTimeCost = table.Column<int>(type: "integer", nullable: false),
                    ArgonMemoryCost = table.Column<int>(type: "integer", nullable: false),
                    ArgonParallelism = table.Column<int>(type: "integer", nullable: false),
                    AuditRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    InviteExpiryHours = table.Column<int>(type: "integer", nullable: false),
                    ConfigVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReconfiguredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instances", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Instances");
        }
    }
}
