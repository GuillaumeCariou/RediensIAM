using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Migrations
{
    /// <inheritdoc />
    public partial class AddMinPasswordLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinPasswordLength",
                table: "projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinPasswordLength",
                table: "projects");
        }
    }
}
