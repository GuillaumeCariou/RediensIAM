using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Covers AuthController social-login callback branches not reached by SocialLoginTests.cs:
///   - OAuthCallback project has no AssignedUserListId     (line 1108 true branch)
///   - OAuthCallback provider not in LoginTheme array      (lines 1110-1111 true branch, 1265-1266)
///   - OAuthCallback exchange returns null profile         (lines 1113-1114)
///   - OAuthCallback success path — new user created       (lines 1119-1145, 1177-1253)
///   - OAuthCallback RequireRoleToLogin denied             (lines 1122-1130)
///   - HandleOAuthLinkModeAsync success                    (lines 1148-1174)
///   - HandleOAuthLinkModeAsync provider already linked    (line 1155)
///   - ResolveProviderSecret corrupt client_secret_enc     (lines 1283-1286)
/// </summary>
[Collection("RediensIAM")]
public class SocialLoginCoverageTests(TestFixture fixture)
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(Organisation org, Project project, UserList list)> ScaffoldWithGithubAsync(
        string clientId = "gh-id", string? clientSecretEnc = null)
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;

        var providerEntry = new Dictionary<string, object>
        {
            ["id"]        = "github",
            ["type"]      = "github",
            ["client_id"] = clientId,
        };
        if (clientSecretEnc != null)
            providerEntry["client_secret_enc"] = clientSecretEnc;
        else
            providerEntry["client_secret"] = "gh-secret";

        project.LoginTheme = new Dictionary<string, object>
        {
            ["providers"] = new[] { providerEntry }
        };
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    /// <summary>Obtains an OAuth state token by calling OAuthStart, returns the state string.</summary>
    private async Task<string> GetOAuthStateAsync(Project project, string challengeId)
    {
        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={challengeId}&provider_id=github");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = res.Headers.Location!.ToString();
        return HttpUtility.ParseQueryString(new Uri(location).Query)["state"]!;
    }

    // ── line 1108: project has no AssignedUserListId ──────────────────────────

    [Fact]
    public async Task OAuthCallback_ProjectNoList_RedirectsToError()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        // project has no AssignedUserListId — but we set a GitHub provider so state storage works
        project.LoginTheme = new Dictionary<string, object>
        {
            ["providers"] = new[] { new Dictionary<string, object>
            {
                ["id"] = "github", ["type"] = "github", ["client_id"] = "gh-id", ["client_secret"] = "s"
            }}
        };
        await fixture.Db.SaveChangesAsync();

        // Store state directly via IDistributedCache (bypass OAuthStart's project-not-ready check)
        var cache = fixture.GetService<IDistributedCache>();
        var stateData = new OAuthStateData(
            Guid.NewGuid().ToString("N"), project.Id.ToString(), "github");
        var stateJson  = JsonSerializer.Serialize(stateData);
        var stateKey   = $"state-nolist-{Guid.NewGuid():N}";
        await cache.SetStringAsync($"oauth2:state:{stateKey}", stateJson,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/callback?code=any&state={Uri.EscapeDataString(stateKey)}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("oauth2/error");
    }

    // ── lines 1110-1111 + 1265-1266: provider ID not in LoginTheme array ──────

    [Fact]
    public async Task OAuthCallback_ProviderNotInTheme_RedirectsToError()
    {
        var (org, project, _) = await ScaffoldWithGithubAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        // Get state for "github"
        var state = await GetOAuthStateAsync(project, challenge);

        // Remove all providers from project's LoginTheme so GetProviderConfig returns null
        // (the state still references "github", but the project now has no providers → null loop exit at 1265-1266)
        project.LoginTheme = new Dictionary<string, object>
        {
            ["providers"] = Array.Empty<Dictionary<string, object>>()
        };
        await fixture.Db.SaveChangesAsync();

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/callback?code=any&state={Uri.EscapeDataString(state)}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("oauth2/error");
    }

    // ── lines 1113-1114: profile is null (exchange fails) ─────────────────────

    [Fact]
    public async Task OAuthCallback_ExchangeFails_ProfileNull_RedirectsToError()
    {
        // HibpStub returns empty body for github.com → JsonDocument.Parse("") throws
        // → ExchangeAndGetProfileAsync catches → returns null → line 1114 redirect
        var (org, project, _) = await ScaffoldWithGithubAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var state = await GetOAuthStateAsync(project, challenge);

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/callback?code=will-fail&state={Uri.EscapeDataString(state)}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("oauth2/error");
    }

    // ── lines 1283-1286: corrupt client_secret_enc falls through ─────────────

    [Fact]
    public async Task OAuthStart_CorruptEncryptedSecret_FallsBackToEmptySecret()
    {
        // Providing a non-decodable client_secret_enc causes ResolveProviderSecret to
        // catch the decryption exception and fall through (lines 1283-1286).
        // The client_id is still set, so OAuthStart still redirects.
        var (org, project, _) = await ScaffoldWithGithubAsync(
            clientId: "gh-id", clientSecretEnc: "not-valid-base64!!!");

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={challenge}&provider_id=github");

        // Redirect still happens — secret falls back to empty string
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("github.com/login/oauth/authorize");
    }

    // ── lines 1119-1145 + 1177-1253: success path — new user created ─────────

    [Fact]
    public async Task OAuthCallback_SuccessPath_CreatesUserAndRedirects()
    {
        fixture.HibpStub.SetupGithubProfile(userId: 99001, email: "gh-new@socialtest.dev");
        try
        {
            var (org, project, _) = await ScaffoldWithGithubAsync();

            var challenge = Guid.NewGuid().ToString("N");
            fixture.Hydra.SetupLoginChallengeWithProject(
                challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());
            fixture.Keto.AllowAll();

            var state = await GetOAuthStateAsync(project, challenge);

            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=valid-code&state={Uri.EscapeDataString(state)}");

            // Hydra AcceptLogin → redirects to Hydra's consent URL
            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            res.Headers.Location!.ToString().Should().NotContain("oauth2/error");

            // Verify the social user was created in the DB
            await fixture.RefreshDbAsync();
            var social = await fixture.Db.UserSocialAccounts
                .FirstOrDefaultAsync(s => s.Provider == "github" && s.ProviderUserId == "99001");
            social.Should().NotBeNull();
        }
        finally
        {
            fixture.HibpStub.ClearGithub();
        }
    }

    // ── lines 1122-1130: RequireRoleToLogin denied ────────────────────────────

    [Fact]
    public async Task OAuthCallback_RequireRoleToLogin_NoRole_Rejects()
    {
        fixture.HibpStub.SetupGithubProfile(userId: 99002, email: "gh-norole@socialtest.dev");
        try
        {
            var (org, project, _) = await ScaffoldWithGithubAsync();
            project.RequireRoleToLogin = true;
            await fixture.Db.SaveChangesAsync();

            var challenge = Guid.NewGuid().ToString("N");
            fixture.Hydra.SetupLoginChallengeWithProject(
                challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());
            fixture.Keto.AllowAll();

            var state = await GetOAuthStateAsync(project, challenge);

            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=valid-code&state={Uri.EscapeDataString(state)}");

            // Hydra RejectLogin → redirect
            res.StatusCode.Should().Be(HttpStatusCode.Redirect);

            // Clean up the social account created (new user was created before role check)
            await fixture.RefreshDbAsync();
            var social = fixture.Db.UserSocialAccounts
                .Where(s => s.Provider == "github" && s.ProviderUserId == "99002").ToList();
            fixture.Db.UserSocialAccounts.RemoveRange(social);
            await fixture.Db.SaveChangesAsync();
        }
        finally
        {
            fixture.HibpStub.ClearGithub();
        }
    }

    // ── lines 1148-1174: HandleOAuthLinkModeAsync success ────────────────────

    [Fact]
    public async Task OAuthCallback_LinkMode_Success_LinksProvider()
    {
        fixture.HibpStub.SetupGithubProfile(userId: 99003, email: "gh-link@socialtest.dev");
        try
        {
            var (org, project, list) = await ScaffoldWithGithubAsync();
            var user = await fixture.Seed.CreateUserAsync(list.Id);

            // Store link-mode state directly in cache
            var cache = fixture.GetService<IDistributedCache>();
            var stateData = new OAuthStateData(
                LoginChallenge: "",
                ProjectId:      project.Id.ToString(),
                ProviderId:     "github",
                LinkMode:       true,
                LinkUserId:     user.Id.ToString(),
                LinkProjectId:  project.Id.ToString());
            var stateJson = JsonSerializer.Serialize(stateData);
            var stateKey  = $"link-ok-{Guid.NewGuid():N}";
            await cache.SetStringAsync($"oauth2:state:{stateKey}", stateJson,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

            fixture.Keto.AllowAll();

            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=valid-code&state={Uri.EscapeDataString(stateKey)}");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            res.Headers.Location!.ToString().Should().Contain("link_success=1");

            // Verify the social account was created
            await fixture.RefreshDbAsync();
            var social = await fixture.Db.UserSocialAccounts
                .FirstOrDefaultAsync(s => s.UserId == user.Id && s.Provider == "github");
            social.Should().NotBeNull();
        }
        finally
        {
            fixture.HibpStub.ClearGithub();
        }
    }

    // ── line 1155: HandleOAuthLinkModeAsync already linked ────────────────────

    [Fact]
    public async Task OAuthCallback_LinkMode_AlreadyLinked_RedirectsWithError()
    {
        fixture.HibpStub.SetupGithubProfile(userId: 99004, email: "gh-dup@socialtest.dev");
        try
        {
            var (org, project, list) = await ScaffoldWithGithubAsync();
            var user = await fixture.Seed.CreateUserAsync(list.Id);

            // Seed an existing social account with the same provider+providerUserId
            fixture.Db.UserSocialAccounts.Add(new UserSocialAccount
            {
                Id             = Guid.NewGuid(),
                UserId         = user.Id,
                Provider       = "github",
                ProviderUserId = "99004",  // matches the stub userId
                LinkedAt       = DateTimeOffset.UtcNow,
            });
            await fixture.Db.SaveChangesAsync();

            var cache     = fixture.GetService<IDistributedCache>();
            var stateData = new OAuthStateData(
                LoginChallenge: "",
                ProjectId:      project.Id.ToString(),
                ProviderId:     "github",
                LinkMode:       true,
                LinkUserId:     user.Id.ToString(),
                LinkProjectId:  project.Id.ToString());
            var stateJson = JsonSerializer.Serialize(stateData);
            var stateKey  = $"link-dup-{Guid.NewGuid():N}";
            await cache.SetStringAsync($"oauth2:state:{stateKey}", stateJson,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=valid-code&state={Uri.EscapeDataString(stateKey)}");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            res.Headers.Location!.ToString().Should().Contain("link_error=already_linked");
        }
        finally
        {
            fixture.HibpStub.ClearGithub();
        }
    }

    // ── Existing user with same email — link via email (lines 1188-1195) ──────

    [Fact]
    public async Task OAuthCallback_ExistingUserWithSameEmail_LinksViaSocialAccount()
    {
        const string existingEmail = "gh-existing@socialtest.dev";
        fixture.HibpStub.SetupGithubProfile(userId: 99005, email: existingEmail);
        try
        {
            var (org, project, list) = await ScaffoldWithGithubAsync();
            // Pre-create user with same email as GitHub profile
            var user = await fixture.Seed.CreateUserAsync(list.Id, email: existingEmail);

            var challenge = Guid.NewGuid().ToString("N");
            fixture.Hydra.SetupLoginChallengeWithProject(
                challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());
            fixture.Keto.AllowAll();

            var state = await GetOAuthStateAsync(project, challenge);

            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=valid-code&state={Uri.EscapeDataString(state)}");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            res.Headers.Location!.ToString().Should().NotContain("oauth2/error");

            // Verify the existing user now has a social account linked
            await fixture.RefreshDbAsync();
            var social = await fixture.Db.UserSocialAccounts
                .FirstOrDefaultAsync(s => s.UserId == user.Id && s.Provider == "github");
            social.Should().NotBeNull("existing user should be linked via email");
        }
        finally
        {
            fixture.HibpStub.ClearGithub();
        }
    }
}
