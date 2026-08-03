using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using RediensIAM.Controllers;
using RediensIAM.Data.Entities;

namespace RediensIAM.Services;

public class SamlService(
    IHttpClientFactory httpClientFactory,
    ILogger<SamlService> logger)
{
    /// <summary>
    /// Builds a Saml2Configuration for the given IdP config.
    /// Loads SSO URL + signing cert either from metadata URL or from explicit fields.
    /// </summary>
    /// <remarks>
    /// <c>acsUrl</c> is deliberately not written into the returned configuration: Saml2Configuration
    /// has no expected-destination slot and ITfoxtec never validates a response's Destination
    /// attribute, so the endpoint-binding check cannot live in the config object. It happens on the
    /// receiving path instead — see <see cref="DestinationMatches"/>, called from SamlController's
    /// ACS. The parameter stays so both call sites keep naming the endpoint being configured for.
    /// </remarks>
    public async Task<Saml2Configuration> BuildConfigAsync(
        SamlIdpConfig idp, string spEntityId, Uri acsUrl)
    {
        var config = new Saml2Configuration
        {
            Issuer                    = spEntityId,
            SingleSignOnDestination   = null!,   // set below
            AllowedIssuer             = idp.EntityId,
            // Deliberately not ChainTrust. The signing certificate is pinned explicitly through
            // SignatureValidationCertificates, and enterprise IdPs routinely present self-signed
            // certificates that no chain would validate — raising this locks those deployments out
            // without adding a control the pin does not already give. The one thing None also
            // switches off is the validity window, which is why both branches below re-check it by
            // hand (IsValidLocalTime / the NotBefore-NotAfter guard).
            CertificateValidationMode = X509CertificateValidationMode.None,
        };
        config.AllowedAudienceUris.Add(spEntityId);

        if (!string.IsNullOrEmpty(idp.MetadataUrl))
            await ApplyMetadataAsync(config, idp);
        else
            ApplyExplicitConfig(config, idp);

        return config;
    }

    private async Task ApplyMetadataAsync(Saml2Configuration config, SamlIdpConfig idp)
    {
        try
        {
            var metaUri = new Uri(idp.MetadataUrl!);
            if (metaUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("SAML metadata URL must use HTTPS");

            if (await WebhookUrlValidator.IsPrivateOrReservedAsync(idp.MetadataUrl!))
                throw new InvalidOperationException("SAML metadata URL must not point to a private or reserved IP address");

            var descriptor = new EntityDescriptor();
            await descriptor.ReadIdPSsoDescriptorFromUrlAsync(httpClientFactory, metaUri);

            if (descriptor.IdPSsoDescriptor == null)
                throw new InvalidOperationException("No IdPSsoDescriptor in metadata");

            config.AllowedIssuer = descriptor.EntityId;
            config.SingleSignOnDestination = descriptor.IdPSsoDescriptor.SingleSignOnServices.First().Location;

            foreach (var cert in descriptor.IdPSsoDescriptor.SigningCertificates.Where(c => c.IsValidLocalTime()))
                config.SignatureValidationCertificates.Add(cert);

            if (config.SignatureValidationCertificates.Count == 0)
                throw new InvalidOperationException(
                    $"SAML IdP {idp.Id}: metadata contains no valid signing certificates. " +
                    "Cannot validate SAML assertions without at least one signing certificate.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAML IdP {IdpId}: failed to load metadata from {Url}", idp.Id, idp.MetadataUrl);
            throw new InvalidOperationException($"SAML IdP {idp.Id}: failed to load metadata", ex);
        }
    }

    private static void ApplyExplicitConfig(Saml2Configuration config, SamlIdpConfig idp)
    {
        if (string.IsNullOrEmpty(idp.SsoUrl))
            throw new InvalidOperationException("SAML IdP has neither MetadataUrl nor SsoUrl");

        // GET /auth/saml/start redirects the browser here without going through
        // RedirectValidator, so an unvalidated SsoUrl is an unauthenticated open redirect to
        // wherever an org admin points it. The metadata branch already requires HTTPS; guarding
        // on the read path rather than the four write paths also covers rows already stored.
        if (!Uri.TryCreate(idp.SsoUrl, UriKind.Absolute, out var ssoUri) || ssoUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"SAML IdP {idp.Id}: SsoUrl must be an absolute HTTPS URL");

        config.SingleSignOnDestination = ssoUri;

        // The signing certificate is the ONLY integrity control on a SAML assertion. Without
        // one, an unsigned (or attacker-signed) assertion could be accepted. The metadata
        // branch already refuses this case; refuse it here too instead of failing open.
        if (string.IsNullOrEmpty(idp.CertificatePem))
            throw new InvalidOperationException(
                $"SAML IdP {idp.Id}: CertificatePem is required when no MetadataUrl is configured. " +
                "Assertions cannot be validated without a signing certificate.");

        // CertificateValidationMode.None (above) switches off ITfoxtec's certificate validator
        // entirely — which is the point for a pinned self-signed cert, but it also means nothing
        // looks at NotBefore/NotAfter. The metadata branch filters on IsValidLocalTime(); without
        // this the explicit branch accepted a superseded signing key for ever, which is exactly
        // the key an IdP rotates away from.
        var cert = X509Certificate2.CreateFromPem(idp.CertificatePem);
        var now = DateTime.Now;
        if (now < cert.NotBefore || now > cert.NotAfter)
            throw new InvalidOperationException(
                $"SAML IdP {idp.Id}: the configured signing certificate is outside its validity window " +
                $"({cert.NotBefore:u} – {cert.NotAfter:u}). Upload the IdP's current certificate.");

        config.SignatureValidationCertificates.Add(cert);
    }

    /// <summary>
    /// True when a SAML response's <c>Destination</c> names this SP's ACS endpoint.
    ///
    /// SAML 2.0 core §3.2.2: <c>Destination</c> is optional, but "if it is present, the actual
    /// recipient MUST check that the URI reference identifies the location at which the message
    /// was received. If it does not, the request MUST be discarded." ITfoxtec parses the attribute
    /// into <c>Saml2Request.Destination</c> but never checks it, so this is ours to do.
    ///
    /// Comparison is deliberately normalising rather than an ordinal string equality, because the
    /// value is whatever the IdP echoed and the three differences below are cosmetic — treating
    /// them as mismatches would lock out working IdPs without denying an attacker anything:
    /// <list type="bullet">
    /// <item>host and scheme case (<c>Uri.Compare</c> folds both);</item>
    /// <item>an explicitly-written default port — <c>:443</c> on https, <c>:80</c> on http —
    /// which <c>UriComponents.SchemeAndServer</c> elides, while a genuinely different port such
    /// as <c>:8443</c> still mismatches;</item>
    /// <item>a trailing slash on the path.</item>
    /// </list>
    /// Everything that distinguishes one endpoint from another stays significant: scheme, host,
    /// non-default port, and the path — compared ordinally, so <c>/auth/saml/ACS</c> does not
    /// satisfy <c>/auth/saml/acs</c>. A query string on the Destination is ignored, as our ACS
    /// location carries none and the endpoint is already pinned by scheme, host and path.
    /// </summary>
    public static bool DestinationMatches(Uri destination, Uri acsUrl) =>
        Uri.Compare(destination, acsUrl, UriComponents.SchemeAndServer,
            UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0
        && string.Equals(destination.AbsolutePath.TrimEnd('/'),
            acsUrl.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>Extracts the user's email from a claims identity using the configured attribute name.</summary>
    public static string? ExtractEmail(
        System.Security.Claims.ClaimsIdentity? identity, string emailAttributeName)
    {
        if (identity == null) return null;
        return identity.FindFirst(emailAttributeName)?.Value
            ?? identity.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? identity.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")?.Value;
    }

    /// <summary>Extracts the display name from a claims identity using the configured attribute name.</summary>
    public static string? ExtractDisplayName(
        System.Security.Claims.ClaimsIdentity? identity, string? displayNameAttributeName)
    {
        if (identity == null) return null;
        if (displayNameAttributeName != null)
            return identity.FindFirst(displayNameAttributeName)?.Value;
        return identity.FindFirst(System.Security.Claims.ClaimTypes.GivenName)?.Value
            ?? identity.FindFirst("displayName")?.Value
            ?? identity.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
    }
}
