namespace RediensIAM.Config;

/// <summary>
/// Lower value = more privileged. <see cref="Filters.RequireManagementLevelAttribute"/> and
/// <see cref="Services.KetoService"/> compare these numerically, so the ordering is the check —
/// renumbering a member, or inserting one out of rank, silently changes who is allowed through.
/// </summary>
public enum ManagementLevel { SuperAdmin = 1, OrgAdmin = 2, ProjectAdmin = 3, None = 99 }

public static class Roles
{
    // ── Management roles (stored in OrgRoles.Role + JWT claims) ───────────────
    public const string SuperAdmin    = "super_admin";
    public const string OrgAdmin      = "org_admin";
    public const string ProjectAdmin  = "project_admin";

    /// <summary>The only role names RediensIAM's own management surface recognises.</summary>
    public static readonly string[] Management = [SuperAdmin, OrgAdmin, ProjectAdmin];

    // ── Tenant (project) role names ───────────────────────────────────────────

    /// <summary>Separates the project scope from the tenant-chosen name in <see cref="ProjectRoleClaim"/>.</summary>
    public const char ProjectRoleSeparator = '/';

    private const int MaxProjectRoleNameLength = 64;

    /// <summary>
    /// Rejects tenant role names that would be indistinguishable from something else once they
    /// reach a token. A tenant admin picks these freely and they are published in
    /// <c>ext.roles</c>, so a name matching a management role would have RediensIAM sign an
    /// assertion of its own platform authority. Returns an error code, or null when acceptable.
    /// </summary>
    public static string? ProjectRoleNameError(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))             return "role_name_required";
        if (name.Length > MaxProjectRoleNameLength)      return "role_name_too_long";
        if (name.Contains(ProjectRoleSeparator))         return "role_name_invalid_character";
        // Case-insensitive: a downstream resource server comparing case-insensitively would
        // otherwise honour "Super_Admin".
        if (Management.Contains(name, StringComparer.OrdinalIgnoreCase)) return "role_name_reserved";
        return null;
    }

    /// <summary>
    /// A tenant role as it appears in <c>ext.roles</c>, qualified by the project that defined it.
    /// Bare names are meaningless across tenants — two tenants both naming a role "admin" would
    /// otherwise be byte-identical in every consumer's <c>ClaimsPrincipal</c>.
    /// </summary>
    public static string ProjectRoleClaim(string projectId, string name) =>
        $"{projectId}{ProjectRoleSeparator}{name}";

    // ── Keto namespaces ───────────────────────────────────────────────────────
    public const string KetoSystemNamespace    = "System";
    public const string KetoOrgsNamespace      = "Organisations";
    public const string KetoProjectsNamespace  = "Projects";
    public const string KetoUserListsNamespace = "UserLists";

    // ── Keto fixed objects ────────────────────────────────────────────────────
    public const string KetoSystemObject = "rediensiam";

    // ── Keto relations ────────────────────────────────────────────────────────
    public const string KetoSuperAdminRelation = "super_admin";
    public const string KetoOrgAdminRelation   = "org_admin";
    public const string KetoManagerRelation    = "manager"; // relation on Projects namespace
    public const string KetoMemberRelation     = "member";  // relation on UserLists namespace

    /// <summary>
    /// Where the admin console is served, and deliberately not <c>admin</c>.
    ///
    /// <para>
    /// The console and the management API shared the <c>/admin</c> prefix, and the API won every
    /// collision: <c>SystemHealthController</c> is mounted on <c>admin/system</c>, which is where
    /// the console's whole System scope lives, so a browser opening any of those thirty pages was
    /// answered with a bare 401 before a single byte of the SPA had loaded. Sharing a namespace
    /// between a human surface and a machine surface makes every new route a chance to take a page
    /// away silently; separating them removes the question.
    /// </para>
    /// </summary>
    public const string ConsoleBasePath = "console";

    // ── Well-known Hydra client IDs ───────────────────────────────────────────
    public const string AdminClientId = "client_admin_system";

    /// <summary>Prefix of the Hydra client registered for a service account (see PatService.AddKeyAsync).</summary>
    public const string ServiceAccountClientPrefix = "sa_";
}
