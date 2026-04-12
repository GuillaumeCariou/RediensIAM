using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace RediensIAM.IntegrationTests.Infrastructure;

/// <summary>
/// WireMock server that stubs Ory Hydra's admin API.
/// </summary>
public sealed class HydraStub : IDisposable
{
    private readonly WireMockServer _server;

    public string Url => _server.Url!;

    public HydraStub()
    {
        _server = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        SetupDefaults();
    }

    /// <summary>Resets all stubs back to the default safe no-ops.</summary>
    public void ResetDefaults()
    {
        _server.Reset();
        SetupDefaults();
    }

    // ── Default stubs (safe no-ops for all Hydra calls) ──────────────────────

    private void SetupDefaults()
    {
        // Health / version — used by SystemHealthController
        _server
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { status = "ok" }));

        _server
            .Given(Request.Create().WithPath("/version").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { version = "v2.0.0-stub" }));

        // Clients — return empty list and accept any create/delete
        _server
            .Given(Request.Create().WithPath("/admin/clients").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        _server
            .Given(Request.Create().WithPath("/admin/clients").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { client_id = "stub-client" }));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/admin/clients/*")).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/admin/clients/*")).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/admin/clients/*")).UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { client_id = "stub-client" }));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/admin/clients/*")).UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { client_id = "stub-client" }));

        // Sessions — accept any revoke
        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/sessions/consent").UsingDelete())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(204));

        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/sessions/consent").UsingGet())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        // Introspect — default: inactive token
        _server
            .Given(Request.Create().WithPath("/admin/oauth2/introspect").UsingPost())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { active = false }));

        // Login requests — default invalid
        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/requests/login").UsingGet())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(404));

        // Login accept — default returns redirect
        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/requests/login/accept").UsingPut())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { redirect_to = "http://localhost/callback" }));

        // Login reject — default returns redirect
        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/requests/login/reject").UsingPut())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { redirect_to = "http://localhost/rejected" }));

        // Consent requests — default invalid
        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/requests/consent").UsingGet())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(404));

        // Consent accept
        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/requests/consent/accept").UsingPut())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { redirect_to = "http://localhost/callback" }));

        // Consent reject
        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/requests/consent/reject").UsingPut())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { redirect_to = "http://localhost/rejected" }));

        // Logout
        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/requests/logout").UsingGet())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(404));

        _server
            .Given(Request.Create().WithPath("/admin/oauth2/auth/requests/logout/accept").UsingPut())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { redirect_to = "http://localhost/logged-out" }));
    }

    // ── Token registration ────────────────────────────────────────────────────

    /// <summary>
    /// Registers a test bearer token so the gateway middleware accepts it and
    /// injects the given claims into the request context.
    /// </summary>
    public void RegisterToken(string token, string userId, string? orgId, string? projectId, string[] roles)
    {
        var ext = new Dictionary<string, object?>
        {
            ["user_id"]    = userId,
            ["org_id"]     = orgId,
            ["project_id"] = projectId,
            ["roles"]      = roles,
        };

        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/introspect")
                .UsingPost()
                .WithBody($"*token={Uri.EscapeDataString(token)}*", WireMock.Matchers.MatchBehaviour.AcceptOnMatch))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { active = true, sub = userId, ext }));
    }

    /// <summary>
    /// Registers a test bearer token where <c>ext.roles</c> is a plain string rather than
    /// an array — this exercises the <c>ValueKind == String</c> branch of ExtClaims.GetRoles.
    /// </summary>
    public void RegisterTokenWithStringRoles(string token, string userId, string? orgId, string rolesString)
    {
        var ext = new Dictionary<string, object?>
        {
            ["user_id"]    = userId,
            ["org_id"]     = orgId,
            ["project_id"] = null,
            ["roles"]      = rolesString,   // string, not string[]
        };

        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/introspect")
                .UsingPost()
                .WithBody($"*token={Uri.EscapeDataString(token)}*", WireMock.Matchers.MatchBehaviour.AcceptOnMatch))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { active = true, sub = userId, ext }));
    }

    /// <summary>
    /// Registers a token where ext.roles is a numeric JSON value (neither array nor string),
    /// triggering the ExtClaims.GetRoles fallback return [] branch (HydraService line 279).
    /// </summary>
    public void RegisterTokenWithNumericRoles(string token, string userId, string? orgId)
    {
        var ext = new Dictionary<string, object?>
        {
            ["user_id"]    = userId,
            ["org_id"]     = orgId,
            ["project_id"] = null,
            ["roles"]      = 0,   // numeric — ValueKind = Number, not Array or String
        };

        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/introspect")
                .UsingPost()
                .WithBody($"*token={Uri.EscapeDataString(token)}*", WireMock.Matchers.MatchBehaviour.AcceptOnMatch))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { active = true, sub = userId, ext }));
    }

    // ── Login challenge helpers ───────────────────────────────────────────────

    /// <summary>
    /// Configures a login challenge response for the given challenge string.
    /// </summary>
    public void SetupLoginChallenge(string challenge, string? clientId, bool skip = false, string subject = "")
    {
        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/requests/login")
                .UsingGet()
                .WithParam("login_challenge", challenge))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    skip,
                    subject,
                    request_url = $"http://localhost/oauth2/auth?client_id={clientId}",
                    client = new { client_id = clientId, metadata = new Dictionary<string, object> { ["project_id"] = "test-project" } },
                    oidc_context = new { extra = new Dictionary<string, object> { ["project_id"] = "test-project" } }
                }));
    }

    /// <summary>
    /// Sets up a login challenge that carries a specific project_id in context.
    /// </summary>
    public void SetupLoginChallengeWithProject(string challenge, string? clientId, string projectId, string orgId, bool skip = false, string subject = "")
    {
        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/requests/login")
                .UsingGet()
                .WithParam("login_challenge", challenge))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    skip,
                    subject,
                    request_url = $"http://localhost/oauth2/auth?client_id={clientId}",
                    client = new
                    {
                        client_id = clientId,
                        metadata  = new Dictionary<string, object> { ["project_id"] = projectId, ["org_id"] = orgId }
                    },
                    oidc_context = new
                    {
                        extra = new Dictionary<string, object> { ["project_id"] = projectId, ["org_id"] = orgId }
                    }
                }));
    }

    /// <summary>
    /// Sets up a login challenge where client.metadata.project_id differs from
    /// oidc_context.extra.project_id — triggers the project_id mismatch rejection branch.
    /// </summary>
    public void SetupLoginChallengeWithProjectIdMismatch(
        string challenge, string? clientId, string oidcProjectId, string registeredProjectId)
    {
        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/requests/login")
                .UsingGet()
                .WithParam("login_challenge", challenge))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    skip        = false,
                    subject     = "",
                    request_url = $"http://localhost/oauth2/auth?client_id={clientId}",
                    client = new
                    {
                        client_id = clientId,
                        metadata  = new Dictionary<string, object> { ["project_id"] = registeredProjectId }
                    },
                    oidc_context = new
                    {
                        extra = new Dictionary<string, object> { ["project_id"] = oidcProjectId }
                    }
                }));
    }

    /// <summary>
    /// Sets up a login challenge where project_id appears only in the request URL,
    /// not in oidc_context extras. Used to exercise the URL-fallback branch of ExtractProjectId.
    /// </summary>
    public void SetupLoginChallengeProjectInUrl(string challenge, string? clientId, string projectId)
    {
        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/requests/login")
                .UsingGet()
                .WithParam("login_challenge", challenge))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    skip        = false,
                    subject     = "",
                    request_url = $"http://localhost/oauth2/auth?client_id={clientId}&project_id={projectId}",
                    client      = new { client_id = clientId, metadata = new Dictionary<string, object>() },
                    oidc_context = new { extra = new Dictionary<string, object>() }   // no project_id
                }));
    }

    // ── Consent challenge helpers ─────────────────────────────────────────────

    public void SetupConsentChallenge(string challenge, string subject, string? clientId,
        string? projectId = null, string? orgId = null)
    {
        var ctx = new Dictionary<string, object> { ["user_id"] = subject };
        if (projectId != null) ctx["project_id"] = projectId;
        if (orgId     != null) ctx["org_id"]     = orgId;

        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/requests/consent")
                .UsingGet()
                .WithParam("consent_challenge", challenge))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    skip            = false,
                    subject,
                    requested_scope = new[] { "openid", "offline" },
                    context         = ctx,
                    client          = new { client_id = clientId }
                }));
    }

    // ── Logout helpers ────────────────────────────────────────────────────────

    public void SetupLogoutChallenge(string challenge)
    {
        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/requests/logout")
                .UsingGet()
                .WithParam("logout_challenge", challenge))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new { request_url = "http://localhost/oauth2/logout?id=abc" }));
    }

    // ── Session helpers ───────────────────────────────────────────────────────

    public void SetupConsentSessions(string subject, object[] sessions)
    {
        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/sessions/consent")
                .UsingGet()
                .WithParam("subject", subject))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(sessions));
    }

    // ── OAuth2 client helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Stubs GET /admin/clients/{clientId} to return a client with a JWKS key.
    /// Needed to cover the GetKeysAsync has_key=true path in PatService.
    /// </summary>
    public void SetupOAuth2ClientWithJwks(string clientId, string kid = "test-key-1")
    {
        _server
            .Given(Request.Create()
                .WithPath($"/admin/clients/{Uri.EscapeDataString(clientId)}")
                .UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    client_id = clientId,
                    jwks = new
                    {
                        keys = new[]
                        {
                            new { kid, kty = "RSA", use = "sig" }
                        }
                    }
                }));
    }

    /// <summary>Configures GET /admin/clients/{clientId} to return a minimal client object (200).</summary>
    public void SetupClientGetResponse(string clientId)
    {
        _server
            .Given(Request.Create()
                .WithPath($"/admin/clients/{Uri.EscapeDataString(clientId)}")
                .UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new { client_id = clientId, client_name = "Test Client" }));
    }

    /// <summary>Configures DELETE /admin/clients/{clientId} to return 500 (simulates Hydra failure).</summary>
    public void SetupClientDeleteFailure(string clientId)
    {
        _server
            .Given(Request.Create()
                .WithPath($"/admin/clients/{Uri.EscapeDataString(clientId)}")
                .UsingDelete())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(500));
    }

    // ── Client creation failure simulation ───────────────────────────────────

    /// <summary>Makes POST /admin/clients return 500 to simulate Hydra being unavailable.</summary>
    public void SetupClientCreationFailure()
    {
        _server
            .Given(Request.Create().WithPath("/admin/clients").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(500).WithBodyAsJson(new { error = "service_unavailable" }));
    }

    /// <summary>Restores the default POST /admin/clients → 201 response, overriding a previous failure stub.</summary>
    public void RestoreClientCreation()
    {
        _server
            .Given(Request.Create().WithPath("/admin/clients").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { client_id = "stub-client" }));
    }

    // ── Health failure simulation ─────────────────────────────────────────────

    /// <summary>
    /// Makes /health/alive return 500 (overrides the default 200 stub at highest priority).
    /// Call SetupDefaults() indirectly via Reset + SetupDefaults() or just restart after the test.
    /// </summary>
    public void SetHealthFailure()
    {
        _server
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(500).WithBodyAsJson(new { error = "simulated_failure" }));
    }

    /// <summary>Restores the default healthy /health/alive response.</summary>
    public void RestoreHealth()
    {
        // Re-add the health stub at priority 0 so it beats the failure stub
        _server
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { status = "ok" }));
    }

    // ── Verification helpers ──────────────────────────────────────────────────

    public bool LoginWasAccepted(string challenge) =>
        _server.LogEntries.Any(e =>
            e.RequestMessage?.Path == "/admin/oauth2/auth/requests/login/accept" &&
            e.RequestMessage?.Query?.ContainsKey("login_challenge") == true &&
            e.RequestMessage.Query["login_challenge"].Contains(challenge));

    public bool LoginWasRejected(string challenge) =>
        _server.LogEntries.Any(e =>
            e.RequestMessage?.Path == "/admin/oauth2/auth/requests/login/reject" &&
            e.RequestMessage?.Query?.ContainsKey("login_challenge") == true &&
            e.RequestMessage.Query["login_challenge"].Contains(challenge));

    public bool ConsentWasAccepted(string challenge) =>
        _server.LogEntries.Any(e =>
            e.RequestMessage?.Path == "/admin/oauth2/auth/requests/consent/accept" &&
            e.RequestMessage?.Query?.ContainsKey("consent_challenge") == true &&
            e.RequestMessage.Query["consent_challenge"].Contains(challenge));

    public bool ConsentWasRejected(string challenge) =>
        _server.LogEntries.Any(e =>
            e.RequestMessage?.Path == "/admin/oauth2/auth/requests/consent/reject" &&
            e.RequestMessage?.Query?.ContainsKey("consent_challenge") == true &&
            e.RequestMessage.Query["consent_challenge"].Contains(challenge));

    public void ResetLog() => _server.ResetLogEntries();

    public void Dispose() => _server.Dispose();
}
