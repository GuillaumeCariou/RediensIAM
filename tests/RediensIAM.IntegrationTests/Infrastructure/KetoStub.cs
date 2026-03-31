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

    /// <summary>Resets to "allow all" mode (default).</summary>
    public void AllowAll()
    {
        _readServer.Reset();
        _writeServer.Reset();

        // Check: always allowed
        _readServer
            .Given(Request.Create().WithPath("/relation-tuples/check").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { allowed = true }));

        // List: always empty
        _readServer
            .Given(Request.Create().WithPath("/relation-tuples").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { relation_tuples = new object[] { } }));

        // Write (insert/delete): always success
        _writeServer
            .Given(Request.Create().WithPath("/admin/relation-tuples").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(200));

        _writeServer
            .Given(Request.Create().WithPath("/admin/relation-tuples").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));
    }

    // ── Specific denials ──────────────────────────────────────────────────────

    /// <summary>
    /// Makes a specific permission check return denied.
    /// All other checks still return allowed.
    /// </summary>
    public void DenySubject(string subjectId)
    {
        _readServer
            .Given(Request.Create()
                .WithPath("/relation-tuples/check")
                .UsingGet()
                .WithParam("subject_id", subjectId))
            .AtPriority(1)
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
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { allowed = false }));
    }

    /// <summary>Deny ALL checks (simulate Keto returning forbidden for everything).</summary>
    public void DenyAll()
    {
        _readServer.Reset();
        _readServer
            .Given(Request.Create().WithPath("/relation-tuples/check").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { allowed = false }));

        _readServer
            .Given(Request.Create().WithPath("/relation-tuples").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { relation_tuples = new object[] { } }));
    }

    public void Dispose()
    {
        _readServer.Dispose();
        _writeServer.Dispose();
    }
}
