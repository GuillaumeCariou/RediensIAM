using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// R-01 — an org_admin grant that outlived its organisation escalated to control of the system
/// service accounts, through four individually-defensible links:
///
///   1. DeleteOrg removed the org_roles rows and the structural Keto tuple, but left the
///      per-user Organisations:{orgId}#org_admin@user:{uid} grants behind.
///   2. GetConsent reads the ROLE from Keto and the ORG from the database, so the orphan minted
///      a token with roles:["org_admin"] and org_id:"".
///   3. LiveAuthorizationService could not parse "" and fell back to "admin of any org", which
///      the orphan satisfies.
///   4. ServiceAccountController mapped the unparseable claim to null, and null == null matched
///      exactly the service accounts whose UserList.OrgId IS NULL — the __system__ list.
///
/// Links 3 and 4 are each sufficient to stop the chain; both are asserted independently so one
/// regression cannot quietly reopen it. Link 1 (DeleteAllOrgTuplesAsync) is the root-cause fix.
/// </summary>
[Collection("RediensIAM")]
public class OrphanedGrantRegressionTests(TestFixture fixture)
{
    /// <summary>Link 4 — the one that decides the blast radius.</summary>
    [Fact]
    public async Task OrgAdminWithNoOrgClaim_CannotReachSystemServiceAccounts()
    {
        // The __system__ list is the one with no organisation — the most privileged SAs live here.
        var systemList = await fixture.Db.UserLists
            .FirstOrDefaultAsync(ul => ul.OrgId == null && ul.Immovable);
        if (systemList == null)
        {
            systemList = new UserList
            {
                Id = Guid.NewGuid(), Name = "__system__", Immovable = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            fixture.Db.UserLists.Add(systemList);
            await fixture.Db.SaveChangesAsync();
        }

        var systemSa = await fixture.Seed.CreateServiceAccountAsync(systemList.Id);

        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var attacker = await fixture.Seed.CreateUserAsync(orgList.Id);

        // An org_admin token whose org_id never resolved — exactly what the orphaned grant mints.
        var token = $"orphan-{Guid.NewGuid():N}";
        fixture.Hydra.RegisterToken(token, attacker.Id.ToString(), orgId: "", projectId: null,
            roles: [Roles.OrgAdmin]);

        fixture.Keto.AllowAll();
        await fixture.FlushCacheAsync();
        var client = fixture.ClientWithToken(token);

        // Minting a PAT for a system service account is the step that ends in full compromise.
        var mint = await client.PostAsJsonAsync($"/service-accounts/{systemSa.Id}/pat",
            new { name = "escalation" });

        mint.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "an org_admin with no resolvable org must never reach a system service account");

        (await client.GetAsync($"/service-accounts/{systemSa.Id}"))
            .StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    /// <summary>Link 3 — an org_admin claim must name the org it applies to.</summary>
    [Fact]
    public async Task LiveAuthorization_OrgAdminWithoutOrgClaim_IsNotGranted()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();

        var live = fixture.GetService<LiveAuthorizationService>();
        var claims = new RediensIAM.Models.TokenClaims
        {
            UserId    = Guid.NewGuid().ToString(),
            OrgId     = "",                 // the orphan's signature
            ProjectId = "",
            Roles     = [Roles.OrgAdmin],
        };

        (await live.IsStillGrantedAsync(claims, ManagementLevel.OrgAdmin))
            .Should().BeFalse("\"admin of some org\" is not an authorisation decision");
    }

    /// <summary>R-13 — a PAT that expires while its cache entry is warm must stop working.</summary>
    [Fact]
    public async Task ExpiredPat_IsRejectedEvenWhenCached()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa       = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var pats = fixture.GetService<PatService>();
        // Expires two seconds out: valid on the first call (which populates the cache),
        // expired on the second — which previously kept working for the rest of the TTL.
        var (raw, _) = await pats.GenerateAsync(sa.Id, "short-lived",
            DateTimeOffset.UtcNow.AddSeconds(2), null);

        (await pats.IntrospectAsync(raw)).Should().NotBeNull("the PAT is valid and now cached");

        await Task.Delay(TimeSpan.FromSeconds(3));

        (await pats.IntrospectAsync(raw)).Should().BeNull(
            "expiry must be re-checked on the cached path, not only on a cache miss");
    }
}
