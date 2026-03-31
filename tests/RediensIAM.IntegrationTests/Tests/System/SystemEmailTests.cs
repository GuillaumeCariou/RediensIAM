using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

[Collection("RediensIAM")]
public class SystemEmailTests(TestFixture fixture)
{
    private async Task<(Organisation org, HttpClient client)> SuperAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(user.Id);
        fixture.Keto.AllowAll();
        return (org, fixture.ClientWithToken(token));
    }

    // ── GET /admin/email/overview ─────────────────────────────────────────────

    [Fact]
    public async Task GetEmailOverview_SuperAdmin_Returns200()
    {
        var (_, client) = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/email/overview");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("global_smtp", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetEmailOverview_Unauthenticated_Returns401Or403()
    {
        var res = await fixture.Client.GetAsync("/admin/email/overview");

        // GET /admin/* without auth → gateway middleware bypassed → filter returns 403
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // ── PUT /admin/organizations/{id}/smtp ────────────────────────────────────

    [Fact]
    public async Task UpdateOrgSmtp_SuperAdmin_Returns200()
    {
        var (org, client) = await SuperAdminClientAsync();

        var res = await client.PutAsJsonAsync($"/admin/organizations/{org.Id}/smtp", new
        {
            host         = "smtp.example.com",
            port         = 587,
            start_tls    = true,
            username     = "test@example.com",
            password     = "SuperSecret!",
            from_address = "noreply@example.com",
            from_name    = "Test IAM"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateOrgSmtp_Unauthenticated_Returns401()
    {
        var (org, _) = await SuperAdminClientAsync();

        var res = await fixture.Client.PutAsJsonAsync($"/admin/organizations/{org.Id}/smtp", new
        {
            host = "smtp.example.com"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateOrgSmtp_Persists()
    {
        var (org, client) = await SuperAdminClientAsync();

        await client.PutAsJsonAsync($"/admin/organizations/{org.Id}/smtp", new
        {
            host         = "mail.persist-test.com",
            port         = 465,
            start_tls    = false,
            username     = "sender@persist-test.com",
            password     = "P@ssword123",
            from_address = "no-reply@persist-test.com",
            from_name    = "Persist Test"
        });

        var getRes = await client.GetAsync($"/admin/organizations/{org.Id}/smtp");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getRes.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("host").GetString().Should().Be("mail.persist-test.com");
    }

    // ── GET /admin/organizations/{id}/smtp ────────────────────────────────────

    [Fact]
    public async Task GetOrgSmtp_NotConfigured_ReturnsFalseConfigured()
    {
        var (org, client) = await SuperAdminClientAsync();

        var res = await client.GetAsync($"/admin/organizations/{org.Id}/smtp");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetOrgSmtp_Configured_ReturnsTrueConfigured()
    {
        var (org, client) = await SuperAdminClientAsync();

        await client.PutAsJsonAsync($"/admin/organizations/{org.Id}/smtp", new
        {
            host         = "smtp.configured.com",
            port         = 587,
            start_tls    = true,
            username     = "user@configured.com",
            password     = "s3cr3t",
            from_address = "noreply@configured.com",
            from_name    = "Configured"
        });

        var res  = await client.GetAsync($"/admin/organizations/{org.Id}/smtp");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configured").GetBoolean().Should().BeTrue();
    }

    // ── DELETE /admin/organizations/{id}/smtp ─────────────────────────────────

    [Fact]
    public async Task DeleteOrgSmtp_Configured_Returns204AndRemoves()
    {
        var (org, client) = await SuperAdminClientAsync();

        await client.PutAsJsonAsync($"/admin/organizations/{org.Id}/smtp", new
        {
            host         = "smtp.delete-me.com",
            port         = 587,
            start_tls    = true,
            username     = "user@delete-me.com",
            password     = "s3cr3t",
            from_address = "noreply@delete-me.com",
            from_name    = "To Delete"
        });

        var res = await client.DeleteAsync($"/admin/organizations/{org.Id}/smtp");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getRes  = await client.GetAsync($"/admin/organizations/{org.Id}/smtp");
        var getBody = await getRes.Content.ReadFromJsonAsync<JsonElement>();
        getBody.GetProperty("configured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DeleteOrgSmtp_NotConfigured_Returns204()
    {
        var (org, client) = await SuperAdminClientAsync();

        var res = await client.DeleteAsync($"/admin/organizations/{org.Id}/smtp");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── POST /admin/organizations/{id}/smtp/test ──────────────────────────────

    [Fact]
    public async Task TestOrgSmtp_SuperAdmin_Returns200WithMessage()
    {
        var (org, client) = await SuperAdminClientAsync();

        var res = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/smtp/test", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("test_email_sent");
        body.TryGetProperty("to", out _).Should().BeTrue();
    }

    [Fact]
    public async Task TestOrgSmtp_UnknownOrg_Returns200()
    {
        var (_, client) = await SuperAdminClientAsync();

        var res = await client.PostAsJsonAsync($"/admin/organizations/{Guid.NewGuid()}/smtp/test", new { });

        // Actor is resolved from the bearer token, not the org id path param,
        // and the stub email service always succeeds — so the endpoint returns 200.
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TestOrgSmtp_Unauthenticated_Returns401Or403()
    {
        var (org, _) = await SuperAdminClientAsync();

        var res = await fixture.Client.PostAsJsonAsync(
            $"/admin/organizations/{org.Id}/smtp/test", new { });

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
