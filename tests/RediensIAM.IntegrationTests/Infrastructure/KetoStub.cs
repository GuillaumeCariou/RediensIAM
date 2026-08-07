using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace RediensIAM.IntegrationTests.Infrastructure;

/// <summary>
/// WireMock server that stubs Ory Keto's read and write APIs.
/// By default every check returns allowed=true and every write is a no-op.
/// Use <see cref="DenySubject"/> to simulate a specific denial.
/// </summary>
public sealed class KetoStub : IDisposable
{
    private readonly WireMockServer _readServer;
    private readonly WireMockServer _writeServer;

    public string ReadUrl  => _readServer.Url!;
    public string WriteUrl => _writeServer.Url!;

    public KetoStub()
    {
        _readServer  = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        _writeServer = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        AllowAll();
    }

    // ── Default mode: allow all ───────────────────────────────────────────────

    /// <summary>
    /// Resets to "allow all" mode (default). The list endpoint below must return a tuple, not an
    /// empty set: live authorisation resolves org_admin and project_admin through the list
    /// endpoint while everything else goes through check, so a stub that allows every check but
    /// lists nothing silently denies every non-super-admin request.
    /// </summary>
    public void AllowAll()
    {
        _readServer.Reset();
        _writeServer.Reset();

        // Health / version — used by SystemHealthController
        _readServer
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { status = "ok" }));
        _readServer
            .Given(Request.Create().WithPath("/version").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { version = "v0.12.0-stub" }));
        _writeServer
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { status = "ok" }));
        _writeServer
            .Given(Request.Create().WithPath("/version").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { version = "v0.12.0-stub" }));

        // Check: always allowed
        _readServer
            .Given(Request.Create().WithPath("/relation-tuples/check").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { allowed = true }));

        // List: returns a tuple, so HasAnyRelationAsync agrees with CheckAsync.
        _readServer
            .Given(Request.Create().WithPath("/relation-tuples").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                relation_tuples = new[]
                {
                    new { @namespace = "Projects", @object = "stub-object", relation = "manager", subject_id = "user:stub" }
                }
            }));

        // Write (insert/delete): always success
        _writeServer
            .Given(Request.Create().WithPath("/admin/relation-tuples").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(200));

        _writeServer
            .Given(Request.Create().WithPath("/admin/relation-tuples").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));
    }

    /// <summary>
    /// Les suppressions de tuple observées, telles qu'elles sont parties sur le fil.
    ///
    /// Le stub répond 204 à tout, donc un appelant qui vise un tuple inexistant réussit sans rien
    /// supprimer : c'est ainsi qu'une suppression de rôle a pu laisser ses porteurs autorisés sans
    /// qu'aucun test ne s'en aperçoive. Le seul moyen de tenir la forme du tuple est de la lire.
    /// </summary>
    public IReadOnlyList<string> DeletedTupleUrls => _writeServer.LogEntries
        .Where(e => e.RequestMessage?.Method == "DELETE")
        .Select(e => e.RequestMessage?.Url ?? "")
        .ToList();

    /// <summary>Makes tuple writes fail, so a dual-write's ordering is observable.</summary>
    public void FailTupleWrites()
    {
        _writeServer
            .Given(Request.Create().WithPath("/admin/relation-tuples").UsingPatch())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(503));
    }

    // ── Specific denials ──────────────────────────────────────────────────────

    /// <summary>
    /// Makes a specific permission check return denied.
    /// All other checks still return allowed.
    ///
    /// Matches the bare subject and its project-scoped form, <c>user:{id}|project:{pid}</c>, which
    /// is what <c>KetoService.AssignManagementRoleAsync</c> writes for a scoped grant. Real Keto
    /// has no tuple for either when the grant does not exist, so a stub that denied only the bare
    /// subject modelled a state the store cannot be in — and let a scoped check through.
    /// </summary>
    public void DenySubject(string subjectId)
    {
        _readServer
            .Given(Request.Create()
                .WithPath("/relation-tuples/check")
                .UsingGet()
                .WithParam("subject_id", new WildcardMatcher($"{subjectId}*")))
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { allowed = false }));
    }

    /// <summary>Deny a specific namespace+object+relation+subject combination.</summary>
    public void DenyCheck(string ns, string obj, string relation, string subjectId)
    {
        _readServer
            .Given(Request.Create()
                .WithPath("/relation-tuples/check")
                .UsingGet()
                .WithParam("namespace", ns)
                .WithParam("object", obj)
                .WithParam("relation", relation)
                .WithParam("subject_id", subjectId))
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { allowed = false }));
    }

    /// <summary>
    /// Makes the list endpoint return a non-empty relation tuple for <paramref name="userId"/>,
    /// so that <c>HasAnyRelationAsync</c> returns <c>true</c> for that user.
    /// Use this when testing code paths that call HasAnyRelationAsync (e.g. AdminLogin org_admin branch).
    /// </summary>
    public void SimulateRelationExists(string userId)
    {
        _readServer
            .Given(Request.Create()
                .WithPath("/relation-tuples")
                .UsingGet()
                .WithParam("subject_id", userId))
            .AtPriority(0)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    relation_tuples = new[]
                    {
                        new { @namespace = "Orgs", @object = "stub-org", relation = "org_admin", subject_id = userId }
                    }
                }));
    }

    // ── Tuple listing (grant reconciliation) ──────────────────────────────────

    /// <summary>
    /// Makes the list endpoint answer with exactly <paramref name="tuples"/> for
    /// <paramref name="ns"/>, the way real Keto answers a namespace query. Registered per
    /// namespace so the reconciler's Organisations and Projects walks can disagree, which is the
    /// only way to model one store holding a grant the other does not.
    ///
    /// No <c>next_page_token</c>: one page, which is what these fixtures need. The paging loop
    /// itself is the caller's, not the stub's.
    /// </summary>
    public void SetTuples(string ns, params RediensIAM.Services.RelationTuple[] tuples)
    {
        _readServer
            .Given(Request.Create().WithPath("/relation-tuples").UsingGet().WithParam("namespace", ns))
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                relation_tuples = tuples.Select(t => new
                {
                    @namespace = t.Namespace,
                    @object = t.Object,
                    relation = t.Relation,
                    subject_id = t.Subject,
                }),
                next_page_token = "",
            }));
    }

    /// <summary>Write-side requests seen so far, newest last — how a test tells a repair actually acted.</summary>
    public IReadOnlyList<(string Method, string Url)> WriteRequests =>
        [.. _writeServer.LogEntries
            .Select(e => e.RequestMessage)
            .Where(r => r is not null)
            .Select(r => (r!.Method, r.Url))];

    /// <summary>Forgets the write-side request log, so a test can assert on its own run alone.</summary>
    public void ResetWriteRequests() => _writeServer.ResetLogEntries();

    /// <summary>Deny ALL checks (simulate Keto returning forbidden for everything).</summary>
    public void DenyAll()
    {
        _readServer.Reset();
        _readServer
            .Given(Request.Create().WithPath("/relation-tuples/check").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { allowed = false }));

        _readServer
            .Given(Request.Create().WithPath("/relation-tuples").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { relation_tuples = Array.Empty<object>() }));
    }

    /// <summary>Makes every read return 500 — models Keto being down, for fail-closed tests.</summary>
    public void SimulateOutage()
    {
        _readServer.Reset();
        _readServer
            .Given(Request.Create().WithPath("/relation-tuples/check").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));
        _readServer
            .Given(Request.Create().WithPath("/relation-tuples").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));
    }

    // ── Health failure simulation ─────────────────────────────────────────────

    /// <summary>Makes both /health/alive endpoints return 500 at highest priority.</summary>
    public void SetHealthFailure()
    {
        _readServer
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(500).WithBodyAsJson(new { error = "simulated_failure" }));
        _writeServer
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(500).WithBodyAsJson(new { error = "simulated_failure" }));
    }

    /// <summary>Makes only the read /health/alive return 500.</summary>
    public void SetReadHealthFailure()
    {
        _readServer
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(500).WithBodyAsJson(new { error = "simulated_failure" }));
    }

    /// <summary>Makes only the write /health/alive return 500.</summary>
    public void SetWriteHealthFailure()
    {
        _writeServer
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(500).WithBodyAsJson(new { error = "simulated_failure" }));
    }

    /// <summary>Restores healthy /health/alive responses (override the failure stubs).</summary>
    public void RestoreHealth()
    {
        _readServer
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { status = "ok" }));
        _writeServer
            .Given(Request.Create().WithPath("/health/alive").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { status = "ok" }));
    }

    /// <summary>Makes both /version endpoints return 200 with invalid JSON — causes JsonException in FetchVersion.</summary>
    public void SetVersionBroken()
    {
        _readServer
            .Given(Request.Create().WithPath("/version").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("!!not-json!!"));
        _writeServer
            .Given(Request.Create().WithPath("/version").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("!!not-json!!"));
    }

    /// <summary>Restores /version to healthy stub.</summary>
    public void RestoreVersion()
    {
        _readServer
            .Given(Request.Create().WithPath("/version").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { version = "v0.12.0-stub" }));
        _writeServer
            .Given(Request.Create().WithPath("/version").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { version = "v0.12.0-stub" }));
    }

    public void Dispose()
    {
        _readServer.Dispose();
        _writeServer.Dispose();
    }
}
