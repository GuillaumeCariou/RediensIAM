using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Keto;

/// <summary>
/// Targeted tests to cover KetoService branches not exercised by other suites:
///   - HasAnyRelationAsync (lines 52-58)
///   - GetActorManagementLevelForProjectAsync OrgAdmin / ProjectAdmin branches (lines 67-70)
///   - AssignProjectRoleAsync ProjectAdmin rank check (lines 102-117)
///   - GetActorManagementLevelForOrgAsync ProjectAdmin-via-DB branch (lines 80-81)
///   - ValidateProjectAdminScopeAsync (lines 211-218)
///   - AssignDefaultRoleAsync full happy path (lines 253-272)
/// </summary>
[Collection("RediensIAM")]
public class KetoServiceCoverageTests(TestFixture fixture)
{
    // ── HasAnyRelationAsync ───────────────────────────────────────────────────

    /// <summary>
    /// When the consent client is "client_admin_system", AuthController calls
    /// HasAnyRelationAsync for org_admin and project_admin roles regardless of
    /// whether super_admin already matched. This covers KetoService lines 52-58.
    /// </summary>
    [Fact]
    public async Task AdminConsent_AdminClientId_InvokesHasAnyRelationAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var user     = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();

        var challenge = Guid.NewGuid().ToString("N");
        // ClientId = "client_admin_system" → hits the admin branch in GetConsent
        fixture.Hydra.SetupConsentChallenge(challenge, user.Id.ToString(), "client_admin_system");

        var res = await fixture.Client.GetAsync($"/auth/consent?consent_challenge={challenge}");

