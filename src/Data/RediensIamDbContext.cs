using Microsoft.EntityFrameworkCore;
using RediensIAM.Data.Entities;

namespace RediensIAM.Data;

public class RediensIamDbContext(DbContextOptions<RediensIamDbContext> options) : DbContext(options)
{
    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<UserList> UserLists => Set<UserList>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
    public DbSet<ServiceAccountRole> ServiceAccountRoles => Set<ServiceAccountRole>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserProjectRole> UserProjectRoles => Set<UserProjectRole>();
    public DbSet<OrgRole> OrgRoles => Set<OrgRole>();
    public DbSet<WebAuthnCredential> WebAuthnCredentials => Set<WebAuthnCredential>();
    public DbSet<BackupCode> BackupCodes => Set<BackupCode>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<UserSocialAccount> UserSocialAccounts => Set<UserSocialAccount>();
    public DbSet<OrgSmtpConfig> OrgSmtpConfigs => Set<OrgSmtpConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RediensIamDbContext).Assembly);
    }
}
