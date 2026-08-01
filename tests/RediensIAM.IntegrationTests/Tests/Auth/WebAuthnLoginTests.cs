using System.Net.Http.Json;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using System.Net.Http.Headers;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;

namespace RediensIAM.IntegrationTests.Tests.Auth;

// ── from WebAuthnLoginTests.cs ──────────────────────────────────

/// <summary>
/// Covers AuthController WebAuthn MFA login paths:
///   - GET  /auth/mfa/webauthn/options  — no session (line 1314)
///   - GET  /auth/mfa/webauthn/options  — valid session, returns assertion options (lines 1316-1329)
///   - POST /auth/mfa/webauthn/verify   — no session (line 1339)
///   - POST /auth/mfa/webauthn/verify   — no assertion options (line 1342)
///   - POST /auth/mfa/webauthn/verify   — unknown credential (line 1349)
///   - POST /auth/mfa/webauthn/verify   — assertion_failed (lines 1357-1369)
/// </summary>
[Collection("RediensIAM")]
public class WebAuthnLoginTests(TestFixture fixture)
{
    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private async Task<(Organisation org, Project project, UserList list)> ScaffoldAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa         = false;   // these tests reach WebAuthn through the user's own
        await fixture.Db.SaveChangesAsync();  // factor, not through the project's enrolment gate
        return (org, project, list);
    }

    // ── GET /auth/mfa/webauthn/options — no MFA session ──────────────────────

    [Fact]
    public async Task WebAuthnOptions_NoSession_Returns400()
    {
        var client = fixture.NewSessionClient();

        var res = await client.GetAsync("/auth/mfa/webauthn/options");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_mfa_session");
    }

    // ── GET /auth/mfa/webauthn/options — valid session ────────────────────────

    [Fact]
    public async Task WebAuthnOptions_ValidSession_ReturnsAssertionOptions()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user              = await fixture.Seed.CreateUserAsync(list.Id);
        user.WebAuthnEnabled  = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        var loginRes = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });
        loginRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        loginBody.GetProperty("mfa_type").GetString().Should().Be("webauthn");

        var res = await client.GetAsync("/auth/mfa/webauthn/options");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("challenge", out _).Should().BeTrue();
    }

    // ── POST /auth/mfa/webauthn/verify — no MFA session ──────────────────────

    [Fact]
    public async Task WebAuthnVerify_NoSession_Returns400()
    {
        var client = fixture.NewSessionClient();

        var res = await client.PostAsJsonAsync("/auth/mfa/webauthn/verify",
            JsonDocument.Parse("{}").RootElement);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_mfa_session");
    }

    // ── POST /auth/mfa/webauthn/verify — no assertion options ─────────────────

    [Fact]
    public async Task WebAuthnVerify_NoAssertionOptions_Returns400()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user              = await fixture.Seed.CreateUserAsync(list.Id);
        user.WebAuthnEnabled  = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        // Session is set (mfa_pending_user etc.) but no fido2.assertionOptions
        var res = await client.PostAsJsonAsync("/auth/mfa/webauthn/verify",
            JsonDocument.Parse("{}").RootElement);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_assertion_options");
    }

    // ── POST /auth/mfa/webauthn/verify — assertion_failed (lines 1351-1369) ────

    /// <summary>
    /// Seeds a real WebAuthn credential in the DB so the unknown_credential check
    /// passes, then sends invalid assertion bytes so MakeAssertionAsync throws →
    /// covers AuthController lines 1351-1369 (lambda, try block, catch).
    /// </summary>
    [Fact]
    public async Task WebAuthnVerify_AssertionFailed_Returns401()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user             = await fixture.Seed.CreateUserAsync(list.Id);
        user.WebAuthnEnabled = true;

        // The credential must exist, otherwise verify fails at lookup and never reaches the
        // assertion check this test is about.
        var credId = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
        fixture.Db.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            Id           = Guid.NewGuid(),
            UserId       = user.Id,
            CredentialId = credId,
            PublicKey    = new byte[65],
            SignCount    = 0L,
            DeviceName   = "TestKey",
            CreatedAt    = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });
        await client.GetAsync("/auth/mfa/webauthn/options");

        // A known credentialId with deliberately invalid assertion bytes: lookup succeeds, then
        // MakeAssertionAsync throws and the failure must be caught rather than escaping as a 500.
        static string B64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var payload = new
        {
            id    = B64Url(credId),
            rawId = B64Url(credId),
            type  = "public-key",
            response = new
            {
                authenticatorData = B64Url(new byte[37]),
                clientDataJSON    = B64Url(new byte[50]),
                signature         = B64Url(new byte[64]),
            }
        };

        var res  = await client.PostAsJsonAsync("/auth/mfa/webauthn/verify", payload);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        body.GetProperty("error").GetString().Should().Be("assertion_failed");
    }

    // ── POST /auth/mfa/webauthn/verify — unknown credential ───────────────────

    [Fact]
    public async Task WebAuthnVerify_UnknownCredential_Returns401()
    {
        var (org, project, list) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var user              = await fixture.Seed.CreateUserAsync(list.Id);
        user.WebAuthnEnabled  = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var client = fixture.NewSessionClient();
        await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });

        // Populates fido2.assertionOptions in the session; verify cannot run without it.
        await client.GetAsync("/auth/mfa/webauthn/options");

        // Base64URL, not standard Base64: the Fido2 library rejects '+', '/' and '=' outright,
        // which would fail the request before the unknown-credential path is reached.
        static string B64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var unknownId = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var payload = new
        {
            id    = B64Url(unknownId),
            rawId = B64Url(unknownId),
            type  = "public-key",
            response = new
            {
                authenticatorData = B64Url(new byte[37]),
                clientDataJSON    = B64Url(new byte[50]),
                signature         = B64Url(new byte[64]),
            }
        };

        var res = await client.PostAsJsonAsync("/auth/mfa/webauthn/verify", payload);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("unknown_credential");
    }
}

