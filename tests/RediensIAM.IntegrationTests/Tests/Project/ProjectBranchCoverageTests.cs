using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ProjectAdmin;

/// <summary>
/// Covers the "false" branches for every null-check guard in ProjectController
/// that existing tests only hit on the "true" path:
///   - GET  /project/info            — project with no list / no default role (lines 60-61)
///   - PATCH /project/info           — empty body (lines 81-94)
///   - GET  /project/users           — no user list (line 131)
///   - GET  /project/users/{id}      — no user list (line 149)
///   - POST /project/users/{id}/roles — project not found (line 163)
///   - DELETE /project/users/{id}/roles/{rid} — project not found (line 177)
///   - POST /project/users           — no user list (line 191)
///   - DELETE /project/users/{id}/sessions — no user list (line 229)
///   - GET  /project/stats           — no user list (line 241)
///   - GET  /project/roles           — project not found (line 260)
///   - POST /project/roles           — project not found (line 271)
///   - PATCH /project/roles/{id}     — project not found (line 286), empty body (289-290)
///   - DELETE /project/roles/{id}    — project not found (line 298)
///   - GET  /project/audit-log       — project not found (line 315)
///   - POST /project/cleanup         — no user list (line 329)
/// </summary>
[Collection("RediensIAM")]
public class ProjectBranchCoverageTests(TestFixture fixture)
{
    // ── Scaffolding ───────────────────────────────────────────────────────────

    /// <summary>Project with an assigned user list (standard happy-path setup).</summary>
    private async Task<(Project project, HttpClient client)> ScaffoldWithListAsync()
    {
        var (org, _)   = await fixture.Seed.CreateOrgAsync();
        var project    = await fixture.Seed.CreateProjectAsync(org.Id);
        var list       = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        return (project, fixture.ClientWithToken(token));
    }

