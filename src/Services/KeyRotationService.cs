using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;

namespace RediensIAM.Services;

/// <param name="Column">Logical name of the encrypted column.</param>
/// <param name="Pending">Rows whose ciphertext is not under the active key id.</param>
public record KeyRotationColumn(string Column, int Pending);

/// <param name="ActiveKeyId">Key id every new ciphertext is written under.</param>
/// <param name="ConfiguredKeyIds">Every key id that can currently decrypt, active first.</param>
/// <param name="Columns">Per-column pending counts.</param>
/// <param name="TotalPending">
/// Sum of <see cref="Columns"/>. <b>Zero is the signal that a retired key can be dropped from
/// Security:EncryptionKeys</b> as far as <i>ciphertexts</i> go — nothing else is.
///
/// It says nothing about the audit hash chain, which has no sweep and can have none: its links
/// were computed when the rows were written, and recomputing them under a new key would mean the
/// application rewriting every row of an append-only table. Dropping a root therefore leaves every
/// audit row written under it permanently unverifiable — not broken, but nothing the deployment
/// can vouch for. Keep retired roots configured, or accept that boundary knowingly; see
/// <see cref="Data.AuditChain"/>.
/// </param>
public record KeyRotationStatus(
    int ActiveKeyId,
    IReadOnlyList<int> ConfiguredKeyIds,
    IReadOnlyList<KeyRotationColumn> Columns,
    int TotalPending);

/// <summary>
/// Re-encrypts every stored ciphertext under the active root key.
///
/// Why an explicit sweep and not lazy-on-write alone: encryption is already lazy — every write
/// goes out under the active key — but a TOTP secret is written once at enrolment and then only
/// read, so lazy migration never touches a cold row. Under lazy-only the old key can never be
/// retired, which is precisely the failure S-10 is about. The sweep is what turns "we added a
/// key" into "we can delete the old one".
///
/// Why operator-triggered rather than a startup/background job: the sweep must run when *every*
/// replica already has both keys loaded, it must be observable, and it must not race N replicas
/// rewriting the same rows on rollout. An admin endpoint reuses the existing authn/authz/audit
/// path; a CLI would need a second entrypoint and its own DB credentials.
/// </summary>
public class KeyRotationService(RediensIamDbContext db, AppConfig appConfig, ILogger<KeyRotationService> logger)
{
    // ponytail: fixed batch, no resume cursor. Each batch is committed before the next is read,
    // so an interrupted sweep is simply re-run — it re-selects whatever is still pending.
    private const int BatchSize = 500;

