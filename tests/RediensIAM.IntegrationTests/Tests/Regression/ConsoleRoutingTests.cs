using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// The console's URLs belong to the browser, not to the management API.
///
/// <para>
/// They used to share one prefix. <c>SystemHealthController</c> is mounted on
/// <c>[Route("admin/system")]</c>, which is exactly where the console's entire System scope lives —
/// thirty of its forty-nine pages. A browser asking for <c>/admin/system/organisations</c> was
/// therefore answered by the API's authorization gate with a bare 401 before the SPA fallback could
/// serve <c>index.html</c>: no bookmark, no refresh, no pasted link worked, and the screen showed a
/// spinner that never resolved because no JavaScript had loaded to resolve it.
/// </para>
///
/// <para>
/// The fix is separation rather than an exemption: the console is served under its own prefix and
/// <c>/admin</c> is the API's alone. This test is what keeps a future controller from quietly
/// claiming a page again — it asks for each console route the way a browser does, with no
/// Authorization header, and refuses a 401.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class ConsoleRoutingTests(TestFixture fixture)
{
    /// <summary>Every client-side route in <c>frontend/admin/src/App.tsx</c>, with ids filled in.</summary>
    public static TheoryData<string> ConsoleRoutes()
    {
        var id = Guid.NewGuid().ToString();
        var routes = new[]
        {
            "", "account",
            "system", "system/admins", "system/audit-log", "system/email", "system/health",
            "system/metrics", "system/organisations", "system/projects", "system/service-accounts",
            "system/users", "system/userlists",
            $"system/organisations/{id}", $"system/organisations/{id}/admins",
            $"system/organisations/{id}/audit-log", $"system/organisations/{id}/email",
            $"system/organisations/{id}/projects", $"system/organisations/{id}/service-accounts",
            $"system/organisations/{id}/settings", $"system/organisations/{id}/userlists",
            $"system/organisations/{id}/webhooks",
            $"system/organisations/{id}/projects/{id}", $"system/organisations/{id}/projects/{id}/users",
            $"system/organisations/{id}/projects/{id}/roles",
            $"system/organisations/{id}/projects/{id}/authentication",
            $"system/organisations/{id}/projects/{id}/settings",
            $"system/service-accounts/{id}", $"system/userlists/{id}",
            "org", "org/admins", "org/audit-log", "org/email", "org/projects",
            "org/service-accounts", "org/settings", "org/userlists", "org/webhooks",
            $"org/service-accounts/{id}", $"org/userlists/{id}",
            "project", "project/authentication", "project/roles", "project/service-accounts",
            "project/settings", "project/users",
        };
        var data = new TheoryData<string>();
        foreach (var r in routes) data.Add(r);
        return data;
    }

    [Theory]
    [MemberData(nameof(ConsoleRoutes))]
    public async Task EveryConsoleRoute_IsServedToABrowserRatherThanRefused(string route)
    {
        // No Authorization header, exactly like a browser opening a bookmark. The response may well
        // be a 404 here — the built SPA is not present in a test run — but it must never be a 401,
        // which means an API gate answered a page request.
        var res = await fixture.Client.GetAsync($"/{RediensIAM.Config.Roles.ConsoleBasePath}/{route}");

        res.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            $"/{RediensIAM.Config.Roles.ConsoleBasePath}/{route} is a page the browser loads before it holds any token");
    }
}
