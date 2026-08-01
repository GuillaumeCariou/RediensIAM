using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using RediensIAM.Config;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Data;

/// <param name="options">Provider and interceptor configuration, as for any EF Core context.</param>
/// <param name="appConfig">
/// Source of the audit-chain HMAC key. Optional so the design-time factory and the tests that new
/// a context up for model inspection keep compiling; a context without it may not write an audit
/// row — see <see cref="ChainKey"/>. Resolved from DI on every context the application itself
/// builds.
/// </param>
public class RediensIamDbContext(DbContextOptions<RediensIamDbContext> options, AppConfig? appConfig = null)
    : DbContext(options)
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
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<SamlIdpConfig> SamlIdpConfigs => Set<SamlIdpConfig>();
    public DbSet<Instance> Instances => Set<Instance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RediensIamDbContext).Assembly);
    }

    // ── S-3: audit as a property of persistence, not of remembering ───────────
    //
    // Audit coverage was ~98 hand-written RecordAsync call sites, and T-N2 was seven
    // security-relevant mutations that had none — closed at the time by adding more call sites,
    // which is the defect rather than the fix. Everything below sits on the one path every
    // mutation already takes, so an endpoint written next year that changes a credential is
    // audited without its author doing anything, and cannot ship unaudited by omission.
    //
    // The hand-written calls stay: they carry intent ("user.password.reset") that a change tracker
    // cannot infer. These rows are the floor beneath them, not a replacement, and are named
    // "entity.*" so a query can tell the two apart.

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        RejectAuditLogTampering();
        await RecordUnloggedSecurityMutationsAsync(cancellationToken);

        var pending = PendingAuditRows();
        if (pending.Count == 0) return await base.SaveChangesAsync(cancellationToken);

        var owned = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await ChainAsync(pending, cancellationToken);
            var written = await base.SaveChangesAsync(cancellationToken);
            if (owned is not null) await owned.CommitAsync(cancellationToken);
            return written;
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    /// <summary>
    /// Only <c>InstanceConfiguration</c> saves synchronously, at startup, before the host serves
    /// anything. Blocking there is safe and keeps one implementation of the chain.
    /// </summary>
    public override int SaveChanges() => SaveChangesAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Application-layer append-only. An audit row may be inserted and never edited or removed —
    /// the one sanctioned deletion is the retention sweep, which uses <c>ExecuteDeleteAsync</c>
    /// and so never enters the change tracker. Database-side enforcement (a rule, or no
    /// UPDATE/DELETE grant to the app role) is the other half and is not this layer's to give.
    /// </summary>
    private void RejectAuditLogTampering()
    {
        var tampered = ChangeTracker.Entries<AuditLog>()
            .FirstOrDefault(e => e.State is EntityState.Modified or EntityState.Deleted);
        if (tampered != null)
            throw new InvalidOperationException(
                $"The audit log is append-only: attempted to {tampered.State.ToString().ToLowerInvariant()} " +
                $"audit row {tampered.Entity.Id}.");
    }

    private List<AuditLog> PendingAuditRows()
        => [.. ChangeTracker.Entries<AuditLog>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)];

    /// <summary>
    /// Links each new row to the last row of its organisation's chain.
    ///
    /// The advisory lock is what keeps the chain a chain: two requests auditing the same
    /// organisation concurrently would otherwise both read the same predecessor and both claim it,
    /// which verification cannot tell apart from a forgery. Groups are locked in a fixed key order
    /// so two transactions touching the same pair of organisations cannot deadlock.
    ///
    /// ponytail: one lock per organisation, held to the end of the caller's transaction, so audit
    /// writes for a single org serialise. If that ever shows up as contention the fix is to move
    /// the audit insert onto its own short transaction, not to widen the lock.
    /// </summary>
    private async Task ChainAsync(List<AuditLog> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            if (row.CreatedAt == default) row.CreatedAt = DateTimeOffset.UtcNow;
            row.CreatedAt = AuditChain.Normalise(row.CreatedAt);
        }

        var groups = rows.GroupBy(r => r.OrgId)
            .Select(g => (Key: ChainLockKey(g.Key), g.Key, Rows: g.ToList()))
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var (key, orgId, group) in groups)
        {
            await Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [key], cancellationToken);

            var prev = await AuditLogs.AsNoTracking()
                .Where(a => a.OrgId == orgId)
                .OrderByDescending(a => a.Id)
                .Select(a => a.Hash)
                .FirstOrDefaultAsync(cancellationToken);

            foreach (var row in group)
            {
                row.PrevHash = string.IsNullOrEmpty(prev) ? null : prev;
                row.Hash = AuditChain.Compute(ChainKey, row, row.PrevHash);
                prev = row.Hash;
            }
        }
    }

    /// <summary>
    /// The key the chain links are computed under.
    ///
    /// <para>
    /// Throwing beats writing an unkeyed row. An audit row whose hash cannot be forged is the
    /// entire point; a context that quietly fell back to a bare digest would produce rows that
    /// verify against nothing and look exactly like the pre-migration ones. The only contexts
    /// without an <see cref="AppConfig"/> are the design-time migration factory and model-only
    /// tests, and neither writes audit rows.
    /// </para>
    /// </summary>
    private KeyRing ChainKey => (appConfig ?? throw new InvalidOperationException(
        "This DbContext was constructed without an AppConfig, so the audit hash chain has no key " +
        "and the row cannot be written. Resolve the context from DI, or pass an AppConfig."))
        .AuditChainKey;

    private static long ChainLockKey(Guid? orgId)
        => orgId is { } id ? BitConverter.ToInt64(id.ToByteArray(), 0) : 0L;

    // ── The mutations nobody remembered to log (T-N2) ─────────────────────────

    /// <summary>
    /// Columns whose change is a change of authentication material or of account standing. A
    /// mutation of any of them earns a row on its own, whatever endpoint made it.
    /// </summary>
    private static readonly string[] CredentialProperties =
    [
        nameof(User.PasswordHash), nameof(User.TotpSecret), nameof(User.TotpEnabled),
        nameof(User.WebAuthnEnabled), nameof(User.Phone), nameof(User.PhoneVerified),
        nameof(User.Email), nameof(User.EmailVerified), nameof(User.Active),
    ];

    private async Task RecordUnloggedSecurityMutationsAsync(CancellationToken cancellationToken)
    {
        var actorId = CurrentActorId();
        var rows = new List<(AuditLog Row, Guid? SubjectUserId)>();

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            (AuditLog? Row, Guid? Subject) described = entry.Entity switch
            {
                User user when entry.State == EntityState.Modified
                    => (CredentialChange(entry, user, actorId), user.Id),
                BackupCode code           => (Describe(entry, "backup_codes", code.UserId.ToString(), actorId), code.UserId),
                WebAuthnCredential cred   => (Describe(entry, "webauthn_credentials", cred.UserId.ToString(), actorId), cred.UserId),
                UserSocialAccount social  => (Describe(entry, "user_social_accounts", social.UserId.ToString(), actorId), social.UserId),
                SamlIdpConfig saml        => (Describe(entry, "saml_idp_configs", saml.Id.ToString(), actorId), null),
                Instance instance         => (Describe(entry, "instances", instance.Id, actorId), null),
                _ => (null, null),
            };
            if (described.Row is not null) rows.Add((described.Row, described.Subject));
        }

        if (rows.Count == 0) return;

        // Without an org these rows land on the deployment-wide chain and are invisible to
        // /org/audit-log — which would leave the tenant whose user was taken over unable to see
        // it, and that is the whole reason T-N2 mattered. One lookup, on a path that only runs
        // when a credential actually changed.
        var subjects = rows.Select(r => r.SubjectUserId).OfType<Guid>().Distinct().ToList();
        var orgByUser = subjects.Count == 0
            ? []
            : await Users.AsNoTracking()
                .Where(u => subjects.Contains(u.Id))
                .Select(u => new { u.Id, u.UserList.OrgId })
                .ToDictionaryAsync(x => x.Id, x => x.OrgId, cancellationToken);

        foreach (var (row, subject) in rows)
        {
            if (subject is { } userId && orgByUser.TryGetValue(userId, out var orgId)) row.OrgId = orgId;
            AuditLogs.Add(row);
        }
    }

    private static AuditLog? CredentialChange(EntityEntry entry, User user, Guid? actorId)
    {
        var changed = CredentialProperties.Where(p => entry.Property(p).IsModified).ToList();
        if (changed.Count == 0) return null;

        return new AuditLog
        {
            UserId     = user.Id,
            ActorId    = actorId,
            Action     = "entity.users.credential_changed",
            TargetType = "user",
            TargetId   = user.Id.ToString(),
            Metadata   = new Dictionary<string, object> { ["properties"] = string.Join(",", changed) },
            CreatedAt  = DateTimeOffset.UtcNow,
        };
    }

    private static AuditLog? Describe(EntityEntry entry, string table, string targetId, Guid? actorId)
    {
        var verb = entry.State switch
        {
            EntityState.Added    => "inserted",
            EntityState.Modified => "updated",
            EntityState.Deleted  => "deleted",
            _ => null,
        };
        if (verb is null) return null;

        return new AuditLog
        {
            ActorId    = actorId,
            Action     = $"entity.{table}.{verb}",
            TargetType = table,
            TargetId   = targetId,
            CreatedAt  = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// The acting identity, when there is a request. A background service or a startup path has
    /// none, and a null actor is the honest answer there — better than attributing the change to
    /// whoever happens to be in scope.
    ///
    /// Read through a fresh accessor because <c>HttpContextAccessor</c>'s backing store is a
    /// static <c>AsyncLocal</c>; a DbContext is built from options alone and has no route to
    /// application services.
    /// </summary>
    private static Guid? CurrentActorId()
    {
        var claims = new HttpContextAccessor().HttpContext?.Items["Claims"] as Models.TokenClaims;
        return claims?.ParsedUserId is { } id && id != Guid.Empty ? id : null;
    }
}