    /// <summary>Project WITHOUT an assigned user list.</summary>
    private async Task<(Project project, HttpClient client)> ScaffoldWithoutListAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        // Deliberately no AssignedUserListId
        var manager = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        return (project, fixture.ClientWithToken(token));
    }

    /// <summary>
    /// Returns a client whose token points to a project that is then deleted from the DB,
    /// so GetProjectAsync() returns null for every subsequent request.
    /// </summary>
    private async Task<(Project project, Role role, HttpClient client)> ScaffoldDeletedProjectAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "TempRole");
        fixture.Keto.AllowAll();

        // Delete project so GetProjectAsync() returns null
        fixture.Db.Projects.Remove(project);
        await fixture.Db.SaveChangesAsync();

        return (project, role, fixture.ClientWithToken(token));
    }

    // ── GET /project/info — null AssignedUserList and DefaultRole (lines 60-61) ─

    [Fact]
    public async Task GetInfo_ProjectWithoutListOrDefaultRole_ReturnsNullNames()
    {
        var (_, client) = await ScaffoldWithoutListAsync();

        var res = await client.GetAsync("/project/info");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // null because AssignedUserList is not loaded (no list assigned)
        body.GetProperty("assigned_user_list_name").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("default_role_name").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── PATCH /project/info — empty body (lines 81-94) ───────────────────────

    [Fact]
    public async Task UpdateInfo_EmptyBody_Returns200_CoversAllFalseBranches()
    {
        // Sending {} means every if(body.X != null/HasValue) is false → the "else" branch
        var (_, client) = await ScaffoldWithListAsync();

        var res = await client.PatchAsJsonAsync("/project/info", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GET /project/users — no user list (line 131) ─────────────────────────

    [Fact]
    public async Task ListUsers_ProjectWithoutUserList_Returns404()
    {
        var (_, client) = await ScaffoldWithoutListAsync();

        var res = await client.GetAsync("/project/users");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /project/users/{id} — no user list (line 149) ────────────────────

    [Fact]
    public async Task GetUser_ProjectWithoutUserList_Returns404()
    {
        var (_, client) = await ScaffoldWithoutListAsync();

        var res = await client.GetAsync($"/project/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /project/users/{id}/roles — project not found (line 163) ─────────

    [Fact]
    public async Task AssignRole_ProjectNotFound_Returns404()
    {
        var (_, role, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.PostAsJsonAsync($"/project/users/{Guid.NewGuid()}/roles",
            new { role_id = role.Id });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /project/users/{id}/roles/{rid} — project not found (line 177) ─

    [Fact]
    public async Task RemoveRole_ProjectNotFound_Returns404()
    {
        var (_, role, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.DeleteAsync($"/project/users/{Guid.NewGuid()}/roles/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /project/users — no user list (line 191) ────────────────────────

    [Fact]
    public async Task CreateUser_ProjectWithoutUserList_Returns400()
    {
        var (_, client) = await ScaffoldWithoutListAsync();

        var res = await client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!1"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_user_list");
    }

    // ── DELETE /project/users/{id}/sessions — no user list (line 229) ─────────

    [Fact]
    public async Task ForceLogout_ProjectWithoutUserList_Returns404()
    {
        var (_, client) = await ScaffoldWithoutListAsync();

        var res = await client.DeleteAsync($"/project/users/{Guid.NewGuid()}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /project/stats — no user list (line 241) ─────────────────────────

    [Fact]
    public async Task GetStats_ProjectWithoutUserList_Returns404()
    {
        var (_, client) = await ScaffoldWithoutListAsync();

        var res = await client.GetAsync("/project/stats");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /project/roles — project not found (line 260) ────────────────────

    [Fact]
    public async Task ListRoles_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.GetAsync("/project/roles");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /project/roles — project not found (line 271) ───────────────────

    [Fact]
    public async Task CreateRole_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.PostAsJsonAsync("/project/roles", new { name = "Ghost" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /project/roles/{id} — project not found (line 286) ─────────────

    [Fact]
    public async Task UpdateRole_ProjectNotFound_Returns404()
    {
        var (_, role, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.PatchAsJsonAsync($"/project/roles/{role.Id}", new { name = "X" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /project/roles/{id} — empty body (lines 289-290) ──────────────

    [Fact]
    public async Task UpdateRole_EmptyBody_Returns200()
    {
        var (project, client) = await ScaffoldWithListAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "PatchMe");

        var res = await client.PatchAsJsonAsync($"/project/roles/{role.Id}", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DELETE /project/roles/{id} — project not found (line 298) ────────────

    [Fact]
    public async Task DeleteRole_ProjectNotFound_Returns404()
    {
        var (_, role, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.DeleteAsync($"/project/roles/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /project/audit-log — project not found (line 315) ────────────────

    [Fact]
    public async Task GetAuditLog_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.GetAsync("/project/audit-log");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /project/cleanup — no user list (line 329) ──────────────────────

    [Fact]
    public async Task Cleanup_ProjectWithoutUserList_Returns400()
    {
        var (_, client) = await ScaffoldWithoutListAsync();

        var res = await client.PostAsJsonAsync("/project/cleanup", new { dry_run = true });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── PATCH /project/info — all fields provided (lines 82-85, 88-94 TRUE branches) ─

    [Fact]
    public async Task UpdateInfo_AllFields_Returns200_CoversTrueBranches()
    {
        var (_, client) = await ScaffoldWithListAsync();

        var res = await client.PatchAsJsonAsync("/project/info", new
        {
            name                       = "Updated Project Name",
            active                     = true,
            require_role_to_login      = false,
            require_mfa                = false,
            sms_verification_enabled   = false,
            allowed_email_domains      = Array.Empty<string>(),
            min_password_length        = 8,
            password_require_uppercase = false,
            password_require_lowercase = false,
            password_require_digit     = false,
            password_require_special   = false
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PATCH /project/info — project not found (line 81) ────────────────────

    [Fact]
    public async Task UpdateInfo_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.PatchAsJsonAsync("/project/info", new { name = "X" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /project/users — project not found (line 131 project==null path) ──

    [Fact]
    public async Task ListUsers_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.GetAsync("/project/users");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /project/users/{id} — project not found (line 149 project==null path) ─

    [Fact]
    public async Task GetUser_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.GetAsync($"/project/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /project/users — project not found (line 191 project==null path) ─
    // project?.AssignedUserListId == null is true when project==null → BadRequest "no_user_list"

    [Fact]
    public async Task CreateUser_ProjectNotFound_ReturnsBadRequest_NoUserList()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!1"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_user_list");
    }

    // ── DELETE /project/users/{id}/sessions — project not found (line 229 project==null path) ─

    [Fact]
    public async Task ForceLogout_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.DeleteAsync($"/project/users/{Guid.NewGuid()}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /project/stats — project not found (line 241 project==null path) ─

    [Fact]
    public async Task GetStats_ProjectNotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.GetAsync("/project/stats");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /project/roles/{id} — description provided (line 289 TRUE path) ─

    [Fact]
    public async Task UpdateRole_WithDescription_Returns200_CoversTrueBranch()
    {
        var (project, client) = await ScaffoldWithListAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "DescRole");

        var res = await client.PatchAsJsonAsync($"/project/roles/{role.Id}", new
        {
            description = "A meaningful description"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /project/cleanup — project not found (line 329 project==null path) ─
    // project?.AssignedUserListId == null is true when project==null → BadRequest

    [Fact]
    public async Task Cleanup_ProjectNotFound_ReturnsBadRequest()
    {
        var (_, _, client) = await ScaffoldDeletedProjectAsync();

        var res = await client.PostAsJsonAsync("/project/cleanup", new { dry_run = true });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
