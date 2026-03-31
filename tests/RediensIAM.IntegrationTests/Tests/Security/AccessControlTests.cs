using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// Verifies that each role level can ONLY access what it's allowed to.
/// These are the critical security boundary tests.
/// </summary>
[Collection("RediensIAM")]
public class AccessControlTests(TestFixture fixture)
{
    // ── Cross-org isolation ───────────────────────────────────────────────────

    [Fact]
    public async Task OrgAdmin_CannotAccessOtherOrgsProjects()
    {
        // Org A admin
        var (orgA, orgAList) = await fixture.Seed.CreateOrgAsync();
        var adminA           = await fixture.Seed.CreateUserAsync(orgAList.Id);
        var tokenA           = fixture.Seed.OrgAdminToken(adminA.Id, orgA.Id);
        var clientA          = fixture.ClientWithToken(tokenA);
        fixture.Keto.AllowAll();

        // Org B project
        var (orgB, _)  = await fixture.Seed.CreateOrgAsync();
        var projectB   = await fixture.Seed.CreateProjectAsync(orgB.Id);

        // Admin A tries to access org B's project
        var res = await clientA.GetAsync($"/org/projects/{projectB.Id}");

        // Must not succeed (404 acceptable — project is not in admin A's org)
        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task ProjectManager_CannotAccessOtherProjectResources()
    {
        // Project A manager
        var (orgA, _)  = await fixture.Seed.CreateOrgAsync();
        var projectA   = await fixture.Seed.CreateProjectAsync(orgA.Id);
        var listA      = await fixture.Seed.CreateUserListAsync(orgA.Id);
        projectA.AssignedUserListId = listA.Id;
        await fixture.Db.SaveChangesAsync();
        var managerA  = await fixture.Seed.CreateUserAsync(listA.Id);
        var tokenA    = fixture.Seed.ProjectManagerToken(managerA.Id, orgA.Id, projectA.Id);
        var clientA   = fixture.ClientWithToken(tokenA);
        fixture.Keto.AllowAll();

        // Project B (different org)
        var (orgB, _) = await fixture.Seed.CreateOrgAsync();
        var projectB  = await fixture.Seed.CreateProjectAsync(orgB.Id);
        var listB     = await fixture.Seed.CreateUserListAsync(orgB.Id);
        projectB.AssignedUserListId = listB.Id;
        await fixture.Db.SaveChangesAsync();
        var userB = await fixture.Seed.CreateUserAsync(listB.Id);

        // Manager A tries to access user in project B
        var res = await clientA.GetAsync($"/project/users/{userB.Id}");

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    // ── Privilege escalation ──────────────────────────────────────────────────

    [Fact]
    public async Task RegularUser_CannotCreateOrg()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);
        fixture.Keto.DenyAll();

        var res = await client.PostAsJsonAsync("/admin/organizations", new
        {
            name = "Escalation Org",
            slug = SeedData.UniqueSlug()
        });

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegularUser_CannotDeleteAnotherUser()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user1  = await fixture.Seed.CreateUserAsync(list.Id);
        var user2  = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user1.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);
        fixture.Keto.DenyAll();

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}/users/{user2.Id}");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegularUser_CannotAssignOrgAdmin()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);
        fixture.Keto.DenyAll();

        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id = user.Id,
            role    = "org_admin"
        });

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    // ── Keto permission denial ────────────────────────────────────────────────

    [Fact]
    public async Task SuperAdminToken_KetoDeniesSuperAdmin_StillAccessed()
    {
        // Super-admin role is checked via JWT claims (RequireManagementLevel filter),
        // NOT via Keto. DenyAll only affects Keto read checks; the admin endpoints
        // do not consult Keto for super-admin level access.
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(user.Id);
        var client         = fixture.ClientWithToken(token);
        fixture.Keto.DenyAll();

        var res = await client.GetAsync("/admin/organizations");

        // Super-admin bypasses Keto; endpoint returns 200 regardless of Keto state
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── IDOR prevention ───────────────────────────────────────────────────────

    [Fact]
    public async Task User_CannotReadAnotherUsersProfile_ViaAdminEndpoint()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user1  = await fixture.Seed.CreateUserAsync(list.Id);
        var user2  = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user1.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);
        fixture.Keto.DenyAll();

        // User 1 should not be able to read user 2 via admin endpoint
        var res = await client.GetAsync($"/admin/users/{user2.Id}");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }
}
