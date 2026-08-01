using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Covers the skip-login re-validation path in AuthController.GetLogin: when Hydra signals
/// req.Skip=true (existing session), the IAM backend MUST still re-check that the user is
/// active, not locked, and that their organisation is not suspended — the cached Hydra
/// session can outlive any of these state changes.
/// </summary>
[Collection("RediensIAM")]
public class SkipLoginValidationTests(TestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.FlushCacheAsync();
    public Task DisposeAsync()    => Task.CompletedTask;

    private async Task<(Organisation org, Project project, User user)> ScaffoldAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        return (org, project, user);
    }

    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("error", out var e) ? e.GetString() : null;
    }

    [Fact]
    public async Task SkipLogin_InactiveUser_Returns401InvalidCredentials()
    {
        var (org, project, user) = await ScaffoldAsync();
        user.Active = false;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString(),
            skip: true, subject: $"{org.Id}:{user.Id}");

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(res)).Should().Be("invalid_credentials");
    }

    [Fact]
    public async Task SkipLogin_LockedUser_Returns401AccountLocked()
    {
        var (org, project, user) = await ScaffoldAsync();
        user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString(),
            skip: true, subject: $"{org.Id}:{user.Id}");

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(res)).Should().Be("account_locked");
    }

    [Fact]
    public async Task SkipLogin_SuspendedOrganisation_Returns401OrganisationSuspended()
    {
        var (org, project, user) = await ScaffoldAsync();
        org.Active = false;
        org.SuspendedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString(),
            skip: true, subject: $"{org.Id}:{user.Id}");

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(res)).Should().Be("organisation_suspended");
    }

    /// <summary>
    /// The subject below is a bare GUID on purpose: system-admin subjects carry no "org:user"
    /// prefix, so this is the alternate branch of ParseSubjectUserId. Do not "normalise" the
    /// subject here — that would silently stop covering the system-admin shape.
    /// </summary>
    [Fact]
    public async Task SkipLogin_BareGuidSubject_SystemAdminFormat_RedirectsSuccessfully()
    {
        var (_, project, user) = await ScaffoldAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), project.OrgId.ToString(),
            skip: true, subject: user.Id.ToString());

        var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");

        ((int)res.StatusCode).Should().BeInRange(200, 399);
    }
}
