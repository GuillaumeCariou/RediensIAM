using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// Everything a caller may change about a project, and the one code path that applies it.
///
/// <para>
/// Three routes write this object — <c>PATCH /org/projects/{id}</c>,
/// <c>PATCH /admin/projects/{id}</c> (aliased as <c>/api/manage</c>) and
/// <c>PATCH /project/info</c> — and each of them had grown its own request record and its own
/// copy of the field loop. They diverged exactly as three copies do, and the divergence was
/// invisible from any one of them:
/// </para>
///
/// <list type="bullet">
/// <item>the password policy and <c>allowed_scopes</c> existed only on <c>/project/info</c>, so
/// neither an organisation admin nor a super-admin could set them from the route their console
/// screen used;</item>
/// <item><c>email_from_name</c> was missing from the system-scope record, so that route answered
/// 200 and applied nothing — System.Text.Json drops what a record does not declare, which is the
/// same failure PlatformRegressionTests already documents for the theme fields;</item>
/// <item>and the redirect URIs, which is where this came to light: they carry the origins CSP and
/// CORS are derived from, and which of the three routes an operator happened to use decided
/// whether the change took.</item>
/// </list>
///
/// <para>
/// A route still decides who may call it and which project it may find. That is genuinely
/// per-scope. What a project <i>is</i> is not, so it is stated once.
/// </para>
/// </summary>
public record ProjectUpdateRequest(
    string? Name = null,
    bool? Active = null,
    bool? RequireRoleToLogin = null,
    bool? RequireMfa = null,
    bool? AllowSelfRegistration = null,
    bool? EmailVerificationEnabled = null,
    bool? SmsVerificationEnabled = null,
    string[]? AllowedEmailDomains = null,
    string[]? AllowedScopes = null,
    Guid? DefaultRoleId = null,
    bool? ClearDefaultRole = null,
    Dictionary<string, object>? LoginTheme = null,
    int? MinPasswordLength = null,
    bool? PasswordRequireUppercase = null,
    bool? PasswordRequireLowercase = null,
    bool? PasswordRequireDigit = null,
    bool? PasswordRequireSpecial = null,
    bool? CheckBreachedPasswords = null,
    string? EmailFromName = null,
    bool? ClearEmailFromName = null,
    string[]? IpAllowlist = null,
    // Registered in Hydra rather than stored here. Null is "not mentioned", not "empty it" — a
    // project whose redirect_uris became [] because someone renamed it is a project nobody can
    // log into.
    string[]? RedirectUris = null,
    string[]? PostLogoutRedirectUris = null,
    // Acknowledges the 409 from MfaDowngradeGuard. Only read when require_mfa goes true → false.
    bool? ConfirmMfaDowngrade = null);