// ── from WebAuthnMockCoverageTests.cs ───────────────────────────

/// <summary>
/// Covers WebAuthn success paths using a mock IFido2 that bypasses real
/// attestation/assertion verification:
///   - AccountController.WebAuthnRegisterComplete success path (lines 274-287)
///   - AuthController.WebAuthnVerify success path (lines 1366, 1372-1392)
/// </summary>
[Collection("RediensIAM")]
public class WebAuthnMockCoverageTests(TestFixture fixture)
{
    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── AccountController.WebAuthnRegisterComplete success (lines 274-287) ────

    /// <summary>
    /// Calls begin (real Fido2 → attestation options in session) then complete with
    /// a mock IFido2 that returns a fake RegisteredPublicKeyCredential — covers the
    /// success path that adds a WebAuthnCredential to the DB and returns 200.
    /// </summary>
    [Fact]
    public async Task WebAuthnRegisterComplete_MockFido2_CoversSuccessPath()
    {
        // Use a mock Fido2 that delegates begin to a real inner instance but
        // returns a fake result for MakeNewCredentialAsync
        var fido2Mock = new SucceedingFido2();
        var (client, factory) = fixture.CreateFido2MockClient(fido2Mock);
        await using var _f = factory;

        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var user  = await fixture.Seed.CreateUserAsync(list.Id);
        var token = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        // Begin — sets fido2.attestationOptions in session
        var beginRes = await client.PostAsync("/account/mfa/webauthn/register/begin", null);
        beginRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // Complete — the mock's MakeNewCredentialAsync returns a fake credential, which is the
        // only way to reach the persist-and-return-200 path without a real authenticator.
        var res = await client.PostAsJsonAsync("/account/mfa/webauthn/register/complete", new
        {
            response    = new { clientDataJSON = B64Url(new byte[50]), attestationObject = B64Url(new byte[100]) },
            device_name = "MockKey"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("passkey_registered");
    }

    // ── AuthController.WebAuthnVerify success (lines 1366, 1372-1392) ────────

    /// <summary>
    /// Full WebAuthn login flow with a seeded credential and a mock IFido2 that
    /// returns a fake VerifyAssertionResult — covers the success path from
    /// try-close (L1366) through Hydra accept and audit log (L1372-1392).
    /// </summary>
    [Fact]
    public async Task WebAuthnVerify_MockFido2_CoversSuccessPath()
    {
        var credId = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02, 0x03, 0x04 };
        var fido2Mock = new SucceedingFido2(assertionCredentialId: credId);
        var (client, factory) = fixture.CreateFido2MockClient(fido2Mock);
        await using var _f = factory;

        var (org, _)   = await fixture.Seed.CreateOrgAsync();
        var project    = await fixture.Seed.CreateProjectAsync(org.Id);
        var list       = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var user             = await fixture.Seed.CreateUserAsync(list.Id);
        user.WebAuthnEnabled = true;
        fixture.Db.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            Id           = Guid.NewGuid(),
            UserId       = user.Id,
            CredentialId = credId,
            PublicKey    = new byte[65],
            SignCount    = 0L,
            DeviceName   = "MockKey",
            CreatedAt    = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        fixture.Keto.AllowAll();
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        var loginRes = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test"
        });
        loginRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        loginBody.GetProperty("mfa_type").GetString().Should().Be("webauthn");