    public async Task<KeyRotationStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var active = appConfig.ActiveEncryptionKeyId;
        var columns = new List<KeyRotationColumn>
        {
            new("User.TotpSecret",           await CountPendingAsync(CandidateTotp(), u => u.TotpSecret, ct)),
            new("Webhook.SecretEnc",         await CountPendingAsync(CandidateWebhooks(), w => w.SecretEnc, ct)),
            new("OrgSmtpConfig.PasswordEnc", await CountPendingAsync(CandidateSmtp(), c => c.PasswordEnc, ct)),
            new("Project.LoginTheme",        (await PendingProjectsAsync(ct)).Count),
        };
        return new KeyRotationStatus(
            active, appConfig.ConfiguredEncryptionKeyIds, columns, columns.Sum(c => c.Pending));
    }

    /// <summary>
    /// Counts rows genuinely under a non-active key. Only the ciphertext column is pulled back,
    /// and the candidate set is empty once the sweep has completed — so this is a cheap query
    /// outside a rotation window and bounded by the pending set inside one.
    /// </summary>
    private async Task<int> CountPendingAsync<T>(
        IQueryable<T> candidates, System.Linq.Expressions.Expression<Func<T, string?>> column, CancellationToken ct)
    {
        var active = appConfig.ActiveEncryptionKeyId;
        var values = await candidates.Select(column).ToListAsync(ct);
        return values.Count(v => NeedsReEncryption(v, active));
    }

    /// <summary>
    /// Re-encrypts every pending row under the active key and returns the resulting status.
    /// Idempotent: rows already on the active key are not touched. Safe to re-run after an
    /// interruption. Decryption of any row still requires its original key to be configured —
    /// if it is not, the sweep fails loudly rather than dropping the value.
    /// </summary>
    public async Task<KeyRotationStatus> ReEncryptAsync(CancellationToken ct = default)
    {
        var rewritten = 0;
        rewritten += await SweepTotpAsync(ct);
        rewritten += await SweepWebhooksAsync(ct);
        rewritten += await SweepSmtpAsync(ct);
        rewritten += await SweepProjectsAsync(ct);

        var status = await GetStatusAsync(ct);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Key rotation sweep re-encrypted {Rewritten} value(s) under key id {ActiveKeyId}; {Pending} still pending",
                rewritten, status.ActiveKeyId, status.TotalPending);
        return status;
    }

    // ── Candidate selection ───────────────────────────────────────────────────
    // The SQL predicate is *exact*, not a superset, and that matters: the sweep pages with
    // Take(BatchSize) and stops on an empty page, so a row the database returns but the
    // application then filters out would stall the sweep short of the end.
    //
    // Exactness comes from one property of the envelope: the Base64 body can never contain ':',
    // so a stored value contains ':' if and only if it carries a key-id prefix.
    //
    //   active key id > 1 → pending ⇔ NOT LIKE 'k{active}:%'
    //   active key id = 1 → pending ⇔ LIKE '%:%' AND NOT LIKE 'k1:%'
    //                                 (i.e. carries some prefix that is not key 1; a prefix-less
    //                                  value is key 1 by definition and is already active)
    //
    // The key-1 case only has rows in it after a rollback to key 1 while key-2 ciphertexts
    // exist. It must still be reported, which is why it is not simply "nothing to do".
    //
    // "carries a prefix" is written as EF.Functions.Like rather than Contains, for two reasons:
    // it is the LIKE the comment above already describes, and it is translatable. The provider
    // has no translation for string.Contains(char) — the overload CA1847 asks for — so writing
    // this the way that rule wants turns the whole sweep into a runtime translation failure.
    private const string CarriesAnyPrefix = "%:%";

    private static bool NeedsReEncryption(string? value, int activeKeyId) =>
        !string.IsNullOrEmpty(value) && TotpEncryption.KeyIdOf(value) != activeKeyId;

    private IQueryable<Data.Entities.User> CandidateTotp()
    {
        var q = db.Users.Where(u => u.TotpSecret != null && u.TotpSecret != "");
        var active = appConfig.ActiveEncryptionKeyId;
        var prefix = ActivePrefix(active);
        return active == TotpEncryption.LegacyKeyId
            ? q.Where(u => EF.Functions.Like(u.TotpSecret!, CarriesAnyPrefix) && !u.TotpSecret!.StartsWith(prefix))
            : q.Where(u => !u.TotpSecret!.StartsWith(prefix));
    }

    private IQueryable<Data.Entities.Webhook> CandidateWebhooks()
    {
        var q = db.Webhooks.Where(w => w.SecretEnc != "");
        var active = appConfig.ActiveEncryptionKeyId;
        var prefix = ActivePrefix(active);
        return active == TotpEncryption.LegacyKeyId
            ? q.Where(w => EF.Functions.Like(w.SecretEnc, CarriesAnyPrefix) && !w.SecretEnc.StartsWith(prefix))
            : q.Where(w => !w.SecretEnc.StartsWith(prefix));
    }

    private IQueryable<Data.Entities.OrgSmtpConfig> CandidateSmtp()
    {
        var q = db.OrgSmtpConfigs.Where(c => c.PasswordEnc != null && c.PasswordEnc != "");
        var active = appConfig.ActiveEncryptionKeyId;
        var prefix = ActivePrefix(active);
        return active == TotpEncryption.LegacyKeyId
            ? q.Where(c => EF.Functions.Like(c.PasswordEnc!, CarriesAnyPrefix) && !c.PasswordEnc!.StartsWith(prefix))
            : q.Where(c => !c.PasswordEnc!.StartsWith(prefix));
    }

    private static string ActivePrefix(int activeKeyId) => $"k{activeKeyId}:";

    /// <summary>
    /// Login themes live in a jsonb column, so the provider secrets inside them cannot be
    /// filtered in SQL. Projects are a small table (one row per tenant application); loading
    /// them all is correct and cheap, and this is the only place where that is true.
    /// </summary>
    private async Task<List<Data.Entities.Project>> PendingProjectsAsync(CancellationToken ct)
    {
        var active = appConfig.ActiveEncryptionKeyId;
        var projects = await db.Projects.ToListAsync(ct);
        return [.. projects.Where(p => TotpEncryption.ProviderSecretKeyIds(p.LoginTheme).Any(id => id != active))];
    }

    // ── Sweeps ────────────────────────────────────────────────────────────────

    private async Task<int> SweepTotpAsync(CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var batch = await CandidateTotp().Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            foreach (var u in batch)
                u.TotpSecret = TotpEncryption.Encrypt(
                    appConfig.TotpEncKey, TotpEncryption.Decrypt(appConfig.TotpEncKey, u.TotpSecret!));
            await db.SaveChangesAsync(ct);
            total += batch.Count;
        }
    }

    private async Task<int> SweepWebhooksAsync(CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var batch = await CandidateWebhooks().Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            foreach (var w in batch)
                w.SecretEnc = TotpEncryption.EncryptString(
                    appConfig.WebhookEncKey, TotpEncryption.DecryptString(appConfig.WebhookEncKey, w.SecretEnc));
            await db.SaveChangesAsync(ct);
            total += batch.Count;
        }
    }

    private async Task<int> SweepSmtpAsync(CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var batch = await CandidateSmtp().Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            foreach (var c in batch)
                c.PasswordEnc = TotpEncryption.EncryptString(
                    appConfig.SmtpEncKey, TotpEncryption.DecryptString(appConfig.SmtpEncKey, c.PasswordEnc!));
            await db.SaveChangesAsync(ct);
            total += batch.Count;
        }
    }

    private async Task<int> SweepProjectsAsync(CancellationToken ct)
    {
        var pending = await PendingProjectsAsync(ct);
        foreach (var p in pending)
            p.LoginTheme = TotpEncryption.ReEncryptProviderSecrets(p.LoginTheme, appConfig.ThemeEncKey);
        if (pending.Count > 0) await db.SaveChangesAsync(ct);
        return pending.Count;
    }
}
