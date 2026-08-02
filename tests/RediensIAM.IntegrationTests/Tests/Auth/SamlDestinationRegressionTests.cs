using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.IdentityModel.Tokens.Saml2;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Endpoint binding for the SAML ACS: SAML 2.0 core §3.2.2 requires that a response carrying a
/// <c>Destination</c> attribute is discarded unless it names the location the message was received
/// at. ITfoxtec parses the attribute but never checks it, so nothing validated it here.
///
/// This is a secondary control. <c>AllowedAudienceUris</c> is what refuses an assertion aimed at a
/// different service provider, and it was already in place; what was missing was the check that
/// stops a response issued for one endpoint of *this* SP being relayed to another.
///
/// Declared as a part of <c>SamlControllerTests</c> to reuse its IdP seeding and form-scraping
/// helpers — the response builder here is separate only because the original hardcodes a matching
/// Destination, which is exactly the variable under test.
/// </summary>
public partial class SamlControllerTests
{
    // ── The comparison rules ──────────────────────────────────────────────────

    /// <summary>
    /// Differences that are cosmetic in a URI and must NOT be treated as a mismatch. Each one is a
    /// shape a real IdP emits; rejecting them would lock out working integrations while denying an
    /// attacker nothing, because none of them changes which endpoint is named.
    /// </summary>
    [Theory]
    [InlineData("https://sp.example.com/auth/saml/acs", "host case")]
    [InlineData("https://SP.EXAMPLE.COM/auth/saml/acs", "host case, upper")]
    [InlineData("HTTPS://sp.example.com/auth/saml/acs", "scheme case")]
    [InlineData("https://sp.example.com:443/auth/saml/acs", "explicit default port")]
    [InlineData("https://sp.example.com/auth/saml/acs/", "trailing slash")]
    [InlineData("https://sp.example.com/auth/../auth/saml/acs", "dot segments")]
    public void DestinationMatches_CosmeticDifferences_AreAccepted(string destination, string why)
    {
        var acs = new Uri("https://sp.example.com/auth/saml/acs");

        SamlService.DestinationMatches(new Uri(destination), acs)
            .Should().BeTrue($"{why} does not change which endpoint is named");
    }

    /// <summary>
    /// Everything that actually distinguishes one endpoint from another stays significant. The
    /// second case is the attack this control exists for: a response legitimately issued for one
    /// endpoint of this SP, relayed to a different one.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example.com/auth/saml/acs", "different host")]
    [InlineData("https://sp.example.com/auth/saml/other", "different endpoint on the same host")]
    [InlineData("http://sp.example.com/auth/saml/acs", "downgraded scheme")]
    [InlineData("https://sp.example.com:8443/auth/saml/acs", "different port")]
    [InlineData("https://sp.example.com/auth/saml/ACS", "path case")]
    public void DestinationMatches_RealDifferences_AreRefused(string destination, string why)
    {
        var acs = new Uri("https://sp.example.com/auth/saml/acs");

        SamlService.DestinationMatches(new Uri(destination), acs)
            .Should().BeFalse($"{why} names a different endpoint");
    }

    // ── Through the real ACS ──────────────────────────────────────────────────

    /// <summary>
    /// The control itself: a correctly signed, otherwise entirely valid response whose Destination
    /// names a different endpoint is refused. Before this check the only thing standing between
    /// such a response and a session was the audience restriction, which does not distinguish
    /// endpoints within one SP.
    /// </summary>
    [Fact]
    public async Task Acs_ResponseDestinedForAnotherEndpoint_IsRefused()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client", projectId: idp.ProjectId.ToString());
        var client = fixture.NewSessionClient();

