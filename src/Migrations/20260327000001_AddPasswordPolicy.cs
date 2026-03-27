using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PasswordRequireUppercase",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PasswordRequireLowercase",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PasswordRequireDigit",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PasswordRequireSpecial",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PasswordRequireUppercase", table: "projects");
            migrationBuilder.DropColumn(name: "PasswordRequireLowercase", table: "projects");
            migrationBuilder.DropColumn(name: "PasswordRequireDigit",     table: "projects");
            migrationBuilder.DropColumn(name: "PasswordRequireSpecial",   table: "projects");
        }
    }
}
