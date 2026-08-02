using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// Every OAuth2 client this application registers must declare where a logout may land.
///
/// <para>
/// Hydra refuses <c>post_logout_redirect_uri</c> outright unless the client whitelists it —
/// "Logout failed because query parameter post_logout_redirect_uri is not a whitelisted as a
/// post_logout_redirect_uri for the client". Nothing here ever wrote that field, so signing out of
/// the console produced Hydra's raw error page, and any integrator calling the browser SDK's
/// <c>logout()</c> got the same. The registration tests that existed asserted only that the call
/// did not throw, which a payload missing the field satisfies perfectly.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class LogoutRedirectTests(TestFixture fixture)
{
    private static string[] PostLogoutUris(string? body)
    {
        body.Should().NotBeNull("the stub recorded no request for this client registration");
        var json = JsonDocument.Parse(body!).RootElement;
        return json.TryGetProperty("post_logout_redirect_uris", out var uris) && uris.ValueKind == JsonValueKind.Array
            ? uris.EnumerateArray().Select(u => u.GetString() ?? "").ToArray()
            : [];
    }

    // ── The console's own client ──────────────────────────────────────────────

    /// <summary>
    /// The admin console signs out through the same SDK it ships. Its client is registered on
    /// startup from <c>App__AdminSpaOrigin</c>, and the URI it lands on has to be the console's own
    /// base path, which is where the console is served — the bare origin is the API.
    /// </summary>
    [Fact]
    public async Task AdminSpaClient_RegistersThePostLogoutRedirectUri()
    {
        fixture.Hydra.ResetDefaults();
        using var scope = fixture.Services.CreateScope();
        var hydra = scope.ServiceProvider.GetRequiredService<RediensIAM.Services.HydraService>();

        await hydra.EnsureAdminSpaClientAsync("https://console.example.test");

        var uris = PostLogoutUris(fixture.Hydra.LastRequestBody("/admin/clients", "POST"));
        uris.Should().Contain("https://console.example.test/console/",
            "a logout that cannot name where it lands is a logout Hydra refuses");
    }

    /// <summary>The update branch must write the same field as the create branch.</summary>
    [Fact]
    public async Task AdminSpaClient_RegistersThePostLogoutRedirectUriOnUpdateToo()
    {
        fixture.Hydra.ResetDefaults();
        fixture.Hydra.SetupClientGetResponse(RediensIAM.Config.Roles.AdminClientId);
        using var scope = fixture.Services.CreateScope();
        var hydra = scope.ServiceProvider.GetRequiredService<RediensIAM.Services.HydraService>();

        await hydra.EnsureAdminSpaClientAsync("https://console.example.test");

        PostLogoutUris(fixture.Hydra.LastRequestBody("/admin/clients/", "PUT"))
            .Should().Contain("https://console.example.test/console/");
    }

    // ── Tenant clients ────────────────────────────────────────────────────────

    /// <summary>
    /// A project's client is the one an integrator's application uses. It carries the redirect URIs
    /// the tenant supplied and nothing else, so the SDK's own <c>logout()</c> — the one this repo
    /// ships and documents — failed against every project ever created.
    /// </summary>
    [Fact]
    public async Task CreateProject_RegistersThePostLogoutRedirectUrisItWasGiven()
    {
        fixture.Hydra.ResetDefaults();
        var (org, list) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));

        var res = await client.PostAsJsonAsync("/org/projects", new
        {
            name = $"proj-{Guid.NewGuid():N}",
            slug = $"p{Guid.NewGuid():N}"[..12],
            redirect_uris = new[] { "https://app.tenant.test/callback" },
            post_logout_redirect_uris = new[] { "https://app.tenant.test/" },
        });
        ((int)res.StatusCode).Should().BeLessThan(400);

        PostLogoutUris(fixture.Hydra.LastRequestBody("/admin/clients", "POST"))
            .Should().Contain("https://app.tenant.test/");
    }

    /// <summary>
    /// The generic client endpoint takes a whole OAuth2 client description. Omitting the field from
    /// its request record means an operator cannot register it at all, by any route.
    /// </summary>
    [Fact]
    public async Task CreateHydraClient_ForwardsThePostLogoutRedirectUris()
    {
        fixture.Hydra.ResetDefaults();
        var (_, list) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        var res = await client.PostAsJsonAsync("/admin/hydra/clients", new
        {
            client_name = "Integration",
            grant_types = new[] { "authorization_code" },
            redirect_uris = new[] { "https://partner.test/cb" },
            post_logout_redirect_uris = new[] { "https://partner.test/bye" },
        });
        ((int)res.StatusCode).Should().BeLessThan(400);

        PostLogoutUris(fixture.Hydra.LastRequestBody("/admin/clients", "POST"))
            .Should().Contain("https://partner.test/bye");
    }
}