        var (relayState, authnReqId) = await StartAndCaptureAsync(client, challenge, idp);
        var form = BuildSignedResponseFormWithDestination(idp, cert, relayState, authnReqId,
            destination: new Uri("http://localhost/auth/saml/somewhere-else"));

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("saml_response_invalid");
    }

    /// <summary>
    /// The check is ordered before the pending request is consumed, so a misdirected response
    /// cannot burn the single-use InResponseTo of a login still in flight. Without that ordering a
    /// relayed response would be a denial of service against the legitimate user.
    /// </summary>
    [Fact]
    public async Task Acs_MisdirectedResponse_DoesNotConsumeThePendingRequest()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client", projectId: idp.ProjectId.ToString());
        var client = fixture.NewSessionClient();

        var (relayState, authnReqId) = await StartAndCaptureAsync(client, challenge, idp);

        var misdirected = BuildSignedResponseFormWithDestination(idp, cert, relayState, authnReqId,
            destination: new Uri("http://localhost/auth/saml/somewhere-else"));
        (await client.PostAsync("/auth/saml/acs", misdirected))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Same authn request, this time correctly addressed: it must still be redeemable.
        var genuine = BuildSignedResponseFormWithDestination(idp, cert, relayState, authnReqId,
            destination: new Uri("http://localhost/auth/saml/acs"));

        var res = await client.PostAsync("/auth/saml/acs", genuine);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "rejecting a misdirected response must not spend the pending request");
    }

    /// <summary>A correctly addressed response still completes — the control is not a lockout.</summary>
    [Fact]
    public async Task Acs_ResponseDestinedForThisEndpoint_StillSucceeds()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client", projectId: idp.ProjectId.ToString());
        var client = fixture.NewSessionClient();

        var (relayState, authnReqId) = await StartAndCaptureAsync(client, challenge, idp);
        var form = BuildSignedResponseFormWithDestination(idp, cert, relayState, authnReqId,
            destination: new Uri("http://localhost/auth/saml/acs"));

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    /// <summary>
    /// Normalisation end to end, not just in the pure helper: an explicit default port and a
    /// trailing slash are the two shapes most likely to arrive from a real IdP's configuration.
    /// </summary>
    [Fact]
    public async Task Acs_DestinationWithDefaultPortAndTrailingSlash_IsAccepted()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client", projectId: idp.ProjectId.ToString());
        var client = fixture.NewSessionClient();

        var (relayState, authnReqId) = await StartAndCaptureAsync(client, challenge, idp);
        var form = BuildSignedResponseFormWithDestination(idp, cert, relayState, authnReqId,
            destination: new Uri("http://LOCALHOST:80/auth/saml/acs/"));

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "host case, an explicit default port and a trailing slash all name the same endpoint");
    }

    /// <summary>
    /// The decision on absence, pinned. §3.2.2 makes Destination optional and requires validation
    /// only "if it is present", so a response without one is accepted rather than refused —
    /// requiring it would break IdPs that legitimately omit it, and this is a defence-in-depth
    /// layer, not the control the flow depends on. The ACS logs a warning when it happens.
    /// </summary>
    [Fact]
    public async Task Acs_ResponseWithNoDestination_IsAccepted()
    {
        var (idp, cert) = await SeedAcsSamlIdpAsync();
        var challenge   = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, "saml-client", projectId: idp.ProjectId.ToString());
        var client = fixture.NewSessionClient();

        var (relayState, authnReqId) = await StartAndCaptureAsync(client, challenge, idp);
        var form = BuildSignedResponseFormWithDestination(idp, cert, relayState, authnReqId,
            destination: null);

        var res = await client.PostAsync("/auth/saml/acs", form);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "Destination is optional per the spec — absence is not grounds to refuse");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs Start and returns the SP-encoded RelayState plus the AuthnRequest ID the response has
    /// to echo, so a caller can build several responses against one pending request.
    /// </summary>
    private static async Task<(string RelayState, string AuthnRequestId)> StartAndCaptureAsync(
        HttpClient client, string challenge, SamlIdpConfig idp)
    {
        var startRes = await client.GetAsync(
            $"/auth/saml/start?login_challenge={Uri.EscapeDataString(challenge)}&idp_id={idp.Id}");
        startRes.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var qp = startRes.Headers.Location!.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

        var compressed = Convert.FromBase64String(qp["SAMLRequest"]);
        string authnXml;
        using (var ms      = new MemoryStream(compressed))
        using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
        using (var sr      = new StreamReader(deflate, Encoding.UTF8))
            authnXml = await sr.ReadToEndAsync();

        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(authnXml);

        return (qp.GetValueOrDefault("RelayState", ""), xmlDoc.DocumentElement!.GetAttribute("ID"));
    }

    /// <summary>
    /// Same as the suite's BuildSignedResponseForm, except the Destination is the caller's choice.
    ///
    /// A null <paramref name="destination"/> means "emit a response carrying no Destination at
    /// all", which the library will not do directly: CreateSecurityToken derives the
    /// SubjectConfirmation Recipient from Destination and throws on null. So that case builds a
    /// normal response and removes the attribute afterwards — sound only because it also switches
    /// to assertion-only signing, leaving the Response element unsigned and its attributes outside
    /// the signature. That combination is itself realistic: ITfoxtec accepts a response whose
    /// document signature is absent as long as the assertion's is valid.
    /// </summary>
    private static FormUrlEncodedContent BuildSignedResponseFormWithDestination(
        SamlIdpConfig idp, X509Certificate2 cert, string relayState, string authnReqId, Uri? destination)
    {
        const string spEntityId = "http://localhost/auth/saml/metadata";

        var idpCfg = new Saml2Configuration
        {
            Issuer             = idp.EntityId,
            SigningCertificate = cert,
            AuthnResponseSignType = destination == null
                ? Saml2AuthnResponseSignTypes.SignAssertion
                : Saml2AuthnResponseSignTypes.SignResponse,
        };
        idpCfg.AllowedAudienceUris.Add(spEntityId);

        var authResp = new Saml2AuthnResponse(idpCfg)
        {
            Status               = Saml2StatusCodes.Success,
            Destination          = destination ?? new Uri("http://localhost/auth/saml/acs"),
            InResponseToAsString = authnReqId,
            NameId               = new Saml2NameIdentifier("saml-destination@test.com"),
            ClaimsIdentity       = new ClaimsIdentity([new Claim("email", "saml-destination@test.com")]),
        };
        authResp.CreateSecurityToken(spEntityId);

        var postBind = new Saml2PostBinding { RelayState = relayState };
        postBind.Bind(authResp);

        // The bound document is the signed one; taking it directly is the same bytes the library
        // would have base64'd into its auto-post form, without scraping them back out of HTML.
        var doc = postBind.XmlDocument;
        if (destination == null)
            doc.DocumentElement!.RemoveAttribute("Destination");

        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml)),
            ["RelayState"]   = relayState,
        });
    }
}
