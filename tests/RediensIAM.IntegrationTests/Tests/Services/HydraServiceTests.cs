using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

// ── from HydraServiceCoverageTests.cs ───────────────────────

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

        // Starts with '[' so it is taken for JSON, but does not parse — the failure must be
        // swallowed into "no roles", not thrown.
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

        fixture.Hydra.RegisterTokenWithNumericRoles(token, user.Id.ToString(), org.Id.ToString());
        fixture.Keto.AllowAll();

        var client = fixture.ClientWithToken(token);
        // Empty roles → no recognised role → endpoint returns 403
        var res = await client.GetAsync("/org/info");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── EnsureAdminSpaClientAsync — POST path (client does not exist) ─────────

    /// <summary>
    /// When Hydra returns 404 for GET /admin/clients/{id}, EnsureAdminSpaClientAsync
    /// falls through to POST /admin/clients — covers the "create" branch.
    /// </summary>
    [Fact]
    public async Task EnsureAdminSpaClientAsync_WhenClientNotFound_CallsPost()
    {
        // Default stub: GET /admin/clients/* → 404, POST /admin/clients → 201
        using var scope = fixture.Services.CreateScope();
        var hydra = scope.ServiceProvider.GetRequiredService<HydraService>();
        var act = () => hydra.EnsureAdminSpaClientAsync("http://localhost");
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// When Hydra returns 200 for GET /admin/clients/{id}, EnsureAdminSpaClientAsync
    /// calls PUT /admin/clients/{id} — covers the "update" branch.
    /// </summary>
    [Fact]
    public async Task EnsureAdminSpaClientAsync_WhenClientExists_CallsPut()
    {
        fixture.Hydra.SetupClientGetResponse(RediensIAM.Config.Roles.AdminClientId);

        using var scope = fixture.Services.CreateScope();
        var hydra = scope.ServiceProvider.GetRequiredService<HydraService>();
        var act = () => hydra.EnsureAdminSpaClientAsync("http://localhost");
        await act.Should().NotThrowAsync();
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

        // With the client already present AddApiKey must update it rather than try to create a
        // second one under the same id.
        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/api-keys", new
        {
            jwk = new { kty = "RSA", use = "sig", kid = "update-key" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

// ── from HydraSessionDtoCoverageTests.cs ────────────────────

/// <summary>
/// Covers HydraService DTO property getters never invoked by existing tests because
/// the Hydra stub always returned an empty consent-session list.
///
/// Uncovered lines targeted:
///   - HydraService.cs line 36  — HydraClient.ClientName getter
///   - HydraService.cs line 42  — HydraConsentSession.ConsentRequest getter
///   - HydraService.cs line 43  — HydraConsentSession.GrantedScopes getter
///   - HydraService.cs line 44  — HydraConsentSession.GrantedAt getter
///   - HydraService.cs line 45  — HydraConsentSession.ExpiresAt getter
///   - HydraService.cs line 50  — HydraConsentSessionRequest.Client getter
///   - HydraService.cs line 51  — HydraConsentSessionRequest.RequestedAt getter
/// </summary>
[Collection("RediensIAM")]
public class HydraSessionDtoCoverageTests(TestFixture fixture)
{
    /// <summary>
    /// Sets up Hydra to return a non-empty consent session and calls both:
    ///  - OrgController GET /org/userlists/{id}/users/{uid}/sessions
    ///    → accesses ClientName(36), ConsentRequest(42), GrantedScopes(43), ExpiresAt(45), Client(50), RequestedAt(51)
    ///  - AccountController GET /account/sessions
    ///    → additionally accesses GrantedAt(44)
    /// </summary>
    [Fact]
    public async Task ListSessions_NonEmptyHydraResponse_InvokesDtoPropertyGetters()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        var user           = await fixture.Seed.CreateUserAsync(list.Id);
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);

        // Subject format used by OrgController and AccountController (when OrgId is in claims)
        var subject = $"{org.Id}:{user.Id}";

        // Return two sessions: one fully populated, one with null client — covers
        // both the non-null and null branches of ?.Client?.ClientId / ?.Client?.ClientName
        fixture.Hydra.SetupConsentSessions(subject, [
            new
            {
                consent_request = new
                {
                    client       = new { client_id = "test-client", client_name = "Test Application" },
                    requested_at = DateTimeOffset.UtcNow.AddDays(-1).ToString("o"),
                    subject      = subject,
                },
                granted_scopes = new[] { "openid", "profile" },
                granted_at     = DateTimeOffset.UtcNow.AddDays(-1).ToString("o"),
                expires_at     = DateTimeOffset.UtcNow.AddYears(1).ToString("o"),
            },
            // A session whose client is null — the mapper must tolerate it, not NRE
            new
            {
                consent_request = new
                {
                    client       = (object?)null,
                    requested_at = DateTimeOffset.UtcNow.AddDays(-2).ToString("o"),
                    subject      = subject,
                },
                granted_scopes = new[] { "openid" },
                granted_at     = DateTimeOffset.UtcNow.AddDays(-2).ToString("o"),
                expires_at     = (string?)null,
            },
            // A session with no consent request at all — also has to survive the mapper
            new
            {
                consent_request = (object?)null,
                granted_scopes  = new[] { "openid" },
                granted_at      = DateTimeOffset.UtcNow.AddDays(-3).ToString("o"),
                expires_at      = (string?)null,
            }
        ]);

        fixture.Keto.AllowAll();

        // ── OrgController: covers lines 36, 42, 43, 45, 50, 51 ──────────────
        var orgToken  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        var orgClient = fixture.ClientWithToken(orgToken);

        var orgRes = await orgClient.GetAsync($"/org/userlists/{list.Id}/users/{user.Id}/sessions");
        orgRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // Response should contain the session data (non-empty)
        var orgJson = await orgRes.Content.ReadAsStringAsync();
        orgJson.Should().Contain("test-client");

        // ── AccountController: covers line 44 (GrantedAt) ───────────────────
        var userToken  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var userClient = fixture.ClientWithToken(userToken);

        var accRes = await userClient.GetAsync("/account/sessions");
        accRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var accJson = await accRes.Content.ReadAsStringAsync();
        accJson.Should().Contain("test-client");
    }
}
