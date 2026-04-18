using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Org;

/// <summary>
/// Covers OrgController endpoints that were not exercised by the original test files:
///   - PATCH /org/settings
///   - PUT /org/smtp (update path)
///   - DELETE /org/smtp
///   - POST /org/smtp/test
///   - GET/PATCH /org/users/{uid}
///   - PATCH /org/userlists/{id}/users/{uid}
///   - GET/PUT /org/projects/{id}/scopes
///   - GET /org/audit-log
///   - GET /org/audit-log/export (JSON + CSV)
///   - GET /org/userlists/{id}/export?format=json
///   - POST/PATCH/DELETE /org/projects/{id}/saml-providers
/// </summary>
[Collection("RediensIAM")]
public class OrgExtendedTests(TestFixture fixture)
{
    private async Task<(Organisation org, User admin, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, admin, fixture.ClientWithToken(token));
    }

    // ── PATCH /org/settings ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateOrgSettings_SetRetentionDays_Returns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PatchAsJsonAsync("/org/settings", new { audit_retention_days = 90 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("audit_retention_days").GetInt32().Should().Be(90);
    }

    [Fact]
    public async Task UpdateOrgSettings_ResetRetentionDays_Returns200WithNull()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        // -1 means "reset to global default" (stored as null)
        var res = await client.PatchAsJsonAsync("/org/settings", new { audit_retention_days = -1 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("audit_retention_days").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── PUT /org/smtp — UPDATE path (config already exists) ──────────────────

    [Fact]
    public async Task UpdateSmtp_ExistingConfig_OverwritesAndReturns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        // Create
        await client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.v1.com", port = 587, start_tls = true,
            username = "u@v1.com", password = "p1",
            from_address = "no@v1.com", from_name = "V1"
        });

        // Update (triggers else branch at line 719)
        var res = await client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.v2.com", port = 465, start_tls = false,
            username = "u@v2.com", password = "p2",
            from_address = "no@v2.com", from_name = "V2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var getRes = await client.GetAsync("/org/smtp");
        var body   = await getRes.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("host").GetString().Should().Be("smtp.v2.com");
        body.GetProperty("port").GetInt32().Should().Be(465);
    }

    // ── DELETE /org/smtp ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSmtp_ExistingConfig_Returns204()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        await client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.del.com", port = 587, start_tls = true,
            username = "u@del.com", from_address = "no@del.com", from_name = "Del"
        });

        var res = await client.DeleteAsync("/org/smtp");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteSmtp_NoConfig_Returns204()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.DeleteAsync("/org/smtp");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── POST /org/smtp/test ───────────────────────────────────────────────────

    [Fact]
    public async Task TestSmtp_AuthenticatedActor_Returns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PostAsync("/org/smtp/test", null);

        // StubEmailService never throws → 200
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("test_email_sent");
    }

    // ── GET /org/users/{uid} ──────────────────────────────────────────────────

    [Fact]
    public async Task GetOrgUser_ExistingUser_Returns200WithOrgRoles()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.GetAsync($"/org/users/{user.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(user.Id.ToString());
        body.TryGetProperty("roles", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetOrgUser_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync($"/org/users/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /org/users/{uid} ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateOrgUser_ChangeDisplayName_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/org/users/{user.Id}", new { display_name = "Updated Name" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("display_name").GetString().Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateOrgUser_NonExistent_Returns404()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.PatchAsJsonAsync($"/org/users/{Guid.NewGuid()}", new { display_name = "X" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateOrgUser_SetPhone_PersistsValue()
    {
        // Covers OrgController line 529: Phone != null, non-empty → sets value
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/org/users/{user.Id}", new { phone = "+1-555-0200" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.Phone.Should().Be("+1-555-0200");
    }

    [Fact]
    public async Task UpdateOrgUser_ClearPhone_SetsNull()
    {
        // Covers OrgController line 529: Phone == "" → null branch
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);
        user.Phone = "+1-555-0200";
        await fixture.Db.SaveChangesAsync();

        var res = await client.PatchAsJsonAsync($"/org/users/{user.Id}", new { phone = "" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.Phone.Should().BeNull();
    }

    // ── PATCH /org/userlists/{id}/users/{uid} ─────────────────────────────────

    [Fact]
    public async Task UpdateUserListUser_ChangeActive_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        var user             = await fixture.Seed.CreateUserAsync(list.Id);

        var res = await client.PatchAsJsonAsync($"/org/userlists/{list.Id}/users/{user.Id}", new { active = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    // ── GET /org/projects/{id}/scopes ─────────────────────────────────────────

    [Fact]
    public async Task GetProjectScopes_ExistingProject_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.GetAsync($"/org/projects/{project.Id}/scopes");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("custom_scopes", out _).Should().BeTrue();
        body.TryGetProperty("built_in", out _).Should().BeTrue();
    }

    // ── PUT /org/projects/{id}/scopes ─────────────────────────────────────────

    [Fact]
    public async Task UpdateProjectScopes_ValidScopes_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/org/projects/{project.Id}/scopes",
            new { scopes = new[] { "read:data", "write:data" } });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("custom_scopes").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task UpdateProjectScopes_InvalidScopeName_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PutAsJsonAsync($"/org/projects/{project.Id}/scopes",
            new { scopes = new[] { "INVALID SCOPE!" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_scope_names");
    }

    // ── GET /org/audit-log ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAuditLog_OrgAdmin_Returns200()
    {
        var (_, _, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync("/org/audit-log?limit=10");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── GET /org/audit-log/export ─────────────────────────────────────────────

    [Fact]
    public async Task ExportAuditLog_JsonFormat_Returns200WithAttachment()
    {
        var (_, _, client) = await OrgAdminClientAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync("/org/audit-log/export?format=json");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task ExportAuditLog_CsvFormat_Returns200WithCsv()
    {
        var (_, _, client) = await OrgAdminClientAsync();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync("/org/audit-log/export?format=csv");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
        var content = await res.Content.ReadAsStringAsync();
        content.Should().StartWith("id,action,");
    }

    [Fact]
    public async Task ExportAuditLog_RateLimited_Returns429()
    {
        var (_, _, client) = await OrgAdminClientAsync();
        await fixture.FlushCacheAsync();

        // First request consumes the rate limit slot
        await client.GetAsync("/org/audit-log/export?format=csv");
        // Second request should be rate-limited
        var res = await client.GetAsync("/org/audit-log/export?format=csv");

        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ExportAuditLog_WithDateRange_Returns200()
    {
        // Covers OrgController line 900: from?.ToString("O") and to?.ToString("O") non-null branches
        var (_, _, client) = await OrgAdminClientAsync();
        await fixture.FlushCacheAsync();

        var from = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        var to   = DateTimeOffset.UtcNow.ToString("O");

        var res = await client.GetAsync(
            $"/org/audit-log/export?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── GET /org/userlists/{id}/export?format=json ────────────────────────────

    [Fact]
    public async Task ExportUserList_JsonFormat_Returns200WithJson()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var list             = await fixture.Seed.CreateUserListAsync(org.Id);
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/org/userlists/{list.Id}/export?format=json");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    // ── POST /org/projects/{id}/saml-providers ────────────────────────────────

    [Fact]
    public async Task CreateSamlProvider_ValidRequest_Returns201()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.example.com/entity",
            sso_url   = "https://idp.example.com/sso"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateSamlProvider_MissingEntityId_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "",
            sso_url   = "https://idp.example.com/sso"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("entity_id_required");
    }

    [Fact]
    public async Task CreateSamlProvider_MissingUrlConfig_Returns400()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var res = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.example.com/entity"
            // neither metadata_url nor sso_url
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("metadata_url_or_sso_url_required");
    }

    // ── PATCH /org/projects/{id}/saml-providers/{pid} ─────────────────────────

    [Fact]
    public async Task UpdateSamlProvider_ChangeSsoUrl_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        // Create
        var createRes = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.update.com",
            sso_url   = "https://idp.update.com/sso/v1"
        });
        var providerId = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        // Update
        var res = await client.PatchAsJsonAsync(
            $"/org/projects/{project.Id}/saml-providers/{providerId}",
            new { sso_url = "https://idp.update.com/sso/v2", active = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    // ── DELETE /org/projects/{id}/saml-providers/{pid} ────────────────────────

    [Fact]
    public async Task DeleteSamlProvider_ExistingProvider_Returns200()
    {
        var (org, _, client) = await OrgAdminClientAsync();
        var project          = await fixture.Seed.CreateProjectAsync(org.Id);

        var createRes = await client.PostAsJsonAsync($"/org/projects/{project.Id}/saml-providers", new
        {
            entity_id = "https://idp.delete.com",
            sso_url   = "https://idp.delete.com/sso"
        });
        var providerId = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var res = await client.DeleteAsync($"/org/projects/{project.Id}/saml-providers/{providerId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
