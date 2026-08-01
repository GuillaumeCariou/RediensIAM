using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// S-3 residual — the audit hash chain was an <b>unkeyed</b> SHA-256 over the row's own columns.
///
/// Every input to that hash was a value the attacker already had: to rewrite a row he rewrote it,
/// recomputed its hash, re-chained every row after it, and handed back a table that verified
/// clean. It proved ordering, never authenticity. The link is now an HMAC under a key derived from
/// the deployment root, which lives in the process environment and not in the database.
///
/// These tests are the four properties that claim rests on:
///   1. a tampered row still fails (the old property must survive);
///   2. a re-chain forged with the algorithm an attacker can run himself fails (the new one);
///   3. the boundary between old unkeyed rows and new keyed ones is reported honestly, and a keyed
///      row cannot be downgraded to an unkeyed one to get around it;
///   4. rotating the root re-keys new rows without invalidating a single historic one.
/// </summary>
[Collection("RediensIAM")]
public class AuditChainKeyingTests(TestFixture fixture)
{
    // ── 1 & 2: what the key buys ─────────────────────────────────────────────

    /// <summary>
    /// The finding itself. The attacker edits a row and then does exactly what the old scheme
    /// allowed — recomputes the SHA-256 of the row he just wrote, and of every row after it, so
    /// that every link lines up again. Under the unkeyed chain that table verified. It must not.
    /// </summary>
    [Fact]
    public async Task ARowRewrittenAndTheChainRecomputedByHand_StillFailsVerification()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var audit = fixture.GetService<AuditLogService>();

        await audit.RecordAsync(org.Id, null, null, "test.forge.one");
        await audit.RecordAsync(org.Id, null, null, "test.forge.two");
        await audit.RecordAsync(org.Id, null, null, "test.forge.three");

        var rows = await ChainRowsAsync(org.Id);
        rows[1].Action = "test.forge.rewritten";

        // The forgery: re-link the tail with the only algorithm an attacker holds — no key.
        string? prev = rows[0].Hash;
        for (var i = 1; i < rows.Count; i++)
        {
            rows[i].PrevHash = prev;
            rows[i].Hash = UnkeyedHash(rows[i], prev);
            prev = rows[i].Hash;
            await WriteRowBehindTheApplicationsBackAsync(rows[i]);
        }

        var status = await audit.VerifyChainAsync(org.Id);

