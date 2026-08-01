using System.Net.Http.Headers;
using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// <c>GET /admin/system/health</c> used to hand the caller the raw <c>ex.Message</c> of whatever
/// failed. An exception thrown by a dependency connection routinely carries a hostname, a port, a
/// DSN fragment or a certificate subject, and this response lands in browser history and in audit
/// metadata — the SMTP username beside it was already redacted for exactly that reason. The two
/// remaining branches now answer with a stable code and log the detail server-side.
///
/// <para>Both tests assert the substance, not the spelling: what matters is that the text the
/// exception carried does not appear in the response body.</para>
/// </summary>
[Collection("RediensIAM")]
public class HealthDetailRegressionTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminClientAsync()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin        = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token        = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(token);
    }

    /// <summary>The HTTP probe branch: Hydra, Keto read and Keto write all route through it.</summary>
    [Fact]
    public async Task Health_ProbeFailure_ReturnsACodeRatherThanTheExceptionText()
    {
        var client = await SuperAdminClientAsync();

        fixture.Hydra.SetHealthFailure();
        try
        {
            var res  = await client.GetAsync("/admin/system/health");
            var raw  = await res.Content.ReadAsStringAsync();
            var body = JsonSerializer.Deserialize<JsonElement>(raw);

            var hydra = body.GetProperty("checks").EnumerateArray()
                .First(c => c.GetProperty("name").GetString() == "Hydra (admin)");

            hydra.GetProperty("status").GetString().Should().Be("Error");
            hydra.GetProperty("detail").GetString().Should().Be("probe_failed");
            raw.Should().NotContain("Response status code does not indicate success",
                "the exception's own words are what carried the deployment detail out");
        }
        finally
        {
            fixture.Hydra.RestoreHealth();
        }
    }

    /// <summary>
    /// The in-process check branch — the database, the cache and SMTP route through it. The
    /// message here is shaped like the ones that make this a finding: a host, a port and a
    /// certificate subject, all of which a failing SMTP or Postgres connection really does put
    /// in <c>ex.Message</c>.
    /// </summary>
    [Fact]
    public async Task Health_CheckFailure_DoesNotEchoTheHostPortOrCertificateInTheException()
    {
        var (client, factory) = fixture.CreateSmtpEnabledClient(new LeakyEmailService());
        await using var _f = factory;

        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.Seed.SuperAdminToken(admin.Id));
        fixture.Keto.AllowAll();

        var res  = await client.GetAsync("/admin/system/health");
        var raw  = await res.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(raw);

        var smtp = body.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "SMTP");

        smtp.GetProperty("status").GetString().Should().Be("Error");
        smtp.GetProperty("detail").GetString().Should().Be("check_failed");
        raw.Should().NotContain(LeakyEmailService.LeakyMessage,
            "an exception message from a failed dependency connection is deployment detail, " +
            "and this response is as readable as a stack trace would be");
    }
}

/// <summary>
/// Throws the shape of message a real failure produces: an internal hostname, a port and the
/// certificate subject it did not accept.
/// </summary>
file sealed class LeakyEmailService : IEmailService
{
    public const string LeakyMessage =
        "Connection to smtp-relay.internal.corp:587 failed: certificate CN=mail.internal.corp not trusted";

    public Task CheckConnectivityAsync() => throw new InvalidOperationException(LeakyMessage);

    public Task SendOtpAsync(string to, string code, string purpose,
        Guid? orgId = null, Guid? projectId = null) => Task.CompletedTask;

    public Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? projectId = null) =>
        Task.CompletedTask;

    public Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent,
        DateTimeOffset loginAt, Guid? orgId = null) => Task.CompletedTask;
}
