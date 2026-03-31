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
    public async Task Register_After5FailedAttempts_RateLimitDoesNotApply()
    {
        // Registration does not currently hit the rate limiter in the same way.
        // Just verify it's accessible normally.
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

        // Should get a response — not necessarily 200 but not 429
        res.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }
}
