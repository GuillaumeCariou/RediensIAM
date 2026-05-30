using FluentAssertions;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

/// <summary>
/// Unit-style tests for <see cref="PasswordService"/> covering backup-code HMAC paths
/// (HashBackupCode + VerifyBackupCode sha256 and legacy argon2 branches) and the
/// timing-equalisation DummyVerify used on user-not-found in the login path.
/// </summary>
[Collection("RediensIAM")]
public class PasswordServiceCoverageTests(TestFixture fixture)
{
    private PasswordService GetService() => fixture.GetService<PasswordService>();

    // ── HashBackupCode ────────────────────────────────────────────────────────

    [Fact]
    public void HashBackupCode_SameInput_ProducesSameOutput()
    {
        var svc = GetService();
        var h1 = svc.HashBackupCode("ABCDEF1234567890");
        var h2 = svc.HashBackupCode("ABCDEF1234567890");
        h1.Should().Be(h2);
    }

    [Fact]
    public void HashBackupCode_DifferentInputs_ProduceDifferentOutputs()
    {
        var svc = GetService();
        svc.HashBackupCode("ABCDEF1234567890")
            .Should().NotBe(svc.HashBackupCode("FEDCBA0987654321"));
    }

    [Fact]
    public void HashBackupCode_UsesSha256Prefix()
    {
        var svc = GetService();
        svc.HashBackupCode("ABCD").Should().StartWith("sha256:");
    }

    // ── VerifyBackupCode — sha256 path ────────────────────────────────────────

    [Fact]
    public void VerifyBackupCode_Sha256_MatchingCode_ReturnsTrue()
    {
        var svc  = GetService();
        var code = "ABCDEF1234567890";
        var hash = svc.HashBackupCode(code);
        svc.VerifyBackupCode(code, hash).Should().BeTrue();
    }

    [Fact]
    public void VerifyBackupCode_Sha256_WrongCode_ReturnsFalse()
    {
        var svc  = GetService();
        var hash = svc.HashBackupCode("ABCDEF1234567890");
        svc.VerifyBackupCode("FEDCBA0987654321", hash).Should().BeFalse();
    }

    // ── VerifyBackupCode — legacy argon2 path (back-compat) ──────────────────

    [Fact]
    public void VerifyBackupCode_LegacyArgon2_MatchingCode_ReturnsTrue()
    {
        var svc  = GetService();
        var code = "ABCDEF1234567890";
        var argon2Hash = svc.Hash(code);          // Produces $argon2id$... format
        argon2Hash.Should().StartWith("$argon2id");
        svc.VerifyBackupCode(code, argon2Hash).Should().BeTrue();
    }

    [Fact]
    public void VerifyBackupCode_LegacyArgon2_WrongCode_ReturnsFalse()
    {
        var svc  = GetService();
        var hash = svc.Hash("ABCDEF1234567890");
        svc.VerifyBackupCode("wrong-code", hash).Should().BeFalse();
    }

    // ── DummyVerify ──────────────────────────────────────────────────────────

    [Fact]
    public void DummyVerify_AlwaysReturnsFalse()
    {
        var svc = GetService();
        svc.DummyVerify("anything").Should().BeFalse();
        svc.DummyVerify("").Should().BeFalse();
        svc.DummyVerify("another").Should().BeFalse();
    }

    [Fact]
    public void DummyVerify_ConsistentTimingAcrossCalls()
    {
        // Both calls must hit the cached dummy hash (covers the lock + cached-path branch).
        var svc = GetService();
        svc.DummyVerify("first");
        svc.DummyVerify("second");
        svc.DummyVerify("third").Should().BeFalse();
    }

    // ── Verify malformed-hash branch ──────────────────────────────────────────

    [Fact]
    public void Verify_MalformedStoredHash_ReturnsFalse()
    {
        var svc = GetService();
        svc.Verify("anything", "not-a-real-hash").Should().BeFalse();
        svc.Verify("anything", "$argon2id$too$short").Should().BeFalse();
        svc.Verify("anything", "").Should().BeFalse();
    }
}
