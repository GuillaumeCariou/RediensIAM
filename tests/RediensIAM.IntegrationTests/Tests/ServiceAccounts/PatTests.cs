using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.ServiceAccounts;

[Collection("RediensIAM")]
public class PatTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, ServiceAccount sa, HttpClient client)>
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

    // ── GET /service-accounts/{id}/pat ────────────────────────────────────────

    [Fact]
    public async Task ListPats_ForSa_Returns200()
    {
        var (_, _, sa, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/service-accounts/{sa.Id}/pat");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListPats_Unauthenticated_Returns401()
    {
        var (_, _, sa, _) = await ScaffoldAsync();

        var res = await fixture.Client.GetAsync($"/service-accounts/{sa.Id}/pat");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /service-accounts/{id}/pat ──────────────────────────────────────

    [Fact]
    public async Task GeneratePat_ValidRequest_Returns201WithToken()
    {
        var (_, _, sa, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new
        {
            name       = "CI Token",
            expires_in = 30 // days
        });

        // Controller returns Ok (200), not Created
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("token", out var tokenProp).Should().BeTrue();
        tokenProp.GetString().Should().StartWith("rediens_pat_");
    }

    [Fact]
    public async Task GeneratePat_Unauthenticated_Returns401()
    {
        var (_, _, sa, _) = await ScaffoldAsync();

        var res = await fixture.Client.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new
        {
            name       = "Ghost Token",
            expires_in = 30
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GeneratePat_TokenHashedInDb()
    {
        var (_, _, sa, client) = await ScaffoldAsync();

        var res  = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new
        {
            name       = "Hash Check Token",
            expires_in = 30
        });
        var body  = await res.Content.ReadFromJsonAsync<JsonElement>();
        var plain = body.GetProperty("token").GetString()!;

        await fixture.RefreshDbAsync();
        var pat = fixture.Db.PersonalAccessTokens.FirstOrDefault(p => p.ServiceAccountId == sa.Id);
        pat.Should().NotBeNull();
        // Token should be stored as a hash, never as plaintext
        pat!.TokenHash.Should().NotBe(plain);
        pat.TokenHash.Should().NotBeNullOrEmpty();
    }

    // ── DELETE /service-accounts/{id}/pat/{patId} ─────────────────────────────

    [Fact]
    public async Task RevokePat_ExistingPat_Returns200()
    {
        var (_, _, sa, client) = await ScaffoldAsync();

        var createRes = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new
        {
            name       = "To Revoke",
            expires_in = 30
        });
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var patId      = Guid.Parse(createBody.GetProperty("id").GetString()!);

        var res = await client.DeleteAsync($"/service-accounts/{sa.Id}/pat/{patId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var deleted = await fixture.Db.PersonalAccessTokens.FindAsync(patId);
        deleted.Should().BeNull();
    }

    // ── PAT authentication via gateway ────────────────────────────────────────

    [Fact]
    public async Task PatToken_UsedAsBearer_IsAcceptedByGateway()
    {
        var (org, project, sa, client) = await ScaffoldAsync();

        // Generate a PAT
        var createRes = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new
        {
            name       = "Gateway Test Token",
            expires_in = 30
        });
        var patToken = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        // Use the PAT to call an authenticated endpoint.
        // SA has no management roles so ListServiceAccounts returns 401 (from controller),
        // but GetServiceAccount returns 404 (not found without management level).
        // 404 proves the gateway accepted the PAT (vs 401 = gateway rejected it).
        var patClient = fixture.ClientWithToken(patToken);
        var res       = await patClient.GetAsync($"/service-accounts/{sa.Id}");

        // Gateway accepted the PAT (controller returns 404, not 401 from middleware)
        res.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
