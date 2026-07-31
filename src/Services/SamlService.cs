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
    public async Task<Saml2Configuration> BuildConfigAsync(
        SamlIdpConfig idp, string spEntityId, Uri acsUrl)
    {
        var config = new Saml2Configuration
        {
            Issuer                    = spEntityId,
            SingleSignOnDestination   = null!,   // set below
            AllowedIssuer             = idp.EntityId,
            // We explicitly provide SignatureValidationCertificates, so skip chain validation.
            // Self-signed IdP certs are common in enterprise SAML deployments.
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