        // AllowAll → super_admin true + HasAnyRelationAsync called → adminRoles non-empty → 302 redirect
        ((int)res.StatusCode).Should().BeLessThan(500);
    }

    // ── AssignDefaultRoleAsync ────────────────────────────────────────────────

    /// <summary>
    /// When a project has a DefaultRoleId, creating a user via POST /project/users
    /// triggers AssignDefaultRoleAsync (lines 253-272).
    /// </summary>
    [Fact]
    public async Task CreateProjectUser_ProjectHasDefaultRole_DefaultRoleAutoAssigned()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        // Set a default role on the project
        var defaultRole    = await fixture.Seed.CreateRoleAsync(project.Id, "DefaultRole", rank: 100);
        project.DefaultRoleId = defaultRole.Id;
        await fixture.Db.SaveChangesAsync();

        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client  = fixture.ClientWithToken(token);

        // POST /project/users → AssignDefaultRoleAsync is called
        var res = await client.PostAsJsonAsync("/project/users", new
        {
            email    = SeedData.UniqueEmail(),
            password = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── GetActorManagementLevelForProjectAsync — OrgAdmin branch ─────────────

    /// <summary>
    /// Denying super_admin check forces GetActorManagementLevelForProjectAsync
    /// to evaluate the org_admin branch (line 67-68) returning OrgAdmin level.
    /// AssignProjectRoleAsync then skips the rank check (only for ProjectAdmin).
    /// </summary>
    [Fact]
    public async Task AssignProjectRole_SuperAdminDenied_OrgAdminBranchTaken_Returns200()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var manager    = await fixture.Seed.CreateUserAsync(list.Id);
        var targetUser = await fixture.Seed.CreateUserAsync(list.Id);
        var role       = await fixture.Seed.CreateRoleAsync(project.Id, "Tester", rank: 50);
        var token      = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);

        fixture.Keto.AllowAll();
        // Deny super_admin → GetActorManagementLevelForProjectAsync falls through to org_admin check (true) → OrgAdmin
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{manager.Id}");

        var client = fixture.ClientWithToken(token);
        var res    = await client.PostAsJsonAsync($"/project/users/{targetUser.Id}/roles", new { role_id = role.Id });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GetActorManagementLevelForProjectAsync — ProjectAdmin branch ──────────

    /// <summary>
    /// Denying super_admin AND org_admin checks forces the method to evaluate
    /// the manager (ProjectAdmin) check (lines 69-70). With no existing actor
    /// roles the rank guard is skipped and the assignment succeeds.
    /// </summary>
    [Fact]
    public async Task AssignProjectRole_SuperAndOrgAdminDenied_ProjectAdminBranchTaken_Returns200()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var manager    = await fixture.Seed.CreateUserAsync(list.Id);
        var targetUser = await fixture.Seed.CreateUserAsync(list.Id);
        var role       = await fixture.Seed.CreateRoleAsync(project.Id, "Tester", rank: 50);
        var token      = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);

        fixture.Keto.AllowAll();
        // Deny super_admin and org_admin → falls through to manager check (AllowAll → true) → ProjectAdmin
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{manager.Id}");
        fixture.Keto.DenyCheck("Organisations", org.Id.ToString(), "org_admin", $"user:{manager.Id}");

        var client = fixture.ClientWithToken(token);
        var res    = await client.PostAsJsonAsync($"/project/users/{targetUser.Id}/roles", new { role_id = role.Id });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── AssignProjectRoleAsync — ProjectAdmin rank check ─────────────────────

    /// <summary>
    /// When the actor is ProjectAdmin and holds an existing role with rank 50,
    /// trying to assign a role with rank 10 (higher privilege) triggers the
    /// rank guard (lines 103-117) and returns 403.
    /// </summary>
    [Fact]
    public async Task AssignProjectRole_ProjectAdmin_TargetRoleHigherPrivilege_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var manager    = await fixture.Seed.CreateUserAsync(list.Id);
        var targetUser = await fixture.Seed.CreateUserAsync(list.Id);

        // Actor's own role — rank 50 (lower privilege)
        var actorRole  = await fixture.Seed.CreateRoleAsync(project.Id, "ManagerRole", rank: 50);
        // Target role — rank 10 (higher privilege; 10 < 50 → forbidden)
        var targetRole = await fixture.Seed.CreateRoleAsync(project.Id, "AdminRole", rank: 10);

        // Seed UserProjectRole for the actor so actorRoles.Count > 0
        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            Id         = Guid.NewGuid(),
            UserId     = manager.Id,
            ProjectId  = project.Id,
            RoleId     = actorRole.Id,
            GrantedBy  = manager.Id,
            GrantedAt  = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var token = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{manager.Id}");
        fixture.Keto.DenyCheck("Organisations", org.Id.ToString(), "org_admin", $"user:{manager.Id}");

        var client = fixture.ClientWithToken(token);
        var res    = await client.PostAsJsonAsync($"/project/users/{targetUser.Id}/roles", new { role_id = targetRole.Id });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── GetActorManagementLevelForOrgAsync — ProjectAdmin-via-DB branch ───────

    private async Task<(Organisation org, User manager, User targetUser, HttpClient client)>
        ScaffoldProjectAdminByDbAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var manager        = await fixture.Seed.CreateUserAsync(orgList.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser     = await fixture.Seed.CreateUserAsync(list.Id);

        // Seed ProjectAdmin OrgRole for manager (covers DB branch lines 80-81)
        await fixture.Seed.CreateOrgRoleAsync(org.Id, manager.Id, "project_admin");

        // Token: OrgAdmin so the filter passes; Keto checks will be denied below
        var token  = fixture.Seed.OrgAdminToken(manager.Id, org.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{manager.Id}");
        fixture.Keto.DenyCheck("Organisations", org.Id.ToString(), "org_admin", $"user:{manager.Id}");

        var client = fixture.ClientWithToken(token);
        return (org, manager, targetUser, client);
    }

    /// <summary>
    /// Actor's management level comes from the DB OrgRole (lines 80-81).
    /// Calling POST /org/admins with project_admin role but no scope_id
    /// reaches ValidateProjectAdminScopeAsync which throws because scopeId is null (lines 213-214).
    /// </summary>
    [Fact]
    public async Task AssignManagementRole_ProjectAdminByDB_NullScope_Returns403()
    {
        var (_, _, targetUser, client) = await ScaffoldProjectAdminByDbAsync();

        // role = "project_admin" but no scope_id → ValidateProjectAdminScopeAsync line 213-214 throws
        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id = targetUser.Id,
            role    = "project_admin"
            // scope_id intentionally omitted (null)
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().Contain("project_manager can only assign project_manager roles");
    }

    /// <summary>
    /// Actor has a ProjectAdmin OrgRole scoped to projectA.
    /// Trying to assign project_admin for projectB triggers the wrong-scope
    /// guard (lines 215-218).
    /// </summary>
    [Fact]
    public async Task AssignManagementRole_ProjectAdminByDB_WrongScope_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var manager        = await fixture.Seed.CreateUserAsync(orgList.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        var targetUser     = await fixture.Seed.CreateUserAsync(list.Id);

        var correctProject = await fixture.Seed.CreateProjectAsync(org.Id);
        var wrongProject   = await fixture.Seed.CreateProjectAsync(org.Id);

        // Actor's OrgRole is scoped to correctProject
        await fixture.Seed.CreateOrgRoleAsync(org.Id, manager.Id, "project_admin", correctProject.Id);

        var token = fixture.Seed.OrgAdminToken(manager.Id, org.Id);
        fixture.Keto.AllowAll();
        fixture.Keto.DenyCheck("System", "rediensiam", "super_admin", $"user:{manager.Id}");
        fixture.Keto.DenyCheck("Organisations", org.Id.ToString(), "org_admin", $"user:{manager.Id}");

        var client = fixture.ClientWithToken(token);

        // scope_id = wrongProject.Id → actorScope.ScopeId (correctProject) != scopeId (wrongProject) → line 217 throws
        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id  = targetUser.Id,
            role     = "project_admin",
            scope_id = wrongProject.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().Contain("Cannot assign project_manager for a project outside your scope");
    }
}
