using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RediensIAM.Data;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Two residuals on the SAML path, closed together because they live three lines apart.
///
/// <para><b>Tenant scope.</b> <c>SamlController</c> resolves a project from the login challenge
/// exactly as <c>AuthController</c> does, and had been able to call
/// <c>TenantScopeInterceptor.PinToOrganisationAsync</c> ever since step 32 added it — it simply
/// never did, so every query the SAML login made ran as <c>'system'</c> with row-level security
/// enforcing nothing on it. The two entry points pin from different sources and that difference
/// is real, so they are tested separately.</para>
///
/// <para><b>Ordering (I-10).</b> The single-use pending record was consumed before the assertion
/// signature was validated, so an unauthenticated caller who could guess or replay a request id
/// could destroy a legitimate login still in flight by POSTing any document at all. This is the
/// same fix step 36 applied to the <c>Destination</c> check, applied to the signature.</para>
///
/// <para>Declared as a part of <c>SamlControllerTests</c> to reuse its IdP seeding and its
/// Start/response builders rather than duplicate them.</para>
/// </summary>
public partial class SamlControllerTests
{
    /// <summary>Database checkouts so far that carried a real tenant rather than <c>'system'</c>.</summary>
    private static double OrgScopedCheckouts() => IamMetrics.DbConnectionScope.WithLabels("org").Value;

    // ── The scope ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The proof that <c>Start</c> takes its scope from <c>client.metadata.org_id</c> and that the
    /// scope is enforced: the IdP is real and active, the project is the one the challenge names,
    /// and the only wrong thing is the organisation its OAuth2 client is registered to. Impossible
    /// for a client this application minted, and refused rather than reconciled — the same answer
    /// and the same error code the password path gives.
    ///
    /// <para>With RLS on, the mismatched scope already makes the IdP row invisible and this never
    /// reaches the check. The explicit check is what makes the guarantee hold with the chart flag
    /// off, which is how it ships today.</para>
    /// </summary>
    [Fact]
    public async Task Start_ChallengeClientNamingADifferentOrganisation_IsRefused()
    {
        var (idp, _)      = await SeedAcsSamlIdpAsync();
        var (stranger, _) = await fixture.Seed.CreateOrgAsync();
        var challenge     = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, "saml-client", idp.ProjectId.ToString(), stranger.Id.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/saml/start?login_challenge={challenge}&idp_id={idp.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_org_mismatch");
    }

    /// <summary>
    /// A challenge whose client carries no <c>org_id</c> — every project client minted before it
    /// was recorded there — must still work. The scope then comes from the project row instead,
    /// one unscoped read later. Falling back is the difference between a narrower window and an
    /// outage.
    /// </summary>
    [Fact]
    public async Task Start_ChallengeWithNoOrgInClientMetadata_StillWorks()
    {
        var (idp, _)  = await SeedAcsSamlIdpAsync();
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client", projectId: idp.ProjectId.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/saml/start?login_challenge={challenge}&idp_id={idp.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    /// <summary>
    /// The finding itself, measured rather than asserted. A whole SAML login — Start, then the
    /// ACS with its user lookup, JIT provisioning, role check and audit row — must open at least
    /// one database connection under a real organisation. Before the pin it opened none: the
    /// interceptor published <c>'system'</c> on every checkout the flow made.
    ///
    /// <para>The counter is the same one <c>TenantScopeInterceptor</c> increments on every
    /// checkout, which is why this measures the scope the connection actually carried rather
    /// than the intent of the code that set it.</para>
    /// </summary>
    [Fact]
    public async Task SamlLogin_OpensConnectionsUnderTheProjectsOrganisation()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client", projectId: idp.ProjectId.ToString());
        var client = fixture.NewSessionClient();

        var before = OrgScopedCheckouts();

        var (relayState, authnReqId) = await StartAndCaptureAsync(client, challenge, idp);
        var form = BuildSignedResponseFormWithDestination(idp, cert, relayState, authnReqId,
            destination: new Uri("http://localhost/auth/saml/acs"));
        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect, "the login itself must still complete");
        OrgScopedCheckouts().Should().BeGreaterThan(before,
            "a SAML login that opens no tenant-scoped connection is a login RLS enforces nothing on");
    }

    /// <summary>
    /// The ACS reaches its tenant through the IdP's own project row, not through the challenge:
    /// its challenge arrives in RelayState, which the browser controls and the assertion
    /// signature does not cover. So the entry in <c>LegitimatelyUnscopedPaths</c> has to keep
    /// naming that one read, and the whole-controller entry has to be gone.
    /// </summary>
    [Fact]
    public void The_Unscoped_Path_List_Names_Only_The_Read_That_Decides_The_Scope()
    {
        var paths = TenantScopeInterceptor.LegitimatelyUnscopedPaths;

        paths.Should().Contain(p => p.Contains("saml_idp_configs", StringComparison.Ordinal),
            "the one read that cannot run under the scope it determines stays named");
        paths.Should().NotContain(p => p.Contains("not yet done", StringComparison.Ordinal),
            "the controller as a whole is no longer unscoped");
    }

    // ── The ordering (I-10) ───────────────────────────────────────────────────

    /// <summary>
    /// I-10. A response whose signature does not verify must not spend the pending request.
    ///
    /// <para>The record is single-use, so consuming it before validating meant an unauthenticated
    /// caller who could guess or replay an <c>InResponseTo</c> could burn a legitimate login still
    /// in flight with a document signed by nobody at all — the whole cost of the denial of service
    /// paid before anything was checked. The response here is well-formed, correctly addressed and
    /// echoes the right request id; the only thing wrong with it is the key it was signed with.</para>
    ///
    /// <para>What this does <b>not</b> claim: an attacker who controls any registered active IdP
    /// can still sign a response of their own, name that IdP in RelayState and burn a guessed id,
    /// because the checks binding the response to the IdP and challenge it was issued for need the
    /// record in hand. Consuming atomically is what stops a valid captured response being redeemed
    /// twice, and that is worth more than closing the rest.</para>
    /// </summary>
    [Fact]
    public async Task Acs_ResponseSignedByTheWrongKey_DoesNotConsumeThePendingRequest()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client", projectId: idp.ProjectId.ToString());
        var client = fixture.NewSessionClient();

        var (relayState, authnReqId) = await StartAndCaptureAsync(client, challenge, idp);

        using var attackerCert = ForeignSigningCert();
        var forged = BuildSignedResponseFormWithDestination(idp, attackerCert, relayState, authnReqId,
            destination: new Uri("http://localhost/auth/saml/acs"));

        var refused = await client.PostAsync("/auth/saml/acs", forged);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("saml_response_invalid");

        // Same authn request, this time genuinely signed: it must still be redeemable.
        var genuine = BuildSignedResponseFormWithDestination(idp, cert, relayState, authnReqId,
            destination: new Uri("http://localhost/auth/saml/acs"));

        var res = await client.PostAsync("/auth/saml/acs", genuine);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "rejecting a forged response must not spend the pending request of the real login");
    }

    /// <summary>A key this SP has never been told about — the attacker's, not the IdP's.</summary>
    private static X509Certificate2 ForeignSigningCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=NotTheIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var raw = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.EphemeralKeySet);
    }
}
