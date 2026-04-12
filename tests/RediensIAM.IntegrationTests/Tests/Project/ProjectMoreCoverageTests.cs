using System.Net.Http.Json;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ProjectAdmin;

/// <summary>
/// Covers ProjectController lines not yet hit by existing test files:
///   - PATCH /project/info   — valid default_role_id (lines 113-114)
///   - DELETE /project/users/{id}/roles/{roleId} — NotFoundException (line 184)
///   - DELETE /project/users/{id}/sessions       — user not in project list (line 232)
///   - POST /project/cleanup — dry_run=false with orphaned roles (line 340)
/// </summary>
[Collection("RediensIAM")]
public class ProjectMoreCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, UserList list, HttpClient client)>
        ScaffoldAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        return (org, project, list, fixture.ClientWithToken(token));
    }

    // ── PATCH /project/info — valid default_role_id (lines 113-114) ──────────

    [Fact]
    public async Task UpdateInfo_ValidDefaultRoleId_SetsDefaultRole()
    {
        var (_, project, _, client) = await ScaffoldAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "Member");

        var res = await client.PatchAsJsonAsync("/project/info", new
        {
            default_role_id = role.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var reloaded = fixture.Db.Projects.Find(project.Id);
        reloaded!.DefaultRoleId.Should().Be(role.Id);
    }

    // ── DELETE /project/users/{id}/roles/{roleId} — NotFoundException (line 184) ─

    [Fact]
    public async Task RemoveUserRole_NonExistentAssignment_Returns404()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        // Delete a role assignment that was never created → NotFoundException
        var res = await client.DeleteAsync($"/project/users/{user.Id}/roles/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /project/users/{id}/sessions — user not in list (line 232) ───

    [Fact]
    public async Task ForceLogout_UserNotInProjectList_Returns404()
    {
        var (org, _, _, client) = await ScaffoldAsync();
        // Create a user in a different list — not the project's assigned list
        var otherList = await fixture.Seed.CreateUserListAsync(org.Id);
        var outsider  = await fixture.Seed.CreateUserAsync(otherList.Id);

        var res = await client.DeleteAsync($"/project/users/{outsider.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /project/info — OrgAdmin without ?project_id= (ProjectId getter line 37) ─

    [Fact]
    public async Task GetProjectInfo_OrgAdminWithoutProjectId_Returns500()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        // OrgAdmin has no project_id claim (empty string) and provides no ?project_id= query param.
        // The ProjectId getter enters the OrgAdmin branch, the inner TryParse fails (q==null),
        // falls through to line 37 (closing brace), then Guid.Parse("") throws → 500.
        var res = await client.GetAsync("/project/info");

        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // ── POST /project/cleanup — dry_run=false with orphaned roles (line 340) ─

    [Fact]
    public async Task Cleanup_DryRunFalse_WithOrphanedRoles_DeletesKetoTuples()
    {
        var (_, project, list, client) = await ScaffoldAsync();

        // Create a user outside the project's assigned list and seed an orphaned role for them
        var (_, otherList) = await fixture.Seed.CreateOrgAsync();
        var orphanUser = await fixture.Seed.CreateUserAsync(otherList.Id);
        var role       = await fixture.Seed.CreateRoleAsync(project.Id, "Orphan");

        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            Id        = Guid.NewGuid(),
            UserId    = orphanUser.Id,
            ProjectId = project.Id,
            RoleId    = role.Id,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/project/cleanup", new { dry_run = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("orphaned_roles_removed").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        body.GetProperty("dry_run").GetBoolean().Should().BeFalse();
    }
}
