namespace RediensIAM.Config;

public static class Roles
{
    // ── Management roles (stored in OrgRoles.Role + JWT claims) ───────────────
    public const string SuperAdmin     = "super_admin";
    public const string OrgAdmin       = "org_admin";
    public const string ProjectManager = "project_manager";

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
}
