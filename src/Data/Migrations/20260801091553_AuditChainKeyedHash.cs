using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Data.Migrations
{
    /// <summary>
    /// Room for the "k{keyId}:" envelope in front of the 64 hex digits, now that a chain link is
    /// an HMAC under a rotatable key rather than a bare digest.
    ///
    /// Deliberately no data migration. Rows hashed under the old unkeyed scheme keep the hashes
    /// they have; re-chaining them would mean rewriting every row of an append-only table with
    /// hashes computed after the fact, which proves nothing about what those rows said when they
    /// were written and would destroy the one signal worth having. They are read back as
    /// unverifiable instead — see AuditChain.Verify.
    /// </summary>
    public partial class AuditChainKeyedHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PrevHash",
                table: "audit_log",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Hash",
                table: "audit_log",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldDefaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PrevHash",
                table: "audit_log",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Hash",
                table: "audit_log",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80,
                oldDefaultValue: "");
        }
    }
}
