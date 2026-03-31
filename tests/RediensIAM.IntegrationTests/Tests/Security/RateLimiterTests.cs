using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// Tests the IP-based login rate limiter.
/// MaxLoginAttempts=5 in test config.
/// </summary>
[Collection("RediensIAM")]
public class RateLimiterTests(TestFixture fixture)
{
    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private async Task<(Project project, Organisation org)> ScaffoldProjectAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        return (project, org);
    }

    [Fact]
    public async Task Login_After5FailedAttempts_Returns429()
    {
        var (project, org) = await ScaffoldProjectAsync();

        // 5 failed attempts
        for (var i = 0; i < 5; i++)
        {
            var ch = NewChallenge();
            fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
                project.Id.ToString(), org.Id.ToString());
            await fixture.Client.PostAsJsonAsync("/auth/login", new
            {
                login_challenge = ch,
                email           = "nonexistent@test.com",
                password        = "WrongPassword!"
            });
        }

        // 6th attempt should be rate-limited
        var ch6 = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(ch6, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch6,
            email           = "nonexistent@test.com",
            password        = "WrongPassword!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("rate_limited");
    }

    [Fact]
    public async Task Register_ValidAttempt_IsNotRateLimited()
    {
        await fixture.FlushCacheAsync();
        var (project, org) = await ScaffoldProjectAsync();
        project.AllowSelfRegistration = true;
        await fixture.Db.SaveChangesAsync();

        var ch = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Register_After5DomainFailures_Returns429()
    {
        await fixture.FlushCacheAsync();
        var (project, org) = await ScaffoldProjectAsync();
        project.AllowSelfRegistration = true;
        project.AllowedEmailDomains   = ["allowed.com"]; // restrict domain so every attempt fails
        await fixture.Db.SaveChangesAsync();

        // 5 domain-blocked failures
        for (var i = 0; i < 5; i++)
        {
            var ch = NewChallenge();
            fixture.Hydra.SetupLoginChallengeWithProject(ch, project.HydraClientId,
                project.Id.ToString(), org.Id.ToString());
            await fixture.Client.PostAsJsonAsync("/auth/register", new
            {
                login_challenge = ch,
                email           = SeedData.UniqueEmail(), // @test.com — not in allowed list
                password        = "P@ssw0rd!Test"
            });
        }

        // 6th attempt should be rate-limited regardless of email
        var ch6 = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(ch6, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch6,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("rate_limited");
    }

    [Fact]
    public async Task PasswordReset_After5Failures_Returns429()
    {
        await fixture.FlushCacheAsync();
        var (project, _) = await ScaffoldProjectAsync();
        project.EmailVerificationEnabled = true;
        await fixture.Db.SaveChangesAsync();

        // 5 failed reset attempts (non-existent email — each records a failure)
        for (var i = 0; i < 5; i++)
        {
            await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
            {
                project_id = project.Id,
                email      = $"ghost{i}@nowhere.com"
            });
        }

        // 6th attempt should be rate-limited
        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = "ghost99@nowhere.com"
        });

        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("rate_limited");
    }
}
