using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ServiceAccounts;

[Collection("RediensIAM")]
public class ServiceAccountTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, UserList list, HttpClient client)> ScaffoldAsync()
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

    // ── GET /service-accounts ─────────────────────────────────────────────────

    [Fact]
    public async Task ListServiceAccounts_ProjectManager_Returns200()
    {
        var (_, _, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync("/service-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListServiceAccounts_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/service-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /service-accounts ────────────────────────────────────────────────

    [Fact]
    public async Task CreateServiceAccount_ProjectManager_Returns201()
    {
        var (_, _, list, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/service-accounts", new
        {
            name         = "My CI Bot",
            user_list_id = list.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateServiceAccount_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsJsonAsync("/service-accounts", new
        {
            name = "Ghost Bot"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateServiceAccount_CreatesInDb()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var name = "DB Check Bot " + Guid.NewGuid().ToString("N")[..6];

        var res  = await client.PostAsJsonAsync("/service-accounts", new { name, user_list_id = list.Id });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id   = Guid.Parse(body.GetProperty("id").GetString()!);

        await fixture.RefreshDbAsync();
        var sa = await fixture.Db.ServiceAccounts.FindAsync(id);
        sa.Should().NotBeNull();
        sa!.Name.Should().Be(name);
    }

    // ── GET /service-accounts/{id} ────────────────────────────────────────────

    [Fact]
    public async Task GetServiceAccount_ExistingSa_Returns200()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var res = await client.GetAsync($"/service-accounts/{sa.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(sa.Id.ToString());
    }

    [Fact]
    public async Task GetServiceAccount_NonExistent_Returns404()
    {
        var (_, _, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/service-accounts/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /service-accounts/{id} ─────────────────────────────────────────

    [Fact]
    public async Task DeleteServiceAccount_ProjectManager_Returns200()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.ServiceAccounts.FindAsync(sa.Id);
        deleted.Should().BeNull();
    }

    // ── Role assignment ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListRoles_ForSa_Returns200()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var res = await client.GetAsync($"/service-accounts/{sa.Id}/roles");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AssignRole_ValidRole_Returns200()
    {
        var (org, project, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        // AssignSaRoleRequest: Role (string management level), OrgId, ProjectId
        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role       = "project_admin",
            org_id     = org.Id,
            project_id = project.Id
        });

        ((int)res.StatusCode).Should().BeOneOf(200, 201);
    }

    [Fact]
    public async Task RemoveRole_ExistingAssignment_Returns200()
    {
        var (org, project, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var assignRes = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role       = "project_admin",
            org_id     = org.Id,
            project_id = project.Id
        });
        var assignBody   = await assignRes.Content.ReadFromJsonAsync<JsonElement>();
        var assignmentId = assignBody.GetProperty("id").GetString()!;

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/roles/{assignmentId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── Scope filtering ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListServiceAccounts_OrgAdmin_OnlySeesOrgSas()
    {
        // Org A — OrgAdmin caller
        var (orgA, orgListA) = await fixture.Seed.CreateOrgAsync();
        var listA            = await fixture.Seed.CreateUserListAsync(orgA.Id);
        var admin            = await fixture.Seed.CreateUserAsync(orgListA.Id);
        var saA              = await fixture.Seed.CreateServiceAccountAsync(listA.Id, "SA-OrgA");
        var tokenA           = fixture.Seed.OrgAdminToken(admin.Id, orgA.Id);
        fixture.Keto.AllowAll();

        // Org B — SA that OrgAdmin from A should NOT see
        var (orgB, _) = await fixture.Seed.CreateOrgAsync();
        var listB     = await fixture.Seed.CreateUserListAsync(orgB.Id);
        var saB       = await fixture.Seed.CreateServiceAccountAsync(listB.Id, "SA-OrgB");

        var client = fixture.ClientWithToken(tokenA);
        var res    = await client.GetAsync("/service-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var ids  = body.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();

        ids.Should().Contain(saA.Id.ToString());
        ids.Should().NotContain(saB.Id.ToString());
    }

    // ── Privilege escalation guard ────────────────────────────────────────────

    [Fact]
    public async Task AssignRole_PrivilegeEscalation_Returns403()
    {
        // ProjectAdmin (level 3) cannot grant OrgAdmin (level 2) to a SA
        var (org, project, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role   = "org_admin",
            org_id = org.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("insufficient_level_to_grant_this_role");
    }

    [Fact]
    public async Task AssignRole_ProjectAdmin_CanOnlyGrantProjectAdminRole()
    {
        var (org, project, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        // Try to grant super_admin — ProjectAdmin cannot do this
        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role       = "super_admin",
            org_id     = org.Id,
            project_id = project.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AssignRole_UnknownRole_Returns400()
    {
        var (org, project, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role       = "not_a_real_role",
            org_id     = org.Id,
            project_id = project.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("unknown_role");
    }

    [Fact]
    public async Task AssignRole_SuperAdminGrantsSuperAdmin_Returns200()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client         = fixture.ClientWithToken(token);

        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa   = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role = "super_admin"
        });

        ((int)res.StatusCode).Should().BeOneOf(200, 201);
    }

    [Fact]
    public async Task AssignRole_OrgAdminGrantsOrgAdmin_Returns200()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client         = fixture.ClientWithToken(token);

        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa   = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/roles", new
        {
            role   = "org_admin",
            org_id = org.Id
        });

        ((int)res.StatusCode).Should().BeOneOf(200, 201);
    }

    [Fact]
    public async Task CreateServiceAccount_ListNotInOrg_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client         = fixture.ClientWithToken(token);

        // List belonging to a different org
        var (otherOrg, _)  = await fixture.Seed.CreateOrgAsync();
        var foreignList    = await fixture.Seed.CreateUserListAsync(otherOrg.Id);

        var res = await client.PostAsJsonAsync("/service-accounts", new
        {
            name         = "Cross-org SA",
            user_list_id = foreignList.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("list_not_in_your_org");
    }

    [Fact]
    public async Task ListServiceAccounts_ProjectManager_OnlySeesProjectListSas()
    {
        var (org, project, list, client) = await ScaffoldAsync();

        // SA in the project's assigned list → should appear
        var saInProject = await fixture.Seed.CreateServiceAccountAsync(list.Id, "SA-in-project");

        // SA in a different list of the same org → should NOT appear
        var otherList    = await fixture.Seed.CreateUserListAsync(org.Id);
        var saOtherList  = await fixture.Seed.CreateServiceAccountAsync(otherList.Id, "SA-other-list");

        var res  = await client.GetAsync("/service-accounts");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var ids  = body.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();

        ids.Should().Contain(saInProject.Id.ToString());
        ids.Should().NotContain(saOtherList.Id.ToString());
    }

    // ── GET /service-accounts/{id}/api-keys ──────────────────────────────────

    [Fact]
    public async Task GetApiKeys_SaWithoutHydraClient_ReturnsHasKeyFalse()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        // SA has no HydraClientId (seed creates with null)
        sa.HydraClientId = null;
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/service-accounts/{sa.Id}/api-keys");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("has_key").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetApiKeys_SaWithHydraClientNotFound_ReturnsHasKeyFalse()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        sa.HydraClientId = "sa_nonexistent_client";
        await fixture.Db.SaveChangesAsync();

        // Hydra stub returns 404 for GET /admin/clients/{id} → has_key = false
        var res = await client.GetAsync($"/service-accounts/{sa.Id}/api-keys");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("has_key").GetBoolean().Should().BeFalse();
    }

    // ── POST /service-accounts/{id}/api-keys ─────────────────────────────────

    [Fact]
    public async Task AddApiKey_ValidJwk_Returns200WithClientId()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/api-keys", new
        {
            jwk = new { kty = "RSA", kid = "test-key-1", use = "sig", n = "test", e = "AQAB" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("client_id", out _).Should().BeTrue();
    }

    // ── DELETE /service-accounts/{id}/api-keys ────────────────────────────────

    [Fact]
    public async Task RemoveApiKey_SaWithHydraClient_Returns200()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        sa.HydraClientId = $"sa_{sa.Id}";
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/api-keys");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("key_removed");
    }

    [Fact]
    public async Task RemoveApiKey_SaWithoutHydraClient_Returns200()
    {
        var (_, _, list, client) = await ScaffoldAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        sa.HydraClientId = null;
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/api-keys");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
