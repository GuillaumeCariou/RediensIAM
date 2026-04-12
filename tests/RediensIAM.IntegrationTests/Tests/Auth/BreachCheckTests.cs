using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Tests for the CheckBreachedPasswords project flag (C5).
///
/// The real HIBP API is not available in the test environment, so BreachCheckService
/// fails open (returns 0). These tests verify the code path is wired correctly:
/// - flag disabled → breach check skipped entirely
/// - flag enabled + HIBP unreachable → fail-open, registration succeeds
/// The "password_breached" error path is covered by unit tests of BreachCheckService.
/// </summary>
[Collection("RediensIAM")]
public class BreachCheckTests(TestFixture fixture)
{
    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private async Task<(Organisation org, Project project)> ScaffoldAsync(bool checkBreached = false)
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);

        project.AssignedUserListId    = list.Id;
        project.AllowSelfRegistration = true;
        project.CheckBreachedPasswords = checkBreached;
        await fixture.Db.SaveChangesAsync();

        return (org, project);
    }

    // ── Flag disabled ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_BreachCheckDisabled_Succeeds()
    {
        var (org, project) = await ScaffoldAsync(checkBreached: false);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Flag enabled — HIBP unreachable in test env (fail-open) ──────────────

    [Fact]
    public async Task Register_BreachCheckEnabled_HibpUnreachable_FailsOpenAndSucceeds()
    {
        var (org, project) = await ScaffoldAsync(checkBreached: true);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        // HIBP is not stubbed — BreachCheckService will catch the network error
        // and return 0 (fail-open), so registration should not be blocked.
        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Flag enabled — project flag persisted correctly ───────────────────────

    [Fact]
    public async Task Project_CheckBreachedPasswords_PersistsToDb()
    {
        var (_, project) = await ScaffoldAsync(checkBreached: true);

        await fixture.RefreshDbAsync();
        var reloaded = fixture.Db.Projects.Find(project.Id);

        reloaded.Should().NotBeNull();
        reloaded!.CheckBreachedPasswords.Should().BeTrue();
    }
}

// ── BreachCheckService unit tests (no fixture required) ───────────────────────

/// <summary>
/// Pure unit tests for BreachCheckService — covers the catch block (fail-open)
/// without relying on the real HIBP API being unreachable.
/// </summary>
public class BreachCheckServiceUnitTests
{
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated network failure");
    }

    private sealed class FailingClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FailingHandler());
    }

    [Fact]
    public async Task GetBreachCount_WhenHibpUnreachable_ReturnsZero()
    {
        // Arrange: HTTP client always throws → catch block must return 0 (fail-open)
        var svc = new BreachCheckService(
            new FailingClientFactory(),
            NullLogger<BreachCheckService>.Instance);

        // Act
        var count = await svc.GetBreachCountAsync("anypassword");

        // Assert: fail-open — should not block the user
        count.Should().Be(0);
    }

    // ── Breach found — line 26 (return int.TryParse branch) ──────────────────

    [Fact]
    public async Task GetBreachCount_WhenPasswordInDatabase_ReturnsCount()
    {
        // SHA1("password") = 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8
        // prefix = "5BAA6", suffix = "1E4C9B93F3F0682250B6CF8331B7EE68FD8"
        const string password = "password";
        const string suffix   = "1E4C9B93F3F0682250B6CF8331B7EE68FD8";
        const int    expected = 3303003;

        var hibpResponse = $"{suffix}:{expected}\nABCDE00000000000000000000000000000000:1\n";

        var factory = new FixedResponseClientFactory(hibpResponse);
        var svc = new BreachCheckService(factory, NullLogger<BreachCheckService>.Instance);

        var count = await svc.GetBreachCountAsync(password);

        count.Should().Be(expected);
    }

    private sealed class FixedResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
    }

    private sealed class FixedResponseClientFactory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FixedResponseHandler(body));
    }
}
