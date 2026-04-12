using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

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
                LinkUserId:     "not-a-guid",   // invalid → Guid.TryParse fails → line 1151
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
