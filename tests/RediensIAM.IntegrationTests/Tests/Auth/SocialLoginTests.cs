using System.Web;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Tests for OAuthStart, OAuthCallback, and OAuthLinkStart controller actions.
/// Covers error branches and success redirects without requiring live external HTTP calls.
/// The success-path redirect to e.g. GitHub is verified by asserting the 302 Location header.
/// </summary>
[Collection("RediensIAM")]
public class SocialLoginTests(TestFixture fixture)
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets project.LoginTheme so GetProviderConfig finds a GitHub provider.
    /// The value must be saved and will be re-read as JsonElement by the API.
    /// </summary>
    private async Task AddGithubProviderAsync(Project project,
        string clientId = "gh-client", string clientSecret = "gh-secret")
    {
        project.LoginTheme = new Dictionary<string, object>
        {
            ["providers"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["id"]            = "github",
                    ["type"]          = "github",
                    ["client_id"]     = clientId,
                    ["client_secret"] = clientSecret,
                }
            }
        };
        await fixture.Db.SaveChangesAsync();
    }

    // ── GET /auth/oauth2/start ────────────────────────────────────────────────

    [Fact]
    public async Task OAuthStart_InvalidChallenge_Returns400()
    {
        // No Hydra stub setup → default returns 404 → HydraService throws → controller returns 400
        var res = await fixture.Client.GetAsync(
            "/auth/oauth2/start?login_challenge=bad-challenge&provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_challenge");
    }

    [Fact]
    public async Task OAuthStart_ProjectNotReady_Returns400()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        // project has no AssignedUserListId

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={challenge}&provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_not_ready");
    }

    [Fact]
    public async Task OAuthStart_ProviderNotFound_Returns400()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        // LoginTheme stays null — no providers configured
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={challenge}&provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("provider_not_found");
    }

    [Fact]
    public async Task OAuthStart_GithubProvider_RedirectsToGithubAuthorizeUrl()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await AddGithubProviderAsync(project);

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={challenge}&provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("github.com/login/oauth/authorize");
        res.Headers.Location.ToString().Should().Contain("client_id=gh-client");
    }

    [Fact]
    public async Task OAuthStart_ProviderNotConfigured_Returns400()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;

        // Provider with empty client_id → "provider_not_configured"
        project.LoginTheme = new Dictionary<string, object>
        {
            ["providers"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["id"]        = "github",
                    ["type"]      = "github",
                    ["client_id"] = "",
                }
            }
        };
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={challenge}&provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("provider_not_configured");
    }

    // ── GET /auth/oauth2/callback ─────────────────────────────────────────────

    [Fact]
    public async Task OAuthCallback_MissingState_Returns400()
    {
        var res = await fixture.Client.GetAsync("/auth/oauth2/callback?code=someCode");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_state");
    }

    [Fact]
    public async Task OAuthCallback_InvalidState_Returns400()
    {
        var res = await fixture.Client.GetAsync(
            "/auth/oauth2/callback?code=someCode&state=unknown-state-xyz");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_or_expired_state");
    }

    [Fact]
    public async Task OAuthCallback_OAuthErrorParam_RedirectsToErrorPage()
    {
        // First store a valid state via OAuthStart, then pass it to callback with error param
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await AddGithubProviderAsync(project);

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        // OAuthStart stores state in Redis and redirects to GitHub with ?state=...
        var startRes  = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={challenge}&provider_id=github");
        var location  = startRes.Headers.Location!.ToString();
        var state     = HttpUtility.ParseQueryString(new Uri(location).Query)["state"]!;

        // Simulate user declining at the OAuth provider — GitHub sends back ?error=access_denied
        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/callback?error=access_denied&state={Uri.EscapeDataString(state)}");

        // Controller redirects to error page (not a 400)
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("oauth2/error");
    }

    // ── GET /auth/oauth2/link/start ───────────────────────────────────────────

    [Fact]
    public async Task OAuthLinkStart_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync(
            "/auth/oauth2/link/start?provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OAuthLinkStart_ProviderNotFound_Returns400()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        // No providers in LoginTheme
        await fixture.Db.SaveChangesAsync();

        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/auth/oauth2/link/start?provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("provider_not_found");
    }

    [Fact]
    public async Task OAuthLinkStart_AlreadyLinked_Returns400()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await AddGithubProviderAsync(project);

        var user = await fixture.Seed.CreateUserAsync(list.Id);

        // Seed an existing GitHub social account for this user
        fixture.Db.UserSocialAccounts.Add(new UserSocialAccount
        {
            Id             = Guid.NewGuid(),
            UserId         = user.Id,
            Provider       = "github",
            ProviderUserId = "gh-123",
            LinkedAt       = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/auth/oauth2/link/start?provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("provider_already_linked");
    }

    [Fact]
    public async Task OAuthLinkStart_GithubProvider_RedirectsToGithubAuthorizeUrl()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();
        var project   = await fixture.Seed.CreateProjectAsync(org.Id);
        var list      = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await AddGithubProviderAsync(project);

        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/auth/oauth2/link/start?provider_id=github");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("github.com/login/oauth/authorize");
        res.Headers.Location.ToString().Should().Contain("client_id=gh-client");
    }
}
