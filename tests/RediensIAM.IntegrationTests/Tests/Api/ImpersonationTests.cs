using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Api;

/// <summary>
/// Delegated sessions — <c>/admin/impersonate</c>.
///
/// <para>
/// The dangerous paths first, deliberately. An impersonation feature without a cross-tenant refusal
/// test is the single most dangerous untested path in this codebase, and a delegated token that
/// keeps a management role turns a support tool into a privilege-escalation primitive. Both are
/// asserted below before anything about the happy path.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class ImpersonationTests(TestFixture fixture)
{
    /// <summary>
    /// The caller this route is built for: a service account holding <c>super_admin</c>,
    /// authenticating with a PAT — the credential another service's gateway actually presents.
    /// Both gates are satisfied only by this shape.
    /// </summary>
    private async Task<(HttpClient Client, Guid ActorId)> OperatorClientAsync()
    {
        var (holderOrg, _) = await fixture.Seed.CreateOrgAsync();
        var holderList     = await fixture.Seed.CreateUserListAsync(holderOrg.Id);
        var sa             = await fixture.Seed.CreateServiceAccountAsync(holderList.Id);

        fixture.Db.ServiceAccountRoles.Add(new ServiceAccountRole
        {
            Id = Guid.NewGuid(), ServiceAccountId = sa.Id, Role = "super_admin",
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(sa.Id, "operator", null, null);
        fixture.Keto.AllowAll();
        return (fixture.ClientWithToken(raw), sa.Id);
    }

    /// <summary>A customer organisation with one project — the tenant being entered.</summary>
    private async Task<(Organisation Org, Project Project)> CustomerAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        return (org, await fixture.Seed.CreateProjectAsync(org.Id));
    }

    private async Task<(Organisation Org, Project Project, HttpClient Client, Guid ActorId)> ScaffoldAsync()
    {
        var (client, actorId) = await OperatorClientAsync();
        var (org, project)    = await CustomerAsync();
        return (org, project, client, actorId);
    }

    private static object Body(Guid orgId, Guid projectId, string mode = "read", string reason = "ticket #4812") =>
        new { org_id = orgId, project_id = projectId, mode, reason };

    // ── The two gates ─────────────────────────────────────────────────────────

    /// <summary>
    /// A plain user token is refused even when its holder is a super-admin. Without this the route
    /// is reachable from a browser session, which is what makes an endpoint an oracle.
    /// </summary>
    [Fact]
    public async Task Open_WithoutAServiceAccountCaller_Is403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var user    = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var client  = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(user.Id));

        var res = await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A service account is not enough on its own: one holding <c>org_admin</c> is refused. The
    /// service-account gate and the level gate are two conditions, and passing one has never been
    /// passing the other.
    /// </summary>
    [Fact]
    public async Task Open_ServiceAccountBelowSuperAdmin_IsRefused()
    {
        var (org, project) = await CustomerAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa   = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        fixture.Db.ServiceAccountRoles.Add(new ServiceAccountRole
        {
            Id = Guid.NewGuid(), ServiceAccountId = sa.Id, Role = "org_admin",
            OrgId = org.Id, GrantedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();
        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(sa.Id, "org-bot", null, null);
        fixture.Keto.AllowAll();

        var res = await fixture.ClientWithToken(raw)
            .PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    // ── Cross-tenant refusal (D-03) ───────────────────────────────────────────

    /// <summary>
    /// A project belonging to another organisation cannot be entered under this organisation's id.
    /// The pair is checked against the database, never taken from the request.
    /// </summary>
    [Fact]
    public async Task Open_WithAProjectFromAnotherOrganisation_IsRefused()
    {
        var (org, _, client, _) = await ScaffoldAsync();
        var (otherOrg, _)       = await fixture.Seed.CreateOrgAsync();
        var otherProject        = await fixture.Seed.CreateProjectAsync(otherOrg.Id);

        var res = await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, otherProject.Id));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("project_not_in_org");
    }

    // ── The token carries no authority ────────────────────────────────────────

    /// <summary>
    /// The invariant that keeps this a support tool: a delegated token names no roles at all, so
    /// entering a customer organisation cannot raise privilege inside it. Asserted through
    /// introspection, which is what every consumer actually reads.
    /// </summary>
    [Fact]
    public async Task IntrospectedDelegatedToken_CarriesNoRolesAndNamesTheActor()
    {
        var (org, project, client, actorId) = await ScaffoldAsync();

        var opened = await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));
        opened.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        token.Should().StartWith(ImpersonationService.TokenPrefix,
            "a leaked delegated token has to be recognisable as one in a log");

        var body = await IntrospectAsync(token, org.Id, project.Id);

        body.GetProperty("active").GetBoolean().Should().BeTrue();
        body.GetProperty("roles").EnumerateArray().Should().BeEmpty(
            "a delegated token carries who acts for whom, never what they may do");
        body.GetProperty("act").GetProperty("sub").GetString().Should().Be(actorId.ToString());
        body.GetProperty("act").GetProperty("mode").GetString().Should().Be("read");
        body.GetProperty("org_id").GetString().Should().Be(org.Id.ToString());
    }

    /// <summary>
    /// The subject is not a user id, and must not parse as one: there is no user in an
    /// organisation-scoped session, and a subject that quietly resolved to some user would be the
    /// worst possible way to learn that.
    /// </summary>
    [Fact]
    public async Task DelegatedSubject_IsNotAUserId()
    {
        var (org, project, client, _) = await ScaffoldAsync();

        var opened = await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));
        var token  = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        var body = await IntrospectAsync(token, org.Id, project.Id);

        body.GetProperty("sub").GetString().Should().StartWith(ImpersonationService.SubjectPrefix);
        body.TryGetProperty("user_id", out var userId).Should().BeTrue();
        userId.ValueKind.Should().Be(JsonValueKind.Null, "an organisation-scoped session impersonates no person");
    }

    /// <summary>An ordinary token answers with no <c>act</c> at all — that is what makes the field mean something.</summary>
    [Fact]
    public async Task AnOrdinaryToken_HasNoActClaim()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list    = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var sa       = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var (pat, _) = await fixture.GetService<PatService>().GenerateAsync(sa.Id, "ordinary", null, null);
        fixture.Keto.AllowAll();

        var body = await IntrospectAsync(pat, org.Id, project.Id);

        body.TryGetProperty("act", out var act).Should().BeTrue();
        act.ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Opening a session revokes the operator's previous one. One active impersonation at a time is
    /// what removes every question about which customer is currently entered.
    /// </summary>
    [Fact]
    public async Task OpeningASecondSession_RevokesTheFirst()
    {
        var (org, project, client, _) = await ScaffoldAsync();

        var first  = await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));
        var firstToken = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        var second = await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        (await IntrospectAsync(firstToken, org.Id, project.Id)).GetProperty("active").GetBoolean()
            .Should().BeFalse("the older session ended the moment a newer one opened");
    }

    /// <summary>Revocation is immediate, not at the TTL.</summary>
    [Fact]
    public async Task RevokedSession_StopsIntrospectingActive()
    {
        var (org, project, client, _) = await ScaffoldAsync();

        var opened  = await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));
        var payload = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var token   = payload.GetProperty("access_token").GetString()!;
        var id      = payload.GetProperty("session_id").GetString()!;

        (await client.PostAsync($"/admin/impersonate/{id}/revoke", null)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        (await IntrospectAsync(token, org.Id, project.Id)).GetProperty("active").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// Expiry is a predicate, not a job: a session whose <c>ExpiresAt</c> has passed is inactive
    /// immediately, with no sweeper involved.
    /// </summary>
    [Fact]
    public async Task ExpiredSession_IsInactiveWithoutAnySweep()
    {
        var (org, project, client, _) = await ScaffoldAsync();

        var opened  = await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));
        var payload = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var token   = payload.GetProperty("access_token").GetString()!;
        var id      = Guid.Parse(payload.GetProperty("session_id").GetString()!);

        var session = await fixture.Db.ImpersonationSessions.FirstAsync(s => s.Id == id);
        session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await fixture.Db.SaveChangesAsync();

        (await IntrospectAsync(token, org.Id, project.Id)).GetProperty("active").GetBoolean().Should().BeFalse();
    }

    // ── The refusals that keep the contract honest ────────────────────────────

    [Fact]
    public async Task Open_WithoutAReason_IsRefused()
    {
        var (org, project, client, _) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read", reason = "   "
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("reason_required");
    }

    /// <summary>
    /// Naming a user is refused rather than ignored. A caller that sends <c>user_id</c> believes it
    /// is entering a person's account; answering 200 to that belief is the defect, not the field.
    /// </summary>
    [Fact]
    public async Task Open_NamingAUser_IsRefusedRatherThanIgnored()
    {
        var (org, project, client, _) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read",
            reason = "ticket #4812", user_id = Guid.NewGuid()
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("user_id_not_supported");
    }

    [Fact]
    public async Task Open_WithAnUnknownMode_IsRefused()
    {
        var (org, project, client, _) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "admin", reason = "ticket #4812"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A delegated token is a loan: an hour is the ceiling, whatever the caller asks for.</summary>
    [Fact]
    public async Task Open_WithAnOverlongTtl_IsClampedNotRefused()
    {
        var (org, project, client, _) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read",
            reason = "ticket #4812", ttl_seconds = 86_400
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("expires_in").GetInt32().Should().Be(ImpersonationService.MaxTtlSeconds);
    }

    // ── Audit ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Both identities in the record, on the entered tenant's chain. A log that says only "Acme"
    /// is a log that hides who acted — and one written against the operator's own organisation
    /// would be invisible to the tenant whose data was entered.
    /// </summary>
    [Fact]
    public async Task OpeningASession_IsAuditedAgainstTheEnteredOrganisation()
    {
        var (org, project, client, actorId) = await ScaffoldAsync();

        await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));

        var row = await fixture.Db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "impersonation.opened" && a.OrgId == org.Id)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();

        row.Should().NotBeNull();
        row!.ActorId.Should().Be(actorId);
        row.Metadata.Should().ContainKey("reason");
        // Metadata is a Dictionary<string, object> that round-trips through jsonb, so the value
        // comes back as a JsonElement rather than a string. ToString() is the comparison, not Be().
        row.Metadata!["reason"].ToString().Should().Be("ticket #4812");
        row.Metadata["act_sub"].ToString().Should().Be(actorId.ToString(),
            "a record that names only the tenant hides who acted");
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListingActiveSessions_ShowsTheOpenOneAndDropsTheRevoked()
    {
        var (org, project, client, _) = await ScaffoldAsync();

        var opened = await client.PostAsJsonAsync("/admin/impersonate", Body(org.Id, project.Id));
        var id     = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

        var listed = await (await client.GetAsync("/admin/impersonate")).Content.ReadFromJsonAsync<JsonElement>();
        listed.EnumerateArray().Select(s => s.GetProperty("session_id").GetString())
            .Should().Contain(id);

        await client.PostAsync($"/admin/impersonate/{id}/revoke", null);

        var after = await (await client.GetAsync("/admin/impersonate")).Content.ReadFromJsonAsync<JsonElement>();
        after.EnumerateArray().Select(s => s.GetProperty("session_id").GetString())
            .Should().NotContain(id, "an impersonation nobody can list is an impersonation nobody can stop");
    }

    [Fact]
    public async Task RevokingAnUnknownSession_Is404()
    {
        var (_, _, client, _) = await ScaffoldAsync();

        (await client.PostAsync($"/admin/impersonate/{Guid.NewGuid()}/revoke", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Introspects as the consuming service would: a gateway holding a service account <b>in the
    /// entered organisation</b>. That is not incidental — <c>IsInCallerScopeAsync</c> answers
    /// "inactive" to a gateway asking about another tenant's token, so this mirrors the only
    /// deployment in which the answer is meaningful.
    /// </summary>
    private async Task<JsonElement> IntrospectAsync(string token, Guid orgId, Guid projectId)
    {
        var list     = await fixture.Seed.CreateUserListAsync(orgId);
        var gateway  = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(gateway.Id, "gateway", null, null);

        var res = await fixture.ClientWithToken(raw).PostAsync("/api/introspect",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = token, ["project_id"] = projectId.ToString(),
            }));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }
}
