using System.Net.Http.Json;
using System.Text.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// The same resource, read through the organisation scope and through the system scope, must be
/// the same resource.
///
/// <para>
/// Twenty-four operations exist in two or three copies, one per scope, and the copies differ only
/// in who may call them and how the target is found. Everything after that was written twice —
/// and what is written twice drifts. This file is where each drift found gets nailed down before
/// the two copies are collapsed into one.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class ScopeParityTests(TestFixture fixture)
{
    private async Task<(HttpClient Org, HttpClient System, Guid ProjectId)> BothScopesAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin   = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var orgClient = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));
        var sysClient = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        return (orgClient, sysClient, project.Id);
    }

    private async Task SeedSamlProvidersAsync(Guid projectId)
    {
        foreach (var name in new[] { "beta", "alpha" })
        {
            fixture.Db.SamlIdpConfigs.Add(new SamlIdpConfig
            {
                Id = Guid.NewGuid(), ProjectId = projectId,
                EntityId = $"urn:{name}:{Guid.NewGuid()}", SsoUrl = $"https://{name}.test/sso",
                EmailAttributeName = "email", Active = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await fixture.Db.SaveChangesAsync();
    }

    private static string[] FieldsOf(JsonElement array) =>
        [.. array.EnumerateArray().First().EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The organisation-scope list omitted <c>updated_at</c> and had no ORDER BY at all — so a
    /// tenant admin saw fewer fields than a super-admin looking at the same providers, in an order
    /// PostgreSQL was free to change between two identical requests.
    /// </summary>
    [Fact]
    public async Task SamlProviderList_HasTheSameFieldsInBothScopes()
    {
        var (orgClient, sysClient, projectId) = await BothScopesAsync();
        await SeedSamlProvidersAsync(projectId);

        var fromOrg = await (await orgClient.GetAsync($"/org/projects/{projectId}/saml-providers"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var fromSystem = await (await sysClient.GetAsync($"/admin/projects/{projectId}/saml-providers"))
            .Content.ReadFromJsonAsync<JsonElement>();

        FieldsOf(fromOrg).Should().Equal(FieldsOf(fromSystem),
            "one scope showing a field the other hides is the console rendering two different truths");
    }

    /// <summary>
    /// Without an ORDER BY the row order is whatever the plan happened to produce. A list an
    /// operator reads has to be stable, and "stable in practice today" is not stable.
    /// </summary>
    [Fact]
    public async Task SamlProviderList_IsOrderedTheSameWayInBothScopes()
    {
        var (orgClient, sysClient, projectId) = await BothScopesAsync();
        await SeedSamlProvidersAsync(projectId);

        var fromOrg = await (await orgClient.GetAsync($"/org/projects/{projectId}/saml-providers"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var fromSystem = await (await sysClient.GetAsync($"/admin/projects/{projectId}/saml-providers"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var orgIds = fromOrg.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        var systemIds = fromSystem.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();

        orgIds.Should().Equal(systemIds);
    }

    /// <summary>The scope read is one expression; it must not answer differently per route.</summary>
    [Fact]
    public async Task ProjectScopes_ReadTheSameInBothScopes()
    {
        var (orgClient, sysClient, projectId) = await BothScopesAsync();

        await orgClient.PutAsJsonAsync($"/org/projects/{projectId}/scopes",
            new { scopes = new[] { "billing:read", "billing:write" } });

        var fromOrg = await (await orgClient.GetAsync($"/org/projects/{projectId}/scopes"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var fromSystem = await (await sysClient.GetAsync($"/admin/projects/{projectId}/scopes"))
            .Content.ReadFromJsonAsync<JsonElement>();

        fromOrg.GetProperty("custom_scopes").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(fromSystem.GetProperty("custom_scopes").EnumerateArray().Select(e => e.GetString()));
        fromOrg.GetProperty("built_in").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(fromSystem.GetProperty("built_in").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>
    /// Same rule for a user. The organisation route audited against the caller's organisation and
    /// the system route against the user's own — equal only because the organisation route's lookup
    /// filters on that same id. A coincidence, not a rule, and the kind that survives until the
    /// lookup changes.
    /// </summary>
    [Fact]
    public async Task UserUpdate_IsAuditedAgainstTheUsersOwnOrganisationFromBothScopes()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var target = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var orgClient = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));
        var sysClient = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        await orgClient.PatchAsJsonAsync($"/org/users/{target.Id}", new { display_name = "From org" });
        await sysClient.PatchAsJsonAsync($"/admin/users/{target.Id}", new { display_name = "From system" });

        var entries = fixture.Db.AuditLogs
            .Where(a => a.TargetId == target.Id.ToString() && a.Action == "user.updated")
            .ToList();

        entries.Should().HaveCountGreaterThanOrEqualTo(2);
        entries.Should().OnlyContain(a => a.OrgId == org.Id,
            "an entry on another chain is one the tenant cannot read");
    }

    /// <summary>
    /// Writing scopes through either route has to land on the tenant's own audit chain. A row
    /// written with a null organisation goes to the deployment-wide chain, where the tenant can
    /// never see that their scopes were changed for them.
    /// </summary>
    [Fact]
    public async Task ProjectScopeChange_IsAuditedAgainstTheTenantFromBothScopes()
    {
        var (orgClient, sysClient, projectId) = await BothScopesAsync();
        var project = await fixture.Db.Projects.FindAsync(projectId);

        await orgClient.PutAsJsonAsync($"/org/projects/{projectId}/scopes", new { scopes = new[] { "a:read" } });
        await sysClient.PutAsJsonAsync($"/admin/projects/{projectId}/scopes", new { scopes = new[] { "b:read" } });

        var entries = fixture.Db.AuditLogs
            .Where(a => a.ProjectId == projectId && a.Action == "project.scopes_updated")
            .ToList();

        entries.Should().HaveCountGreaterThanOrEqualTo(2);
        entries.Should().OnlyContain(a => a.OrgId == project!.OrgId,
            "an entry on the deployment-wide chain is one the tenant cannot read");
    }
}
