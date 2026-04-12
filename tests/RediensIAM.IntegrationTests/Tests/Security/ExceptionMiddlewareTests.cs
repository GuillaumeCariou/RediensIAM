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
    // OrgController.ListProjects throws ForbiddenException("No org context")
    // when a super-admin token (no org in claims) calls GET /org/projects
    // without an ?org_id= query parameter. This exception is not caught in
    // the controller, so it bubbles to AppExceptionMiddleware.

    [Fact]
    public async Task ForbiddenException_Middleware_Returns403WithJsonError()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.SuperAdminToken(user.Id);   // no org in claims
        var client = fixture.ClientWithToken(token);

        // No ?org_id= → throws ForbiddenException("No org context") uncaught
        var res = await client.GetAsync("/org/projects");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // AppExceptionMiddleware format: { "error": "forbidden", "detail": "..." }
        body.GetProperty("error").GetString().Should().Be("forbidden");
        body.GetProperty("detail").GetString().Should().Be("No org context");
    }

    // ── NotFoundException → 404 ───────────────────────────────────────────────
    // OrgController.RemoveOrgListManager has NO local try-catch.
    // DELETE /org/admins/{nonExistentId} → KetoService.RemoveManagementRoleAsync
    // throws NotFoundException("Role assignment not found") → bubbles to middleware.

    [Fact]
    public async Task NotFoundException_Middleware_Returns404WithJsonError()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        // Non-existent role id → RemoveManagementRoleAsync throws NotFoundException uncaught
        var res = await client.DeleteAsync($"/org/admins/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("not_found");
    }

    // ── BadRequestException → 400 ─────────────────────────────────────────────
    // OrgController.AssignOrgListManager has NO local try-catch.
    // POST /org/admins with an unknown role string → KetoService.AssignManagementRoleAsync
    // hits the switch default and throws BadRequestException("Unknown management role: ...")
    // which bubbles to AppExceptionMiddleware.

    [Fact]
    public async Task BadRequestException_Middleware_Returns400WithJsonError()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        // Unknown role → switch default in AssignManagementRoleAsync → BadRequestException uncaught
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
