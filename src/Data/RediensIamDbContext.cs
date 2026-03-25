using Microsoft.EntityFrameworkCore;
using RediensIAM.Entities;

namespace RediensIAM.Data;

public class RediensIamDbContext(DbContextOptions<RediensIamDbContext> options) : DbContext(options)
{
    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<UserList> UserLists => Set<UserList>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserProjectRole> UserProjectRoles => Set<UserProjectRole>();
    public DbSet<OrgRole> OrgRoles => Set<OrgRole>();
    public DbSet<ServiceAccountProjectRole> ServiceAccountProjectRoles => Set<ServiceAccountProjectRole>();
    public DbSet<ServiceAccountOrgRole> ServiceAccountOrgRoles => Set<ServiceAccountOrgRole>();
    public DbSet<WebAuthnCredential> WebAuthnCredentials => Set<WebAuthnCredential>();
    public DbSet<BackupCode> BackupCodes => Set<BackupCode>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<UserSocialAccount> UserSocialAccounts => Set<UserSocialAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RediensIamDbContext).Assembly);
    }
}
