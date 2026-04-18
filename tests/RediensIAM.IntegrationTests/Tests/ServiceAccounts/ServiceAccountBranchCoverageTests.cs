using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ServiceAccounts;

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

    // ── GET /service-accounts — Level.None → Unauthorized (line 56) ──────────

    [Fact]
    public async Task ListServiceAccounts_LevelNone_ReturnsUnauthorized()
    {
        var client = await RegularUserClientAsync();

        var res = await client.GetAsync("/service-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /service-accounts — ProjectAdmin with no assigned list (line 70) ─

    [Fact]
    public async Task ListServiceAccounts_ProjectAdminNoAssignedList_Returns404()
    {
        // Covers line 70: project.AssignedUserListId is null → listId == null → NotFound
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

    // ── POST /service-accounts — Level.None → Unauthorized (line 91) ─────────

    [Fact]
    public async Task CreateServiceAccount_LevelNone_ReturnsUnauthorized()
    {
        var client = await RegularUserClientAsync();

        var res = await client.PostAsJsonAsync("/service-accounts", new
        {
            name         = "Test SA",
            user_list_id = Guid.NewGuid()
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
        // Covers line 97: Level == OrgAdmin && list.OrgId != CallerOrgId → 403
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
        // Covers line 315: body.ProjectId != pId → project_mismatch
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
        // Covers line 299 TRUE: ProjectAdmin (Level=3) tries to assign "org_admin" (targetLevel=2)
        // → targetLevel(2) < Level(3) → "insufficient_level_to_grant_this_role"
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
        // Covers line 65: Level == ProjectAdmin but !Guid.TryParse(Claims.ProjectId) → no_project_context
        var userId = Guid.NewGuid();
        var orgId  = Guid.NewGuid();
        var token  = $"badpid-{userId:N}";
        // Register token with project_admin role but an unparseable ProjectId
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
        // Covers line 103: Level == ProjectAdmin but !Guid.TryParse(Claims.ProjectId) → no_project_context
        // First we need an existing list so the list-not-found check (line 94) passes
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
