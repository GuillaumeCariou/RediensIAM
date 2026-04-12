using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

[Collection("RediensIAM")]
public class SystemAdminExtendedTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> SuperAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── GET /admin/users/{id}/sessions ────────────────────────────────────────

    [Fact]
    public async Task ListSessions_ExistingUser_Returns200WithArray()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var user  = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.GetAsync($"/admin/users/{user.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListSessions_NonExistentUser_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync($"/admin/users/{Guid.NewGuid()}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/userlists ──────────────────────────────────────────────────

    [Fact]
    public async Task ListAllUserLists_SuperAdmin_Returns200WithArray()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync("/admin/userlists");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListAllUserLists_FilteredByOrg_ReturnsOrgLists()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.Seed.CreateUserListAsync(org.Id);

        var res  = await client.GetAsync($"/admin/userlists?org_id={org.Id}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    // ── DELETE /admin/userlists/{id}/users/{uid} ──────────────────────────────

    [Fact]
    public async Task RemoveUserFromList_ExistingUser_Returns204()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.DeleteAsync($"/admin/userlists/{list.Id}/users/{user.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.Users.FindAsync(user.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task RemoveUserFromList_NonExistentUser_Returns404()
    {
        var (org, _, client) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);

        var res = await client.DeleteAsync($"/admin/userlists/{list.Id}/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/projects/{id}/stats ────────────────────────────────────────

    [Fact]
    public async Task GetProjectStats_ProjectWithUserList_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/admin/projects/{project.Id}/stats");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("total_users", out _).Should().BeTrue();
        body.TryGetProperty("active_users", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetProjectStats_ProjectWithoutUserList_Returns404()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.GetAsync($"/admin/projects/{project.Id}/stats");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /admin/projects/{id}/roles/{rid} ───────────────────────────────

    [Fact]
    public async Task DeleteRole_ExistingRole_Returns204()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "ToDelete");

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}/roles/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.Roles.FindAsync(role.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteRole_NonExistentRole_Returns404()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}/roles/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /admin/projects/{id} — DefaultRoleId / ClearDefaultRole / LoginTheme branches ─

    [Fact]
    public async Task UpdateProject_SetDefaultRole_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "DefaultRole");

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            default_role_id = role.Id
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.DefaultRoleId.Should().Be(role.Id);
    }

    [Fact]
    public async Task UpdateProject_InvalidDefaultRoleId_Returns400()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            default_role_id = Guid.NewGuid()
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateProject_ClearDefaultRole_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "ToClear");
        project.DefaultRoleId = role.Id;
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            clear_default_role = true
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Projects.FindAsync(project.Id);
        updated!.DefaultRoleId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProject_WithLoginTheme_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new
        {
            login_theme = new Dictionary<string, object> { ["background_color"] = "#ffffff" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── PUT /admin/organizations/{id}/smtp — update (second call) branch ──────

    [Fact]
    public async Task UpdateOrgSmtp_CalledTwice_UpdatesExistingConfig()
    {
        var (org, _, client) = await SuperAdminAsync();
        var smtpPayload = new
        {
            host         = "smtp.initial.com",
            port         = 587,
            start_tls    = true,
            username     = "user@initial.com",
            password     = "initial-secret",
            from_address = "noreply@initial.com",
            from_name    = "Initial"
        };
        await client.PutAsJsonAsync($"/admin/organizations/{org.Id}/smtp", smtpPayload);

        var res = await client.PutAsJsonAsync($"/admin/organizations/{org.Id}/smtp", new
        {
            host         = "smtp.updated.com",
            port         = 465,
            start_tls    = false,
            username     = "user@updated.com",
            password     = "updated-secret",
            from_address = "noreply@updated.com",
            from_name    = "Updated"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var config = await fixture.Db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == org.Id);
        config!.Host.Should().Be("smtp.updated.com");
    }

    // ── GET /admin/hydra/clients ───────────────────────────────────────────────

    [Fact]
    public async Task ListHydraClients_SuperAdmin_Returns200WithArray()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync("/admin/hydra/clients");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── POST /admin/hydra/clients ─────────────────────────────────────────────

    [Fact]
    public async Task CreateHydraClient_SuperAdmin_Returns200()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync("/admin/hydra/clients", new
        {
            client_name   = "Test External Client",
            grant_types   = new[] { "authorization_code", "refresh_token" },
            redirect_uris = new[] { "https://app.example.com/callback" },
            scope         = "openid profile"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GET /admin/hydra/clients/{id} ─────────────────────────────────────────

    [Fact]
    public async Task GetHydraClient_NonExistent_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync("/admin/hydra/clients/nonexistent-client");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /admin/hydra/clients/{id} ──────────────────────────────────────

    [Fact]
    public async Task DeleteHydraClient_SuperAdmin_Returns204()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.DeleteAsync("/admin/hydra/clients/any-client-id");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── GET /admin/organizations/{id}/export/users ────────────────────────────

    [Fact]
    public async Task ExportUsers_CsvFormat_Returns200WithCsvContent()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/export/users");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await res.Content.ReadAsStringAsync();
        content.Should().Contain("id,email,username");
    }

    [Fact]
    public async Task ExportUsers_JsonFormat_Returns200WithJson()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/export/users?format=json");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ExportUsers_NonExistentOrg_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{Guid.NewGuid()}/export/users");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/organizations/{id}/export/audit-log ────────────────────────

    [Fact]
    public async Task ExportAuditLog_CsvFormat_Returns200WithCsvContent()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/export/audit-log");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await res.Content.ReadAsStringAsync();
        content.Should().Contain("id,action");
    }

    [Fact]
    public async Task ExportAuditLog_JsonFormat_Returns200WithJson()
    {
        var (org, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/export/audit-log?format=json");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ExportAuditLog_NonExistentOrg_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/admin/organizations/{Guid.NewGuid()}/export/audit-log");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/projects/{id}/saml-providers ───────────────────────────────

    [Fact]
    public async Task ListSamlProviders_SuperAdmin_Returns200WithArray()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.GetAsync($"/admin/projects/{project.Id}/saml-providers");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListSamlProviders_NonExistentProject_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.GetAsync($"/admin/projects/{Guid.NewGuid()}/saml-providers");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /admin/projects/{id}/saml-providers ──────────────────────────────

    [Fact]
    public async Task CreateSamlProvider_ValidPayload_Returns200WithId()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/admin/projects/{project.Id}/saml-providers", new
        {
            entity_id       = "https://idp.example.com/saml2",
            sso_url         = "https://idp.example.com/saml2/sso",
            certificate_pem = "-----BEGIN CERTIFICATE-----\nMIID...\n-----END CERTIFICATE-----"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateSamlProvider_NonExistentProject_Returns404()
    {
        var (_, _, client) = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync($"/admin/projects/{Guid.NewGuid()}/saml-providers", new
        {
            entity_id = "https://idp.example.com/saml2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /admin/projects/{projectId}/saml-providers/{providerId} ─────────

    [Fact]
    public async Task UpdateSamlProvider_ValidPayload_Returns200()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        // Create provider first
        var createRes = await client.PostAsJsonAsync($"/admin/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.example.com/original"
        });
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = createBody.GetProperty("id").GetString();

        // Update it
        var res = await client.PatchAsJsonAsync($"/admin/projects/{project.Id}/saml-providers/{providerId}", new
        {
            entity_id       = "https://idp.example.com/updated",
            jit_provisioning = false,
            active          = false
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DELETE /admin/projects/{projectId}/saml-providers/{providerId} ────────

    [Fact]
    public async Task DeleteSamlProvider_ExistingProvider_Returns204()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        var createRes = await client.PostAsJsonAsync($"/admin/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.delete-me.com"
        });
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = createBody.GetProperty("id").GetString();

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}/saml-providers/{providerId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /admin/projects/{id} — without HydraClientId ──────────────────

    [Fact]
    public async Task DeleteProject_WithoutHydraClientId_Returns204()
    {
        var (org, _, client) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.HydraClientId = null;
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/admin/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
