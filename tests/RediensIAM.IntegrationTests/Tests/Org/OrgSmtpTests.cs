using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Org;

[Collection("RediensIAM")]
public class OrgSmtpTests(TestFixture fixture)
{
    private async Task<(Organisation org, HttpClient client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        return (org, fixture.ClientWithToken(token));
    }

    // ── GET /org/smtp ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSmtp_OrgAdmin_Returns200()
    {
        var (_, client) = await OrgAdminClientAsync();

        var res = await client.GetAsync("/org/smtp");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSmtp_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/org/smtp");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PUT /org/smtp ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SetSmtp_OrgAdmin_Returns200()
    {
        var (_, client) = await OrgAdminClientAsync();

        var res = await client.PutAsJsonAsync("/org/smtp", new
        {
            host         = "smtp.org-test.com",
            port         = 587,
            start_tls    = true,
            username     = "mailer@org-test.com",
            password     = "OrgSmtpPass!",
            from_address = "noreply@org-test.com",
            from_name    = "Org Test IAM"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetSmtp_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.org-test.com"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetSmtp_ThenGet_ReturnsSavedConfig()
    {
        var (_, client) = await OrgAdminClientAsync();

        await client.PutAsJsonAsync("/org/smtp", new
        {
            host         = "smtp.persist-test.com",
            port         = 465,
            start_tls    = false,
            username     = "user@persist-test.com",
            password     = "PersistP@ss!",
            from_address = "no-reply@persist-test.com",
            from_name    = "Persist Org"
        });

        var getRes = await client.GetAsync("/org/smtp");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getRes.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("host").GetString().Should().Be("smtp.persist-test.com");
        body.GetProperty("port").GetInt32().Should().Be(465);
    }

    [Fact]
    public async Task SetSmtp_PasswordIsEncryptedInDb()
    {
        var (org, client) = await OrgAdminClientAsync();

        await client.PutAsJsonAsync("/org/smtp", new
        {
            host         = "smtp.encrypt-check.com",
            port         = 587,
            start_tls    = true,
            username     = "user@encrypt-check.com",
            password     = "PlainTextP@ss!",
            from_address = "noreply@encrypt-check.com",
            from_name    = "Encrypt Check"
        });

        await fixture.RefreshDbAsync();
        var config = fixture.Db.OrgSmtpConfigs.FirstOrDefault(c => c.OrgId == org.Id);
        config.Should().NotBeNull();
        // Password should be stored encrypted — not plaintext
        config!.PasswordEnc.Should().NotBe("PlainTextP@ss!");
        config.PasswordEnc.Should().NotBeNullOrEmpty();
    }
}
