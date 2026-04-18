using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

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

        // Complete — mock MakeNewCredentialAsync returns fake credential → covers L274-287
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

        // Login → redirected to WebAuthn MFA
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

        // Verify with known credentialId — mock MakeAssertionAsync returns success
        // → covers L1366 (try close), L1372-1392 (update SignCount, Hydra accept, audit)
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
