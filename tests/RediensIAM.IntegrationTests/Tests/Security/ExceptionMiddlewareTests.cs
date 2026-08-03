using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// Exercises AppExceptionMiddleware by triggering controller paths that throw
/// app-domain exceptions without catching them locally.
/// </summary>
[Collection("RediensIAM")]
public class ExceptionMiddlewareTests(TestFixture fixture)
{
    // ── ForbiddenException → 403 ──────────────────────────────────────────────

    /// <summary>
    /// The route below is chosen because nothing catches its exception: a super-admin token carries
    /// no org, so <c>OrgController.ListProjects</c> without <c>?org_id=</c> throws
    /// <c>ForbiddenException</c> and it reaches <c>AppExceptionMiddleware</c> untouched. If a local
    /// try/catch is ever added there this stops testing the middleware and starts testing the
    /// controller, without failing.
    /// </summary>
    [Fact]
    public async Task ForbiddenException_Middleware_Returns403WithJsonError()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.SuperAdminToken(user.Id);   // no org in claims
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/org/projects");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("forbidden");
        body.GetProperty("detail").GetString().Should().Be("No org context");
    }

    // ── NotFoundException → 404 ───────────────────────────────────────────────

    /// <summary>
    /// Same reasoning as the 403 case: <c>OrgController.RemoveOrgListManager</c> has no local
    /// try/catch, so the <c>NotFoundException</c> from <c>RemoveManagementRoleAsync</c> is handled
    /// by the middleware and nowhere else. Adding a catch there would hollow this test out silently.
    /// </summary>
    [Fact]
    public async Task NotFoundException_Middleware_Returns404WithJsonError()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/org/admins/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("not_found");
    }

    // ── BadRequestException → 400 ─────────────────────────────────────────────

    /// <summary>
    /// Same reasoning again: <c>OrgController.AssignOrgListManager</c> has no local try/catch, so
    /// the unknown role name below reaches the switch default in <c>AssignManagementRoleAsync</c>
    /// and its <c>BadRequestException</c> is shaped into a response by the middleware alone.
    /// </summary>
    [Fact]
    public async Task BadRequestException_Middleware_Returns400WithJsonError()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/org/admins", new
        {
            user_id = admin.Id,
            role    = "invalid_role_that_does_not_exist",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("bad_request");
    }
}
