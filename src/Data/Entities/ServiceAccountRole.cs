namespace RediensIAM.Data.Entities;

/// <summary>
/// A management role assigned to a service account.
/// Mirrors OrgRole for human users — the same three scopes apply:
///   super_admin  → OrgId null,  ProjectId null
///   org_admin    → OrgId set,   ProjectId null
///   project_admin→ OrgId set,   ProjectId set
/// </summary>
public class ServiceAccountRole
{
    public Guid Id { get; set; }
    public Guid ServiceAccountId { get; set; }
    public string Role { get; set; } = string.Empty;
    public Guid? OrgId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }

    public ServiceAccount ServiceAccount { get; set; } = null!;
}
