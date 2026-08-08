using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Data.Entities;

namespace RediensIAM.IntegrationTests.Tests.ProjectAdmin;

// ── from ProjectBranchCoverageTests.cs ───────────────────────

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

        // The token is minted before the project is removed, so the caller holds a claim naming a
        // project that no longer exists.
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

    // These three used to assert 404 for a project with no user list assigned, which pinned the
    // defect in place: the console logged an API error on every freshly created project, and the
    // members panel — which fetches users and roles in one Promise.all — rendered empty. A project
    // that exists with no users is an ordinary state, not a missing resource. A project that does
    // not exist is still a 404, which the sibling tests cover.
    [Fact]
    public async Task ListUsers_ProjectWithoutUserList_ReturnsEmpty()
    {
        var (_, client) = await ScaffoldWithoutListAsync();

        var res = await client.GetAsync("/project/users");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().Should().Be(0);
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
    public async Task GetStats_ProjectWithoutUserList_ReturnsZeroes()
    {
        var (_, client) = await ScaffoldWithoutListAsync();

        var res = await client.GetAsync("/project/stats");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("total_users").GetInt32().Should().Be(0);
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

// ── from ProjectCoverageTests.cs ─────────────────────────────

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
        role.IsDefault = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync("/project/info", new
        {
            clear_default_role = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        fixture.Db.Roles.Find(role.Id)!.IsDefault.Should().BeFalse();
    }

    // ── PATCH /project/info — default_role_ids, l'ensemble énoncé entier ──────

    /// <summary>
    /// Le champ pluriel remplace l'ensemble : les deux rôles nommés sont accordés à l'inscription,
    /// et celui qui n'y figure plus ne l'est plus. Sans le second test un PATCH additif passerait.
    /// </summary>
    [Fact]
    public async Task UpdateInfo_DefaultRoleIds_FlagsExactlyThoseRoles()
    {
        var (_, project, _, _, client) = await ScaffoldAsync();
        var admin  = await fixture.Seed.CreateRoleAsync(project.Id, "Admin",  rank: 1);
        var editor = await fixture.Seed.CreateRoleAsync(project.Id, "Editor", rank: 50);
        var viewer = await fixture.Seed.CreateRoleAsync(project.Id, "Viewer", rank: 100);
        viewer.IsDefault = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync("/project/info", new
        {
            default_role_ids = new[] { admin.Id, editor.Id }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        fixture.Db.Roles.Find(admin.Id)!.IsDefault.Should().BeTrue();
        fixture.Db.Roles.Find(editor.Id)!.IsDefault.Should().BeTrue();
        fixture.Db.Roles.Find(viewer.Id)!.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateInfo_EmptyDefaultRoleIds_LeavesNoDefaultAtAll()
    {
        var (_, project, _, _, client) = await ScaffoldAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "Starter");
        role.IsDefault = true;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync("/project/info", new
        {
            default_role_ids = Array.Empty<Guid>()
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        fixture.Db.Roles.Find(role.Id)!.IsDefault.Should().BeFalse();
    }

    /// <summary>
    /// Un identifiant étranger au projet est refusé, pas ignoré : un 200 qui n'accorde rien laisse
    /// la console afficher une case cochée qu'elle n'a jamais enregistrée.
    /// </summary>
    [Fact]
    public async Task UpdateInfo_DefaultRoleIdsWithAnUnknownRole_Returns400AndChangesNothing()
    {
        var (_, project, _, _, client) = await ScaffoldAsync();
        var role = await fixture.Seed.CreateRoleAsync(project.Id, "Starter");

        var res = await client.PatchAsJsonAsync("/project/info", new
        {
            default_role_ids = new[] { role.Id, Guid.NewGuid() }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_default_role");
        await fixture.RefreshDbAsync();
        fixture.Db.Roles.Find(role.Id)!.IsDefault.Should().BeFalse();
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

        // Deleting a role that is still assigned must cascade, not fail on the dependent row.
        await client.PostAsJsonAsync($"/project/users/{user.Id}/roles", new { role_id = role.Id });

        var res = await client.DeleteAsync($"/project/roles/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}

// ── from ProjectMoreCoverageTests.cs ─────────────────────────

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
        fixture.Db.Roles.Find(role.Id)!.IsDefault.Should().BeTrue();
    }

    // ── DELETE /project/users/{id}/roles/{roleId} — NotFoundException (line 184) ─

    [Fact]
    public async Task RemoveUserRole_NonExistentAssignment_Returns404()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.DeleteAsync($"/project/users/{user.Id}/roles/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /project/users/{id}/sessions — user not in list (line 232) ───

    [Fact]
    public async Task ForceLogout_UserNotInProjectList_Returns404()
    {
        var (org, _, _, client) = await ScaffoldAsync();
        var otherList = await fixture.Seed.CreateUserListAsync(org.Id);
        var outsider  = await fixture.Seed.CreateUserAsync(otherList.Id);

        var res = await client.DeleteAsync($"/project/users/{outsider.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /project/info — OrgAdmin without ?project_id= (ProjectId getter line 37) ─

    [Fact]
    public async Task GetProjectInfo_OrgAdminWithoutProjectId_Returns404()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        // OrgAdmin has no project_id claim (empty string) and provides no ?project_id= query param.
        // "No project context" is a client mistake, not a server fault: the getter yields
        // Guid.Empty and GetProjectAsync finds nothing. It used to throw FormatException → 500.
        var res = await client.GetAsync("/project/info");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /project/cleanup — dry_run=false with orphaned roles (line 340) ─

    [Fact]
    public async Task Cleanup_DryRunFalse_WithOrphanedRoles_DeletesKetoTuples()
    {
        var (_, project, list, client) = await ScaffoldAsync();

        // The role row is seeded directly because the API will not grant a project role to somebody
        // outside the project's list — an orphan can only arise after the fact.
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
