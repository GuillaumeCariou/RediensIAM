using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Api;

/// <summary>
/// The resource-server surface a gateway integrates against. Before it existed, an external
/// service had to call Hydra's admin API directly, probe /account/me as an oracle, or validate
/// JWTs locally with no view of revocation.
/// </summary>
[Collection("RediensIAM")]
public class IntrospectionTests(TestFixture fixture)
{
    // `aud` names the tenant the calling resource server serves. Mandatory since the P-06 fix.
    private static FormUrlEncodedContent Form(string token, Guid aud) =>
        new([new KeyValuePair<string, string>("token", token),
             new KeyValuePair<string, string>("aud", aud.ToString()),
             new KeyValuePair<string, string>("token_type_hint", "access_token")]);

    /// <summary>Creates a service account with a PAT — the credential a gateway presents.</summary>
    private async Task<(HttpClient Client, Organisation Org, UserList List)> GatewayClientAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa       = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var pats = fixture.GetService<PatService>();
        var (raw, _) = await pats.GenerateAsync(sa.Id, "gateway", null, null);

        return (fixture.ClientWithToken(raw), org, list);
    }

    // ── Caller authentication ────────────────────────────────────────────────

    [Fact]
    public async Task Introspect_WithoutCredentials_IsRejected()
    {
        var res = await fixture.Client.PostAsync("/api/introspect", Form("anything", Guid.NewGuid()));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A plain user token must not be able to probe other tokens — that would turn the endpoint
    /// into the very oracle it exists to replace.
    /// </summary>
    [Fact]
    public async Task Introspect_WithUserToken_IsRejected()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var client = fixture.ClientWithToken(fixture.Seed.UserToken(user.Id, org.Id, project.Id));

        var res = await client.PostAsync("/api/introspect", Form("anything", org.Id));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Introspection ────────────────────────────────────────────────────────

    [Fact]
    public async Task Introspect_ValidPat_ReturnsActiveWithIdentity()
    {
        var (client, org, list) = await GatewayClientAsync();

        var subject = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var pats = fixture.GetService<PatService>();
        var (target, _) = await pats.GenerateAsync(subject.Id, "subject", null, null);

        var res = await client.PostAsync("/api/introspect", Form(target, org.Id));
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("active").GetBoolean().Should().BeTrue();
        body.GetProperty("is_service_account").GetBoolean().Should().BeTrue();
        body.GetProperty("sub").GetString().Should().Be($"sa:{subject.Id}");
    }

    [Fact]
    public async Task Introspect_UnknownToken_ReportsInactiveWithoutLeakingWhy()
    {
        var (client, org, _) = await GatewayClientAsync();

        var res = await client.PostAsync("/api/introspect", Form("rediens_pat_definitely-not-real", org.Id));
        res.StatusCode.Should().Be(HttpStatusCode.OK, "an unusable token is an answer, not an error");

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("active").GetBoolean().Should().BeFalse();
        body.TryGetProperty("sub", out var sub).Should().BeTrue();
        sub.ValueKind.Should().Be(JsonValueKind.Null, "nothing about the subject may leak");
    }

    /// <summary>
    /// The point of introspecting rather than verifying a signature locally: revocation is
    /// visible straight away.
    /// </summary>
    [Fact]
    public async Task Introspect_AfterServiceAccountDeactivated_ReportsInactive()
    {
        var (client, org, list) = await GatewayClientAsync();

        var subject = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var pats = fixture.GetService<PatService>();
        var (target, _) = await pats.GenerateAsync(subject.Id, "subject", null, null);

        (await (await client.PostAsync("/api/introspect", Form(target, org.Id)))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("active").GetBoolean().Should().BeTrue();

        await fixture.RefreshDbAsync();
        var stored = await fixture.Db.ServiceAccounts.FirstAsync(x => x.Id == subject.Id);
        stored.Active = false;
        await fixture.Db.SaveChangesAsync();

        var body = await (await client.PostAsync("/api/introspect", Form(target, org.Id)))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("active").GetBoolean().Should().BeFalse(
            "a gateway must see revocation without waiting for the token to expire");
    }

    /// <summary>
    /// A management role that Keto no longer grants must not be reported to the gateway, or every
    /// downstream service would keep honouring it.
    /// </summary>
    [Fact]
    public async Task Introspect_RoleRevokedInKeto_IsNotReported()
    {
        var (client, org, orgList) = await GatewayClientAsync();

        // Same organisation as the calling service account: a tenant-scoped gateway only ever
        // sees its own organisation's tokens (T-N6).
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = $"sa-org-{admin.Id:N}";
        fixture.Hydra.RegisterToken(token, admin.Id.ToString(), org.Id.ToString(), null, [Roles.SuperAdmin]);

        fixture.Keto.AllowAll();
        await fixture.FlushCacheAsync();

        var granted = await (await client.PostAsync("/api/introspect", Form(token, org.Id)))
            .Content.ReadFromJsonAsync<JsonElement>();
        granted.GetProperty("roles").EnumerateArray().Select(r => r.GetString())
            .Should().Contain(Roles.SuperAdmin);

        fixture.Keto.DenyAll();
        await fixture.FlushCacheAsync();

        try
        {
            var revoked = await (await client.PostAsync("/api/introspect", Form(token, org.Id)))
                .Content.ReadFromJsonAsync<JsonElement>();

            revoked.GetProperty("roles").EnumerateArray().Select(r => r.GetString())
                .Should().NotContain(Roles.SuperAdmin);
        }
        finally
        {
            fixture.Keto.AllowAll();
            await fixture.FlushCacheAsync();
        }
    }

    // ── Authorization ────────────────────────────────────────────────────────

    [Fact]
    public async Task Authorize_DelegatesTheDecisionToKeto()
    {
        var (client, org, orgList) = await GatewayClientAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = $"sa-org-{user.Id:N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), org.Id.ToString(), null, [Roles.SuperAdmin]);

        fixture.Keto.AllowAll();
        var allowed = await client.PostAsJsonAsync("/api/authorize", new
        {
            token,
            @namespace = Roles.KetoOrgsNamespace,
            @object    = org.Id.ToString(),
            relation   = Roles.KetoOrgAdminRelation,
            aud        = org.Id,
        });
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await allowed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should().BeTrue();

        fixture.Keto.DenyAll();
        try
        {
            var denied = await client.PostAsJsonAsync("/api/authorize", new
            {
                token,
                @namespace = Roles.KetoOrgsNamespace,
                @object    = org.Id.ToString(),
                relation   = Roles.KetoOrgAdminRelation,
                aud        = org.Id,
            });

            (await denied.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("allowed").GetBoolean().Should().BeFalse();
        }
        finally
        {
            fixture.Keto.AllowAll();
            await fixture.FlushCacheAsync();
        }
    }

    [Fact]
    public async Task Authorize_UnknownToken_IsDenied()
    {
        var (client, org, _) = await GatewayClientAsync();
        fixture.Keto.AllowAll();

        var res = await client.PostAsJsonAsync("/api/authorize", new
        {
            token      = "rediens_pat_definitely-not-real",
            @namespace = Roles.KetoOrgsNamespace,
            @object    = org.Id.ToString(),
            relation   = Roles.KetoOrgAdminRelation,
            aud        = org.Id,
        });

        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should().BeFalse(
                "an unusable token grants nothing, even when Keto would allow the subject");
    }
}
