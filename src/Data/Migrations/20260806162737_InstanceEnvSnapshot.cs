using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstanceEnvSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvSnapshot",
                table: "Instances",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvSnapshot",
                table: "Instances");
        }
    }
}
