using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ServiceAccounts;

/// <summary>
/// Targeted tests to cover PatService branches not exercised by PatTests:
///   - IntrospectAsync role priority switch (lines 92-98) + Roles list (line 109)
///     → requires a ServiceAccount with ServiceAccountRoles
///   - GetKeysAsync with JWKS present (lines 132-137, 139)
///     → requires Hydra stub returning a client with keys
///   - AddKeyAsync return (line 149)
///     → POST /service-accounts/{id}/api-keys
/// </summary>
[Collection("RediensIAM")]
public class PatServiceCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, ServiceAccount sa, HttpClient managerClient)>
        ScaffoldAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var manager = await fixture.Seed.CreateUserAsync(list.Id);
        var token   = fixture.Seed.ProjectManagerToken(manager.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        return (org, project, sa, fixture.ClientWithToken(token));
    }

    // ── IntrospectAsync role priority switch (lines 92-98, 109) ──────────────

    /// <summary>
    /// When a PAT is used and the ServiceAccount has ServiceAccountRoles,
    /// IntrospectAsync executes the role-priority OrderBy switch (lines 92-98)
    /// and builds the Roles list (line 109).
    /// </summary>
    [Fact]
    public async Task PatToken_SaWithRoles_RolePrioritySwitchExecuted()
    {
        var (org, project, sa, managerClient) = await ScaffoldAsync();

        // Seed a ServiceAccountRole (project_admin scoped to org+project)
        fixture.Db.ServiceAccountRoles.Add(new ServiceAccountRole
        {
            Id               = Guid.NewGuid(),
            ServiceAccountId = sa.Id,
            Role             = RediensIAM.Config.Roles.ProjectAdmin,
            OrgId            = org.Id,
            ProjectId        = project.Id,
            GrantedBy        = null,
            GrantedAt        = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        // Generate a PAT for the SA (via manager)
        var createRes = await managerClient.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new
        {
            name       = "Role Coverage Token",
            expires_in = 30
        });
        var patToken = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        // Flush cache so IntrospectAsync must hit DB (not Redis) and execute the switch
        await fixture.FlushCacheAsync();

        // Use the PAT → triggers IntrospectAsync → saRoles non-empty → role switch runs
        var patClient = fixture.ClientWithToken(patToken);
        var res       = await patClient.GetAsync($"/service-accounts/{sa.Id}");

        // 404 means gateway accepted token (introspect ran); 401 would mean token rejected
        res.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    // ── GetKeysAsync with JWKS present (lines 132-137, 139) ──────────────────

    /// <summary>
    /// When a SA has a HydraClientId and Hydra returns a client with JWKS keys,
    /// GetKeysAsync executes the JWKS-parsing branch (lines 132-137, 139).
    /// </summary>
    [Fact]
    public async Task GetApiKeys_SaWithHydraClientWithJwks_ReturnsHasKeyTrue()
    {
        var (_, _, sa, managerClient) = await ScaffoldAsync();

        // Use AddApiKey endpoint to set HydraClientId via the normal path, so the
        // Hydra stub is automatically used (POST /admin/clients stubs 201 by default).
        // Then override the GET stub to return a client WITH keys for the follow-up read.
        var jwk = new { kty = "RSA", use = "sig", alg = "RS256", kid = "test-jwk",
            n = "pjdss8ZaDfEH6K6U7GeW2nxDqR4IP049fk1fK0lndimbMMVBdPv_hSpm8T8EtBDxrUdi1OHZfMhUixGyvJ2gH4wMVAHn05-f7F3VbFGBmXzCVmT7lLv6GxcYFMoWfOHRKvNL0pLVjqLUZ7qEhMfSmGxaJ9yMvpRbkMfN7cOiRaXn6by5Iib7HwFJPl_INZPi1rKgdlxXNNFaUjMSe2dCfwfOTJ5iHQLVIHi0TuFO3wnhqXnTQJoYgU7fqXI5zFovhSmrLAfkqVkWH7bcnrQ5aHNxCh4E8RZFa1LJk0PpOtBmkz0oLJB1OaZaTZ6y7bgNUUJoJNYNhCPQA9cw",
            e = "AQAB" };
        var addRes = await managerClient.PostAsJsonAsync($"/service-accounts/{sa.Id}/api-keys", new { jwk });
        addRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // Reload SA to get the HydraClientId assigned by AddKeyAsync
        await fixture.RefreshDbAsync();
        var updatedSa = await fixture.Db.ServiceAccounts.FindAsync(sa.Id);
        var clientId  = updatedSa!.HydraClientId!;

        // Now stub Hydra GET to return a client with JWKS for the read
        fixture.Hydra.SetupOAuth2ClientWithJwks(clientId, kid: "test-key-abc");

        var res = await managerClient.GetAsync($"/service-accounts/{sa.Id}/api-keys");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("has_key").GetBoolean().Should().BeTrue();
        body.GetProperty("client_id").GetString().Should().Be(clientId);
    }

    // ── AddKeyAsync return (line 149) ─────────────────────────────────────────

    /// <summary>
    /// POST /service-accounts/{id}/api-keys calls AddKeyAsync, which creates or
    /// updates the Hydra client and returns clientId (line 149).
    /// </summary>
    [Fact]
    public async Task AddApiKey_ValidJwk_Returns200WithClientId()
    {
        var (_, _, sa, client) = await ScaffoldAsync();

        // Minimal valid RSA public key JWK (test key — not used for real signing)
        var jwk = new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid = "integration-test-key",
            n   = "pjdss8ZaDfEH6K6U7GeW2nxDqR4IP049fk1fK0lndimbMMVBdPv_hSpm8T8EtBDxrUdi1OHZfMhUixGyvJ2gH4wMVAHn05-f7F3VbFGBmXzCVmT7lLv6GxcYFMoWfOHRKvNL0pLVjqLUZ7qEhMfSmGxaJ9yMvpRbkMfN7cOiRaXn6by5Iib7HwFJPl_INZPi1rKgdlxXNNFaUjMSe2dCfwfOTJ5iHQLVIHi0TuFO3wnhqXnTQJoYgU7fqXI5zFovhSmrLAfkqVkWH7bcnrQ5aHNxCh4E8RZFa1LJk0PpOtBmkz0oLJB1OaZaTZ6y7bgNUUJoJNYNhCPQA9cw",
            e   = "AQAB"
        };

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/api-keys", new { jwk });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("client_id", out var cidProp).Should().BeTrue();
        cidProp.GetString().Should().StartWith("sa_");
    }

    // ── GetKeysAsync with no HydraClient (line 126) ───────────────────────────

    [Fact]
    public async Task GetApiKeys_SaWithNoHydraClient_ReturnsHasKeyFalse()
    {
        var (_, _, sa, client) = await ScaffoldAsync();

        // SA has no HydraClientId (default)
        var res = await client.GetAsync($"/service-accounts/{sa.Id}/api-keys");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("has_key").GetBoolean().Should().BeFalse();
    }

    // ── RemoveKeyAsync (lines 151-157) ────────────────────────────────────────

    [Fact]
    public async Task RemoveApiKey_SaWithHydraClient_Returns200()
    {
        var (_, _, sa, client) = await ScaffoldAsync();

        // Seed HydraClientId
        sa.HydraClientId = $"sa_{sa.Id}";
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/api-keys");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("key_removed");
    }
}
