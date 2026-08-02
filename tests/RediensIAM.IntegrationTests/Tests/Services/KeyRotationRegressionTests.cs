using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

/// <summary>
/// S-10 — key rotation for the HKDF root and the Argon2 pepper.
///
/// Before this, a ciphertext carried no key identifier, so every TOTP secret, webhook secret,
/// per-org SMTP password and provider client secret in every tenant was decryptable only by the
/// single current root. Rotating that root destroyed all of them at once, with no migration
/// path — which is why C-3 (registry compromise → key exfiltration) had disclosure but no
/// recovery. The key was, in practice, never rotatable.
///
/// The three properties these tests exist to hold, in the order they matter:
///   1. an old ciphertext with no key id still decrypts (nothing existing breaks);
///   2. a value encrypted under key 1 still decrypts after key 2 becomes active
///      (rotation is incremental, not a cutover);
///   3. a value written after rotation is NOT readable by key 1 alone
///      (the new key is really a new key, and "it still works" is not a false positive).
/// </summary>
public class KeyRotationEnvelopeTests
{
    private static byte[] Root(byte fill) => [.. Enumerable.Repeat(fill, 32)];

    private static KeyRing Ring(int activeId, params (int Id, byte Fill)[] keys) =>
        new(activeId, keys.ToDictionary(k => k.Id, k => Root(k.Fill)));

    // ── Property 1: backward compatibility ────────────────────────────────────

    /// <summary>
    /// The exact bytes written before S-10 existed. A single-key ring at id 1 emits no prefix
    /// by design, so this is not an approximation of the legacy format — it is the legacy format.
    /// </summary>
    private static string LegacyCiphertext(byte fill, string plaintext) =>
        TotpEncryption.EncryptString(new KeyRing(TotpEncryption.LegacyKeyId, Root(fill)), plaintext);

    [Fact]
    public void PreRotationCiphertext_CarriesNoKeyIdPrefix()
    {
        var stored = LegacyCiphertext(0xA1, "totp-secret");

        // Base64 only. No 'k<n>:' prefix, and in particular no ':' at all — that is what lets
        // the parser tell a prefixed value from a legacy one with no ambiguity.
        stored.Should().NotContain(":");
        Convert.FromBase64String(stored).Should().NotBeEmpty();
        TotpEncryption.KeyIdOf(stored).Should().Be(1, "absence of a key id means key 1");
    }

    [Fact]
    public void OldCiphertextWithNoKeyId_StillDecrypts_AfterKey2BecomesActive()
    {
        var legacy = LegacyCiphertext(0xA1, "totp-secret");

        // Operator has added key 2 and made it active; key 1 is retained for decryption.
        var rotated = Ring(2, (2, 0xB2), (1, 0xA1));

        TotpEncryption.DecryptString(rotated, legacy).Should().Be("totp-secret");
    }

    [Fact]
    public void NothingChangesOnDisk_UntilAnOperatorActuallyRotates()
    {
        // A deployment that has not rotated keeps writing byte-identical envelopes, so the
        // data-format change is inert until Security:EncryptionKeys names a non-1 active key.
        var beforeRotation = Ring(1, (1, 0xA1));
        TotpEncryption.EncryptString(beforeRotation, "x").Should().NotContain(":");
    }

    // ── Property 2: multi-key decryption ──────────────────────────────────────

    [Fact]
    public void ValueEncryptedUnderKey1_DecryptsAfterKey2BecomesActive()
    {
        var beforeRotation = Ring(1, (1, 0xA1));
        var stored = TotpEncryption.EncryptString(beforeRotation, "webhook-secret");

        var afterRotation = Ring(2, (2, 0xB2), (1, 0xA1));
        TotpEncryption.DecryptString(afterRotation, stored).Should().Be("webhook-secret");
    }

    [Fact]
    public void BothKeyGenerationsDecrypt_UnderTheSameRing()
    {
        var ring = Ring(2, (2, 0xB2), (1, 0xA1));
        var underKey1 = TotpEncryption.EncryptString(Ring(1, (1, 0xA1)), "old");
        var underKey2 = TotpEncryption.EncryptString(ring, "new");

        TotpEncryption.DecryptString(ring, underKey1).Should().Be("old");
        TotpEncryption.DecryptString(ring, underKey2).Should().Be("new");
    }

