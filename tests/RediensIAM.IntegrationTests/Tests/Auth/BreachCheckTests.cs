using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

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
