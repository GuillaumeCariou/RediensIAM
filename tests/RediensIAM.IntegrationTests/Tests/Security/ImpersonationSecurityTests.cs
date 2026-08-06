using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Controllers;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// What a delegated token must <b>not</b> be able to do.
///
/// <para>
/// Impersonation is the one feature in this codebase where a mistake hands an operator a
/// customer's data under the customer's own identity. Every test below is written from the
/// attacker's side: hold a delegated token, or hold one tenant's gateway credential, and try to
/// reach past the boundary. A green run here is the claim that the boundary holds; each test names
/// the specific way it could have failed.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class ImpersonationSecurityTests(TestFixture fixture)
{
    // ── Scaffolding ───────────────────────────────────────────────────────────

    private async Task<(HttpClient Client, Guid ActorId)> OperatorClientAsync()
    {
        var (holderOrg, _) = await fixture.Seed.CreateOrgAsync();
        var list = await fixture.Seed.CreateUserListAsync(holderOrg.Id);
        var sa   = await fixture.Seed.CreateServiceAccountAsync(list.Id);
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

    /// <summary>A gateway credential belonging to a given organisation — what a consuming service holds.</summary>
    private async Task<HttpClient> GatewayForAsync(Guid orgId)
    {
        var list = await fixture.Seed.CreateUserListAsync(orgId);
        var sa   = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(sa.Id, "gateway", null, null);
        return fixture.ClientWithToken(raw);
    }

    private async Task<(Organisation Org, Project Project)> CustomerAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        return (org, await fixture.Seed.CreateProjectAsync(org.Id));
    }

    private static FormUrlEncodedContent Form(string token, Guid projectId) =>
        new([new KeyValuePair<string, string>("token", token),
             new KeyValuePair<string, string>("project_id", projectId.ToString())]);

    private static async Task<string> OpenSessionAsync(HttpClient operatorClient, Guid orgId, Guid projectId, string mode = "read")
    {
        var res = await operatorClient.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = orgId, project_id = projectId, mode, reason = "ticket #4812"
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    // ── The token grants nothing inside RediensIAM itself ─────────────────────

    /// <summary>
    /// The escalation that would matter most: a delegated token used as a credential against
    /// RediensIAM's own management surface. It is not a service account and holds no roles, so it
    /// must not authenticate there at all — otherwise entering a customer would hand the bearer
    /// the deployment's administration API.
    /// </summary>
    [Theory]
    [InlineData("/admin/organizations")]
    [InlineData("/service-accounts")]
    [InlineData("/org/projects")]
    [InlineData("/admin/impersonate")]
    public async Task ADelegatedToken_CannotReachManagementRoutes(string path)
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();
        var delegated      = await OpenSessionAsync(op, org.Id, project.Id);

        var res = await fixture.ClientWithToken(delegated).GetAsync(path);

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        res.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "a delegated token carries no roles and is not a service account — it may not administer anything");
    }

    /// <summary>
    /// The positive control for the theory above, and the reason it is not vacuous. A refusal on a
    /// route that does not exist proves nothing: these four paths answer <b>200</b> to a caller
    /// that holds the right credential, so the refusals above are about the credential and not
    /// about the path.
    ///
    /// <para>
    /// Without this test, deleting a route would turn its security assertion green.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/admin/organizations")]
    [InlineData("/service-accounts")]
    [InlineData("/admin/impersonate")]
    public async Task TheSameRoutesAnswerAProperlyCredentialledCaller(string path)
    {
        var (op, _) = await OperatorClientAsync();

        var res = await op.GetAsync(path);

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "otherwise the refusal asserted above is a 404 about the route, not a refusal about the token");
    }

    /// <summary>
    /// The chain that would turn one session into unlimited reach: opening a *new* impersonation
    /// while holding a delegated one. Refused, because the route needs both a service-account
    /// caller and a live super_admin grant, and a delegated token is neither.
    /// </summary>
    [Fact]
    public async Task ADelegatedToken_CannotOpenAnotherSession()
    {
        var (op, _)          = await OperatorClientAsync();
        var (org, project)   = await CustomerAsync();
        var (other, otherPr) = await CustomerAsync();
        var delegated        = await OpenSessionAsync(op, org.Id, project.Id);

        var res = await fixture.ClientWithToken(delegated).PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = other.Id, project_id = otherPr.Id, mode = "write", reason = "chaining"
        });

        res.StatusCode.Should().NotBe(HttpStatusCode.OK);
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A delegated token must not be usable as the *caller* credential on the introspection
    /// surface: that gate exists so the endpoint is never an oracle, and a delegated token is the
    /// credential most likely to be sitting in a browser.
    /// </summary>
    [Fact]
    public async Task ADelegatedToken_CannotCallIntrospection()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();
        var delegated      = await OpenSessionAsync(op, org.Id, project.Id);

        var res = await fixture.ClientWithToken(delegated)
            .PostAsync("/api/introspect", Form(delegated, project.Id));

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Opening is the act that mints a credential, and it stays closed to anything but a service
    /// account. A super-admin's own browser session may supervise — list and revoke — but may not
    /// create: that is the difference between a console and an oracle.
    /// </summary>
    [Fact]
    public async Task AUserTokenMaySuperviseButNotOpen()
    {
        var (holderOrg, orgList) = await fixture.Seed.CreateOrgAsync();
        var user   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var (org, project) = await CustomerAsync();
        fixture.Keto.AllowAll();
        var console = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(user.Id));

        var opened = await console.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read", reason = "from a browser"
        });
        opened.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "minting a delegated credential from a browser session is what the service-account gate refuses");

        (await console.GetAsync("/admin/impersonate")).StatusCode.Should().Be(HttpStatusCode.OK,
            "a session nobody can list is a session nobody can stop");
        holderOrg.Id.Should().NotBe(org.Id);
    }

    /// <summary>
    /// And the console can end one. Revocation only ever removes access, so it is the one
    /// supervision act that is safe to reach with a cookie-backed session.
    /// </summary>
    [Fact]
    public async Task AUserTokenMayRevoke()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();
        var opened  = await op.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read", reason = "ticket #4812"
        });
        var payload = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var token   = payload.GetProperty("access_token").GetString()!;
        var id      = payload.GetProperty("session_id").GetString()!;

        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user    = await fixture.Seed.CreateUserAsync(orgList.Id);
        var console = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(user.Id));

        (await console.PostAsync($"/admin/impersonate/{id}/revoke", null)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        var body = await (await (await GatewayForAsync(org.Id)).PostAsync("/api/introspect", Form(token, project.Id)))
            .Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    // ── Cross-tenant ──────────────────────────────────────────────────────────

    /// <summary>
    /// D-03, on this surface. A gateway holding another tenant's service account asks about a
    /// delegated token: the answer is <c>inactive</c> — never the session's organisation, never
    /// "belongs to someone else", which would be the disclosure.
    /// </summary>
    [Fact]
    public async Task AnotherTenantsGateway_CannotSeeTheSession()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();
        var (other, _)     = await CustomerAsync();
        var delegated      = await OpenSessionAsync(op, org.Id, project.Id);

        var res  = await (await GatewayForAsync(other.Id)).PostAsync("/api/introspect", Form(delegated, project.Id));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("active").GetBoolean().Should().BeFalse();
        body.TryGetProperty("act", out var act).Should().BeTrue();
        act.ValueKind.Should().Be(JsonValueKind.Null, "an inactive answer must disclose nothing about the session");
        body.GetProperty("org_id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// The token is bound to the project it was issued for. Presented under another project of the
    /// <b>same</b> organisation it answers inactive — the binding is per project, which is the unit
    /// the wire contract calls the authentication boundary.
    /// </summary>
    [Fact]
    public async Task ASessionIsBoundToItsProject()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();
        var sibling        = await fixture.Seed.CreateProjectAsync(org.Id);
        var delegated      = await OpenSessionAsync(op, org.Id, project.Id);

        var res  = await (await GatewayForAsync(org.Id)).PostAsync("/api/introspect", Form(delegated, sibling.Id));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    // ── The credential itself ─────────────────────────────────────────────────

    /// <summary>
    /// Only the hash is stored. If the table leaks, nothing in it can be replayed — the same
    /// property personal access tokens have, and for the same reason.
    /// </summary>
    [Fact]
    public async Task TheRawTokenIsNeverStored()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();
        var delegated      = await OpenSessionAsync(op, org.Id, project.Id);

        var stored = await fixture.Db.ImpersonationSessions.AsNoTracking()
            .Where(s => s.OrgId == org.Id).Select(s => s.TokenHash).ToListAsync();

        stored.Should().NotBeEmpty();
        stored.Should().NotContain(delegated, "the row holds a SHA-256, never the credential");
        stored.Should().OnlyContain(h => h.Length == 64);
    }

    /// <summary>Two sessions never share a credential, and neither is derived from anything guessable.</summary>
    [Fact]
    public async Task EachSessionGetsADistinctCredential()
    {
        var (op1, _) = await OperatorClientAsync();
        var (op2, _) = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();

        var first  = await OpenSessionAsync(op1, org.Id, project.Id);
        var second = await OpenSessionAsync(op2, org.Id, project.Id);

        first.Should().NotBe(second);
        first.Length.Should().BeGreaterThan(ImpersonationService.TokenPrefix.Length + 40);
    }

    /// <summary>
    /// The listing must not hand back anything replayable. A support console renders this; a hash
    /// or a token in it would put the credential on an operator's screen and in their logs.
    /// </summary>
    [Fact]
    public async Task TheListingLeaksNoCredential()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();
        var delegated      = await OpenSessionAsync(op, org.Id, project.Id);

        var raw = await (await op.GetAsync("/admin/impersonate")).Content.ReadAsStringAsync();

        raw.Should().NotContain(delegated);
        raw.Should().NotContain("token_hash", "the hash is not for reading either");
        raw.Should().NotContain("access_token");
    }

    // ── Lifecycle cannot be cheated ───────────────────────────────────────────

    /// <summary>
    /// A revoked session stays revoked: revoking again is a 404, and no path reopens the row. The
    /// failure this guards is a revoke that merely toggled a flag some later write could clear.
    /// </summary>
    [Fact]
    public async Task ARevokedSessionCannotBeRevived()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();

        var opened  = await op.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read", reason = "ticket #1"
        });
        var payload = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var token   = payload.GetProperty("access_token").GetString()!;
        var id      = payload.GetProperty("session_id").GetString()!;

        (await op.PostAsync($"/admin/impersonate/{id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await op.PostAsync($"/admin/impersonate/{id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var body = await (await (await GatewayForAsync(org.Id)).PostAsync("/api/introspect", Form(token, project.Id)))
            .Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// The mode is decided at issuance and there is no route that changes it. Asserted against the
    /// stored row after a write-mode session is opened, because "no endpoint exists" is only true
    /// until someone adds one — this fails the moment the value becomes mutable through the API.
    /// </summary>
    [Fact]
    public async Task TheModeIsFixedAtIssuance()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();

        await OpenSessionAsync(op, org.Id, project.Id, "write");
        var session = await fixture.Db.ImpersonationSessions.AsNoTracking()
            .Where(s => s.OrgId == org.Id).OrderByDescending(s => s.CreatedAt).FirstAsync();

        session.Mode.Should().Be("write");

        var patched = await op.PatchAsJsonAsync($"/admin/impersonate/{session.Id}", new { mode = "read" });
        patched.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>
    /// A ludicrously short TTL is floored, not honoured: a zero or negative lifetime would either
    /// mint a token that is dead on arrival or, worse, one whose expiry comparison never fires.
    /// </summary>
    [Fact]
    public async Task AnUnderlongTtlIsFloored()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();

        var res = await op.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read",
            reason = "ticket #4812", ttl_seconds = -1
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("expires_in").GetInt32().Should().BeGreaterThanOrEqualTo(60);
    }

    /// <summary>
    /// An over-long reason is a 400 naming the limit, not a 500 from the column width. A caller
    /// cannot fix what it is not told.
    /// </summary>
    [Fact]
    public async Task AnOverlongReasonIsRefusedCleanly()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();

        var res = await op.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read",
            reason = new string('x', ImpersonationController.MaxReasonLength + 1)
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("reason_too_long");
    }

    // ── The invariant, asserted where it actually holds ───────────────────────

    /// <summary>
    /// <b>The central invariant, at its source.</b> A delegated token names no roles, and this
    /// asserts it on <see cref="ImpersonationService.ClaimsFor"/> rather than through introspection.
    ///
    /// <para>
    /// That distinction is not pedantry — it was found by mutation. Injecting
    /// <c>Roles = ["super_admin"]</c> into <c>ClaimsFor</c> left <b>every</b> end-to-end test in
    /// this file green, because <c>IntrospectionController</c> re-verifies management roles live
    /// and strips the ones Keto no longer grants. The deployment was defended twice; the test suite
    /// was only ever watching the second defence.
    /// </para>
    ///
    /// <para>
    /// And the second defence is narrower than it looks: it strips <b>management</b> roles only. A
    /// tenant role — <c>{project}/admin</c> — injected at this seam would have travelled all the
    /// way to a consumer's role check. This test is what closes that.
    /// </para>
    /// </summary>
    [Fact]
    public void ClaimsForADelegatedSession_CarryNoRolesAtAll()
    {
        var session = new ImpersonationSession
        {
            Id = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), ActorLevel = "super_admin",
            OrgId = Guid.NewGuid(), ProjectId = Guid.NewGuid(),
            Mode = ImpersonationModes.Read, Reason = "ticket #4812",
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };

        var claims = ImpersonationService.ClaimsFor(session);

        claims.Roles.Should().BeEmpty(
            "a delegated token says who acts for whom, never what they may do — and no later " +
            "filter is responsible for making that true");
        claims.IsServiceAccount.Should().BeFalse();
        claims.ParsedUserId.Should().Be(Guid.Empty, "an organisation-scoped session impersonates no person");
        claims.Act!.Sub.Should().Be(session.ActorUserId.ToString());
    }

    // ── The actor claim cannot be forged ──────────────────────────────────────

    /// <summary>
    /// <c>act</c> comes from the stored session, never from anything a caller sends. A request
    /// that tries to dictate it is not honoured — otherwise the audit trail could be made to name
    /// somebody else.
    /// </summary>
    [Fact]
    public async Task TheActorCannotBeChosenByTheCaller()
    {
        var (op, actorId)  = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();
        var impostor       = Guid.NewGuid();

        var opened = await op.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read", reason = "ticket #4812",
            act = new { sub = impostor, level = "super_admin", mode = "write", session = Guid.NewGuid() },
        });
        opened.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        var body = await (await (await GatewayForAsync(org.Id)).PostAsync("/api/introspect", Form(token, project.Id)))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("act").GetProperty("sub").GetString().Should().Be(actorId.ToString());
        body.GetProperty("act").GetProperty("sub").GetString().Should().NotBe(impostor.ToString());
        body.GetProperty("act").GetProperty("mode").GetString().Should().Be("read");
    }

    /// <summary>
    /// A credential that merely looks delegated is not one. The prefix selects the lookup; the row
    /// is what decides, and there is no row here.
    /// </summary>
    [Theory]
    [InlineData("rediens_imp_")]
    [InlineData("rediens_imp_notarealtoken")]
    [InlineData("rediens_imp_../../etc/passwd")]
    public async Task AForgedDelegatedTokenIsInactive(string forged)
    {
        var (org, project) = await CustomerAsync();

        var body = await (await (await GatewayForAsync(org.Id)).PostAsync("/api/introspect", Form(forged, project.Id)))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    // ── Authorisation decisions ───────────────────────────────────────────────

    /// <summary>
    /// <c>/api/authorize</c> must not grant anything to a delegated token. It carries no roles, so
    /// every question about it answers denied — including, especially, questions about the system
    /// namespace where the deployment's administrators live.
    /// </summary>
    [Fact]
    public async Task ADelegatedTokenIsDeniedByAuthorize()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();
        var delegated      = await OpenSessionAsync(op, org.Id, project.Id);
        var gateway        = await GatewayForAsync(org.Id);

        var res = await gateway.PostAsJsonAsync("/api/authorize", new
        {
            token      = delegated,
            @namespace = "System",
            @object    = "rediensiam",
            relation   = "super_admin",
            project_id = project.Id.ToString(),
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should().BeFalse();
    }

    // ── Audit ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The audit row lands on the entered tenant's chain and <b>not</b> on the operator's holder
    /// organisation. Written the other way round, the customer whose data was entered could not see
    /// it in their own log — which is the entire reason both identities are recorded.
    /// </summary>
    [Fact]
    public async Task TheAuditRowBelongsToTheEnteredTenantOnly()
    {
        var (op, actorId)  = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();

        await OpenSessionAsync(op, org.Id, project.Id);

        var rows = await fixture.Db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "impersonation.opened").ToListAsync();

        rows.Should().Contain(r => r.OrgId == org.Id && r.ActorId == actorId);
        rows.Where(r => r.ActorId == actorId).Should().OnlyContain(r => r.OrgId == org.Id,
            "a session entered into Acme is Acme's record, never the operator's own tenant's");
    }

    /// <summary>Revocation is recorded too. A session that ends with no trace is half an audit.</summary>
    [Fact]
    public async Task RevocationIsAudited()
    {
        var (op, _)        = await OperatorClientAsync();
        var (org, project) = await CustomerAsync();

        var opened = await op.PostAsJsonAsync("/admin/impersonate", new
        {
            org_id = org.Id, project_id = project.Id, mode = "read", reason = "ticket #4812"
        });
        var id = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;

        await op.PostAsync($"/admin/impersonate/{id}/revoke", null);

        (await fixture.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == "impersonation.revoked" && a.OrgId == org.Id))
            .Should().BeTrue();
    }
}