    // ── Property 3: single-key encryption, and the new key is really new ──────

    [Fact]
    public void ValueWrittenAfterRotation_CarriesTheActiveKeyId()
    {
        var stored = TotpEncryption.EncryptString(Ring(2, (2, 0xB2), (1, 0xA1)), "s");

        stored.Should().StartWith("k2:");
        TotpEncryption.KeyIdOf(stored).Should().Be(2);
    }

    [Fact]
    public void ValueWrittenAfterRotation_IsNotReadableByKey1Alone()
    {
        var stored = TotpEncryption.EncryptString(Ring(2, (2, 0xB2), (1, 0xA1)), "s");

        // The old key on its own — the state after the operator drops key 2, or the state of a
        // replica that has not picked up the new configuration.
        var key1Only = Ring(1, (1, 0xA1));

        var act = () => TotpEncryption.DecryptString(key1Only, stored);
        act.Should().Throw<CryptographicException>()
            .WithMessage("*key id 2*not configured*",
                "dropping a key that still has data under it must fail loudly, never silently");
    }

    [Fact]
    public void WrongKeyMaterialUnderTheRightKeyId_FailsAuthentication()
    {
        // A key id is a label, not a proof. If an operator pastes the wrong 32 bytes under id 2,
        // AES-GCM's tag must reject it rather than return garbage plaintext.
        var stored = TotpEncryption.EncryptString(Ring(2, (2, 0xB2)), "s");
        var wrongKey2 = Ring(2, (2, 0xCC));

        var act = () => TotpEncryption.DecryptString(wrongKey2, stored);
        act.Should().Throw<CryptographicException>();
    }

    // ── Envelope parsing edge cases ───────────────────────────────────────────

    [Fact]
    public void Base64BodyBeginningWithK_IsNotMistakenForAKeyIdPrefix()
    {
        // Base64 legitimately produces bodies starting with 'k'. The prefix is only recognised
        // when a ':' follows, and ':' is outside the Base64 alphabet — so this can never collide.
        var ring = Ring(1, (1, 0xA1));
        var seen = 0;
        for (var i = 0; i < 400; i++)
        {
            var stored = TotpEncryption.EncryptString(ring, $"payload-{i}");
            if (!stored.StartsWith('k')) continue;
            seen++;
            TotpEncryption.KeyIdOf(stored).Should().Be(1);
            TotpEncryption.DecryptString(ring, stored).Should().Be($"payload-{i}");
        }
        seen.Should().BeGreaterThan(0, "the 400 random nonces should produce at least one body starting with 'k'");
    }

    [Fact]
    public void KeyIdsAboveSingleDigit_RoundTrip()
    {
        var ring = Ring(37, (37, 0x11), (1, 0xA1));
        var stored = TotpEncryption.EncryptString(ring, "s");
        stored.Should().StartWith("k37:");
        TotpEncryption.DecryptString(ring, stored).Should().Be("s");
    }