        status.FirstBreak.Should().Be(rows[1].Id,
            "the rewritten row is where a chain the attacker could not key stops being the one the application wrote");
        status.FullyVerified.Should().BeFalse();
    }

    /// <summary>
    /// The same forgery with the right <i>shape</i> — a <c>k1:</c> envelope and an HMAC — but the
    /// wrong key. This is what separates "the format changed" from "the format is authenticated":
    /// the attacker can read the code, so the format is public; the key is not.
    /// </summary>
    [Fact]
    public async Task AChainForgedUnderTheWrongKey_FailsVerification()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var audit = fixture.GetService<AuditLogService>();

        await audit.RecordAsync(org.Id, null, null, "test.wrongkey.one");
        await audit.RecordAsync(org.Id, null, null, "test.wrongkey.two");

        var attackersRing = new KeyRing(1, RandomNumberGenerator.GetBytes(32));
        var rows = await ChainRowsAsync(org.Id);
        rows[1].Action = "test.wrongkey.rewritten";
        rows[1].Hash = AuditChain.Compute(attackersRing, rows[1], rows[1].PrevHash);
        await WriteRowBehindTheApplicationsBackAsync(rows[1]);

        (await audit.VerifyChainAsync(org.Id)).FirstBreak.Should().Be(rows[1].Id);
    }

    /// <summary>The rows the application itself writes verify, and say so as the stronger claim.</summary>
    [Fact]
    public async Task RowsWrittenByTheApplication_AreFullyVerified()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var audit = fixture.GetService<AuditLogService>();

        await audit.RecordAsync(org.Id, null, null, "test.keyed.one");
        await audit.RecordAsync(org.Id, null, null, "test.keyed.two");

        var status = await audit.VerifyChainAsync(org.Id);

        status.FullyVerified.Should().BeTrue();
        status.Verified.Should().Be(2);
        status.Unverifiable.Should().Be(0);
        (await ChainRowsAsync(org.Id)).Should().OnlyContain(r => r.Hash.StartsWith("k1:", StringComparison.Ordinal),
            "every row written under the ring's active key names it in its envelope");
    }

    // ── 3: the migration boundary ────────────────────────────────────────────

    /// <summary>
    /// The rows already on the dev cluster. They keep the unkeyed hashes they have — re-chaining
    /// an append-only table with hashes computed after the fact would prove nothing about what
    /// those rows said when they were written — and the verifier refuses to launder them into
    /// "valid". The chain walks (no break), and every one of them is counted as a row nothing can
    /// vouch for.
    /// </summary>
    [Fact]
    public async Task RowsFromBeforeKeying_WalkButAreReportedUnverifiable()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var audit = fixture.GetService<AuditLogService>();

        await SeedLegacyChainAsync(org.Id, "test.legacy.one", "test.legacy.two");
        await audit.RecordAsync(org.Id, null, null, "test.legacy.keyed");

        var status = await audit.VerifyChainAsync(org.Id);

        status.Intact.Should().BeTrue("the old rows still link to each other and to the first keyed row");
        status.Unverifiable.Should().Be(2, "an unkeyed digest is reproducible by anyone who can write the row");
        status.Verified.Should().Be(1);
        status.FullyVerified.Should().BeFalse(
            "'no break' is not 'authentic' while any row predates the key, and the status must not pretend otherwise");
    }

    /// <summary>
    /// The attack the boundary would otherwise open: keep writing rows, but in the old unkeyed
    /// format. If unkeyed rows were accepted anywhere in the chain, an attacker would simply
    /// downgrade the rows he wanted to rewrite. They are accepted only in the leading run, before
    /// the first keyed row.
    /// </summary>
    [Fact]
    public async Task AKeyedRowDowngradedToTheUnkeyedFormat_IsABreak()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var audit = fixture.GetService<AuditLogService>();

        await audit.RecordAsync(org.Id, null, null, "test.downgrade.one");
        await audit.RecordAsync(org.Id, null, null, "test.downgrade.two");

        var rows = await ChainRowsAsync(org.Id);
        var last = rows[^1];
        last.Action = "test.downgrade.rewritten";
        last.Hash = UnkeyedHash(last, last.PrevHash);   // a perfectly consistent unkeyed link
        await WriteRowBehindTheApplicationsBackAsync(last);

        (await audit.VerifyChainAsync(org.Id)).FirstBreak.Should().Be(last.Id,
            "an unkeyed hash after a keyed one is a downgrade, not history");
    }

    /// <summary>
    /// A row inserted straight into the table, with no hash at all. Empty hashes are tolerated only
    /// in the leading run — the rows from before the chain existed — and anywhere else they are a
    /// row that never went through <c>SaveChangesAsync</c>.
    /// </summary>
    [Fact]
    public async Task ARowInsertedWithNoHashAfterTheChainStarts_IsABreak()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var audit = fixture.GetService<AuditLogService>();
        await audit.RecordAsync(org.Id, null, null, "test.nohash.one");

        // Braces doubled: ExecuteSqlRaw runs the string through string.Format, so a literal
        // '{}'::jsonb would be read as a placeholder.
        await fixture.Db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO audit_log ("OrgId", "Action", "Metadata", "CreatedAt", "Hash")
            VALUES ({0}, 'test.nohash.planted', '{{}}'::jsonb, now(), '')
            """, org.Id);

        var planted = await fixture.Db.AuditLogs.AsNoTracking()
            .Where(a => a.OrgId == org.Id && a.Action == "test.nohash.planted")
            .Select(a => a.Id).SingleAsync();

        (await audit.VerifyChainAsync(org.Id)).FirstBreak.Should().Be(planted);
    }

    // ── The other half of a control: something has to run it ─────────────────

    /// <summary>
    /// <c>VerifyChainAsync</c> had no production caller, which makes it a function that would have
    /// noticed rather than a control. This pins the caller: the verifier is registered as a hosted
    /// service, and one pass of it publishes the break as the gauge an alert rule can watch.
    /// </summary>
    [Fact]
    public async Task TheChainVerifier_IsRunByAHostedServiceAndPublishesWhatItFinds()
    {
        var monitor = fixture.Services.GetServices<IHostedService>().OfType<IntegrityMonitorService>().Single();

        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var audit = fixture.GetService<AuditLogService>();
        await audit.RecordAsync(org.Id, null, null, "test.monitor.one");
        var rows = await ChainRowsAsync(org.Id);
        rows[0].Action = "test.monitor.rewritten";
        await WriteRowBehindTheApplicationsBackAsync(rows[0]);

        await monitor.RunPassAsync(CancellationToken.None);

        IamMetrics.AuditChainBroken.Value.Should().BeGreaterThan(0,
            "a pass that finds a rewritten row has to leave a number behind that an alert can fire on");
    }

    // ── 4: rotation ──────────────────────────────────────────────────────────
    //
    // Written against AuditChain directly rather than through the host: the ring is fixed when the
    // test host boots, and the property under test is precisely what happens when it changes.

    /// <summary>
    /// A rotated root must not cost the deployment its history. Each row names the key its MAC was
    /// written under, so a ring that has gained a new active key still verifies every row written
    /// under the old one — and writes the next row under the new one.
    /// </summary>
    [Fact]
    public void RotatingTheRoot_LeavesEveryHistoricRowVerifiable()
    {
        var key1 = RandomNumberGenerator.GetBytes(32);
        var key2 = RandomNumberGenerator.GetBytes(32);
        var beforeRotation = new KeyRing(1, key1);
        var afterRotation = new KeyRing(2, new Dictionary<int, byte[]> { [2] = key2, [1] = key1 });

        var chain = Chain(beforeRotation, 3);
        AuditChain.Verify(beforeRotation, chain).FullyVerified.Should().BeTrue();

        AuditChain.Verify(afterRotation, chain).FullyVerified.Should().BeTrue(
            "the rows name key 1, the rotated ring still holds key 1");

        chain.Add(NextRow(afterRotation, chain[^1], 4));
        chain[^1].Hash.Should().StartWith("k2:", "new rows go under the active key");
        AuditChain.Verify(afterRotation, chain).FullyVerified.Should().BeTrue(
            "a chain spanning a rotation verifies end to end");
    }

    /// <summary>
    /// Retiring a root is not the same as tampering, and the verifier must not confuse the two.
    /// Rows under a key that is no longer configured become unverifiable — the deployment threw
    /// away its ability to check them — while the chain itself still walks.
    /// </summary>
    [Fact]
    public void RetiringARoot_MakesItsRowsUnverifiableRatherThanBroken()
    {
        var key1 = RandomNumberGenerator.GetBytes(32);
        var key2 = RandomNumberGenerator.GetBytes(32);
        var withKey1 = new KeyRing(1, key1);
        var key1Retired = new KeyRing(2, new Dictionary<int, byte[]> { [2] = key2 });

        var chain = Chain(withKey1, 3);

        var status = AuditChain.Verify(key1Retired, chain);

        status.FirstBreak.Should().BeNull("a key nobody kept is not evidence of an attack");
        status.Unverifiable.Should().Be(3);
        status.Verified.Should().Be(0);
        status.FullyVerified.Should().BeFalse();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<List<AuditLog>> ChainRowsAsync(Guid orgId) =>
        await fixture.Db.AuditLogs.AsNoTracking()
            .Where(a => a.OrgId == orgId).OrderBy(a => a.Id).ToListAsync();

    /// <summary>Writes a row's tampered contents and links straight past the application.</summary>
    private Task WriteRowBehindTheApplicationsBackAsync(AuditLog row) =>
        fixture.Db.Database.ExecuteSqlRawAsync(
            """
            UPDATE audit_log SET "Action" = {1}, "Hash" = {2}, "PrevHash" = NULLIF({3}, '') WHERE "Id" = {0}
            """,
            row.Id, row.Action, row.Hash, row.PrevHash ?? "");

    /// <summary>Two rows in the pre-keying format, linked to each other, inserted directly.</summary>
    private async Task SeedLegacyChainAsync(Guid orgId, params string[] actions)
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string? prev = null;
        foreach (var action in actions)
        {
            var row = new AuditLog { OrgId = orgId, Action = action, CreatedAt = createdAt, Metadata = [] };
            var hash = UnkeyedHash(row, prev);
            await fixture.Db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO audit_log ("OrgId", "Action", "Metadata", "CreatedAt", "Hash", "PrevHash")
                VALUES ({0}, {1}, '{{}}'::jsonb, {2}, {3}, NULLIF({4}, ''))
                """,
                orgId, action, createdAt, hash, prev ?? "");
            prev = hash;
        }
    }

    /// <summary>
    /// The pre-keying hash, reimplemented here on purpose. Anyone who can write to
    /// <c>audit_log</c> can write this function — that is the finding — so the test computes it
    /// the way an attacker would rather than borrowing the application's copy.
    /// </summary>
    private static string UnkeyedHash(AuditLog row, string? prevHash)
    {
        const char fs = '\u001e';
        var canonical = new StringBuilder()
            .Append(prevHash ?? "").Append(fs)
            .Append(row.OrgId?.ToString() ?? "").Append(fs)
            .Append(row.ProjectId?.ToString() ?? "").Append(fs)
            .Append(row.UserId?.ToString() ?? "").Append(fs)
            .Append(row.ActorId?.ToString() ?? "").Append(fs)
            .Append(row.Action).Append(fs)
            .Append(row.TargetType ?? "").Append(fs)
            .Append(row.TargetId ?? "").Append(fs)
            .Append(row.IpAddress ?? "").Append(fs)
            .Append(row.UserAgent ?? "").Append(fs)
            .Append(AuditChain.Normalise(row.CreatedAt).ToString("O", CultureInfo.InvariantCulture)).Append(fs)
            .Append("")
            .ToString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static List<AuditLog> Chain(KeyRing ring, int length)
    {
        var rows = new List<AuditLog>();
        for (var i = 1; i <= length; i++)
            rows.Add(NextRow(ring, rows.Count == 0 ? null : rows[^1], i));
        return rows;
    }

    private static AuditLog NextRow(KeyRing ring, AuditLog? previous, int ordinal)
    {
        var row = new AuditLog
        {
            Id = ordinal,
            Action = $"test.rotation.{ordinal}",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(ordinal),
            Metadata = [],
            PrevHash = previous?.Hash,
        };
        row.Hash = AuditChain.Compute(ring, row, row.PrevHash);
        return row;
    }
}
