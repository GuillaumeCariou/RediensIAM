using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Data.Migrations
{
    /// <summary>
    /// A project's default role becomes a set: the single <c>projects.DefaultRoleId</c> foreign key
    /// gives way to a <c>roles.IsDefault</c> flag, so a project may grant several roles on sign-up
    /// or none.
    ///
    /// <para>
    /// The order below is the whole point and is why this is not the scaffolded version: the flag
    /// is added and backfilled <i>before</i> the old column goes. Dropping first would take every
    /// tenant's configured default with it, and nothing in the schema would record that it ever
    /// existed. <c>Down</c> reverses it the same way and keeps the strongest of the defaults, which
    /// is the only one a single column can hold.
    /// </para>
    /// </summary>
    public partial class ProjectDefaultRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE roles r
                   SET "IsDefault" = true
                  FROM projects p
                 WHERE p."DefaultRoleId" = r."Id";
                """);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.Sql("""
                UPDATE projects p
                   SET "DefaultRoleId" = (
                       SELECT r."Id" FROM roles r
                        WHERE r."ProjectId" = p."Id" AND r."IsDefault"
                        ORDER BY r."Rank", r."Id"
                        LIMIT 1);
                """);

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "roles");
        }
    }
}
