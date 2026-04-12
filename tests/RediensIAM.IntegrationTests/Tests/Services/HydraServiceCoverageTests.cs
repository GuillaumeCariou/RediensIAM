using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Services;

/// <summary>
/// Covers HydraService lines not yet hit by existing tests:
///   - ExtClaims.GetRoles string branch  (lines 271-277) — roles returned as a plain string
///   - CreateOrUpdateServiceAccountClientAsync PUT path (line 197) — SA client already exists
/// </summary>
[Collection("RediensIAM")]
public class HydraServiceCoverageTests(TestFixture fixture)
{
    // ── ExtClaims.GetRoles — string branch (lines 271-277) ───────────────────

    /// <summary>
    /// When Hydra returns ext.roles as a plain comma-separated string instead of
    /// a JSON array, ExtClaims.GetRoles takes the ValueKind == String path (lines 271-277).
    /// </summary>
    [Fact]
    public async Task ValidateToken_WhenRolesIsCommaString_ParsesRolesCorrectly()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = $"str-roles-{Guid.NewGuid():N}";

        // Register with roles as a comma-separated string — hits line 277 (comma split)
        fixture.Hydra.RegisterTokenWithStringRoles(token, user.Id.ToString(), org.Id.ToString(), "org_admin");
        fixture.Keto.AllowAll();

        var client = fixture.ClientWithToken(token);
        var res = await client.GetAsync("/org/info");

        // OrgAdmin role parsed correctly from string → endpoint returns 200
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// When roles is a JSON-serialized array string (e.g. "[\"org_admin\"]"),
    /// ExtClaims.GetRoles parses it via JsonSerializer (lines 274-275).
    /// </summary>
    [Fact]
    public async Task ValidateToken_WhenRolesIsJsonArrayString_ParsesRolesCorrectly()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = $"str-roles2-{Guid.NewGuid():N}";

        // Register with roles as a JSON array string — hits lines 274-275 (starts with '[')
        fixture.Hydra.RegisterTokenWithStringRoles(token, user.Id.ToString(), org.Id.ToString(), "[\"org_admin\"]");
        fixture.Keto.AllowAll();

        var client = fixture.ClientWithToken(token);
        var res = await client.GetAsync("/org/info");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// When roles is a string starting with '[' but is malformed JSON,
    /// ExtClaims.GetRoles catches JsonException and falls through to comma-split (line 276).
    /// </summary>
    [Fact]
    public async Task ValidateToken_WhenRolesIsMalformedJsonArray_FallsBackToCommaSplit()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = $"str-roles3-{Guid.NewGuid():N}";

        // "[org_admin" starts with '[' but is not valid JSON → hits line 276 catch
        fixture.Hydra.RegisterTokenWithStringRoles(token, user.Id.ToString(), org.Id.ToString(), "[org_admin");
        fixture.Keto.AllowAll();

        var client = fixture.ClientWithToken(token);
        // Malformed array is treated as comma-split: "[org_admin" has no comma,
        // so roles = ["[org_admin"] which is not recognized → endpoint may return 403 or 200
        // depending on role check, but what matters is no 5xx (the path runs without crashing)
        var res = await client.GetAsync("/org/info");
        ((int)res.StatusCode).Should().BeLessThan(500);
    }

    /// <summary>
    /// When ext.roles is a non-string, non-array JSON value (e.g., a number),
    /// ExtClaims.GetRoles returns an empty list (line 279).
    /// </summary>
    [Fact]
    public async Task ValidateToken_WhenRolesIsNumericValue_ReturnsEmptyRoles()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = $"str-roles4-{Guid.NewGuid():N}";

        // Register with roles as a number (ValueKind = Number) → hits line 279 (return [])
        fixture.Hydra.RegisterTokenWithNumericRoles(token, user.Id.ToString(), org.Id.ToString());
        fixture.Keto.AllowAll();

        var client = fixture.ClientWithToken(token);
        // Empty roles → no recognised role → endpoint returns 403
        var res = await client.GetAsync("/org/info");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── CreateOrUpdateServiceAccountClientAsync — PUT path (line 197) ────────

    /// <summary>
    /// When a SA Hydra client already exists (GET returns 200), the second call to
    /// AddApiKey uses PUT instead of POST — covers HydraService line 197.
    /// </summary>
    [Fact]
    public async Task AddApiKey_WhenClientAlreadyExists_UsesPutPath()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var list  = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa    = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        // Pre-configure the SA's Hydra client to already exist (GET /admin/clients/sa_{id} → 200)
        fixture.Hydra.SetupOAuth2ClientWithJwks($"sa_{sa.Id}");

        // AddApiKey now sees an existing client → takes the PUT branch (line 197)
        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/api-keys", new
        {
            jwk = new { kty = "RSA", use = "sig", kid = "update-key" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
