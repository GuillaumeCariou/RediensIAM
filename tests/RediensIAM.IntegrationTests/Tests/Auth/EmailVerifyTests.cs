using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

[Collection("RediensIAM")]
public class EmailVerifyTests(TestFixture fixture)
{
    private static string TokenHash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private async Task<(User user, string rawToken)> SeedTokenAsync(
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? usedAt    = null)
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var user     = await fixture.Seed.CreateUserAsync(list.Id);
        user.EmailVerified = false;

        var raw   = Guid.NewGuid().ToString("N");
        var token = new EmailToken
        {
            Id        = Guid.NewGuid(),
            UserId    = user.Id,
            Kind      = "verify_email",
            TokenHash = TokenHash(raw),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(24),
            UsedAt    = usedAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.EmailTokens.Add(token);
        await fixture.Db.SaveChangesAsync();
        return (user, raw);
    }

    // ── Valid token ───────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyEmail_ValidToken_Returns200AndMarksVerified()
    {
        var (user, raw) = await SeedTokenAsync();

        var res = await fixture.Client.GetAsync($"/auth/verify-email?token={raw}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("email_verified");

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.EmailVerified.Should().BeTrue();
        updated.EmailVerifiedAt.Should().NotBeNull();
    }

    // ── Invalid token ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyEmail_InvalidToken_Returns400()
    {
        var res = await fixture.Client.GetAsync("/auth/verify-email?token=not-a-real-token");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_or_expired_token");
    }

    // ── Expired token ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyEmail_ExpiredToken_Returns400()
    {
        var (_, raw) = await SeedTokenAsync(expiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        var res = await fixture.Client.GetAsync($"/auth/verify-email?token={raw}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_or_expired_token");
    }

    // ── Already-used token ────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyEmail_AlreadyUsedToken_Returns400()
    {
        var (_, raw) = await SeedTokenAsync(usedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var res = await fixture.Client.GetAsync($"/auth/verify-email?token={raw}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_or_expired_token");
    }

    // ── Token consumed after use ──────────────────────────────────────────────

    [Fact]
    public async Task VerifyEmail_ValidToken_SetsUsedAt()
    {
        var (user, raw) = await SeedTokenAsync();

        await fixture.Client.GetAsync($"/auth/verify-email?token={raw}");

        await fixture.RefreshDbAsync();
        var hash  = TokenHash(raw);
        var token = fixture.Db.EmailTokens.FirstOrDefault(t => t.TokenHash == hash);
        token!.UsedAt.Should().NotBeNull();
    }
}
