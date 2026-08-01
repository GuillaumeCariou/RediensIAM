using RediensIAM.IntegrationTests.Infrastructure;
using System.Net.Http.Json;

namespace RediensIAM.IntegrationTests.Tests.ServiceAccounts;

// ── from ServiceAccountBranchCoverageTests.cs ────────

/// <summary>
/// Covers ServiceAccountController branches where only one path was exercised.
///   - GET  /service-accounts              — Level.None → Unauthorized (line 56)
///   - GET  /service-accounts              — ProjectAdmin with no assigned list → NotFound (line 70)
///   - POST /service-accounts              — Level.None → Unauthorized (line 91)
///   - POST /service-accounts              — list not found → BadRequest (line 94)
///   - POST /service-accounts              — OrgAdmin list not in org → 403 (line 97/98)
///   - DELETE /service-accounts/{id}/roles/{rid} — role not found → NotFound (line 270)
///   - DELETE /service-accounts/{id}/roles/{rid} — targetLevel < Level → 403 (line 280)
///   - POST /service-accounts/{id}/roles   — project_admin can only assign project_admin (line 313)
/// </summary>
[Collection("RediensIAM")]
public class ServiceAccountBranchCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, UserList list, ServiceAccount sa, HttpClient adminClient)> SuperAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa    = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, list, sa, fixture.ClientWithToken(token));
    }

    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    /// <summary>Creates a token with ManagementLevel.None (no admin roles).</summary>
    private async Task<HttpClient> RegularUserClientAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user  = await fixture.Seed.CreateUserAsync(list.Id);
        var token = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(token);
    }

    // ── GET /service-accounts — Level.None → Forbidden ───────────────────────

    /// <summary>
    /// [RequireManagementLevel] now runs ahead of the action, so a token with no management
    /// level is refused with 403 by the filter rather than 401 by the action body (R-22).
    /// </summary>
    [Fact]
    public async Task ListServiceAccounts_LevelNone_IsRefused()
    {
        var client = await RegularUserClientAsync();

        var res = await client.GetAsync("/service-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── GET /service-accounts — ProjectAdmin with no assigned list (line 70) ─

    [Fact]
    public async Task ListServiceAccounts_ProjectAdminNoAssignedList_Returns404()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        // Do NOT assign a user list to the project
        var manager = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/service-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /service-accounts — Level.None → Forbidden ──────────────────────

    [Fact]
    public async Task CreateServiceAccount_LevelNone_IsRefused()
    {
        var client = await RegularUserClientAsync();

        var res = await client.PostAsJsonAsync("/service-accounts", new
        {
            name         = "Test SA",
            user_list_id = Guid.NewGuid()
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── POST /service-accounts — list not found → BadRequest (line 94) ────────

    [Fact]
    public async Task CreateServiceAccount_ListNotFound_ReturnsBadRequest()
    {
        var (_, _, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync("/service-accounts", new
        {
            name         = "Ghost SA",
            user_list_id = Guid.NewGuid()  // non-existent
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("user_list_not_found");
    }

    // ── POST /service-accounts — OrgAdmin with list from different org (line 97/98) ─

    [Fact]
    public async Task CreateServiceAccount_OrgAdminListFromOtherOrg_Returns403()
    {
        // An org admin may only place a service account in a list their own org owns.
        var (_, _, client) = await OrgAdminAsync();
        // Create a list in a DIFFERENT org
        var (otherOrg, _) = await fixture.Seed.CreateOrgAsync();
        var foreignList   = await fixture.Seed.CreateUserListAsync(otherOrg.Id);

        var res = await client.PostAsJsonAsync("/service-accounts", new
        {
            name         = "Test SA",
            user_list_id = foreignList.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("list_not_in_your_org");
    }

    // ── DELETE /service-accounts/{id}/roles/{rid} — role not found (line 270) ─

    [Fact]
    public async Task RemoveSaRole_RoleNotFound_Returns404()
    {
        var (_, _, sa, client) = await SuperAdminAsync();

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/roles/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /service-accounts/{id}/roles/{rid} — targetLevel < Level (line 280) ─

    [Fact]
    public async Task RemoveSaRole_TargetLevelHigherThanCaller_Returns403()
    {
        // OrgAdmin tries to remove a super_admin role → targetLevel(SuperAdmin=1) < Level(OrgAdmin=2) → 403
        var (org, admin, orgClient) = await OrgAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa   = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        // Seed a super_admin role on the SA directly
        var roleRecord = new RediensIAM.Data.Entities.ServiceAccountRole
        {
            Id               = Guid.NewGuid(),
            ServiceAccountId = sa.Id,
            Role             = "super_admin",
            GrantedBy        = admin.Id,
            GrantedAt        = DateTimeOffset.UtcNow,
        };
        fixture.Db.ServiceAccountRoles.Add(roleRecord);
        await fixture.Db.SaveChangesAsync();

        var res = await orgClient.DeleteAsync($"/service-accounts/{sa.Id}/roles/{roleRecord.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("insufficient_level_to_remove_this_role");
    }

    // ── POST /service-accounts/{id}/roles — ProjectAdmin assigns to different project (line 315) ─

    [Fact]
    public async Task AssignSaRole_ProjectAdminWrongProject_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var sa      = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        // Assign project_admin role but for a DIFFERENT project_id
        var otherProject = await fixture.Seed.CreateProjectAsync(org.Id);
        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role       = "project_admin",
            org_id     = org.Id,
            project_id = otherProject.Id  // different from token's projectId
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_mismatch");
    }

    // ── SA access denied — covers !CanAccessAsync(sa) TRUE for CRUD endpoints ─

    /// <summary>
    /// OrgAdmin from org1 tries to access an SA in org2 → CanAccessAsync returns false.
    /// Covers the `sa == null || !await CanAccessAsync(sa)` FALSE+TRUE branch for
    /// GetServiceAccount, DeleteServiceAccount, ListPats, GeneratePat, RevokePat,
    /// GetApiKeys, AddApiKey, RemoveApiKey, ListRoles, AssignRole.
    /// </summary>
    private async Task<(ServiceAccount sa, HttpClient foreignClient)> ScaffoldCrossOrgAsync()
    {
        // SA is in org1
        var (org1, org1List) = await fixture.Seed.CreateOrgAsync();
        var list1 = await fixture.Seed.CreateUserListAsync(org1.Id);
        var sa    = await fixture.Seed.CreateServiceAccountAsync(list1.Id);

        // Caller is OrgAdmin for org2
        var (org2, org2List) = await fixture.Seed.CreateOrgAsync();
        var admin2 = await fixture.Seed.CreateUserAsync(org2List.Id);
        var token  = fixture.Seed.OrgAdminToken(admin2.Id, org2.Id);
        fixture.Keto.AllowAll();
        return (sa, fixture.ClientWithToken(token));
    }

    [Fact]
    public async Task GetServiceAccount_AccessDenied_Returns404()
    {
        var (sa, client) = await ScaffoldCrossOrgAsync();
        var res = await client.GetAsync($"/service-accounts/{sa.Id}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteServiceAccount_AccessDenied_Returns404()
    {
        var (sa, client) = await ScaffoldCrossOrgAsync();
        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListPats_AccessDenied_Returns404()
    {
        var (sa, client) = await ScaffoldCrossOrgAsync();
        var res = await client.GetAsync($"/service-accounts/{sa.Id}/pat");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GeneratePat_AccessDenied_Returns404()
    {
        var (sa, client) = await ScaffoldCrossOrgAsync();
        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new { name = "test" });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetApiKeys_AccessDenied_Returns404()
    {
        var (sa, client) = await ScaffoldCrossOrgAsync();
        var res = await client.GetAsync($"/service-accounts/{sa.Id}/api-keys");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveApiKey_AccessDenied_Returns404()
    {
        var (sa, client) = await ScaffoldCrossOrgAsync();
        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/api-keys");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListRoles_AccessDenied_Returns404()
    {
        var (sa, client) = await ScaffoldCrossOrgAsync();
        var res = await client.GetAsync($"/service-accounts/{sa.Id}/roles");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AssignRole_AccessDenied_Returns404()
    {
        var (sa, client) = await ScaffoldCrossOrgAsync();
        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role   = "org_admin",
            org_id = Guid.NewGuid()
        });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /service-accounts/{id}/roles — ProjectAdmin assigns higher-privilege role ─

    [Fact]
    public async Task AssignSaRole_ProjectAdminAssignsOrgAdminRole_Returns403InsufficientLevel()
    {
        // Privilege escalation attempt: a project admin must not be able to grant a service
        // account a role that outranks their own.
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var sa      = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role   = "org_admin",  // targetLevel=2 < Level=3 → insufficient
            org_id = org.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("insufficient_level_to_grant_this_role");
    }

    // ── GET /service-accounts — ProjectAdmin with non-parseable ProjectId (line 65) ─

    [Fact]
    public async Task ListServiceAccounts_ProjectAdminInvalidProjectId_Returns403()
    {
        // A project_admin claim carrying an unparseable project id must be refused outright rather
        // than degrade to "admin of no particular project" and list everything.
        var userId = Guid.NewGuid();
        var orgId  = Guid.NewGuid();
        var token  = $"badpid-{userId:N}";
        fixture.Hydra.RegisterToken(token, userId.ToString(), orgId.ToString(), "not-a-guid", ["project_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/service-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_project_context");
    }

    // ── POST /service-accounts — ProjectAdmin with non-parseable ProjectId (line 103) ─

    [Fact]
    public async Task CreateServiceAccount_ProjectAdminInvalidProjectId_Returns403()
    {
        // Same unparseable-project-id refusal as the list endpoint. The list has to exist first,
        // otherwise the request stops at the list-not-found check and never reaches it.
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var userId = Guid.NewGuid();
        var token  = $"badpid2-{userId:N}";
        fixture.Hydra.RegisterToken(token, userId.ToString(), org.Id.ToString(), "not-a-guid", ["project_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/service-accounts", new
        {
            name         = "Test SA",
            user_list_id = list.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_project_context");
    }
}

// ── from ServiceAccountCoverageTests.cs ──────────────

/// <summary>
/// Targeted tests that cover specific uncovered lines in ServiceAccountController
/// identified via SonarQube line-coverage analysis.
///   - GET  /service-accounts                     — ProjectAdmin with bad project_id (line 65)
///   - POST /service-accounts                     — ProjectAdmin with bad project_id (line 103)
///   - POST /service-accounts                     — ProjectAdmin wrong list (line 106)
///   - POST /service-accounts/{id}/api-keys       — Hydra error catch (line 205)
///   - DELETE /service-accounts/{id}/pat/{patId}  — KeyNotFoundException catch (line 186)
///   - POST /service-accounts/{id}/roles          — duplicate returns existing (line 244)
///   - POST /service-accounts/{id}/roles          — OrgAdmin/ProjectAdmin validation (lines 275-278, 301, 305, 307, 316, 318)
/// </summary>
[Collection("RediensIAM")]
public class ServiceAccountCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, UserList list, ServiceAccount sa, HttpClient client)> SuperAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa    = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, list, sa, fixture.ClientWithToken(token));
    }

    private async Task<(Organisation org, UserList list, ServiceAccount sa, HttpClient client)> OrgAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa    = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, list, sa, fixture.ClientWithToken(token));
    }

    private async Task<(Organisation org, Project project, UserList list, ServiceAccount sa, HttpClient client)> ProjectAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var sa      = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        return (org, project, list, sa, fixture.ClientWithToken(token));
    }

    // ── GET /service-accounts — ProjectAdmin with non-parseable project_id (line 65) ─

    [Fact]
    public async Task ListServiceAccounts_ProjectAdminBadProjectId_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        // Register token with project_id = "not-a-guid" so Guid.TryParse fails
        var token = $"pm-badid-{Guid.NewGuid():N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), org.Id.ToString(), "not-a-guid", ["project_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/service-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_project_context");
    }

    // ── POST /service-accounts — ProjectAdmin with non-parseable project_id (line 103) ─

    [Fact]
    public async Task CreateServiceAccount_ProjectAdminBadProjectId_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var token = $"pm-badid2-{Guid.NewGuid():N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), org.Id.ToString(), "not-a-guid", ["project_admin"]);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/service-accounts", new
        {
            name         = "Bad Bot",
            user_list_id = list.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_project_context");
    }

    // ── POST /service-accounts — ProjectAdmin with wrong list (line 106) ──────

    [Fact]
    public async Task CreateServiceAccount_ProjectAdminWrongList_Returns403()
    {
        var (org, project, _, _, client) = await ProjectAdminAsync();
        // Create a different list not assigned to the project
        var otherList = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.PostAsJsonAsync("/service-accounts", new
        {
            name         = "Wrong List Bot",
            user_list_id = otherList.Id   // not the project's list → forbidden
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("can_only_create_sa_in_your_project_list");
    }

    // ── POST /service-accounts/{id}/api-keys — Hydra error catch (line 205) ──

    [Fact]
    public async Task AddApiKey_HydraFails_Returns400WithHydraError()
    {
        var (_, _, sa, client) = await SuperAdminAsync();
        fixture.Hydra.SetupClientCreationFailure();
        try
        {
            // Send a minimal valid JWK structure
            var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/api-keys", new
            {
                jwk = new { kty = "RSA", use = "sig", kid = "test-key" }
            });

            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Be("hydra_error");
        }
        finally
        {
            fixture.Hydra.RestoreClientCreation();
        }
    }

    // ── DELETE /service-accounts/{id}/pat/{patId} — KeyNotFoundException (line 186) ─

    [Fact]
    public async Task RevokePat_NonExistentPatId_Returns404()
    {
        var (_, _, sa, client) = await SuperAdminAsync();

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/pat/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /service-accounts/{id}/roles — duplicate returns existing (line 244) ─

    [Fact]
    public async Task AssignRole_DuplicateAssignment_ReturnsExistingEntry()
    {
        var (org, _, sa, client) = await SuperAdminAsync();
        var payload = new { role = "org_admin", org_id = org.Id };

        var first      = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", payload);
        var firstBody  = await first.Content.ReadFromJsonAsync<JsonElement>();
        var existingId = firstBody.GetProperty("id").GetString();

        var second     = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", payload);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        secondBody.GetProperty("id").GetString().Should().Be(existingId);
    }

    // ── DELETE /service-accounts/{id}/roles/{id} — removes OrgAdmin role (line 276) ─

    [Fact]
    public async Task RemoveRole_OrgAdminRole_Returns204()
    {
        var (org, _, sa, client) = await SuperAdminAsync();

        var assignRes  = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new { role = "org_admin", org_id = org.Id });
        var assignBody = await assignRes.Content.ReadFromJsonAsync<JsonElement>();
        var roleId     = assignBody.GetProperty("id").GetString();

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/roles/{roleId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /service-accounts/{id}/roles/{id} — removes SuperAdmin role (line 275) ─

    [Fact]
    public async Task RemoveRole_SuperAdminRole_Returns204()
    {
        var (_, _, sa, client) = await SuperAdminAsync();

        // Assign a super_admin SA role (SuperAdmin can do this: targetLevel = Level = 1, no 403)
        var assignRes  = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new { role = "super_admin" });
        var assignBody = await assignRes.Content.ReadFromJsonAsync<JsonElement>();
        var roleId     = assignBody.GetProperty("id").GetString();

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/roles/{roleId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /service-accounts/{id}/roles/{id} — custom role (_ arm, line 278) ─

    [Fact]
    public async Task RemoveRole_CustomRoleType_Returns204()
    {
        var (_, _, sa, client) = await SuperAdminAsync();

        // An unrecognised role name must map to the lowest management level, not to a privileged
        // one. Seeded directly because the API refuses to grant a role it does not know.
        var customRole = new RediensIAM.Data.Entities.ServiceAccountRole
        {
            Id               = Guid.NewGuid(),
            ServiceAccountId = sa.Id,
            Role             = "custom_mgmt_role",   // not super_admin/org_admin/project_admin
            GrantedAt        = DateTimeOffset.UtcNow,
        };
        fixture.Db.ServiceAccountRoles.Add(customRole);
        await fixture.Db.SaveChangesAsync();

        // SuperAdmin deletes it — targetLevel=None(99), Level=SuperAdmin(1), 99<1=false → proceeds
        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/roles/{customRole.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── PAT auth — SA with custom role (PatService line 97: _ => 99) ──────────

    [Fact]
    public async Task PatToken_SaWithCustomRole_HitsDefaultSwitchBranch()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa    = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var adminClient = fixture.ClientWithToken(token);

        // Seed a custom role type that hits the _ => 99 switch default
        fixture.Db.ServiceAccountRoles.Add(new RediensIAM.Data.Entities.ServiceAccountRole
        {
            Id               = Guid.NewGuid(),
            ServiceAccountId = sa.Id,
            Role             = "custom_role",
            GrantedAt        = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        // Generate a PAT for the SA
        var genRes = await adminClient.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new
        {
            name       = "Custom Role Token",
            expires_in = 30
        });
        var patToken = (await genRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        // Use the PAT — triggers PatService.ValidateAsync which hits the _ => 99 branch
        var patClient = fixture.ClientWithToken(patToken);
        var res       = await patClient.GetAsync($"/service-accounts/{sa.Id}");

        // Gateway accepted the PAT (not 401)
        res.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    // ── POST /service-accounts/{id}/roles — OrgAdmin for different org (line 301) ─

    [Fact]
    public async Task AssignRole_OrgAdminTargetingDifferentOrg_Returns403()
    {
        var (_, _, sa, client) = await OrgAdminAsync();
        var (otherOrg, _)      = await fixture.Seed.CreateOrgAsync();

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role   = "org_admin",
            org_id = otherOrg.Id    // different from caller's org → org_mismatch
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("org_mismatch");
    }

    // ── POST /service-accounts/{id}/roles — OrgAdmin without org_id (line 305) ─

    [Fact]
    public async Task AssignRole_OrgAdminRoleWithoutOrgId_Returns400()
    {
        var (_, _, sa, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role = "org_admin"
            // org_id omitted
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("org_id_required_for_org_admin");
    }

    // ── POST /service-accounts/{id}/roles — ProjectAdmin without org/project_id (line 307) ─

    [Fact]
    public async Task AssignRole_ProjectAdminRoleWithoutOrgAndProjectId_Returns400()
    {
        var (_, _, sa, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role = "project_admin"
            // org_id and project_id omitted
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("org_id_and_project_id_required_for_project_admin");
    }

    // ── POST /service-accounts/{id}/roles — ProjectAdmin mismatched project_id (line 316) ─

    [Fact]
    public async Task AssignRole_ProjectAdminDifferentProject_Returns403()
    {
        var (org, _, _, sa, client) = await ProjectAdminAsync();
        var other = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role       = "project_admin",
            org_id     = org.Id,
            project_id = other.Id   // different project → project_mismatch
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_mismatch");
    }

    // ── POST /service-accounts/{id}/roles — ProjectAdmin correct project but no org_id (line 318) ─

    [Fact]
    public async Task AssignRole_ProjectAdminCorrectProjectMissingOrgId_Returns400()
    {
        var (_, project, _, sa, client) = await ProjectAdminAsync();

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role       = "project_admin",
            project_id = project.Id   // correct project, but org_id missing → bad request
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("org_id_and_project_id_required_for_project_admin");
    }
}
