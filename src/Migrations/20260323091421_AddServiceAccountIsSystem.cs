using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceAccountIsSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "service_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "service_accounts");
        }
    }
}
