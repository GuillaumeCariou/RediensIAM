using System.Security.Cryptography.X509Certificates;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
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
        };
        config.AllowedAudienceUris.Add(spEntityId);

        if (!string.IsNullOrEmpty(idp.MetadataUrl))
        {
            try
            {
                var descriptor = new EntityDescriptor();
                await descriptor.ReadIdPSsoDescriptorFromUrlAsync(
                    httpClientFactory, new Uri(idp.MetadataUrl));

                if (descriptor.IdPSsoDescriptor == null)
                    throw new InvalidOperationException("No IdPSsoDescriptor in metadata");

                config.AllowedIssuer = descriptor.EntityId;
                config.SingleSignOnDestination =
                    descriptor.IdPSsoDescriptor.SingleSignOnServices.First().Location;

                foreach (var cert in descriptor.IdPSsoDescriptor.SigningCertificates)
                    if (cert.IsValidLocalTime())
                        config.SignatureValidationCertificates.Add(cert);

                if (config.SignatureValidationCertificates.Count == 0)
                    logger.LogWarning("SAML IdP {IdpId}: no valid signing certs in metadata", idp.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SAML IdP {IdpId}: failed to load metadata from {Url}", idp.Id, idp.MetadataUrl);
                throw;
            }
        }
        else
        {
            if (string.IsNullOrEmpty(idp.SsoUrl))
                throw new InvalidOperationException("SAML IdP has neither MetadataUrl nor SsoUrl");

            config.SingleSignOnDestination = new Uri(idp.SsoUrl);

            if (!string.IsNullOrEmpty(idp.CertificatePem))
                config.SignatureValidationCertificates.Add(
                    X509Certificate2.CreateFromPem(idp.CertificatePem));
        }

        return config;
    }

    /// <summary>Extracts the user's email from a claims identity using the configured attribute name.</summary>
    public static string? ExtractEmail(
        System.Security.Claims.ClaimsIdentity? identity, string emailAttributeName)
    {
        if (identity == null) return null;
        return identity.FindFirst(emailAttributeName)?.Value
            ?? identity.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? identity.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")?.Value
            ?? identity.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
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
