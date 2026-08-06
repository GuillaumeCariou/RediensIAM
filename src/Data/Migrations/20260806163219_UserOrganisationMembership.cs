using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RediensIAM.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserOrganisationMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_OrgId",
                table: "users",
                column: "OrgId");

            migrationBuilder.AddForeignKey(
                name: "FK_users_organisations_OrgId",
                table: "users",
                column: "OrgId",
                principalTable: "organisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_organisations_OrgId",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_OrgId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "users");
        }
    }
}
