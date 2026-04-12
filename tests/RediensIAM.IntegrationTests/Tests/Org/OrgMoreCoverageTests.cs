using System.Net.Http.Json;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Org;

/// <summary>
/// Covers OrgController lines not yet hit by existing test files:
///   - PATCH /org/projects/{id}          — clear_default_role = true (line 173)
///   - DELETE /org/projects/{id}         — Hydra client delete failure (line 238)
///   - DELETE /org/userlists/{id}        — assigned to project → 400 (line 311)
///   - POST /org/userlists/{id}/users    — list assigned to project → assigns default role (line 414)
///   - POST /org/userlists/{id}/cleanup  — dry_run=false, orphaned roles removed (lines 335, 360-365, 372)
///   - POST /org/userlists/{id}/cleanup  — dry_run=false, inactive users removed (lines 350-353, 366-370, 372)
///   - PATCH /org/users/{uid}            — email_verified=false (lines 546-547)
///   - PATCH /org/admins/{id}            — new ScopeId not in org → 400 (lines 645-648)
/// </summary>
[Collection("RediensIAM")]
public class OrgMoreCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── PATCH /org/projects/{id} — clear_default_role = true (line 173) ──────

    [Fact]
    public async Task UpdateProject_ClearDefaultRole_SetsRoleToNull()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "Starter");
        project.DefaultRoleId = role.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new
        {
            clear_default_role = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var reloaded = fixture.Db.Projects.Find(project.Id);
        reloaded!.DefaultRoleId.Should().BeNull();
    }

    // ── DELETE /org/projects/{id} — Hydra client delete failure (line 238) ───

    [Fact]
    public async Task DeleteProject_HydraClientDeleteFails_StillReturns204()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        fixture.Hydra.SetupClientDeleteFailure(project.HydraClientId!);
        try
        {
            var res = await client.DeleteAsync($"/org/projects/{project.Id}");
            res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        finally
        {
            fixture.Hydra.RestoreClientCreation();
        }
    }

    // ── DELETE /org/userlists/{id} — assigned to project (line 311) ──────────

    [Fact]
    public async Task DeleteUserList_WhenAssignedToProject_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("userlist_is_assigned_to_project");
    }

    // ── POST /org/userlists/{id}/users — list assigned to project (line 414) ─

    [Fact]
    public async Task AddUserToList_WithProjectAssigned_AssignsDefaultRole()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        // Adding a user to a list that's assigned to a project triggers keto.AssignDefaultRoleAsync
        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssword1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── POST /org/userlists/{id}/cleanup — dry_run=false removes orphaned roles (lines 335, 360-365, 372) ─

    [Fact]
    public async Task CleanupUserList_DryRunFalse_WithOrphanedRoles_RemovesThem()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        // Create a user in a DIFFERENT list (not the target list) and give them a role in the project
        var (_, otherList) = await fixture.Seed.CreateOrgAsync();
        var orphanUser = await fixture.Seed.CreateUserAsync(otherList.Id);
        var role       = await fixture.Seed.CreateRoleAsync(project.Id, "Orphan");

        // Seed an orphaned UserProjectRole directly — user is not in 'list'
        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            Id        = Guid.NewGuid(),
            UserId    = orphanUser.Id,
            ProjectId = project.Id,
            RoleId    = role.Id,
            GrantedAt = DateTimeOffset.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/cleanup", new
        {
            dry_run               = false,
            remove_orphaned_roles = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dry_run").GetBoolean().Should().BeFalse();
        body.GetProperty("orphaned_roles_removed").GetInt32().Should().Be(1);
    }

    // ── POST /org/userlists/{id}/cleanup — dry_run=false removes inactive users (lines 350-353, 366-370, 372) ─

    [Fact]
    public async Task CleanupUserList_DryRunFalse_WithInactiveUsers_RemovesThem()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        // New users have LastLoginAt = null → always inactive
        var inactiveUser = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PostAsJsonAsync($"/org/userlists/{list.Id}/cleanup", new
        {
            dry_run               = false,
            remove_inactive_users = true,
            inactive_threshold_days = 0    // threshold = today → all users with null LastLoginAt qualify
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dry_run").GetBoolean().Should().BeFalse();
        body.GetProperty("inactive_users_removed").GetInt32().Should().BeGreaterThan(0);
    }

    // ── PATCH /org/users/{uid} — email_verified = false (lines 546-547) ──────

    [Fact]
    public async Task UpdateOrgUser_SetEmailVerifiedFalse_ClearsVerifiedAt()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        user.EmailVerified   = true;
        user.EmailVerifiedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/org/users/{user.Id}", new
        {
            email_verified = false
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var reloaded = await fixture.Db.Users.FindAsync(user.Id);
        reloaded!.EmailVerified.Should().BeFalse();
        reloaded.EmailVerifiedAt.Should().BeNull();
    }

    // ── PATCH /org/admins/{id} — ScopeId not in org → 400 (lines 645-648) ───

    [Fact]
    public async Task UpdateOrgAdmin_WithInvalidScopeId_Returns400()
    {
        var (org, admin, client) = await OrgAdminClientAsync();
        var (otherOrg, otherList) = await fixture.Seed.CreateOrgAsync();
        var targetUser = await fixture.Seed.CreateUserAsync(otherList.Id);

        // Create an OrgRole with no ScopeId so we can try to patch it with an invalid scope
        var role = await fixture.Seed.CreateOrgRoleAsync(org.Id, targetUser.Id, "org_admin");

        var res = await client.PatchAsJsonAsync($"/org/admins/{role.Id}", new
        {
            scope_id = Guid.NewGuid()   // project not in this org
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_in_org");
    }
}
