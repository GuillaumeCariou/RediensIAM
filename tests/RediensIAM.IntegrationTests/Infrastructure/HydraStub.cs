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

    /// <summary>
    /// The body of the last request this stub received whose path contains <paramref name="pathFragment"/>,
    /// optionally narrowed to one HTTP method.
    ///
    /// <para>
    /// The registration tests used to assert only that the call did not throw, which is true of a
    /// payload missing every field that matters — <c>post_logout_redirect_uris</c> went unregistered
    /// for as long as the client existed and no test noticed, because nothing ever read the body.
    /// </para>
    /// </summary>
    public string? LastRequestBody(string pathFragment, string? method = null)
    {
        for (var i = _server.LogEntries.Count - 1; i >= 0; i--)
        {
            var entry = _server.LogEntries.ElementAt(i);
            var req = entry?.RequestMessage;
            if (req?.Path is null || !req.Path.Contains(pathFragment, StringComparison.Ordinal)) continue;
            if (method != null && !string.Equals(req.Method, method, StringComparison.OrdinalIgnoreCase)) continue;
            return req.Body;
        }
        return null;
    }

    /// <summary>
    /// True when the body is a JSON array, which is all Hydra's PATCH accepts. Deliberately shallow:
    /// this is a stub asserting the shape RediensIAM must send, not a JSON Patch implementation.
    /// </summary>
    private static bool IsJsonPatchArray(string? body) =>
        body?.TrimStart().StartsWith('[') == true;

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

        // Clients — return empty list and accept any create/delete.
        // Priority 100 so SetupRegisteredClients can override it: WireMock takes the lowest
        // priority number among the mappings that match, and the default here has to lose.
        _server
            .Given(Request.Create().WithPath("/admin/clients").UsingGet())
            .AtPriority(100)
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

        // PATCH takes an RFC 6902 array, and Hydra 400s anything else. Answering 200 to a partial
        // client object is what let UpdateOAuth2ClientScopeAsync send the wrong shape for as long
        // as it existed: every custom-scope change was rejected in production and green here. The
        // body matcher is the whole point of these two mappings — do not collapse them into one.
        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/admin/clients/*")).UsingPatch()
                .WithBody(IsJsonPatchArray))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { client_id = "stub-client" }));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/admin/clients/*")).UsingPatch())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithBodyAsJson(new { error = "json_patch_expected", error_description = "PATCH /admin/clients/{id} takes an RFC 6902 array." }));

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
                .WithBodyAsJson(new { active = true, sub = userId, client_id = RediensIAM.Config.Roles.AdminClientId, token_use = "access_token", ext }));
    }

    /// <summary>
    /// Registers a token that Hydra reports as issued to <paramref name="clientId"/> with
    /// audience <paramref name="audience"/>. Used by the audience-confusion regression test:
    /// a token minted for a tenant's own OAuth2 client must not be accepted by the
    /// IAM management API even when its ext claims carry management roles.
    /// </summary>
    public void RegisterTokenForClient(
        string token, string userId, string? orgId, string? projectId,
        string[] roles, string clientId, string[] audience)
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
                .WithBodyAsJson(new
                {
                    active     = true,
                    sub        = userId,
                    client_id  = clientId,
                    aud        = audience,
                    token_use  = "access_token",
                    ext
                }));
    }

    /// <summary>
    /// Registers a token that Hydra reports as a <c>refresh_token</c> rather than an
    /// access token. Introspection without <c>token_type_hint</c> reports refresh tokens
    /// as active, so the gateway must reject them explicitly.
    /// </summary>
    public void RegisterRefreshToken(string token, string userId, string[] roles)
    {
        var ext = new Dictionary<string, object?>
        {
            ["user_id"]    = userId,
            ["org_id"]     = (string?)null,
            ["project_id"] = (string?)null,
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
                .WithBodyAsJson(new
                {
                    active    = true,
                    sub       = userId,
                    client_id = "client_admin_system",
                    token_use = "refresh_token",
                    ext
                }));
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
                .WithBodyAsJson(new { active = true, sub = userId, client_id = RediensIAM.Config.Roles.AdminClientId, token_use = "access_token", ext }));
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
                .WithBodyAsJson(new { active = true, sub = userId, client_id = RediensIAM.Config.Roles.AdminClientId, token_use = "access_token", ext }));
    }

    // ── Login challenge helpers ───────────────────────────────────────────────

    /// <summary>
    /// Configures a login challenge response for the given challenge string.
    /// </summary>
    /// <summary>
    /// Configures a login challenge. <paramref name="projectId"/> lands in BOTH
    /// <c>client.metadata</c> (the authority, see <c>LoginChallengeProject</c>) and
    /// <c>oidc_context.extra</c>, mirroring what RediensIAM writes when it registers a client.
    /// </summary>
    public void SetupLoginChallenge(
        string challenge, string? clientId, bool skip = false, string subject = "",
        string projectId = "test-project")
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
                    client = new { client_id = clientId, metadata = new Dictionary<string, object> { ["project_id"] = projectId } },
                    oidc_context = new { extra = new Dictionary<string, object> { ["project_id"] = projectId } }
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
                        login_hint = "test-login-hint",
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
                    // The client is registered for this project (the authority); the request
                    // repeats it in the URL only — that is the branch under test.
                    client      = new { client_id = clientId, metadata = new Dictionary<string, object> { ["project_id"] = projectId } },
                    oidc_context = new { extra = new Dictionary<string, object>() }   // no project_id
                }));
    }

    /// <summary>
    /// Sets up a login challenge with NO project_id in either oidc_context or URL.
    /// Used to exercise the ExtractProjectId → null path (BadRequest missing_project_id).
    /// </summary>
    public void SetupLoginChallengeWithNoProjectId(string challenge, string? clientId)
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
                    client      = new { client_id = clientId, metadata = new Dictionary<string, object>() },
                    oidc_context = new { extra = new Dictionary<string, object>() }  // no project_id
                }));
    }

    /// <summary>
    /// Sets up a consent challenge where the context is completely null (no user_id).
    /// Used to exercise the userIdStr == null guard (line 483).
    /// </summary>
    public void SetupConsentChallengeNullContext(string challenge, string? clientId)
    {
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
                    skip             = false,
                    subject          = "",
                    login_session_id = "test-login-session-id",
                    requested_scope  = new[] { "openid" },
                    context          = (object?)null,
                    client           = new { client_id = clientId }
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
                    skip             = false,
                    subject,
                    login_session_id = "test-login-session-id",
                    requested_scope  = new[] { "openid", "offline" },
                    context          = ctx,
                    client           = new { client_id = clientId }
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

    /// <summary>
    /// Makes session revocation fail, so a caller that discards the status can be caught claiming
    /// it revoked something it did not. Scoped to one subject: the stub server is shared across the
    /// whole collection, and a blanket failure mapping breaks every later test that revokes.
    /// </summary>
    public void SetupSessionRevocationFailure(string subject)
    {
        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/sessions/consent")
                .UsingDelete()
                .WithParam("subject", subject))
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(500).WithBodyAsJson(new { error = "stub_failure" }));
    }

    // ── OAuth2 client helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Stubs GET /admin/clients — the deployment's registered OAuth2 clients — so a test can state
    /// exactly which redirect_uris exist. Hydra is the only place those live, and CSP, CORS and the
    /// server-side redirect allowlist are all derived from them, so this is the one input that
    /// decides which origins the deployment will talk to.
    /// </summary>
    public void SetupRegisteredClients(params StubOAuth2Client[] clients)
    {
        _server
            .Given(Request.Create().WithPath("/admin/clients").UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(clients.Select(c => new
                {
                    client_id                 = c.ClientId,
                    redirect_uris             = c.RedirectUris,
                    post_logout_redirect_uris = c.PostLogoutRedirectUris ?? [],
                }).ToArray()));
    }

    /// <summary>
    /// Overrides where Hydra says the browser goes once a login is accepted. In production that is
    /// the client's own registered redirect_uri, which is exactly the case the default stub's
    /// same-origin "http://localhost/callback" hides.
    /// </summary>
    public void SetupLoginAcceptRedirect(string redirectTo)
    {
        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/requests/login/accept")
                .UsingPut())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { redirect_to = redirectTo }));
    }

    /// <summary>Puts the login-accept redirect back to the default same-origin callback.</summary>
    public void RestoreLoginAcceptRedirect()
    {
        _server
            .Given(Request.Create()
                .WithPath("/admin/oauth2/auth/requests/login/accept")
                .UsingPut())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { redirect_to = "http://localhost/callback" }));
    }

    /// <summary>
    /// Stubs GET /admin/clients/{clientId} with the URIs it has registered — what the console reads
    /// before it can offer to edit them.
    /// </summary>
    public void SetupClientRedirectUris(string clientId, string[] redirectUris, string[] postLogoutUris)
    {
        _server
            .Given(Request.Create()
                .WithPath($"/admin/clients/{Uri.EscapeDataString(clientId)}")
                .UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                client_id                 = clientId,
                redirect_uris             = redirectUris,
                post_logout_redirect_uris = postLogoutUris,
            }));
    }

    /// <summary>Puts GET /admin/clients back to the empty list, undoing SetupRegisteredClients.</summary>
    public void RestoreRegisteredClients()
    {
        _server
            .Given(Request.Create().WithPath("/admin/clients").UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));
    }

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

    /// <summary>Configures PATCH /admin/clients/{clientId} to return 500 (simulates Hydra scope update failure).</summary>
    public void SetupClientPatchFailure(string clientId)
    {
        _server
            .Given(Request.Create()
                .WithPath($"/admin/clients/{Uri.EscapeDataString(clientId)}")
                .UsingPatch())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(500));
    }

    /// <summary>Restores the default PATCH /admin/clients/* → 200 response.</summary>
    public void RestoreClientPatch()
    {
        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/admin/clients/*"))
                .UsingPatch())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { client_id = "stub-client" }));
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

    /// <summary>Makes /version return 200 with an invalid JSON body — causes JsonException in FetchVersion.</summary>
    public void SetVersionBroken()
    {
        _server
            .Given(Request.Create().WithPath("/version").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("!!not-json!!"));
    }

    /// <summary>Restores /version to the default healthy stub.</summary>
    public void RestoreVersion()
    {
        _server
            .Given(Request.Create().WithPath("/version").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { version = "v2.0.0-stub" }));
    }

    // ── Verification helpers ──────────────────────────────────────────────────

    public bool LoginWasAccepted(string challenge) =>
        _server.LogEntries.Any(e =>
            e.RequestMessage?.Path == "/admin/oauth2/auth/requests/login/accept" &&
            e.RequestMessage?.Query?.ContainsKey("login_challenge") == true &&
            e.RequestMessage.Query["login_challenge"].Contains(challenge));

    /// <summary>
    /// Le corps envoyé à <c>login/accept</c> pour ce challenge — subject et contexte compris.
    ///
    /// Sans lui, un test ne peut vérifier que « la connexion a été acceptée », pas SOUS QUELLE
    /// identité. Or c'est exactement ce que change l'appartenance à une organisation : le sujet
    /// et le <c>org_id</c> du contexte, que deux chemins distincts relisent ensuite.
    /// </summary>
    public string? AcceptedLoginBody(string challenge) =>
        _server.LogEntries.LastOrDefault(e =>
            e.RequestMessage?.Path == "/admin/oauth2/auth/requests/login/accept" &&
            e.RequestMessage?.Query?.ContainsKey("login_challenge") == true &&
            e.RequestMessage.Query["login_challenge"].Contains(challenge))
        ?.RequestMessage?.Body;

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

    /// <summary>Raw session body RediensIAM sent to Hydra when accepting a consent — the exact
    /// claim set that ends up in the issued access token.</summary>
    public string? AcceptedConsentBody(string challenge) =>
        _server.LogEntries
            .Where(e => e.RequestMessage?.Path == "/admin/oauth2/auth/requests/consent/accept"
                && e.RequestMessage?.Query?.ContainsKey("consent_challenge") == true
                && e.RequestMessage.Query["consent_challenge"].Contains(challenge))
            .Select(e => e.RequestMessage?.Body)
            .LastOrDefault();

    /// <summary>True if RediensIAM asked Hydra to revoke the consent sessions of this subject.</summary>
    public bool SessionsRevokedFor(string subject) =>
        _server.LogEntries.Any(e =>
            e.RequestMessage?.Path == "/admin/oauth2/auth/sessions/consent" &&
            e.RequestMessage?.Method == "DELETE" &&
            e.RequestMessage?.Query?.ContainsKey("subject") == true &&
            e.RequestMessage.Query["subject"].Contains(subject));

    public bool ConsentWasRejected(string challenge) =>
        _server.LogEntries.Any(e =>
            e.RequestMessage?.Path == "/admin/oauth2/auth/requests/consent/reject" &&
            e.RequestMessage?.Query?.ContainsKey("consent_challenge") == true &&
            e.RequestMessage.Query["consent_challenge"].Contains(challenge));

    public void ResetLog() => _server.ResetLogEntries();

    public void Dispose() => _server.Dispose();
}

/// <summary>One OAuth2 client as Hydra reports it on GET /admin/clients.</summary>
public sealed record StubOAuth2Client(
    string ClientId,
    string[] RedirectUris,
    string[]? PostLogoutRedirectUris = null);
