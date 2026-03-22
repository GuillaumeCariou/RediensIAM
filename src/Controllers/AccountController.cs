using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using RediensIAM.Data;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
public class AccountController(
    RediensIamDbContext db,
    PasswordService passwords,
    TotpEncryptionService totpEncryption,
    AuditLogService audit,
    HydraAdminService hydra,
    ISmsService smsService,
    OtpCacheService otpCache) : ControllerBase
{
    private TokenClaims Claims => HttpContext.GetClaims()!;

    [HttpGet("/account/me")]
    public async Task<IActionResult> GetMe()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var userId = claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        return Ok(new
        {
            user.Id, user.Username, user.Discriminator, user.Email,
            user.DisplayName, user.EmailVerified, user.TotpEnabled,
            user.WebAuthnEnabled, user.LastLoginAt,
            roles = claims.Roles,
            project_id = claims.ProjectId,
            org_id = claims.OrgId
        });
    }

    [HttpPatch("/account/me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateMeRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var userId = claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        if (body.DisplayName != null) user.DisplayName = body.DisplayName;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { user.Id, user.DisplayName });
    }

    [HttpPost("/account/change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var userId = claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        if (!passwords.Verify(body.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "invalid_current_password" });
        user.PasswordHash = passwords.Hash(body.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(Guid.TryParse(claims.OrgId, out var oid) ? oid : null, null, userId, "user.password_changed");
        return Ok(new { message = "password_changed" });
    }

    [HttpPost("/account/mfa/totp/setup")]
    public async Task<IActionResult> SetupTotp()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var userId = claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        var secret = KeyGeneration.GenerateRandomKey(20);
        var encrypted = totpEncryption.Encrypt(secret);
        HttpContext.Session.SetString("totp_setup_secret", encrypted);

        var base32 = Base32Encoding.ToString(secret);
        var issuer = "RediensIAM";
        var otpAuthUrl = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(user.Email)}?secret={base32}&issuer={Uri.EscapeDataString(issuer)}";
        return Ok(new { otpauth_url = otpAuthUrl, secret = base32 });
    }

    [HttpPost("/account/mfa/totp/confirm")]
    public async Task<IActionResult> ConfirmTotp([FromBody] TotpConfirmRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var userId = claims.ParsedUserId;
        var encryptedSecret = HttpContext.Session.GetString("totp_setup_secret");
        if (encryptedSecret == null) return BadRequest(new { error = "no_setup_session" });

        var secret = totpEncryption.Decrypt(encryptedSecret);
        var totp = new Totp(secret);
        if (!totp.VerifyTotp(body.Code, out _, new VerificationWindow(1, 1)))
            return BadRequest(new { error = "invalid_code" });

        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        user.TotpSecret = encryptedSecret;
        user.TotpEnabled = true;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        HttpContext.Session.Remove("totp_setup_secret");

        var backupCodes = Enumerable.Range(0, 8).Select(_ =>
        {
            var code = Guid.NewGuid().ToString("N")[..8].ToUpper();
            return (code, hash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))));
        }).ToList();

        db.BackupCodes.RemoveRange(db.BackupCodes.Where(c => c.UserId == userId));
        db.BackupCodes.AddRange(backupCodes.Select(c => new Entities.BackupCode
        {
            UserId = userId, CodeHash = c.hash, CreatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();
        return Ok(new { message = "totp_enabled", backup_codes = backupCodes.Select(c => c.code).ToList() });
    }

    [HttpPost("/account/mfa/backup-codes/generate")]
    public async Task<IActionResult> RegenerateBackupCodes()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var userId = claims.ParsedUserId;

        var codes = Enumerable.Range(0, 8).Select(_ =>
        {
            var code = Guid.NewGuid().ToString("N")[..8].ToUpper();
            return (code, hash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))));
        }).ToList();

        db.BackupCodes.RemoveRange(db.BackupCodes.Where(c => c.UserId == userId));
        db.BackupCodes.AddRange(codes.Select(c => new Entities.BackupCode
        {
            UserId = userId, CodeHash = c.hash, CreatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();
        return Ok(new { backup_codes = codes.Select(c => c.code).ToList() });
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    [HttpGet("/account/sessions")]
    public async Task<IActionResult> GetSessions()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        // Subject format: "{org_id}:{user_id}" for project users, "{user_id}" for admin users
        var subject = string.IsNullOrEmpty(claims.OrgId)
            ? claims.UserId
            : $"{claims.OrgId}:{claims.ParsedUserId}";
        var sessions = await hydra.ListConsentSessionsAsync(subject);
        return Ok(sessions.Select(s => new
        {
            client_id   = s.ConsentRequest?.Client?.ClientId,
            client_name = s.ConsentRequest?.Client?.ClientName,
            granted_at  = s.GrantedAt,
            expires_at  = s.ExpiresAt,
        }));
    }

    [HttpDelete("/account/sessions")]
    public async Task<IActionResult> RevokeAllSessions()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var subject = string.IsNullOrEmpty(claims.OrgId)
            ? claims.UserId
            : $"{claims.OrgId}:{claims.ParsedUserId}";
        await hydra.RevokeAllConsentSessionsAsync(subject);
        return Ok(new { message = "all_sessions_revoked" });
    }

    [HttpDelete("/account/sessions/{clientId}")]
    public async Task<IActionResult> RevokeSession(string clientId)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var subject = string.IsNullOrEmpty(claims.OrgId)
            ? claims.UserId
            : $"{claims.OrgId}:{claims.ParsedUserId}";
        await hydra.RevokeConsentSessionAsync(subject, clientId);
        return Ok(new { message = "session_revoked" });
    }

    // ── Phone / SMS MFA setup ─────────────────────────────────────────────────

    [HttpPost("/account/phone/setup")]
    public async Task<IActionResult> SetupPhone([FromBody] PhoneSetupRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        HttpContext.Session.SetString("phone_setup_number", body.Phone);
        await otpCache.StoreSessionOtpAsync("phone_setup", claims.UserId, code);
        await smsService.SendOtpAsync(body.Phone, code, "phone_setup");
        return Ok(new { sent = true });
    }

    [HttpPost("/account/phone/verify")]
    public async Task<IActionResult> VerifyPhone([FromBody] PhoneVerifyRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var phone = HttpContext.Session.GetString("phone_setup_number");
        if (phone == null) return BadRequest(new { error = "no_setup_session" });
        if (!await otpCache.VerifySessionOtpAsync("phone_setup", claims.UserId, body.Code))
            return BadRequest(new { error = "invalid_code" });
        var user = await db.Users.FindAsync(claims.ParsedUserId);
        if (user == null) return NotFound();
        user.Phone = phone;
        user.PhoneVerified = true;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        HttpContext.Session.Remove("phone_setup_number");
        return Ok(new { message = "phone_verified" });
    }

    [HttpDelete("/account/phone")]
    public async Task<IActionResult> RemovePhone()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var user = await db.Users.FindAsync(claims.ParsedUserId);
        if (user == null) return NotFound();
        user.Phone = null;
        user.PhoneVerified = false;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { message = "phone_removed" });
    }

    [HttpGet("/account/mfa")]
    public async Task<IActionResult> GetMfaStatus()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var userId = claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        var backupCount = await db.BackupCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null);
        return Ok(new { user.TotpEnabled, user.WebAuthnEnabled, user.PhoneVerified, backup_codes_remaining = backupCount });
    }
}

public record UpdateMeRequest(string? DisplayName);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record TotpConfirmRequest(string Code);
public record PhoneSetupRequest(string Phone);
public record PhoneVerifyRequest(string Code);
