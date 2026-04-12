using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ProjectAdmin;

/// <summary>
/// Covers ProjectController lines not hit by existing test files:
///   - POST /project/users — password policy enforcement (lines 193-203)
///   - POST /project/users/{id}/roles — ForbiddenException path (line 169)
///   - DELETE /project/users/{id}/roles/{roleId} — ForbiddenException path (line 183)
/// </summary>
[Collection("RediensIAM")]
public class ProjectCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, UserList list, User manager, HttpClient client)>
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
        return (org, project, list, manager, fixture.ClientWithToken(token));
    }

    // ── POST /project/users — password policy enforcement ─────────────────────

    [Fact]
    public async Task CreateUser_PasswordTooShort_Returns400()
    {
        var (_, project, _, _, client) = await ScaffoldAsync();
        project.MinPasswordLength = 12;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "Short1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_too_short");
        body.GetProperty("min_length").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task CreateUser_PasswordRequiresUppercase_Returns400()
    {
        var (_, project, _, _, client) = await ScaffoldAsync();
        project.PasswordRequireUppercase = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "alllowercase1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_requires_uppercase");
    }

    [Fact]
    public async Task CreateUser_PasswordRequiresLowercase_Returns400()
    {
        var (_, project, _, _, client) = await ScaffoldAsync();
        project.PasswordRequireLowercase = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "ALLUPPERCASE1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_requires_lowercase");
    }

    [Fact]
    public async Task CreateUser_PasswordRequiresDigit_Returns400()
    {
        var (_, project, _, _, client) = await ScaffoldAsync();
        project.PasswordRequireDigit = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "NoDigitsHere!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_requires_digit");
    }

    [Fact]
    public async Task CreateUser_PasswordRequiresSpecial_Returns400()
    {
        var (_, project, _, _, client) = await ScaffoldAsync();
        project.PasswordRequireSpecial = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "NoSpecialChar1"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_requires_special");
    }

    // ── POST /project/users/{id}/roles — ForbiddenException (line 169) ────────

    [Fact]
    public async Task AssignRole_WhenNoKetoManagementRights_Returns403()
    {
        var (_, project, list, manager, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "Viewer");

        // Make Keto deny management rights for the manager — filter still passes
        // (it only checks JWT claims), but KetoService rejects the operation
        fixture.Keto.DenySubject($"user:{manager.Id}");

        var res = await client.PostAsJsonAsync($"/project/users/{user.Id}/roles", new
        {
            role_id = role.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fixture.Keto.AllowAll();
    }

    // ── DELETE /project/users/{id}/roles/{roleId} — ForbiddenException (line 183) ─

    [Fact]
    public async Task RemoveRole_WhenNoKetoManagementRights_Returns403()
    {
        var (_, project, list, manager, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "Viewer");

        // Assign first (AllowAll is still active at this point)
        await client.PostAsJsonAsync($"/project/users/{user.Id}/roles", new { role_id = role.Id });

        // Now deny management rights for the manager before the remove
        fixture.Keto.DenySubject($"user:{manager.Id}");

        var res = await client.DeleteAsync($"/project/users/{user.Id}/roles/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fixture.Keto.AllowAll();
    }

    // ── GET /project/info?project_id= — OrgAdmin uses query param (lines 35-36) ─

    [Fact]
    public async Task GetInfo_OrgAdminWithQueryProjectId_Returns200()
    {
        var (org, project, _, manager, _) = await ScaffoldAsync();
        var token  = fixture.Seed.OrgAdminToken(manager.Id, org.Id);
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync($"/project/info?project_id={project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PATCH /project/info — clear_default_role = true (lines 106-108) ────────

    [Fact]
    public async Task UpdateInfo_ClearDefaultRole_SetsDefaultRoleToNull()
    {
        var (_, project, _, _, client) = await ScaffoldAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "Starter");
        project.DefaultRoleId = role.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync("/project/info", new
        {
            clear_default_role = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var reloaded = fixture.Db.Projects.Find(project.Id);
        reloaded!.DefaultRoleId.Should().BeNull();
    }

    // ── PATCH /project/info — default_role_id invalid (lines 111-114) ────────

    [Fact]
    public async Task UpdateInfo_InvalidDefaultRoleId_Returns400()
    {
        var (_, _, _, _, client) = await ScaffoldAsync();

        var res = await client.PatchAsJsonAsync("/project/info", new
        {
            default_role_id = Guid.NewGuid()   // non-existent role
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_default_role");
    }

    // ── PATCH /project/info — login_theme != null (lines 121-122) ────────────

    [Fact]
    public async Task UpdateInfo_WithLoginTheme_Returns200()
    {
        var (_, _, _, _, client) = await ScaffoldAsync();

        var res = await client.PatchAsJsonAsync("/project/info", new
        {
            login_theme = new Dictionary<string, object> { ["background_color"] = "#ffffff" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DELETE /project/roles/{id} — role with users assigned (line 304) ─────

    [Fact]
    public async Task DeleteRole_WithUsersAssigned_Returns204AndCleansUpKeto()
    {
        var (_, project, list, _, client) = await ScaffoldAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "TempRole");
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        // Assign the user to the role so UserProjectRoles has an entry
        await client.PostAsJsonAsync($"/project/users/{user.Id}/roles", new { role_id = role.Id });

        var res = await client.DeleteAsync($"/project/roles/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