public static class ProjectUpdate
{
    /// <summary>
    /// Applies <paramref name="body"/> to <paramref name="project"/>, or returns the error result
    /// that refuses it. Does not save: the caller owns the transaction and its audit entry.
    ///
    /// <para>
    /// Order is deliberate and is the order the three routes between them used to imply: refuse
    /// before writing. The IP allowlist, the MFA downgrade and the theme can each reject the whole
    /// request, so they run before anything is copied onto the entity — a half-applied PATCH that
    /// then returns 400 is worse than either outcome.
    /// </para>
    /// </summary>
    public static async Task<IActionResult?> ApplyAsync(
        RediensIamDbContext db,
        HydraService hydra,
        AuditLogService audit,
        RediensIAM.Config.AppConfig appConfig,
        Guid actorId,
        Project project,
        ProjectUpdateRequest body)
    {
        if (ValidateIpAllowlist(body.IpAllowlist) is { } allowlistErr) return allowlistErr;
        if (await MfaDowngradeGuard.CheckAsync(db, audit, actorId, project, body.RequireMfa, body.ConfirmMfaDowngrade) is { } mfaErr)
            return mfaErr;
        if (LoginThemeValidator.Validate(body.LoginTheme) is { } themeErr)
            return new BadRequestObjectResult(new { error = themeErr });

        if (await ApplyDefaultRoleAsync(db, project, body) is { } roleErr) return roleErr;

        ApplyPlainFields(project, body);
        ApplyPasswordPolicy(project, body);
        ApplyEmailFromName(project, body);
        if (body.IpAllowlist != null) project.IpAllowlist = body.IpAllowlist;
        if (body.LoginTheme != null)
            project.LoginTheme = TotpEncryption.EncryptProviderSecretsInTheme(body.LoginTheme, project.LoginTheme, appConfig.ThemeEncKey)!;

        if (await ApplyRedirectUrisAsync(hydra, project, body.RedirectUris, body.PostLogoutRedirectUris) is { } uriErr)
            return uriErr;

        project.UpdatedAt = DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>
    /// Persiste la mise à jour et la journalise.
    ///
    /// <para>
    /// La ligne d'audit vivait chez un seul des trois appelants d'<see cref="ApplyAsync"/> :
    /// <c>PATCH /project/info</c> l'écrivait, <c>PATCH /org/projects/{id}</c> et
    /// <c>PATCH /admin/projects/{id}</c> non. Ces deux-là modifiaient le nom, la politique de mot
    /// de passe, les <c>redirect_uris</c> et l'allowlist IP sans rien laisser au journal — et elles
    /// sont atteignables au jeton personnel comme par <c>/api/manage</c>. <c>project.updated</c>
    /// est de surcroît un événement webhook souscriptible
    /// (<see cref="Services.WebhookService"/>) : un locataire qui surveille ses projets n'entendait
    /// jamais parler des changements passés par ces routes.
    /// </para>
    ///
    /// <para>
    /// L'ordre compte et c'est pourquoi ceci n'est pas dans <see cref="ApplyAsync"/> :
    /// <see cref="Services.AuditLogService.RecordAsync"/> écrit dans son propre
    /// <c>DbContext</c>, donc journaliser avant la sauvegarde consignerait un changement qui peut
    /// encore échouer.
    /// </para>
    /// </summary>
    public static async Task SaveAndAuditAsync(
        RediensIamDbContext db, AuditLogService audit, Guid actorId, Project project)
    {
        await db.SaveChangesAsync();
        await audit.RecordAsync(project.OrgId, project.Id, actorId, "project.updated",
            "project", project.Id.ToString());
    }

    /// <summary>
    /// Writes a project's redirect URIs, which live in Hydra rather than in this database, and the
    /// allowed origins derived from them. Public because the create paths need the same derivation.
    /// </summary>
    public static async Task<IActionResult?> ApplyRedirectUrisAsync(
        HydraService hydra, Project project, string[]? redirectUris, string[]? postLogoutUris)
    {
        if (redirectUris == null && postLogoutUris == null) return null;
        if (project.HydraClientId is not { } clientId)
            return new BadRequestObjectResult(new { error = "project_has_no_client" });

        var (currentRedirects, currentPostLogout) = await hydra.GetClientRedirectUrisAsync(clientId);
        await hydra.UpdateClientRedirectUrisAsync(
            clientId, redirectUris ?? currentRedirects, postLogoutUris ?? currentPostLogout);
        return null;
    }

    /// <summary>
    /// Fills the project's non-persisted URI fields from Hydra, so every read of a project returns
    /// what its own write accepts. A read that returns less than that is a data-loss bug: the
    /// console round-trips what it reads, so a field it cannot see is a field Save erases.
    /// </summary>
    public static async Task<Project> WithRedirectUrisAsync(HydraService hydra, Project project)
    {
        if (project.HydraClientId is not { } clientId) return project;
        (project.RedirectUris, project.PostLogoutRedirectUris) = await hydra.GetClientRedirectUrisAsync(clientId);
        return project;
    }

    private static BadRequestObjectResult? ValidateIpAllowlist(string[]? allowlist)
    {
        if (allowlist == null) return null;
        // An unparseable CIDR silently matches nothing in IpInRange, which locks the whole tenant
        // out of its own project instead of reporting the typo.
        var invalid = allowlist.Where(entry => !ProjectController.IsValidCidr(entry)).ToArray();
        return invalid.Length > 0
            ? new BadRequestObjectResult(new { error = "invalid_ip_allowlist", invalid })
            : null;
    }

    private static async Task<IActionResult?> ApplyDefaultRoleAsync(
        RediensIamDbContext db, Project project, ProjectUpdateRequest body)
    {
        if (body.ClearDefaultRole == true) { project.DefaultRoleId = null; return null; }
        if (!body.DefaultRoleId.HasValue) return null;

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == body.DefaultRoleId && r.ProjectId == project.Id);
        if (role == null) return new BadRequestObjectResult(new { error = "invalid_default_role" });
        project.DefaultRoleId = body.DefaultRoleId;
        return null;
    }

    private static void ApplyPlainFields(Project project, ProjectUpdateRequest body)
    {
        if (body.Name != null)                      project.Name                     = body.Name;
        if (body.Active.HasValue)                   project.Active                   = body.Active.Value;
        if (body.RequireRoleToLogin.HasValue)       project.RequireRoleToLogin       = body.RequireRoleToLogin.Value;
        if (body.RequireMfa.HasValue)               project.RequireMfa               = body.RequireMfa.Value;
        if (body.AllowSelfRegistration.HasValue)    project.AllowSelfRegistration    = body.AllowSelfRegistration.Value;
        if (body.EmailVerificationEnabled.HasValue) project.EmailVerificationEnabled = body.EmailVerificationEnabled.Value;
        if (body.SmsVerificationEnabled.HasValue)   project.SmsVerificationEnabled   = body.SmsVerificationEnabled.Value;
        if (body.AllowedEmailDomains != null)       project.AllowedEmailDomains      = body.AllowedEmailDomains;
        if (body.AllowedScopes != null)             project.AllowedScopes            = body.AllowedScopes;
    }

    private static void ApplyPasswordPolicy(Project project, ProjectUpdateRequest body)
    {
        if (body.MinPasswordLength.HasValue)        project.MinPasswordLength        = Math.Max(0, body.MinPasswordLength.Value);
        if (body.PasswordRequireUppercase.HasValue) project.PasswordRequireUppercase = body.PasswordRequireUppercase.Value;
        if (body.PasswordRequireLowercase.HasValue) project.PasswordRequireLowercase = body.PasswordRequireLowercase.Value;
        if (body.PasswordRequireDigit.HasValue)     project.PasswordRequireDigit     = body.PasswordRequireDigit.Value;
        if (body.PasswordRequireSpecial.HasValue)   project.PasswordRequireSpecial   = body.PasswordRequireSpecial.Value;
        if (body.CheckBreachedPasswords.HasValue)   project.CheckBreachedPasswords   = body.CheckBreachedPasswords.Value;
    }

    private static void ApplyEmailFromName(Project project, ProjectUpdateRequest body)
    {
        if (body.ClearEmailFromName == true) project.EmailFromName = null;
        else if (body.EmailFromName != null) project.EmailFromName = body.EmailFromName;
    }
}
