using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RediensIAM.Data.Entities;

namespace RediensIAM.Data;

/// <summary>
/// Hash chain over <see cref="AuditLog"/> rows (S-3, tamper-evidence half).
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

    /// <summary>Hash of <paramref name="row"/> linked to <paramref name="prevHash"/>.</summary>
    public static string Compute(AuditLog row, string? prevHash)
    {
        var canonical = new StringBuilder()
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

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

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
    /// Walks <paramref name="allRows"/> in insertion order and returns the id of the first row whose
    /// link does not hold, or null when the chain is intact.
    ///
    /// The first row supplied is taken as the start of the surviving chain and its own
    /// <c>PrevHash</c> is not checked — a retention purge legitimately removes the rows before it.
    /// Rows written before this deployment had a chain carry an empty hash and are skipped: they
    /// are unverifiable, which is not the same as tampered with.
    /// </summary>
    public static long? FirstBreak(IReadOnlyList<AuditLog> allRows)
    {
        var rows = allRows.SkipWhile(r => string.IsNullOrEmpty(r.Hash)).ToList();
        string? expectedPrev = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i > 0 && row.PrevHash != expectedPrev) return row.Id;
            if (row.Hash != Compute(row, row.PrevHash)) return row.Id;
            expectedPrev = row.Hash;
        }
        return null;
    }
}
