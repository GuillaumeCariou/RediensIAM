using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// Verifies that social login client_secret values are never stored in plaintext
/// and are never exposed through API responses.
/// </summary>
[Collection("RediensIAM")]
public class SocialLoginSecretTests(TestFixture fixture)
{
    private static readonly Dictionary<string, object> GoogleProvider = new()
    {
        ["providers"] = new[]
        {
            new Dictionary<string, object>
            {
                ["id"]            = "google",
                ["type"]          = "google",
                ["enabled"]       = true,
                ["client_id"]     = "my-google-client-id",
                ["client_secret"] = "super-secret-value-123"
            }
        }
    };

    // ── Secret stored encrypted, not plaintext ────────────────────────────────

    [Fact]
    public async Task UpdateLoginTheme_ClientSecret_IsStoredEncrypted()
    {
        var (org, orgList)  = await fixture.Seed.CreateOrgAsync();
        var project         = await fixture.Seed.CreateProjectAsync(org.Id);
        var admin           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token           = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client          = fixture.ClientWithToken(token);

        await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { login_theme = GoogleProvider });

        await fixture.RefreshDbAsync();
        var saved = await fixture.Db.Projects.FirstAsync(p => p.Id == project.Id);

        // client_secret must not be present anywhere in the raw JSONB
        var json = JsonSerializer.Serialize(saved.LoginTheme);
        json.Should().NotContain("super-secret-value-123",
            "client_secret must not be stored in plaintext");
        json.Should().Contain("client_secret_enc",
            "encrypted secret must be stored as client_secret_enc");
        json.Should().NotContain("\"client_secret\"",
            "the plain client_secret key must not appear in storage");
    }

    // ── Secret never exposed in GET responses ─────────────────────────────────

    [Fact]
    public async Task GetProject_OrgEndpoint_NeverExposesSecret()
    {
        var (org, orgList)  = await fixture.Seed.CreateOrgAsync();
        var project         = await fixture.Seed.CreateProjectAsync(org.Id);
        var admin           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token           = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client          = fixture.ClientWithToken(token);

        // Save a theme with a secret via the admin path
        var superToken  = fixture.Seed.SuperAdminToken(admin.Id);
        var superClient = fixture.ClientWithToken(superToken);
        await superClient.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { login_theme = GoogleProvider });

        // Read it back via org endpoint
        var res  = await client.GetAsync($"/org/projects/{project.Id}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var themeJson = body.GetProperty("login_theme").GetRawText();

        themeJson.Should().NotContain("super-secret-value-123");
        themeJson.Should().NotContain("client_secret_enc");
    }

    [Fact]
    public async Task GetLoginTheme_ViaAuthEndpoint_NeverExposesSecret()
    {
        var (org, orgList)  = await fixture.Seed.CreateOrgAsync();
        var project         = await fixture.Seed.CreateProjectAsync(org.Id);
        var admin           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var superToken      = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var superClient     = fixture.ClientWithToken(superToken);

        await superClient.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { login_theme = GoogleProvider });

        // GET /auth/login/theme via a login challenge — used by the login SPA
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res  = await fixture.Client.GetAsync($"/auth/login/theme?login_challenge={challenge}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var themeJson = body.GetProperty("login_theme").GetRawText();

        themeJson.Should().NotContain("super-secret-value-123");
        themeJson.Should().NotContain("client_secret_enc");
    }

    // ── Secret preserved when not re-sent ─────────────────────────────────────

    [Fact]
    public async Task UpdateLoginTheme_SecretOmitted_PreservesExistingSecret()
    {
        var (org, orgList)  = await fixture.Seed.CreateOrgAsync();
        var project         = await fixture.Seed.CreateProjectAsync(org.Id);
        var admin           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token           = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client          = fixture.ClientWithToken(token);

        // First save — sets the secret
        await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { login_theme = GoogleProvider });

        await fixture.RefreshDbAsync();
        var firstSave = await fixture.Db.Projects.FirstAsync(p => p.Id == project.Id);
        var firstJson = JsonSerializer.Serialize(firstSave.LoginTheme);
        var firstEnc  = JsonDocument.Parse(firstJson)
            .RootElement.GetProperty("providers")[0]
            .GetProperty("client_secret_enc").GetString()!;

        // Second save — omits client_secret (simulates frontend "secret saved" UX)
        var themeWithoutSecret = new Dictionary<string, object>
        {
            ["providers"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["id"]        = "google",
                    ["type"]      = "google",
                    ["enabled"]   = true,
                    ["client_id"] = "my-google-client-id-updated"
                    // no client_secret
                }
            }
        };
        await client.PatchAsJsonAsync($"/admin/projects/{project.Id}", new { login_theme = themeWithoutSecret });

        await fixture.RefreshDbAsync();
        var secondSave  = await fixture.Db.Projects.FirstAsync(p => p.Id == project.Id);
        var secondJson  = JsonSerializer.Serialize(secondSave.LoginTheme);
        var secondEnc   = JsonDocument.Parse(secondJson)
            .RootElement.GetProperty("providers")[0]
            .GetProperty("client_secret_enc").GetString()!;

        secondEnc.Should().Be(firstEnc, "existing encrypted secret must be preserved when not re-sent");

        // client_id update must be persisted
        var clientId = JsonDocument.Parse(secondJson)
            .RootElement.GetProperty("providers")[0]
            .GetProperty("client_id").GetString()!;
        clientId.Should().Be("my-google-client-id-updated");
    }

    // ── OrgController path also encrypts ─────────────────────────────────────

    [Fact]
    public async Task OrgController_UpdateLoginTheme_EncryptsSecret()
    {
        var (org, orgList)  = await fixture.Seed.CreateOrgAsync();
        var project         = await fixture.Seed.CreateProjectAsync(org.Id);
        var admin           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token           = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client          = fixture.ClientWithToken(token);

        var res = await client.PatchAsJsonAsync($"/org/projects/{project.Id}", new { login_theme = GoogleProvider });

        res.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        await fixture.RefreshDbAsync();
        var saved = await fixture.Db.Projects.FirstAsync(p => p.Id == project.Id);
        var json  = JsonSerializer.Serialize(saved.LoginTheme);

        json.Should().NotContain("super-secret-value-123");
        json.Should().Contain("client_secret_enc");
    }
}
