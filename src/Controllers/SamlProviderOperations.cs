using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// Registering, changing and removing a project's SAML identity providers, written once.
///
/// <para>
/// Both scopes offered all four operations and each was written twice. They had drifted in four
/// ways, two of which are not tidiness:
/// </para>
///
/// <list type="bullet">
/// <item><b>The system route validated nothing.</b> The organisation route refuses a provider with
/// no entity id, with neither a metadata URL nor an SSO URL, and — the one that matters — with
/// neither a metadata URL nor a certificate: without metadata there is nowhere to discover the
/// signing key, so an assertion from that provider could never be verified. The system route
/// accepted all three and stored them. More authority is not less validation.</item>
/// <item><b>The organisation update recorded nothing.</b> <c>sso_url</c> decides where users are
/// sent to authenticate and <c>certificate_pem</c> decides which key this deployment will trust to
/// sign what comes back. Changing either is an authentication-takeover primitive, and on the
/// tenant's own route it left no trace.</item>
/// <item>Creation answered 201 with the entity id on one side, 200 with the id alone on the other.</item>
/// <item><c>Guid.Empty</c> meant "clear the default role" on the system route and was written
/// through as a value on the organisation route, where no role will ever match it — so JIT
/// provisioning silently assigned nothing instead of the role the operator thought they had left
/// in place.</item>
/// </list>
///
/// <para>
/// The controller still finds the project and the provider, because that is the part that is
/// genuinely per-scope. Same shape as <see cref="ProjectOperations"/>.
/// </para>
/// </summary>
public static class SamlProviderOperations
{
    /// <summary>
    /// Refuses a registration that could not work, whoever sends it. Returned as an error result
    /// rather than thrown so both routes answer with the same body.
    /// </summary>
    public static BadRequestObjectResult? Validate(SamlProviderInput input)
    {
        if (string.IsNullOrEmpty(input.EntityId))
            return new BadRequestObjectResult(new { error = "entity_id_required" });
        if (string.IsNullOrEmpty(input.MetadataUrl) && string.IsNullOrEmpty(input.SsoUrl))
            return new BadRequestObjectResult(new { error = "metadata_url_or_sso_url_required" });
        // Without metadata there is no way to discover the signing key, so it must be supplied.
        if (string.IsNullOrEmpty(input.MetadataUrl) && string.IsNullOrEmpty(input.CertificatePem))
            return new BadRequestObjectResult(new { error = "certificate_pem_required_without_metadata_url" });
        return null;
    }

    public static async Task<IActionResult> CreateAsync(
        RediensIamDbContext db, AuditLogService audit, Guid actorId, Project project, SamlProviderInput input)
    {
        if (Validate(input) is { } error) return error;

        var provider = new SamlIdpConfig
        {
            ProjectId                = project.Id,
            EntityId                 = input.EntityId!,
            MetadataUrl              = input.MetadataUrl,
            SsoUrl                   = input.SsoUrl,
            CertificatePem           = input.CertificatePem,
            EmailAttributeName       = input.EmailAttributeName ?? "email",
            DisplayNameAttributeName = input.DisplayNameAttributeName,
            JitProvisioning          = input.JitProvisioning ?? true,
            DefaultRoleId            = NormaliseRoleId(input.DefaultRoleId),
            Active                   = true,
            CreatedAt                = DateTimeOffset.UtcNow,
            UpdatedAt                = DateTimeOffset.UtcNow,
        };
        db.SamlIdpConfigs.Add(provider);
        await db.SaveChangesAsync();

        // project.OrgId, never the caller's: an entry on another chain is one the tenant cannot read.
        await audit.RecordAsync(project.OrgId, project.Id, actorId,
            "saml_provider.created", AuditTargetType, provider.Id.ToString());

        return new CreatedResult(
            $"/org/projects/{project.Id}/saml-providers/{provider.Id}",
            new { provider.Id, provider.EntityId });
    }

    public static async Task<IActionResult> UpdateAsync(
        RediensIamDbContext db, AuditLogService audit, Guid actorId, SamlIdpConfig provider, SamlProviderInput input)
    {
        if (input.EntityId != null)                 provider.EntityId                 = input.EntityId;
        if (input.MetadataUrl != null)              provider.MetadataUrl              = input.MetadataUrl;
        if (input.SsoUrl != null)                   provider.SsoUrl                   = input.SsoUrl;
        if (input.CertificatePem != null)           provider.CertificatePem           = input.CertificatePem;
        if (input.EmailAttributeName != null)       provider.EmailAttributeName       = input.EmailAttributeName;
        if (input.DisplayNameAttributeName != null) provider.DisplayNameAttributeName = input.DisplayNameAttributeName;
        if (input.JitProvisioning.HasValue)         provider.JitProvisioning          = input.JitProvisioning.Value;
        if (input.DefaultRoleId.HasValue)           provider.DefaultRoleId            = NormaliseRoleId(input.DefaultRoleId);
        if (input.Active.HasValue)                  provider.Active                   = input.Active.Value;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await audit.RecordAsync(provider.Project.OrgId, provider.ProjectId, actorId,
            "saml_provider.updated", AuditTargetType, provider.Id.ToString());

        return new OkObjectResult(new { provider.Id, provider.EntityId, provider.Active });
    }

    public static async Task<IActionResult> DeleteAsync(
        RediensIamDbContext db, AuditLogService audit, Guid actorId, SamlIdpConfig provider)
    {
        var orgId = provider.Project.OrgId;
        var projectId = provider.ProjectId;
        var providerId = provider.Id;

        db.SamlIdpConfigs.Remove(provider);
        await db.SaveChangesAsync();

        await audit.RecordAsync(orgId, projectId, actorId,
            "saml_provider.deleted", AuditTargetType, providerId.ToString());
        return new NoContentResult();
    }

    /// <summary>Loads a provider with the project the scope check needs. Null when it is not there.</summary>
    public static Task<SamlIdpConfig?> FindAsync(RediensIamDbContext db, Guid projectId, Guid providerId) =>
        db.SamlIdpConfigs.Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Id == providerId && x.ProjectId == projectId);

    /// <summary>
    /// What the audit log calls this, everywhere. It said <c>saml_idp_config</c> on one route and
    /// <c>saml_provider</c> on the other, which made a query filtered by type miss half the events
    /// — see AuditTargetTypeTests.
    /// </summary>
    private const string AuditTargetType = "saml_provider";

    /// <summary>
    /// The empty guid is how a JSON body says "no role" — it is what a form sends for a cleared
    /// select. Stored as-is it matches no role that will ever exist, so provisioning assigns
    /// nothing while the record claims a role is set.
    /// </summary>
    private static Guid? NormaliseRoleId(Guid? roleId) =>
        roleId is null || roleId == Guid.Empty ? null : roleId;
}

/// <summary>
/// One shape for what a caller may say about a SAML provider, on creation and on update alike.
/// Every field is optional: creation refuses what it must through
/// <see cref="SamlProviderOperations.Validate"/>, and an update leaves unmentioned fields alone.
/// </summary>
public record SamlProviderInput(
    string? EntityId = null,
    string? MetadataUrl = null,
    string? SsoUrl = null,
    string? CertificatePem = null,
    string? EmailAttributeName = null,
    string? DisplayNameAttributeName = null,
    bool? JitProvisioning = null,
    Guid? DefaultRoleId = null,
    bool? Active = null);