    [Fact]
    public void KeyRing_RejectsAnActiveIdItDoesNotHold()
    {
        var act = () => new KeyRing(3, new Dictionary<int, byte[]> { [1] = Root(0xA1) });
        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// S-10 — the configuration surface: how an operator expresses "key 2 is now active, keep key 1
/// for decryption", and what happens when they express it wrongly.
/// </summary>
public class KeyRotationConfigTests
{
    private static AppConfig Config(params (string Key, string? Value)[] overrides)
    {
        var dict = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"]        = "Host=localhost;Database=test",
            ["App:Domain"]                       = "localhost",
            ["Security:TotpSecretEncryptionKey"] = new string('a', 64),
        };
        foreach (var (k, v) in overrides) dict[k] = v;
        return new AppConfig(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private const string Key1 = "11111111111111111111111111111111111111111111111111111111111111aa";
    private const string Key2 = "22222222222222222222222222222222222222222222222222222222222222bb";

    [Fact]
    public void NoEncryptionKeysConfigured_YieldsTheSingleLegacyKeyId()
    {
        var cfg = Config();

        cfg.ActiveEncryptionKeyId.Should().Be(1);
        cfg.ConfiguredEncryptionKeyIds.Should().Equal(1);
        cfg.TotpEncKey.ActiveId.Should().Be(1);
        // Unchanged behaviour: values are written with no prefix.
        TotpEncryption.EncryptString(cfg.TotpEncKey, "s").Should().NotContain(":");
    }

    [Fact]
    public void FirstEntryIsActive_RestAreDecryptOnly()
    {
        var cfg = Config(("Security:EncryptionKeys", $"2:{Key2},1:{Key1}"));

        cfg.ActiveEncryptionKeyId.Should().Be(2);
        cfg.ConfiguredEncryptionKeyIds.Should().Equal(2, 1);
        cfg.TotpEncKey.ActiveId.Should().Be(2);
        cfg.TotpEncKey.KeyIds.Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public void ARealRotation_MigratesAValueWrittenBeforeIt()
    {
        // Step 1 of the runbook: one key, values written under it.
        var before = Config(("Security:TotpSecretEncryptionKey", Key1));
        var stored = TotpEncryption.EncryptString(before.TotpEncKey, "totp");

        // Step 2: add key 2 as active, keep key 1. The old value must survive.
        var after = Config(("Security:EncryptionKeys", $"2:{Key2},1:{Key1}"));
        TotpEncryption.DecryptString(after.TotpEncKey, stored).Should().Be("totp");

        // Step 3: re-encrypted values carry key 2 …
        var rewritten = TotpEncryption.EncryptString(after.TotpEncKey, "totp");
        rewritten.Should().StartWith("k2:");

        // … and only then is it safe to drop key 1.
        var dropped = Config(("Security:EncryptionKeys", $"2:{Key2}"));
        TotpEncryption.DecryptString(dropped.TotpEncKey, rewritten).Should().Be("totp");
        var act = () => TotpEncryption.DecryptString(dropped.TotpEncKey, stored);
        act.Should().Throw<CryptographicException>("key 1 is gone — this is why totalPending must reach 0 first");
    }

    [Fact]
    public void SubkeysStayIndependentPerPurpose_AcrossEveryRoot()
    {
        var cfg = Config(("Security:EncryptionKeys", $"2:{Key2},1:{Key1}"));
        var stored = TotpEncryption.EncryptString(cfg.TotpEncKey, "s");

        // HKDF per-purpose separation is unchanged by rotation: the webhook ring holds the same
        // two key ids but different bytes, so it must not be able to read a TOTP ciphertext.
        var act = () => TotpEncryption.DecryptString(cfg.WebhookEncKey, stored);
        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData("2")]                       // no ':'
    [InlineData("0:" + Key2)]               // id below 1
    [InlineData("x:" + Key2)]               // non-numeric id
    [InlineData("2:nothex")]                // not hex
    [InlineData("2:aabb")]                  // wrong length
    [InlineData("2:" + Key2 + ",2:" + Key1)] // duplicate id
    public void MalformedEncryptionKeys_ThrowAtStartupRatherThanOnFirstDecrypt(string value)
    {
        var cfg = Config(("Security:EncryptionKeys", value));
        var act = () => cfg.ActiveEncryptionKeyId;
        act.Should().Throw<InvalidOperationException>().WithMessage("*Security:EncryptionKeys*");
    }
}

/// <summary>
/// S-10 §5 — the Argon2 pepper. A password hash cannot be re-derived without the plaintext, so
/// there is no sweep: rotation happens one successful login at a time and leaves a long tail of
/// accounts that never sign in. These tests pin what rotation does and does not guarantee.
/// </summary>
public class Argon2PepperRotationTests
{
    private const string Pepper1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa11";
    private const string Pepper2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb22";

    private static PasswordService Service(params (string Key, string? Value)[] overrides)
    {
        var dict = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"]        = "Host=localhost;Database=test",
            ["App:Domain"]                       = "localhost",
            ["Security:TotpSecretEncryptionKey"] = new string('a', 64),
            // Keep the tests fast — the pepper behaviour is independent of the Argon2 cost.
            ["Security:ArgonTimeCost"]           = "2",
            ["Security:ArgonMemoryCost"]         = "19456",
            ["Security:ArgonParallelism"]        = "1",
        };
        foreach (var (k, v) in overrides) dict[k] = v;
        return new PasswordService(new AppConfig(new ConfigurationBuilder().AddInMemoryCollection(dict).Build()));
    }

    [Fact]
    public void WithNoPepper_HashIsUnmarkedAndVerifies()
    {
        var svc = Service();
        var hash = svc.Hash("pw");

        hash.Should().NotContain("$k=");
        svc.Verify("pw", hash).Should().BeTrue();
        svc.NeedsRepepper(hash).Should().BeFalse();
    }

    [Fact]
    public void WithTheLegacySinglePepper_StoredFormatIsUnchanged()
    {
        // Security:Argon2Pepper is pepper id 1, and pepper id 1 writes no marker — so enabling
        // rotation support changes nothing about hashes already in the database.
        var svc = Service(("Security:Argon2Pepper", Pepper1));
        var hash = svc.Hash("pw");

        hash.Should().NotContain("$k=");
        svc.Verify("pw", hash).Should().BeTrue();
        svc.NeedsRepepper(hash).Should().BeFalse();
    }

    [Fact]
    public void AfterRotation_OldHashStillVerifies_AndIsFlaggedForRepeppering()
    {
        var before = Service(("Security:Argon2Pepper", Pepper1));
        var oldHash = before.Hash("pw");

        var after = Service(("Security:Argon2Peppers", $"2:{Pepper2},1:{Pepper1}"));

        after.Verify("pw", oldHash).Should().BeTrue("nobody may be locked out by a pepper rotation");
        after.NeedsRepepper(oldHash).Should().BeTrue("the login path must rewrite it while it holds the plaintext");

        var rehashed = after.Hash("pw");
        rehashed.Should().EndWith("$k=2");
        after.Verify("pw", rehashed).Should().BeTrue();
        after.NeedsRepepper(rehashed).Should().BeFalse();
    }

    [Fact]
    public void AHashUnderPepper2_IsNotVerifiableByPepper1Alone()
    {
        var after = Service(("Security:Argon2Peppers", $"2:{Pepper2},1:{Pepper1}"));
        var newHash = after.Hash("pw");

        // The operator dropped pepper 2 — or rolled back. Fail closed: never silently fall back
        // to a different pepper, which would turn a config error into a wrong-password error
        // that looks identical to a real one.
        var pepper1Only = Service(("Security:Argon2Pepper", Pepper1));
        pepper1Only.Verify("pw", newHash).Should().BeFalse();
    }

    [Fact]
    public void AnUnmarkedHash_IsReadAsPepper1_NotAsPepperless()
    {
        // This is the whole back-compat rule for peppers. If an unmarked hash were treated as
        // pepper-less, every existing password in a peppered deployment would stop verifying.
        var legacy = Service(("Security:Argon2Pepper", Pepper1));
        var legacyHash = legacy.Hash("pw");
        PasswordService.PepperIdOf(legacyHash).Should().Be(1);

        var rotated = Service(("Security:Argon2Peppers", $"2:{Pepper2},1:{Pepper1}"));
        rotated.Verify("pw", legacyHash).Should().BeTrue();
    }

    [Fact]
    public void BackupCodes_CarryThePepperIdAndSurviveRotation()
    {
        var before = Service(("Security:Argon2Pepper", Pepper1));
        var legacyCode = before.HashBackupCode("ABCD1234");
        legacyCode.Should().StartWith("sha256:p:", "'p' is the pre-rotation marker for pepper id 1");

        var after = Service(("Security:Argon2Peppers", $"2:{Pepper2},1:{Pepper1}"));
        after.VerifyBackupCode("ABCD1234", legacyCode).Should().BeTrue();

        var newCode = after.HashBackupCode("ABCD1234");
        newCode.Should().StartWith("sha256:2:");
        after.VerifyBackupCode("ABCD1234", newCode).Should().BeTrue();

        // And a code under a pepper that is no longer configured must not verify.
        before.VerifyBackupCode("ABCD1234", newCode).Should().BeFalse();
    }

    [Fact]
    public void UnpepperedBackupCodes_KeepVerifying()
    {
        var svc = Service();
        var code = svc.HashBackupCode("ABCD1234");
        code.Should().StartWith("sha256:0:");
        svc.VerifyBackupCode("ABCD1234", code).Should().BeTrue();
    }
}

/// <summary>
/// S-10 §3 — the re-encryption sweep, against a real PostgreSQL instance so the candidate-selection
/// predicates are exercised as SQL and not as LINQ-to-objects.
///
/// Encryption is already lazy: every write goes out under the active key. What lazy cannot do is
/// migrate a row nobody writes to — a TOTP secret is written once at enrolment and thereafter only
/// read. Without the sweep the old key can never be retired, which is the failure S-10 names.
/// </summary>
[Collection("RediensIAM")]
public class KeyRotationSweepTests(TestFixture fixture) : IAsyncLifetime
{
    private const string Key1 = "11111111111111111111111111111111111111111111111111111111111111aa";
    private const string Key2 = "22222222222222222222222222222222222222222222222222222222222222bb";

    // The sweep is deliberately global: it rewrites every encrypted row in the database it is
    // pointed at, across every tenant. That is the correct product behaviour and the reason
    // these tests cannot share the collection's database — a sweep there would re-encrypt the
    // TOTP secrets every other test seeded under the fixture's own root key, and those tests
    // would then fail with an authentication-tag mismatch. So: our own database, migrated from
    // the same migrations the app uses, dropped afterwards.
    private string _dbName = "";
    private RediensIamDbContext _db = null!;
    private SeedData _seed = null!;

    public async Task InitializeAsync()
    {
        _dbName = "keyrot_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{_dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString) { Database = _dbName }.ToString();
        // Seeding a TOTP secret is a credential change, which the save path audits — and an audit
        // row's chain link is keyed, so this context needs a root of its own. Key 1: the sweep
        // tests rotate the *encryption* ring around it and never touch the chain.
        _db = new RediensIamDbContext(
            new DbContextOptionsBuilder<RediensIamDbContext>().UseNpgsql(cs).Options, Config($"1:{Key1}"));
        await _db.Database.MigrateAsync();
        _seed = new SeedData(_db, fixture.Hydra, fixture.GetService<PasswordService>());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(fixture.PostgresConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    private static AppConfig Config(string encryptionKeys)
    {
        return new AppConfig(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"]        = "Host=localhost;Database=test",
            ["App:Domain"]                       = "localhost",
            ["Security:TotpSecretEncryptionKey"] = Key1,
            ["Security:EncryptionKeys"]          = encryptionKeys,
        }).Build());
    }

    private KeyRotationService Sweeper(AppConfig cfg) =>
        new(_db, cfg, NullLogger<KeyRotationService>.Instance);

    private static Dictionary<string, object> ThemeWithSecret(KeyRing ring, string secret)
    {
        var enc = TotpEncryption.EncryptString(ring, secret);
        var doc = JsonDocument.Parse($$"""
            {"providers": [{"id": "gh", "type": "oidc", "client_secret_enc": {{JsonSerializer.Serialize(enc)}}}]}
            """);
        return new Dictionary<string, object> { ["providers"] = doc.RootElement.GetProperty("providers").Clone() };
    }

    /// <summary>
    /// Seeds one row of every encrypted shape, written under key 1 in the pre-rotation format,
    /// and returns the ids plus the plaintexts they must still decrypt to afterwards.
    /// </summary>
    private async Task<(Guid UserId, Guid WebhookId, Guid SmtpOrgId, Guid ProjectId)> SeedUnderKey1Async(AppConfig key1Only)
    {
        var (org, _) = await _seed.CreateOrgAsync();
        var project  = await _seed.CreateProjectAsync(org.Id);
        var list     = await _seed.CreateUserListAsync(org.Id);
        var user     = await _seed.CreateUserAsync(list.Id);

        user.TotpEnabled = true;
        user.TotpSecret  = TotpEncryption.EncryptString(key1Only.TotpEncKey, "TOTPSECRET");

        var webhook = new RediensIAM.Data.Entities.Webhook
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Url = "https://example.invalid/hook",
            Events = ["webhook.test"],
            SecretEnc = TotpEncryption.EncryptString(key1Only.WebhookEncKey, "WEBHOOKSECRET"),
        };
        _db.Webhooks.Add(webhook);

        _db.OrgSmtpConfigs.Add(new RediensIAM.Data.Entities.OrgSmtpConfig
        {
            OrgId = org.Id,
            Host = "smtp.example.invalid",
            Port = 587,
            Username = "u",
            PasswordEnc = TotpEncryption.EncryptString(key1Only.SmtpEncKey, "SMTPPASSWORD"),
        });

        project.LoginTheme = ThemeWithSecret(key1Only.ThemeEncKey, "CLIENTSECRET");

        await _db.SaveChangesAsync();
        return (user.Id, webhook.Id, org.Id, project.Id);
    }

    [Fact]
    public async Task Sweep_MigratesEveryEncryptedShape_AndPlaintextIsPreserved()
    {
        var key1Only = Config($"1:{Key1}");
        var ids = await SeedUnderKey1Async(key1Only);

        // Pre-rotation: nothing carries a prefix.
        var seededTotp = await _db.Users.Where(u => u.Id == ids.UserId)
            .Select(u => u.TotpSecret).SingleAsync();
        seededTotp.Should().NotContain(":");

        var rotated = Config($"2:{Key2},1:{Key1}");
        var before = await Sweeper(rotated).GetStatusAsync();
        before.ActiveKeyId.Should().Be(2);
        before.TotalPending.Should().BeGreaterThanOrEqualTo(4, "one row of each of the four encrypted shapes is pending");

        var after = await Sweeper(rotated).ReEncryptAsync();
        after.TotalPending.Should().Be(0, "reaching 0 is the only signal that key 1 may be dropped");

        _db.ChangeTracker.Clear();

        var totp = await _db.Users.Where(u => u.Id == ids.UserId).Select(u => u.TotpSecret).SingleAsync();
        totp.Should().StartWith("k2:");
        TotpEncryption.DecryptString(rotated.TotpEncKey, totp!).Should().Be("TOTPSECRET");

        var hook = await _db.Webhooks.Where(w => w.Id == ids.WebhookId).Select(w => w.SecretEnc).SingleAsync();
        hook.Should().StartWith("k2:");
        TotpEncryption.DecryptString(rotated.WebhookEncKey, hook).Should().Be("WEBHOOKSECRET");

        var smtp = await _db.OrgSmtpConfigs.Where(c => c.OrgId == ids.SmtpOrgId)
            .Select(c => c.PasswordEnc).SingleAsync();
        smtp.Should().StartWith("k2:");
        TotpEncryption.DecryptString(rotated.SmtpEncKey, smtp!).Should().Be("SMTPPASSWORD");

        var theme = await _db.Projects.Where(p => p.Id == ids.ProjectId).Select(p => p.LoginTheme).SingleAsync();
        TotpEncryption.ProviderSecretKeyIds(theme).Should().AllSatisfy(id => id.Should().Be(2));
    }

    [Fact]
    public async Task AfterTheSweep_TheOldKeyCanBeDropped_AndSweptValuesStillRead()
    {
        var key1Only = Config($"1:{Key1}");
        var ids = await SeedUnderKey1Async(key1Only);

        var rotated = Config($"2:{Key2},1:{Key1}");
        await Sweeper(rotated).ReEncryptAsync();
        _db.ChangeTracker.Clear();

        // Key 1 removed from the configuration entirely — the end state of the runbook.
        var key2Only = Config($"2:{Key2}");
        var totp = await _db.Users.Where(u => u.Id == ids.UserId).Select(u => u.TotpSecret).SingleAsync();
        TotpEncryption.DecryptString(key2Only.TotpEncKey, totp!).Should().Be("TOTPSECRET");
    }

    [Fact]
    public async Task Sweep_IsIdempotent()
    {
        var key1Only = Config($"1:{Key1}");
        await SeedUnderKey1Async(key1Only);

        var rotated = Config($"2:{Key2},1:{Key1}");
        await Sweeper(rotated).ReEncryptAsync();
        _db.ChangeTracker.Clear();

        var second = await Sweeper(rotated).ReEncryptAsync();
        second.TotalPending.Should().Be(0);
    }

    [Fact]
    public async Task RollingBackToKey1_ReportsTheKey2RowsAsPending()
    {
        // Mid-rotation rollback: the app is put back on key 1 while key-2 ciphertexts exist.
        // The status must not report "0 pending" — those rows are unreadable under key 1 alone
        // and the operator has to know.
        var key1Only = Config($"1:{Key1}");
        var ids = await SeedUnderKey1Async(key1Only);

        var rotated = Config($"2:{Key2},1:{Key1}");
        await Sweeper(rotated).ReEncryptAsync();
        _db.ChangeTracker.Clear();

        var rolledBack = Config($"1:{Key1},2:{Key2}");
        var status = await Sweeper(rolledBack).GetStatusAsync();
        status.ActiveKeyId.Should().Be(1);
        status.TotalPending.Should().BeGreaterThanOrEqualTo(4);

        // And the sweep runs the other way just as well, because both keys are still configured.
        await Sweeper(rolledBack).ReEncryptAsync();
        _db.ChangeTracker.Clear();
        var totp = await _db.Users.Where(u => u.Id == ids.UserId).Select(u => u.TotpSecret).SingleAsync();
        totp.Should().NotContain(":", "rolling back to key 1 restores the prefix-less format");
        TotpEncryption.DecryptString(key1Only.TotpEncKey, totp!).Should().Be("TOTPSECRET");
    }

    [Fact]
    public async Task Sweep_RefusesToRun_WhenTheOriginalKeyIsMissing()
    {
        // The one thing worse than a failed sweep is a sweep that "succeeds" by dropping values
        // it could not decrypt. Configure key 2 as active with key 1 absent, and it must throw.
        var key1Only = Config($"1:{Key1}");
        await SeedUnderKey1Async(key1Only);

        var key2Only = Config($"2:{Key2}");
        var act = async () => await Sweeper(key2Only).ReEncryptAsync();
        await act.Should().ThrowAsync<CryptographicException>();
    }
}

/// <summary>
/// S-10 §7 — the operator surface the runbook tells people to use. An endpoint that does not
/// route, or that is reachable by the wrong caller, would make the runbook worse than useless.
///
/// These run against the shared collection database, which is deliberately safe: it has no
/// rotation configured (active key id 1), so the candidate predicate matches no rows and the
/// sweep is a verified no-op. That is itself worth asserting — running the sweep on a deployment
/// that has not rotated must never rewrite anything.
/// </summary>
[Collection("RediensIAM")]
public class KeyRotationEndpointTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminClientAsync()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(fixture.Seed.SuperAdminToken(user.Id));
    }

    [Fact]
    public async Task Status_SuperAdmin_ReportsTheKeyRing()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/key-rotation");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("active_key_id").GetInt32().Should().Be(1);
        body.GetProperty("configured_key_ids").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(1);
        body.GetProperty("columns").GetArrayLength().Should().Be(4, "all four encrypted shapes must be reported");
        body.GetProperty("total_pending").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Status_RegularUser_IsRefused()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(orgList.Id);
        var client = fixture.ClientWithToken(fixture.Seed.UserToken(user.Id, org.Id, project.Id));

        var res = await client.GetAsync("/admin/key-rotation");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReEncrypt_WithNoRotationConfigured_IsANoOp()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.PostAsync("/admin/key-rotation/reencrypt", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("total_pending").GetInt32().Should().Be(0);
    }
}
