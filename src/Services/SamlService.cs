using System.Net;
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

        config.SingleSignOnDestination = new Uri(idp.SsoUrl);

        if (!string.IsNullOrEmpty(idp.CertificatePem))
            config.SignatureValidationCertificates.Add(X509Certificate2.CreateFromPem(idp.CertificatePem));
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
