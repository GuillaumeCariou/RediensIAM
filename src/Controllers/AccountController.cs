using System.Security.Cryptography;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
[Route("account")]
public class AccountController(
    RediensIamDbContext db,
    PasswordService passwords,
    AuditLogService audit,
    HydraService hydra,
    AppConfig appConfig,
    ISmsService smsService,
    OtpCacheService otpCache,
    IFido2 fido2) : ControllerBase
{
    // /account/* routes are protected by GatewayAuthMiddleware — Claims is always non-null here.
    private TokenClaims Claims => HttpContext.GetClaims()!;

    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var user = await db.Users.FindAsync(Claims.ParsedUserId);
        if (user == null) return NotFound();
        return Ok(new
        {
            user.Id, user.Username, user.Discriminator, user.Email,
            user.DisplayName, user.EmailVerified, user.TotpEnabled,
            user.WebAuthnEnabled, user.LastLoginAt, user.NewDeviceAlertsEnabled,
            roles      = Claims.Roles,
            project_id = Claims.ProjectId,
            org_id     = Claims.OrgId
        });
    }

    [HttpPatch("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateMeRequest body)
    {
        var user = await db.Users.FindAsync(Claims.ParsedUserId);
        if (user == null) return NotFound();
        if (body.DisplayName != null) user.DisplayName = body.DisplayName;
        if (body.NewDeviceAlertsEnabled.HasValue) user.NewDeviceAlertsEnabled = body.NewDeviceAlertsEnabled.Value;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { user.Id, user.DisplayName, user.NewDeviceAlertsEnabled });
    }

    [HttpPatch("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest body)
    {
        var userId = Claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        if (user.PasswordHash == null || !passwords.Verify(body.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "invalid_current_password" });
        user.PasswordHash = passwords.Hash(body.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(Guid.TryParse(Claims.OrgId, out var oid) ? oid : null, null, userId, "user.password_changed");
        return Ok(new { message = "password_changed" });
    }

    [HttpPost("mfa/totp/setup")]
    public async Task<IActionResult> SetupTotp()
    {
        var user = await db.Users.FindAsync(Claims.ParsedUserId);
        if (user == null) return NotFound();
        var secret = KeyGeneration.GenerateRandomKey(20);
        var encrypted = TotpEncryption.Encrypt(Convert.FromHexString(appConfig.TotpSecretEncryptionKey), secret);
        HttpContext.Session.SetString("totp_setup_secret", encrypted);
        var base32 = Base32Encoding.ToString(secret);
        var issuer = "RediensIAM";
        var otpAuthUrl = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(user.Email)}?secret={base32}&issuer={Uri.EscapeDataString(issuer)}";
        return Ok(new { otpauth_url = otpAuthUrl, secret = base32 });
    }

    [HttpPost("mfa/totp/confirm")]
    public async Task<IActionResult> ConfirmTotp([FromBody] TotpConfirmRequest body)
    {
        var userId = Claims.ParsedUserId;
        var encryptedSecret = HttpContext.Session.GetString("totp_setup_secret");
        if (encryptedSecret == null) return BadRequest(new { error = "no_setup_session" });
        var secret = TotpEncryption.Decrypt(Convert.FromHexString(appConfig.TotpSecretEncryptionKey), encryptedSecret);
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
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToUpper();
            return (code, hash: Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))));
        }).ToList();
        db.BackupCodes.RemoveRange(db.BackupCodes.Where(c => c.UserId == userId));
        db.BackupCodes.AddRange(backupCodes.Select(c => new BackupCode
        {
            UserId = userId, CodeHash = c.hash, CreatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();
        return Ok(new { message = "totp_enabled", backup_codes = backupCodes.Select(c => c.code).ToList() });
    }

    [HttpPost("mfa/backup-codes")]
    public async Task<IActionResult> RegenerateBackupCodes()
    {
        var userId = Claims.ParsedUserId;
        var codes = Enumerable.Range(0, 8).Select(_ =>
        {
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToUpper();
            return (code, hash: Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))));
        }).ToList();
        db.BackupCodes.RemoveRange(db.BackupCodes.Where(c => c.UserId == userId));
        db.BackupCodes.AddRange(codes.Select(c => new BackupCode
        {
            UserId = userId, CodeHash = c.hash, CreatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();
        return Ok(new { backup_codes = codes.Select(c => c.code).ToList() });
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions()
    {
        var subject = string.IsNullOrEmpty(Claims.OrgId) ? Claims.UserId : $"{Claims.OrgId}:{Claims.ParsedUserId}";
        var sessions = await hydra.ListConsentSessionsAsync(subject);
        return Ok(sessions.Select(s => new
        {
            client_id   = s.ConsentRequest?.Client?.ClientId,
            client_name = s.ConsentRequest?.Client?.ClientName,
            granted_at  = s.GrantedAt,
            expires_at  = s.ExpiresAt,
        }));
    }

    [HttpDelete("sessions")]
    public async Task<IActionResult> RevokeAllSessions()
    {
        var subject = string.IsNullOrEmpty(Claims.OrgId) ? Claims.UserId : $"{Claims.OrgId}:{Claims.ParsedUserId}";
        await hydra.RevokeAllConsentSessionsAsync(subject);
        return Ok(new { message = "all_sessions_revoked" });
    }

    [HttpDelete("sessions/{clientId}")]
    public async Task<IActionResult> RevokeSession(string clientId)
    {
        var subject = string.IsNullOrEmpty(Claims.OrgId) ? Claims.UserId : $"{Claims.OrgId}:{Claims.ParsedUserId}";
        await hydra.RevokeConsentSessionAsync(subject, clientId);
        return Ok(new { message = "session_revoked" });
    }

    // ── Phone / SMS MFA setup ─────────────────────────────────────────────────

    [HttpPost("mfa/phone/setup")]
    public async Task<IActionResult> SetupPhone([FromBody] PhoneSetupRequest body)
    {
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        HttpContext.Session.SetString("phone_setup_number", body.Phone);
        await otpCache.StoreSessionOtpAsync("phone_setup", Claims.UserId, code);
        await smsService.SendOtpAsync(body.Phone, code, "phone_setup");
        return Ok(new { sent = true });
    }

    [HttpPost("mfa/phone/verify")]
    public async Task<IActionResult> VerifyPhone([FromBody] PhoneVerifyRequest body)
    {
        var phone = HttpContext.Session.GetString("phone_setup_number");
        if (phone == null) return BadRequest(new { error = "no_setup_session" });
        if (!await otpCache.VerifySessionOtpAsync("phone_setup", Claims.UserId, body.Code))
            return BadRequest(new { error = "invalid_code" });
        var user = await db.Users.FindAsync(Claims.ParsedUserId);
        if (user == null) return NotFound();
        user.Phone = phone;
        user.PhoneVerified = true;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        HttpContext.Session.Remove("phone_setup_number");
        return Ok(new { message = "phone_verified" });
    }

    [HttpDelete("mfa/phone")]
    public async Task<IActionResult> RemovePhone()
    {
        var user = await db.Users.FindAsync(Claims.ParsedUserId);
        if (user == null) return NotFound();
        user.Phone = null;
        user.PhoneVerified = false;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { message = "phone_removed" });
    }

    [HttpGet("mfa")]
    public async Task<IActionResult> GetMfaStatus()
    {
        var userId = Claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        var backupCount = await db.BackupCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null);
        return Ok(new { user.TotpEnabled, user.WebAuthnEnabled, user.PhoneVerified, backup_codes_remaining = backupCount });
    }

    // ── WebAuthn / Passkeys ───────────────────────────────────────────────────

    [HttpPost("mfa/webauthn/register/begin")]
    public async Task<IActionResult> WebAuthnRegisterBegin()
    {
        var userId = Claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        var fido2User = new Fido2User
        {
            Id          = userId.ToByteArray(),
            Name        = user.Email,
            DisplayName = user.DisplayName ?? user.Username
        };
        var existingKeys = await db.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync();
        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User                   = fido2User,
            ExcludeCredentials     = existingKeys,
            AuthenticatorSelection = AuthenticatorSelection.Default,
            AttestationPreference  = AttestationConveyancePreference.None
        });
        HttpContext.Session.SetString("fido2.attestationOptions", options.ToJson());
        return Ok(options);
    }

    [HttpPost("mfa/webauthn/register/complete")]
    public async Task<IActionResult> WebAuthnRegisterComplete([FromBody] WebAuthnCompleteRequest body)
    {
        var userId = Claims.ParsedUserId;
        var json = HttpContext.Session.GetString("fido2.attestationOptions");
        if (json == null) return BadRequest(new { error = "no_registration_session" });
        HttpContext.Session.Remove("fido2.attestationOptions");
        var options     = CredentialCreateOptions.FromJson(json);
        var attestation = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
            JsonSerializer.Serialize(body.Response))!;
        IsCredentialIdUniqueToUserAsyncDelegate isUnique = async (args, _) =>
            !await db.WebAuthnCredentials.AnyAsync(c => c.CredentialId == args.CredentialId);
        RegisteredPublicKeyCredential result;
        try
        {
            result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse               = attestation,
                OriginalOptions                   = options,
                IsCredentialIdUniqueToUserCallback = isUnique
            });
        }
        catch (Exception ex) { return BadRequest(new { error = "attestation_failed", detail = ex.Message }); }
        db.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            Id           = Guid.NewGuid(),
            UserId       = userId,
            CredentialId = result.Id,
            PublicKey    = result.PublicKey,
            SignCount    = (long)result.SignCount,
            DeviceName   = body.DeviceName,
            CreatedAt    = DateTimeOffset.UtcNow
        });
        var user = await db.Users.FindAsync(userId);
        if (user != null) { user.WebAuthnEnabled = true; user.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync();
        return Ok(new { message = "passkey_registered" });
    }

    [HttpGet("mfa/webauthn/credentials")]
    public async Task<IActionResult> ListWebAuthnCredentials()
    {
        var userId = Claims.ParsedUserId;
        var creds = await db.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.Id, c.DeviceName, c.CreatedAt, c.LastUsedAt })
            .ToListAsync();
        return Ok(creds);
    }

    [HttpDelete("mfa/webauthn/credentials/{id}")]
    public async Task<IActionResult> DeleteWebAuthnCredential(Guid id)
    {
        var userId = Claims.ParsedUserId;
        var cred = await db.WebAuthnCredentials.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (cred == null) return NotFound();
        db.WebAuthnCredentials.Remove(cred);
        var remaining = await db.WebAuthnCredentials.CountAsync(c => c.UserId == userId && c.Id != id);
        if (remaining == 0)
        {
            var user = await db.Users.FindAsync(userId);
            if (user != null) { user.WebAuthnEnabled = false; user.UpdatedAt = DateTimeOffset.UtcNow; }
        }
        await db.SaveChangesAsync();
        return Ok(new { message = "credential_deleted" });
    }

    // ── Linked social accounts ────────────────────────────────────────────────

    [HttpGet("social-accounts")]
    public async Task<IActionResult> GetSocialAccounts()
    {
        var userId = Claims.ParsedUserId;
        var accounts = await db.UserSocialAccounts
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.LinkedAt)
            .Select(s => new { s.Id, s.Provider, s.Email, s.LinkedAt })
            .ToListAsync();
        return Ok(accounts);
    }

    [HttpDelete("social-accounts/{id}")]
    public async Task<IActionResult> UnlinkSocialAccount(Guid id)
    {
        var userId = Claims.ParsedUserId;
        var account = await db.UserSocialAccounts.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (account == null) return NotFound();

        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        // Guard: must not remove the last auth method
        var otherSocial = await db.UserSocialAccounts.CountAsync(s => s.UserId == userId && s.Id != id);
        if (user.PasswordHash == null && otherSocial == 0)
            return BadRequest(new { error = "cannot_remove_last_auth_method" });

        db.UserSocialAccounts.Remove(account);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record UpdateMeRequest(string? DisplayName, bool? NewDeviceAlertsEnabled);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record TotpConfirmRequest(string Code);
public record PhoneSetupRequest(string Phone);
public record PhoneVerifyRequest(string Code);
public record WebAuthnCompleteRequest(object Response, string? DeviceName);
