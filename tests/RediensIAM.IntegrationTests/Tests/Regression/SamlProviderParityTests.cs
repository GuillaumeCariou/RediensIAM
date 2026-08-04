using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// A SAML provider registered through either scope has to be the same registration.
///
/// <para>
/// The two copies had drifted in four ways, and two of them matter beyond tidiness:
/// </para>
///
/// <list type="bullet">
/// <item><b>The system scope validated nothing.</b> The organisation route refuses a provider with
/// no entity id, with neither metadata URL nor SSO URL, and — the important one — with neither a
/// metadata URL nor a certificate, because without metadata there is no way to discover the
/// signing key. The system route accepted all three, so a super-admin could register an identity
/// provider whose assertions could never be verified.</item>
/// <item><b>The organisation update wrote no audit entry.</b> Changing a provider's
/// <c>sso_url</c> or <c>certificate_pem</c> redirects where users authenticate and decides which
/// key signs the assertion this deployment trusts. On the system route that was recorded; on the
/// tenant's own route it left no trace at all.</item>
/// <item>Creation answered 201 with the entity id on one side and 200 with the id alone on the
/// other.</item>
/// <item>Clearing the default role worked only on the system route, which reads
/// <c>Guid.Empty</c> as "clear"; the organisation route wrote the empty guid through.</item>
/// </list>
/// </summary>
[Collection("RediensIAM")]
public class SamlProviderParityTests(TestFixture fixture)
{
    private async Task<(HttpClient Org, HttpClient System, Guid ProjectId)> BothScopesAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        return (fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id)),
                fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id)),
                project.Id);
    }

    private static object Valid(string entityId) => new
    {
        entity_id = entityId,
        sso_url = "https://idp.test/sso",
        certificate_pem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----",
    };

    /// <summary>
    /// The one that is not merely untidy: an identity provider with neither metadata to fetch a key
    /// from nor a key supplied is one whose assertions cannot be verified.
    /// </summary>
    [Theory]
    [InlineData("entity_id", """{"sso_url":"https://idp.test/sso","certificate_pem":"x"}""")]
    [InlineData("no endpoint", """{"entity_id":"urn:x"}""")]
    [InlineData("no key and no metadata", """{"entity_id":"urn:x","sso_url":"https://idp.test/sso"}""")]
    public async Task BothScopesRefuseAProviderThatCouldNeverWork(string _, string body)
    {
        var (orgClient, sysClient, projectId) = await BothScopesAsync();
        var content = () => new StringContent(body, Encoding.UTF8, "application/json");

        var fromOrg = await orgClient.PostAsync($"/org/projects/{projectId}/saml-providers", content());
        var fromSystem = await sysClient.PostAsync($"/admin/projects/{projectId}/saml-providers", content());

        fromOrg.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fromSystem.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the system scope has more authority, not less validation");
    }

    [Fact]
    public async Task BothScopesAnswerCreationTheSameWay()
    {
        var (orgClient, sysClient, projectId) = await BothScopesAsync();

        var fromOrg = await orgClient.PostAsJsonAsync(
            $"/org/projects/{projectId}/saml-providers", Valid($"urn:org:{Guid.NewGuid()}"));
        var fromSystem = await sysClient.PostAsJsonAsync(
            $"/admin/projects/{projectId}/saml-providers", Valid($"urn:sys:{Guid.NewGuid()}"));

        fromSystem.StatusCode.Should().Be(fromOrg.StatusCode);

        var orgFields = (await fromOrg.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
        var systemFields = (await fromSystem.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
        systemFields.Should().Equal(orgFields);
    }

    /// <summary>
    /// Changing sso_url or certificate_pem decides where users authenticate and which key signs
    /// what this deployment will trust. That is not an edit to leave unrecorded on either route.
    /// </summary>
    [Fact]
    public async Task BothScopesRecordAnUpdate()
    {
        var (orgClient, sysClient, projectId) = await BothScopesAsync();

        var created = await (await orgClient.PostAsJsonAsync(
            $"/org/projects/{projectId}/saml-providers", Valid($"urn:audit:{Guid.NewGuid()}")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var providerId = created.GetProperty("id").GetString()!;

        await orgClient.PatchAsJsonAsync(
            $"/org/projects/{projectId}/saml-providers/{providerId}",
            new { sso_url = "https://elsewhere.test/sso" });
        await sysClient.PatchAsJsonAsync(
            $"/admin/projects/{projectId}/saml-providers/{providerId}",
            new { sso_url = "https://elsewhere-again.test/sso" });

        var entries = fixture.Db.AuditLogs
            .Where(a => a.TargetId == providerId && a.Action == "saml_provider.updated")
            .ToList();

        entries.Should().HaveCountGreaterThanOrEqualTo(2,
            "an unrecorded change of signing key is a change nobody can review");
    }

    /// <summary>
    /// Deleting a provider from the organisation scope recorded target_type "saml_provider"; from
    /// the system scope, "saml_idp_config" — the database table's name leaking into a record read
    /// by people. Same event, same action string, two types, so an audit query filtered by type saw
    /// half the deletions and reported the other half as never having happened.
    ///
    /// <para>
    /// Asserted through the routes rather than by reading the source. The first version of this
    /// test scanned the controller files for the literal, and stopped testing anything the moment
    /// the call sites moved into SamlProviderOperations — it passed by finding nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task BothScopesRecordTheSameResourceType()
    {
        var (orgClient, sysClient, projectId) = await BothScopesAsync();

        var ids = new List<string>();
        foreach (var (client, prefix) in new[] { (orgClient, "/org"), (sysClient, "/admin") })
        {
            var created = await (await client.PostAsJsonAsync(
                $"{prefix}/projects/{projectId}/saml-providers", Valid($"urn:type:{Guid.NewGuid()}")))
                .Content.ReadFromJsonAsync<JsonElement>();
            var id = created.GetProperty("id").GetString()!;
            ids.Add(id);
            await client.DeleteAsync($"{prefix}/projects/{projectId}/saml-providers/{id}");
        }

        var types = fixture.Db.AuditLogs
            .Where(a => ids.Contains(a.TargetId!) && a.Action.StartsWith("saml_provider."))
            .Select(a => a.TargetType)
            .Distinct()
            .ToList();

        types.Should().ContainSingle("two names for one resource make an audit query miss half of it")
            .Which.Should().Be("saml_provider", "it is what the action already says, and what the API calls it");
    }

    /// <summary>
    /// The empty guid means "clear" on one route and was written through as a value on the other,
    /// which no role will ever match — so the provisioning silently assigned nothing.
    /// </summary>
    [Fact]
    public async Task BothScopesClearTheDefaultRoleTheSameWay()
    {
        var (orgClient, sysClient, projectId) = await BothScopesAsync();

        foreach (var (client, prefix) in new[] { (orgClient, "/org"), (sysClient, "/admin") })
        {
            var created = await (await client.PostAsJsonAsync(
                $"{prefix}/projects/{projectId}/saml-providers", Valid($"urn:role:{Guid.NewGuid()}")))
                .Content.ReadFromJsonAsync<JsonElement>();
            var providerId = Guid.Parse(created.GetProperty("id").GetString()!);

            await client.PatchAsJsonAsync(
                $"{prefix}/projects/{projectId}/saml-providers/{providerId}",
                new { default_role_id = Guid.Empty });

            fixture.Db.ChangeTracker.Clear();
            var stored = await fixture.Db.SamlIdpConfigs.FindAsync(providerId);
            stored!.DefaultRoleId.Should().BeNull(
                $"{prefix} wrote the empty guid through, and no role will ever match it");
        }
    }
}
