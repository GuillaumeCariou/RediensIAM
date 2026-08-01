using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RediensIAM.Data.Entities;
using RediensIAM.Services;

namespace RediensIAM.Data;

/// <summary>
/// What one organisation's chain can be said to be, after walking it.
/// </summary>
/// <param name="FirstBreak">
/// Id of the first row whose link does not hold, or null when every link that <i>could</i> be
/// checked held. Null is not "the log is authentic" — read it together with
/// <paramref name="Unverifiable"/>.
/// </param>
/// <param name="Verified">
/// Rows whose HMAC was recomputed under a configured key and matched. These are the rows the
/// deployment can actually vouch for.
/// </param>
/// <param name="Unverifiable">
/// Rows this deployment cannot speak to: written before the chain existed (empty hash), written
/// before the chain was keyed (unkeyed SHA-256, forgeable by anyone with database write access),
/// or written under a key id that is no longer configured. Not evidence of tampering — evidence
/// that there is no evidence.
/// </param>
public sealed record AuditChainStatus(long? FirstBreak, int Verified, int Unverifiable)
{
    /// <summary>Every link that could be checked held.</summary>
    public bool Intact => FirstBreak is null;

    /// <summary>Every surviving row was recomputed under a key this deployment holds.</summary>
    public bool FullyVerified => FirstBreak is null && Unverifiable == 0;
}

/// <summary>
/// Keyed hash chain over <see cref="AuditLog"/> rows (S-3, tamper-evidence half).
///
/// <para>
/// Each row carries the hash of the row before it in its organisation's chain, so the log is a
/// linked list whose links are content-addressed. Editing a row changes its hash and orphans every
/// row after it; deleting a row leaves its successor pointing at a hash no surviving row has.
/// Neither is preventable from inside the application — the app holds the credentials that could
/// do it — but both become <i>detectable</i>, which is the property an audit log has to have to be
/// worth keeping.
/// </para>
///
/// <para>
/// <b>The link is an HMAC, not a bare digest.</b> Under plain SHA-256 the chain proved ordering
/// and nothing else: every input to the hash is a column of the row being hashed, so anyone who
/// could write to <c>audit_log</c> could rewrite a row, recompute its hash, re-chain every row
/// after it, and hand back a table that verified clean. The key lives in the application's
/// environment (HKDF-derived from the deployment root, <see cref="Config.AppConfig.AuditChainKey"/>),
/// not in the database, so the same attacker can still <i>delete</i> rows — that is what the chain
/// makes visible — but can no longer produce a chain that verifies.
/// </para>
///
/// <para>
/// <b>Hash format is the version marker.</b> A keyed hash is <c>k{keyId}:{64 hex}</c>; a hash
/// written before keying is 64 hex characters with no prefix; a row written before the chain
/// existed has an empty hash. Hex contains no ':', so the three are distinguishable by shape and
/// no migration of existing rows is needed — see <see cref="Verify"/> for what each one is worth.
/// Note the deliberate difference from <see cref="TotpEncryption"/>'s envelope, where a missing
/// prefix means key id 1: here a missing prefix means <i>unkeyed</i>, and treating it as key 1
/// would re-admit exactly the forgery this exists to stop.
/// </para>
///
/// <para>
/// Chains are per organisation because retention purges are per organisation
/// (<c>AuditLogRetentionService</c>). A purge removes an org's oldest rows, which shortens that
/// org's chain from the front and leaves the rest verifiable; a single global chain would be left
/// with holes scattered through it and would be indistinguishable from tampering.
/// </para>
/// </summary>
public static class AuditChain
{
    private const char FieldSeparator = '\u001e';
    private const char PairSeparator  = '\u001f';

    /// <summary>Longest hash this can emit — <c>k{keyId}:</c> plus 64 hex characters.</summary>
    public const int MaxHashLength = 80;

