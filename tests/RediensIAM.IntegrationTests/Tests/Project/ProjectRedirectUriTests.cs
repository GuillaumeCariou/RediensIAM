using System.Net.Http.Json;
using System.Text.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ProjectAdmin;

/// <summary>
/// A project's redirect URIs could be set when it was created and never again.
///
/// <para>
/// They live in Hydra rather than in this database, and <c>PATCH /org/projects/{id}</c> only ever
/// wrote database columns — so nothing in the product could add a second front to an existing
/// project, correct a typo in a callback, or withdraw one. The only remedy was the Hydra admin API
/// by hand, on a cluster, which is not a remedy.
/// </para>
///
/// <para>
/// It matters more now that CSP and CORS are derived from those same URIs: deriving them is
/// worthless if the list they derive from cannot be edited. Both are the same registration, so
/// they are written together and the allowed origins are recomputed from them — see
/// ClientOriginsService.CorsOriginsFor.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class ProjectRedirectUriTests(TestFixture fixture)
{
    /// <summary>
    /// The value one RFC 6902 operation carries. Hydra's PATCH takes an array of operations, not a
    /// partial client — reading it as an object is how the shape went unchecked for as long as it did.
    /// </summary>
    private static string[] Patched(string body, string path) =>
        [.. JsonDocument.Parse(body).RootElement.EnumerateArray()
            .Single(op => op.GetProperty("path").GetString() == path)
            .GetProperty("value").EnumerateArray().Select(e => e.GetString()!)];

    private async Task<(HttpClient Client, Guid ProjectId, string ClientId)> ProjectAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin   = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var client  = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));

        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        project.HydraClientId = $"client_{project.Id}";
        await fixture.Db.SaveChangesAsync();
        return (client, project.Id, project.HydraClientId);
    }

    [Fact]
    public async Task TheyCanBeReplacedAfterTheProjectExists()
    {
        var (client, projectId, clientId) = await ProjectAsync();
        fixture.Hydra.ResetLog();

        var res = await client.PatchAsJsonAsync($"/org/projects/{projectId}", new
        {
            redirect_uris = new[] { "https://one.test/cb", "https://two.test/cb" },
            post_logout_redirect_uris = new[] { "https://one.test/", "https://two.test/" },
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var sent = fixture.Hydra.LastRequestBody($"/admin/clients/{clientId}");
        sent.Should().NotBeNull("the URIs live in Hydra, so updating them has to reach Hydra");
        Patched(sent!, "/redirect_uris").Should().Equal("https://one.test/cb", "https://two.test/cb");
        Patched(sent!, "/post_logout_redirect_uris").Should().Equal("https://one.test/", "https://two.test/");
    }

    /// <summary>
    /// Hydra applies CORS on its public port, where the SPA's token call goes. Registering a
    /// redirect URI without the matching origin leaves the front able to be redirected to and
    /// unable to finish — so the two are written by the same call, from the same derivation.
    /// </summary>
    [Fact]
    public async Task ReplacingThemRewritesTheClientsAllowedCorsOrigins()
    {
        var (client, projectId, clientId) = await ProjectAsync();
        fixture.Hydra.ResetLog();

        await client.PatchAsJsonAsync($"/org/projects/{projectId}", new
        {
            redirect_uris = new[] { "https://one.test/cb", "https://two.test/deep/path" },
            post_logout_redirect_uris = new[] { "https://three.test/" },
        });

        Patched(fixture.Hydra.LastRequestBody($"/admin/clients/{clientId}")!, "/allowed_cors_origins")
            .Should().Equal("https://one.test", "https://three.test", "https://two.test");
    }

    /// <summary>
    /// A PATCH that says nothing about the URIs must not silently clear them. Every other field on
    /// this route is optional in exactly that sense, and a project whose redirect_uris became []
    /// because someone renamed it would be a project nobody can log into.
    /// </summary>
    [Fact]
    public async Task APatchThatDoesNotMentionThemLeavesThemAlone()
    {
        var (client, projectId, clientId) = await ProjectAsync();
        fixture.Hydra.ResetLog();

        var res = await client.PatchAsJsonAsync($"/org/projects/{projectId}", new { name = "Renamed" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Hydra.LastRequestBody($"/admin/clients/{clientId}").Should().BeNull(
            "not mentioning a field is not the same as emptying it");
    }

    /// <summary>The list has to be readable to be editable — the console renders what is there.</summary>
    [Fact]
    public async Task TheyAreReadableFromTheProjectItself()
    {
        var (client, projectId, clientId) = await ProjectAsync();
        fixture.Hydra.SetupClientRedirectUris(clientId, ["https://shown.test/cb"], ["https://shown.test/"]);

        var project = await (await client.GetAsync($"/org/projects/{projectId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        project.GetProperty("redirect_uris").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("https://shown.test/cb");
        project.GetProperty("post_logout_redirect_uris").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("https://shown.test/");
    }
}
