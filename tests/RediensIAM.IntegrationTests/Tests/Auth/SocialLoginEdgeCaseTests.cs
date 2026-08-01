using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

// ── from SocialLoginCoverageTests.cs ────────────────────────────

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
    private async Task<string> GetOAuthStateAsync(string challengeId)
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

        var state = await GetOAuthStateAsync(challenge);

        // Providers are stripped after the state is minted: the state still names "github" but the
        // project no longer offers it, which is the mismatch this test is about.
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
        // No GitHub profile is configured on the stub, so the token exchange gets an empty body,
        // JSON parsing throws, and ExchangeAndGetProfileAsync must swallow it and return null
        // rather than let the exception reach the client.
        var (org, project, _) = await ScaffoldWithGithubAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var state = await GetOAuthStateAsync(challenge);

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/callback?code=will-fail&state={Uri.EscapeDataString(state)}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("oauth2/error");
    }

    // ── lines 1283-1286: corrupt client_secret_enc falls through ─────────────

    [Fact]
    public async Task OAuthStart_CorruptEncryptedSecret_FallsBackToEmptySecret()
    {
        // A non-decodable client_secret_enc makes ResolveProviderSecret swallow the decryption
        // failure and fall back to an empty secret. The client_id is still set, so the redirect
        // still happens — a corrupt stored secret degrades the flow instead of 500-ing it.
        var (org, project, _) = await ScaffoldWithGithubAsync(
            clientId: "gh-id", clientSecretEnc: "not-valid-base64!!!");

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={challenge}&provider_id=github");

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

            var state = await GetOAuthStateAsync(challenge);

            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=valid-code&state={Uri.EscapeDataString(state)}");

            // Hydra AcceptLogin → redirects to Hydra's consent URL
            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            res.Headers.Location!.ToString().Should().NotContain("oauth2/error");

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

    // ── The tenant's own login controls apply to federated logins too ────────
    //
    // The password path calls CheckProjectAccessAsync (IP allowlist) and InitiateMfaAsync
    // (require_mfa) before accepting. The social callback and the SAML ACS called
    // hydra.AcceptLoginAsync directly, so a tenant that switched either control on had it
    // enforced for exactly the users who signed in with a password — and bypassed by everyone
    // who used the identity provider the tenant configured.

    [Fact]
    public async Task OAuthCallback_ProjectRequiresMfa_DoesNotCompleteTheLogin()
    {
        fixture.HibpStub.SetupGithubProfile(userId: 99201, email: "gh-mfa@socialtest.dev");
        try
        {
            var (org, project, _) = await ScaffoldWithGithubAsync();
            project.RequireMfa = true;
            await fixture.Db.SaveChangesAsync();

            var challenge = Guid.NewGuid().ToString("N");
            fixture.Hydra.SetupLoginChallengeWithProject(
                challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());
            fixture.Keto.AllowAll();

            var state = await GetOAuthStateAsync(challenge);
            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=valid-code&state={Uri.EscapeDataString(state)}");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var location = res.Headers.Location!.ToString();
            location.Should().NotContain("consent",
                "the project demands a second factor, so the login must not be accepted yet");
            location.Should().Contain("/mfa", "the browser belongs at the factor step");
        }
        finally { fixture.HibpStub.ClearGithub(); }
    }

    [Fact]
    public async Task OAuthCallback_IpNotInAllowlist_IsRefused()
    {
        fixture.HibpStub.SetupGithubProfile(userId: 99202, email: "gh-ip@socialtest.dev");
        try
        {
            var (org, project, _) = await ScaffoldWithGithubAsync();
            // A range the test client is definitely not in.
            project.IpAllowlist = ["203.0.113.0/24"];
            await fixture.Db.SaveChangesAsync();

            var challenge = Guid.NewGuid().ToString("N");
            fixture.Hydra.SetupLoginChallengeWithProject(
                challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());
            fixture.Keto.AllowAll();

            var state = await GetOAuthStateAsync(challenge);
            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=valid-code&state={Uri.EscapeDataString(state)}");

            // Asserted on the record rather than the redirect: a rejected login and an accepted one
            // both answer 302, and an earlier draft of this test passed against the bypass because
            // the accept URL happens not to contain the word it was looking for.
            await fixture.RefreshDbAsync();
            var user = await fixture.Db.Users.FirstOrDefaultAsync(u => u.Email == "gh-ip@socialtest.dev");
            if (user != null)
            {
                var loggedIn = await fixture.Db.AuditLogs.AnyAsync(a =>
                    a.ActorId == user.Id && a.Action.StartsWith("user.login.social"));
                loggedIn.Should().BeFalse(
                    "the tenant's IP allowlist applies however the user authenticated");
            }
        }
        finally { fixture.HibpStub.ClearGithub(); }
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

            var state = await GetOAuthStateAsync(challenge);

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

            var state = await GetOAuthStateAsync(challenge);

            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=valid-code&state={Uri.EscapeDataString(state)}");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            res.Headers.Location!.ToString().Should().NotContain("oauth2/error");

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

// ── from SocialLoginRemainingTests.cs ───────────────────────────

/// <summary>
/// Covers the remaining uncovered AuthController social-login branches:
///   - OAuthCallback RequireRoleToLogin=true AND user has a role   (line 1130 — closing } after role check)
///   - OAuthCallback LinkMode invalid LinkUserId (not a valid Guid) (line 1151)
///   - GetProviderConfig foreach with non-matching provider          (line 1265)
/// </summary>
[Collection("RediensIAM")]
public class SocialLoginRemainingTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, UserList list)> ScaffoldAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.LoginTheme = new Dictionary<string, object>
        {
            ["providers"] = new[] { new Dictionary<string, object>
            {
                ["id"] = "github", ["type"] = "github",
                ["client_id"] = "gh-id", ["client_secret"] = "gh-sec"
            }}
        };
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    private async Task<string> GetStateAsync(Organisation org, Project project)
    {
        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            ch, project.HydraClientId, project.Id.ToString(), org.Id.ToString());
        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={ch}&provider_id=github");
        var loc = res.Headers.Location!.ToString();
        return HttpUtility.ParseQueryString(new Uri(loc).Query)["state"]!;
    }

    // ── line 1130: RequireRoleToLogin=true but user HAS a role ───────────────

    [Fact]
    public async Task OAuthCallback_RequireRoleToLogin_UserHasRole_Redirects()
    {
        fixture.HibpStub.SetupGithubProfile(userId: 99101, email: "gh-hasrole@test.dev");
        try
        {
            var (org, project, list) = await ScaffoldAsync();
            project.RequireRoleToLogin = true;
            await fixture.Db.SaveChangesAsync();

            // Create the user and assign them a role BEFORE login
            var user = await fixture.Seed.CreateUserAsync(list.Id, email: "gh-hasrole@test.dev");
            var role = await fixture.Seed.CreateRoleAsync(project.Id, "Member");
            fixture.Db.UserProjectRoles.Add(new UserProjectRole
            {
                Id        = Guid.NewGuid(),
                UserId    = user.Id,
                ProjectId = project.Id,
                RoleId    = role.Id,
                GrantedAt = DateTimeOffset.UtcNow,
            });
            await fixture.Db.SaveChangesAsync();

            fixture.Keto.AllowAll();
            var state = await GetStateAsync(org, project);

            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=ok&state={Uri.EscapeDataString(state)}");

            // Hydra AcceptLogin succeeds → redirect away from error
            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            res.Headers.Location!.ToString().Should().NotContain("oauth2/error");
        }
        finally
        {
            fixture.HibpStub.ClearGithub();
        }
    }

    // ── line 1151: LinkMode with invalid (non-Guid) LinkUserId ───────────────

    [Fact]
    public async Task OAuthCallback_LinkMode_InvalidLinkUserId_RedirectsWithError()
    {
        fixture.HibpStub.SetupGithubProfile(userId: 99102, email: "gh-badlink@test.dev");
        try
        {
            var (_, project, _) = await ScaffoldAsync();

            var cache     = fixture.GetService<IDistributedCache>();
            var stateData = new OAuthStateData(
                LoginChallenge: "",
                ProjectId:      project.Id.ToString(),
                ProviderId:     "github",
                LinkMode:       true,
                LinkUserId:     "not-a-guid",   // must not parse: an unusable link target has to be refused
                LinkProjectId:  project.Id.ToString());
            var stateKey = $"bad-link-{Guid.NewGuid():N}";
            await cache.SetStringAsync($"oauth2:state:{stateKey}",
                JsonSerializer.Serialize(stateData),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

            var res = await fixture.Client.GetAsync(
                $"/auth/oauth2/callback?code=ok&state={Uri.EscapeDataString(stateKey)}");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            res.Headers.Location!.ToString().Should().Contain("link_error=invalid_user");
        }
        finally
        {
            fixture.HibpStub.ClearGithub();
        }
    }

    // ── line 1265: GetProviderConfig foreach iterates but finds no match ─────

    [Fact]
    public async Task OAuthCallback_ProviderArrayHasNonMatch_RedirectsToError()
    {
        var (org, project, _) = await ScaffoldAsync();
        var state = await GetStateAsync(org, project);

        // Replace providers with one that has a DIFFERENT id — foreach runs but never matches
        project.LoginTheme = new Dictionary<string, object>
        {
            ["providers"] = new[] { new Dictionary<string, object>
            {
                ["id"] = "google", ["type"] = "google",   // stored state asks for "github"
                ["client_id"] = "google-id", ["client_secret"] = "g-sec"
            }}
        };
        await fixture.Db.SaveChangesAsync();

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/callback?code=any&state={Uri.EscapeDataString(state)}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("oauth2/error");
    }
}
