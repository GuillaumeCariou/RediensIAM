using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

[Collection("RediensIAM")]
public class IpAllowlistTests(TestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.FlushCacheAsync();
    public Task DisposeAsync()    => Task.CompletedTask;

    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private async Task<(Organisation org, Project project, User user)> ScaffoldAsync(
        string[]? allowlist = null)
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);

        project.AssignedUserListId = list.Id;
        if (allowlist != null) project.IpAllowlist = allowlist;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);
        return (org, project, user);
    }

    // ── No allowlist (default) ────────────────────────────────────────────────

    [Fact]
    public async Task Login_NoAllowlist_Succeeds()
    {
        var (org, project, user) = await ScaffoldAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Exact IP match ────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_AllowlistContainsLoopback_Succeeds()
    {
        // Test server uses 127.0.0.1 as RemoteIpAddress
        var (org, project, user) = await ScaffoldAsync(allowlist: ["127.0.0.1"]);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_AllowlistExcludesClientIp_Returns401()
    {
        // Use an IP that will never match the test server's loopback address
        var (org, project, user) = await ScaffoldAsync(allowlist: ["10.20.30.40"]);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("ip_not_allowed");
    }

    // ── CIDR range ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_AllowlistCidrCoversLoopback_Succeeds()
    {
        // 127.0.0.0/8 covers 127.0.0.1
        var (org, project, user) = await ScaffoldAsync(allowlist: ["127.0.0.0/8"]);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_AllowlistCidrDoesNotCoverClientIp_Returns401()
    {
        // 192.168.0.0/16 does not cover 127.0.0.1
        var (org, project, user) = await ScaffoldAsync(allowlist: ["192.168.0.0/16"]);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("ip_not_allowed");
    }

    // ── Multiple entries in allowlist ─────────────────────────────────────────

    [Fact]
    public async Task Login_AllowlistMultipleEntries_MatchOnSecond_Succeeds()
    {
        var (org, project, user) = await ScaffoldAsync(allowlist: ["10.0.0.0/8", "127.0.0.0/8"]);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