        // Get assertion options → sets fido2.assertionOptions in session
        var optRes = await client.GetAsync("/auth/mfa/webauthn/options");
        optRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // The mock's MakeAssertionAsync succeeds, so this reaches everything past the assertion:
        // the SignCount update, the Hydra accept and the audit write.
        var payload = new
        {
            id    = B64Url(credId),
            rawId = B64Url(credId),
            type  = "public-key",
            response = new
            {
                authenticatorData = B64Url(new byte[37]),
                clientDataJSON    = B64Url(new byte[50]),
                signature         = B64Url(new byte[64]),
            }
        };

        var res  = await client.PostAsJsonAsync("/auth/mfa/webauthn/verify", payload);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty();
    }
}

// ── Local stubs ───────────────────────────────────────────────────────────────

/// <summary>
/// IFido2 implementation that delegates RequestNewCredential / GetAssertionOptions
/// to a real inner Fido2 instance so session options are valid JSON, while
/// MakeNewCredentialAsync and MakeAssertionAsync always succeed with fake data.
/// </summary>
file sealed class SucceedingFido2 : IFido2
{
    private readonly byte[] _assertionCredentialId;

    public SucceedingFido2(byte[]? assertionCredentialId = null)
    {
        _assertionCredentialId = assertionCredentialId ?? new byte[] { 0x01, 0x02, 0x03, 0x04 };
        _inner = new Fido2(new Fido2Configuration
        {
            ServerDomain            = "localhost",
            ServerName              = "RediensIAM-test",
            Origins                 = new HashSet<string> { "http://localhost" },
            TimestampDriftTolerance = 300_000,
        });
    }

    private readonly Fido2 _inner;

    public CredentialCreateOptions RequestNewCredential(RequestNewCredentialParams p)
        => _inner.RequestNewCredential(p);

    public AssertionOptions GetAssertionOptions(GetAssertionOptionsParams p)
        => _inner.GetAssertionOptions(p);

    public Task<RegisteredPublicKeyCredential> MakeNewCredentialAsync(
        MakeNewCredentialParams p, CancellationToken ct = default)
    {
        // Use a unique credential ID each call so tests don't share a DB row
        var result = new RegisteredPublicKeyCredential
        {
            Id        = Guid.NewGuid().ToByteArray(),
            PublicKey = new byte[65],
            SignCount = 0u,
        };
        return Task.FromResult(result);
    }

    public Task<VerifyAssertionResult> MakeAssertionAsync(
        MakeAssertionParams p, CancellationToken ct = default)
    {
        var result = new VerifyAssertionResult
        {
            CredentialId = _assertionCredentialId,
            SignCount    = (p.StoredSignatureCounter) + 1u,
        };
        return Task.FromResult(result);
    }
}
