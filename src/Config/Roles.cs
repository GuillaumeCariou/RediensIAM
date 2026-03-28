namespace RediensIAM.Config;

// Lower value = more privileged. Used by RequireManagementLevelAttribute and KetoService.
public enum ManagementLevel { SuperAdmin = 1, OrgAdmin = 2, ProjectAdmin = 3, None = 99 }

public static class Roles
{
    // ── Management roles (stored in OrgRoles.Role + JWT claims) ───────────────
    public const string SuperAdmin    = "super_admin";
    public const string OrgAdmin      = "org_admin";
    public const string ProjectAdmin  = "project_admin";

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

    // ── Well-known Hydra client IDs ───────────────────────────────────────────
    public const string AdminClientId = "client_admin_system";
}