    /// <summary>
    /// Postgres <c>timestamptz</c> keeps microseconds; .NET keeps 100ns ticks. Without truncating
    /// first, a row hashed before the insert and re-hashed after a read would disagree on the
    /// timestamp alone.
    /// </summary>
    public static DateTimeOffset Normalise(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    /// <summary>
    /// Hash of <paramref name="row"/> linked to <paramref name="prevHash"/>, under the ring's
    /// <b>active</b> key. Rotation therefore takes effect on the next row written and leaves every
    /// earlier row verifiable under the key named in its own envelope.
    /// </summary>
    public static string Compute(KeyRing ring, AuditLog row, string? prevHash)
    {
        ArgumentNullException.ThrowIfNull(ring);
        return Envelope(ring.ActiveId, Mac(ring.ActiveKey, Canonical(row, prevHash)));
    }

    private static string Envelope(int keyId, byte[] mac) =>
        $"k{keyId.ToString(CultureInfo.InvariantCulture)}:{Convert.ToHexStringLower(mac)}";

    private static byte[] Mac(byte[] key, string canonical) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));

    /// <summary>The key id a stored hash names, or null when it carries no envelope (unkeyed).</summary>
    private static int? KeyIdOf(string hash)
    {
        if (hash.Length < 3 || hash[0] != 'k') return null;
        var colon = hash.IndexOf(':', StringComparison.Ordinal);
        return colon > 1
            && int.TryParse(hash.AsSpan(1, colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0
                ? id
                : null;
    }

    private static string Canonical(AuditLog row, string? prevHash) =>
        new StringBuilder()
            .Append(prevHash ?? "").Append(FieldSeparator)
            .Append(row.OrgId?.ToString() ?? "").Append(FieldSeparator)
            .Append(row.ProjectId?.ToString() ?? "").Append(FieldSeparator)
            .Append(row.UserId?.ToString() ?? "").Append(FieldSeparator)
            .Append(row.ActorId?.ToString() ?? "").Append(FieldSeparator)
            .Append(row.Action).Append(FieldSeparator)
            .Append(row.TargetType ?? "").Append(FieldSeparator)
            .Append(row.TargetId ?? "").Append(FieldSeparator)
            .Append(row.IpAddress ?? "").Append(FieldSeparator)
            .Append(row.UserAgent ?? "").Append(FieldSeparator)
            .Append(Normalise(row.CreatedAt).ToString("O", CultureInfo.InvariantCulture)).Append(FieldSeparator)
            .Append(CanonicalMetadata(row.Metadata))
            .ToString();

    /// <summary>
    /// Metadata is stored as <c>jsonb</c>, which does not preserve key order and hands values back
    /// as <c>JsonElement</c> rather than the CLR types they went in as. Sorting the keys and
    /// re-serialising every value through JSON makes the write-side and read-side forms identical
    /// for the scalar values this codebase stores.
    /// </summary>
    private static string CanonicalMetadata(Dictionary<string, object> metadata)
        => string.Join(PairSeparator, metadata
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={JsonSerializer.Serialize(kv.Value)}"));

    /// <summary>
    /// The pre-keying hash, kept only so rows written under it can still be walked. It is not
    /// authentication: every input is a column of the row, so a writer with database access can
    /// reproduce it. Never used to write.
    /// </summary>
    private static string LegacyCompute(AuditLog row, string? prevHash)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(row, prevHash))));

    /// <summary>
    /// Walks <paramref name="allRows"/> in insertion order and reports what can honestly be said
    /// about the chain.
    ///
    /// <para>
    /// The leading run of rows with an empty hash — written before this deployment had a chain at
    /// all — is skipped and counted as unverifiable. The first row after it is taken as the start
    /// of the surviving chain and its own <c>PrevHash</c> is not checked: a retention purge
    /// legitimately removes the rows before it.
    /// </para>
    ///
    /// <para>
    /// <b>The old/new boundary is positional, and that is load-bearing.</b> Unkeyed rows are
    /// accepted only <i>before</i> the first keyed row. Without that rule an attacker could
    /// downgrade a keyed row — recompute it with the unkeyed algorithm he can run himself, drop
    /// the <c>k1:</c> prefix — and the chain would still walk. After the first keyed row, an
    /// unkeyed hash is a break.
    /// </para>
    ///
    /// <para>
    /// A row whose envelope names a key id this deployment no longer has configured is counted
    /// unverifiable and its link is accepted, because the alternative — calling it a break —
    /// would make retiring a root indistinguishable from an attack on the log.
    /// </para>
    /// </summary>
    public static AuditChainStatus Verify(KeyRing ring, IReadOnlyList<AuditLog> allRows)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(allRows);

        var rows = allRows.SkipWhile(r => string.IsNullOrEmpty(r.Hash)).ToList();
        var unverifiable = allRows.Count - rows.Count;
        var verified = 0;
        var seenKeyed = false;

        string? expectedPrev = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i > 0 && row.PrevHash != expectedPrev)
                return new AuditChainStatus(row.Id, verified, unverifiable);

            // An empty hash *after* the leading run is a row that reached the table without going
            // through SaveChangesAsync — an insert straight into the database.
            if (string.IsNullOrEmpty(row.Hash))
                return new AuditChainStatus(row.Id, verified, unverifiable);

            switch (KeyIdOf(row.Hash))
            {
                case { } keyId:
                    seenKeyed = true;
                    if (!ring.KeyIds.Contains(keyId)) { unverifiable++; break; }
                    if (row.Hash != Envelope(keyId, Mac(ring.KeyFor(keyId), Canonical(row, row.PrevHash))))
                        return new AuditChainStatus(row.Id, verified, unverifiable);
                    verified++;
                    break;

                case null when seenKeyed:
                    return new AuditChainStatus(row.Id, verified, unverifiable);

                default:
                    unverifiable++;
                    if (row.Hash != LegacyCompute(row, row.PrevHash))
                        return new AuditChainStatus(row.Id, verified, unverifiable);
                    break;
            }

            expectedPrev = row.Hash;
        }

        return new AuditChainStatus(null, verified, unverifiable);
    }
}
