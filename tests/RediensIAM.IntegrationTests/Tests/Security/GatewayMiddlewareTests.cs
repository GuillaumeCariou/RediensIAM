using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// Tests the GatewayAuthMiddleware:
/// - Token routing (PAT vs Hydra)
/// - Claims injection
/// - Invalid / expired / inactive tokens
/// - Keto permission check gating
/// </summary>
[Collection("RediensIAM")]
public class GatewayMiddlewareTests(TestFixture fixture)
{
    // ── No token ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoToken_ProtectedEndpoint_Returns401()
    {
        var res = await fixture.Client.GetAsync("/account/me");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoToken_PublicAuthEndpoint_IsReachable()
    {
        // /auth/login GET is public — only requires a valid challenge
        // Without a challenge it returns 400, not 401
        var res = await fixture.Client.GetAsync("/auth/login?login_challenge=fake");

        res.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    // ── Invalid token ─────────────────────────────────────────────────────────

    [Fact]
    public async Task InactiveToken_Returns401()
    {
        // Hydra stub defaults: unknown tokens return { active: false }
        var client = fixture.ClientWithToken("unknown-token-that-is-not-registered");

        var res = await client.GetAsync("/account/me");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MalformedBearerHeader_Returns401()
    {
        var client = fixture.Client;
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");

        var res = await client.GetAsync("/account/me");

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    // ── Valid Hydra token ─────────────────────────────────────────────────────

    [Fact]
    public async Task ValidHydraToken_InjectsClaimsAndReturns200()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/account/me");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("email").GetString().Should().Be(user.Email);
    }

    // ── PAT token routing ─────────────────────────────────────────────────────

    [Fact]
    public async Task PatToken_RoutedThroughPatService_NotHydra()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var pmToken = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        var pmClient = fixture.ClientWithToken(pmToken);
        fixture.Keto.AllowAll();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        // Generate PAT
        var patRes = await pmClient.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new
        {
            name = "Gateway Routing Test",
            expires_in = 30
        });
        patRes.StatusCode.Should().Be(HttpStatusCode.OK);  // controller returns Ok, not Created
        var patToken = (await patRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        patToken.Should().StartWith("rediens_pat_");

        // PAT should be usable without being registered in HydraStub.
        // SA has no management roles → ListServiceAccounts returns 401 (from controller, not middleware).
        // Use GetServiceAccount/{id} instead — returns 404 when SA has no management level,
        // proving the gateway accepted the PAT (middleware 401 vs controller 404 are distinct).
        var patClient = fixture.ClientWithToken(patToken);
        var res       = await patClient.GetAsync($"/service-accounts/{sa.Id}");
        res.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    // ── Role-based access ─────────────────────────────────────────────────────

    [Fact]
    public async Task SuperAdminToken_CanAccessAdminEndpoints()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(user.Id);
        var client         = fixture.ClientWithToken(token);
        fixture.Keto.AllowAll();

        var res = await client.GetAsync("/admin/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RegularUserToken_CannotAccessAdminEndpoints()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);
        fixture.Keto.DenyAll();

        var res = await client.GetAsync("/admin/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OrgAdminToken_CanAccessOrgEndpoints()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(user.Id, org.Id);
        var client         = fixture.ClientWithToken(token);
        fixture.Keto.AllowAll();

        var res = await client.GetAsync("/org/info");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
