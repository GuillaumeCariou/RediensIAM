using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

/// <summary>
/// Tests for OtpCacheService userId-based methods.
/// These methods are currently not called from any controller (dead code) but the
/// implementation should still be correct and covered.
/// </summary>
[Collection("RediensIAM")]
public class OtpCacheServiceTests(TestFixture fixture)
{
    private OtpCacheService GetService() => fixture.GetService<OtpCacheService>();

    // ── StoreOtpAsync / VerifyOtpAsync ────────────────────────────────────────

    [Fact]
    public async Task StoreAndVerifyOtp_CorrectCode_ReturnsTrue()
    {
        await fixture.FlushCacheAsync();
        var svc    = GetService();
        var userId = Guid.NewGuid();

        await svc.StoreOtpAsync("test", userId, "123456");
        var result = await svc.VerifyOtpAsync("test", userId, "123456");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyOtp_WrongCode_ReturnsFalse()
    {
        await fixture.FlushCacheAsync();
        var svc    = GetService();
        var userId = Guid.NewGuid();

        await svc.StoreOtpAsync("test", userId, "111111");
        var result = await svc.VerifyOtpAsync("test", userId, "999999");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyOtp_NoEntryInCache_ReturnsFalse()
    {
        await fixture.FlushCacheAsync();
        var svc = GetService();

        var result = await svc.VerifyOtpAsync("test", Guid.NewGuid(), "000000");

        result.Should().BeFalse();
    }

    // ── EnforceSmsRateLimitAsync ──────────────────────────────────────────────

    [Fact]
    public async Task EnforceSmsRateLimit_UnderLimit_DoesNotThrow()
    {
        await fixture.FlushCacheAsync();
        var svc    = GetService();
        var userId = Guid.NewGuid();

        var act = async () => await svc.EnforceSmsRateLimitAsync(userId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnforceSmsRateLimit_ExceedsLimit_ThrowsRateLimitException()
    {
        await fixture.FlushCacheAsync();
        var svc    = GetService();
        var userId = Guid.NewGuid();

        // MaxSmsPerWindow is configured as 3 in tests
        await svc.EnforceSmsRateLimitAsync(userId);
        await svc.EnforceSmsRateLimitAsync(userId);
        await svc.EnforceSmsRateLimitAsync(userId);

        var act = async () => await svc.EnforceSmsRateLimitAsync(userId);

        await act.Should().ThrowAsync<RediensIAM.Exceptions.RateLimitException>()
            .WithMessage("Too many SMS*");
    }
}
