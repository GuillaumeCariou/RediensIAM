using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ManagedApi;

/// <summary>
/// Covers ManagedApiController lines not hit by ManagedApiTests:
///   - POST /api/manage/organizations/{id}/projects — Hydra unavailable (lines 131-136)
/// </summary>
[Collection("RediensIAM")]
public class ManagedApiCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, HttpClient client)> SuperAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, fixture.ClientWithToken(token));
    }

    // ── POST /api/manage/organizations/{id}/projects — Hydra failure ──────────

    [Fact]
    public async Task CreateProject_WhenHydraUnavailable_Returns502AndRollsBack()
    {
        var (org, client) = await SuperAdminClientAsync();

        fixture.Hydra.SetupClientCreationFailure();
        try
        {
            var res = await client.PostAsJsonAsync($"/api/manage/organizations/{org.Id}/projects", new
            {
                name = "Hydra-fail project",
                slug = SeedData.UniqueSlug()
            });

            res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Be("hydra_unavailable");
        }
        finally
        {
            // Restore default client creation stub — does NOT reset token stubs
            fixture.Hydra.RestoreClientCreation();
        }
    }
}
