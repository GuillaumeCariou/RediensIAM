using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultRoleId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultRoleId",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_DefaultRoleId",
                table: "projects",
                column: "DefaultRoleId");

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
                name: "FK_projects_roles_DefaultRoleId",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_projects_DefaultRoleId",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "DefaultRoleId",
                table: "projects");
        }
    }
}
