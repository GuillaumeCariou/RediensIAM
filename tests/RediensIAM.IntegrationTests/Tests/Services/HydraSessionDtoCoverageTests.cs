using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Services;

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

        // Return a fully-populated HydraConsentSession so every DTO property getter is exercised
        fixture.Hydra.SetupConsentSessions(subject, [new
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
        }]);

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
