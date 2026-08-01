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
    AccountControllerServices svc,
    AuditLogService audit,
    AppConfig appConfig,
    ILogger<AccountController> logger) : ControllerBase
{
    // Bundle forwarders — the constructor takes one aggregate to satisfy S107; see ControllerServices.
    private PasswordService passwords    => svc.Passwords;
    private HydraService hydra           => svc.Hydra;
    private ISmsService smsService       => svc.Sms;
    private OtpCacheService otpCache     => svc.Otp;
    private IFido2 fido2                 => svc.Fido2;
    private LoginRateLimiter rateLimiter => svc.RateLimiter;
    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // Enrolment state lives in Redis keyed by the authenticated user, never in a cookie.
    private const string TotpSetupPrefix     = "totp_setup";
    private const string PhoneSetupPrefix    = "phone_setup_number";
    private const string WebAuthnSetupPrefix = "webauthn_setup";

    /// <summary>
    /// Every /account/* route sits behind <see cref="Middleware.GatewayAuthMiddleware"/>, which 401s
    /// before the action runs — which is what makes the null-forgiving operator safe here.
    /// </summary>
    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid? OrgId => Guid.TryParse(Claims.OrgId, out var oid) ? oid : null;

    // ── MFA re-authentication ─────────────────────────────────────────────────

    private const string ReauthPurpose = "mfareauth";

    /// <summary>
    /// Proves the caller still controls an authentication factor before an existing MFA factor
    /// is replaced or removed.
    ///
    /// A valid access token is not that proof — surviving a stolen token is the whole point of
    /// MFA. Without this, <c>ConfirmTotp</c> silently overwrites the victim's TOTP secret and
    /// reissues their backup codes, and the attacker's factor outlives the victim's password
    /// reset because <c>ChangePassword</c> revokes sessions but never touches the secret.
    ///
    /// Returns null when the caller re-authenticated, otherwise the response to send back.
    /// </summary>
    private async Task<IActionResult?> RequireReauthAsync(User user, MfaReauth? proof)
    {
        if (await rateLimiter.IsBlockedAsync(Ip, user.Id, ReauthPurpose))
            return StatusCode(429, new { error = "rate_limited" });

        if (VerifyCurrentPassword(user, proof?.CurrentPassword)) return null;
        if (await VerifyCurrentTotpAsync(user, proof?.TotpCode)) return null;

        // Step aside only when there is nothing to prove AND nothing to protect. The predicate
        // has to be HasAnyFactorAsync, not "no password and no TOTP": every social-login user is
        // provisioned with PasswordHash == null (AuthController.CreateSocialUserAsync), so the
        // old condition handed a bearer token full control of the factors of every federated
        // account whose second factor was SMS or a passkey. Such an account now gets 401 with an
        // empty `methods` and must go through password reset — a support cost, not a takeover.
        if (ReauthMethods(user).Length == 0 && !await HasAnyFactorAsync(user)) return null;

        await rateLimiter.RecordFailureAsync(Ip, user.Id, ReauthPurpose);
        return StatusCode(401, new
        {
            error   = "reauthentication_required",
            methods = ReauthMethods(user),
        });
    }

    /// <summary>
    /// True when a second factor already gates this account's logins.
    ///
    /// Step 4 guarded replacing and removing a factor. Adding one is the same takeover from the
    /// other side: a stolen token enrols the attacker's own authenticator, that factor then
    /// satisfies MFA on every future login, and unlike a stolen password it survives
    /// <c>ChangePassword</c> — which revokes sessions but does not touch enrolled factors.
    /// First enrolment on an account with no factor stays a one-step flow: there is nothing to
    /// take over and nothing to re-authenticate against.
    /// </summary>
    private async Task<bool> HasAnyFactorAsync(User user) =>
        user.TotpEnabled || user.PhoneVerified
        || await db.WebAuthnCredentials.AnyAsync(c => c.UserId == user.Id);

    private static string[] ReauthMethods(User user)
    {
        var methods = new List<string>(2);
        if (user.PasswordHash != null) methods.Add("current_password");
        if (user.TotpEnabled) methods.Add("totp_code");
        return [.. methods];
    }

    private bool VerifyCurrentPassword(User user, string? password) =>
        user.PasswordHash != null
        && !string.IsNullOrEmpty(password)
        && passwords.Verify(password, user.PasswordHash);

    private async Task<bool> VerifyCurrentTotpAsync(User user, string? code)
    {
        if (!user.TotpEnabled || user.TotpSecret == null || string.IsNullOrEmpty(code)) return false;
        // Same anti-replay window as the login path: a code observed once must not be reusable.
        if (await otpCache.IsTotpUsedAsync(user.Id, code)) return false;
        var secret = TotpEncryption.Decrypt(appConfig.TotpEncKey, user.TotpSecret);
        if (!new Totp(secret).VerifyTotp(code, out _, new VerificationWindow(1, 1))) return false;
        await otpCache.StoreTotpUsedAsync(user.Id, code);
        return true;
    }

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
        if (await rateLimiter.IsBlockedAsync(Ip, userId, "pwchange"))
            return StatusCode(429, new { error = "rate_limited" });
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        // Distinguish "passwordless account" from "wrong password" so we do NOT charge
        // the rate-limiter for users who can never satisfy the current_password check
        // (e.g. WebAuthn-only accounts).
        if (user.PasswordHash == null)
            return BadRequest(new { error = "set_password_required" });
        if (!passwords.Verify(body.CurrentPassword, user.PasswordHash))
        {
            await rateLimiter.RecordFailureAsync(Ip, userId, "pwchange");
            return BadRequest(new { error = "invalid_current_password" });
        }
        // Enforce the tenant's policy, not a hardcoded floor: otherwise a user signs up under
        // the project policy and then downgrades below it on their next password change.
        var project = Guid.TryParse(Claims.ProjectId, out var pid)
            ? await db.Projects.FirstOrDefaultAsync(p => p.Id == pid)
            : null;
        var (policy, breachCount) = await svc.PasswordPolicy.EvaluateAsync(project, body.NewPassword);
        if (policy != PasswordPolicyResult.Ok)
            return BadRequest(new
            {
                error      = PasswordPolicyService.ErrorCode(policy),
                min_length = PasswordPolicyService.EffectiveMinimumLength(project),
                count      = breachCount,
            });

        user.PasswordHash = passwords.Hash(body.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var orgId = Guid.TryParse(Claims.OrgId, out var oid) ? oid : (Guid?)null;
        // Audit FIRST so a Hydra outage cannot erase the password-change record.
        await audit.RecordAsync(orgId, null, userId, "user.password_changed");
        var subject = orgId.HasValue ? $"{orgId}:{user.Id}" : user.Id.ToString();
        var sessionsRevoked = true;
        try
        {
            await hydra.RevokeSessionsAsync(subject);
        }
        catch (Exception ex)
        {
            // Password is already saved; failing the request would leave a confusing UX.
            // Surface the revocation failure to the client (and to ops via log) so the
            // user can self-trigger a sign-out from all devices if needed.
            sessionsRevoked = false;
            logger.LogWarning(ex, "ChangePassword: Hydra session revocation failed for user {UserId}", userId);
        }
        return Ok(new { message = "password_changed", sessions_revoked = sessionsRevoked });
    }

    [HttpPost("mfa/totp/setup")]
    public async Task<IActionResult> SetupTotp()
    {
        var user = await db.Users.FindAsync(Claims.ParsedUserId);
        if (user == null) return NotFound();
        var secret = KeyGeneration.GenerateRandomKey(20);
        var encrypted = TotpEncryption.Encrypt(appConfig.TotpEncKey, secret);
        // Server-side, keyed by the bearer token's user. The ASP.NET session cookie is
        // SameSite=Strict, so it is not sent at all when the admin console runs on a different
        // origin from the API (the documented NodePort / Tailscale / private-ingress layout) —
        // enrolment simply could not complete there.
        await otpCache.StorePendingAsync(TotpSetupPrefix, Claims.UserId, encrypted,
            OtpCacheService.EnrolmentTtlSeconds);
        var base32 = Base32Encoding.ToString(secret);
        var issuer = "RediensIAM";
        if (Guid.TryParse(Claims.OrgId, out var orgGuid))
        {
            var org = await db.Organisations.FindAsync(orgGuid);
            if (org != null) issuer = org.Name;
        }
        var otpAuthUrl = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(user.Email)}?secret={base32}&issuer={Uri.EscapeDataString(issuer)}";
        // Recorded even though nothing is persisted yet: an enrolment started against an account
        // that already has TOTP is the first observable step of a factor takeover.
        await audit.RecordAsync(OrgId, null, user.Id, "user.mfa.totp_setup_started", null, null,
            new() { ["replacing_existing"] = user.TotpEnabled.ToString() });
        return Ok(new { otpauth_url = otpAuthUrl, secret = base32 });
    }

    [HttpPost("mfa/totp/confirm")]
    public async Task<IActionResult> ConfirmTotp([FromBody] TotpConfirmRequest body)
    {
        var userId = Claims.ParsedUserId;
        var encryptedSecret = await otpCache.PeekPendingAsync(TotpSetupPrefix, Claims.UserId);
        if (encryptedSecret == null) return BadRequest(new { error = "no_setup_session" });
        var secret = TotpEncryption.Decrypt(appConfig.TotpEncKey, encryptedSecret);
        var totp = new Totp(secret);
        if (!totp.VerifyTotp(body.Code, out _, new VerificationWindow(1, 1)))
            return BadRequest(new { error = "invalid_code" });
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        // Replacing a live factor is a takeover, not an enrolment: prove an existing one first.
        // The condition is "has any factor", not "has TOTP" — adding TOTP to a passkey-protected
        // account is the same escalation as overwriting its TOTP secret.
        if (await HasAnyFactorAsync(user) && await RequireReauthAsync(user, body.Reauth) is { } reauthErr)
            return reauthErr;
        var replaced = user.TotpEnabled;
        await otpCache.DeletePendingAsync(TotpSetupPrefix, Claims.UserId);
        user.TotpSecret = encryptedSecret;
        user.TotpEnabled = true;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var backupCodes = Enumerable.Range(0, 8).Select(_ =>
        {
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToUpper();
            return (code, hash: passwords.HashBackupCode(code));
        }).ToList();
        db.BackupCodes.RemoveRange(db.BackupCodes.Where(c => c.UserId == userId));
        db.BackupCodes.AddRange(backupCodes.Select(c => new BackupCode
        {
            UserId = userId, CodeHash = c.hash, CreatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, userId, replaced ? "user.mfa.totp_replaced" : "user.mfa.totp_enabled");
        return Ok(new { message = "totp_enabled", backup_codes = backupCodes.Select(c => c.code).ToList() });
    }

    [HttpPost("mfa/backup-codes")]
    public async Task<IActionResult> RegenerateBackupCodes([FromBody] MfaReauth? body = null)
    {
        var userId = Claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        // Regeneration invalidates every existing code — a recovery-factor takeover on its own.
        if (await RequireReauthAsync(user, body) is { } reauthErr) return reauthErr;
        var codes = Enumerable.Range(0, 8).Select(_ =>
        {
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToUpper();
            return (code, hash: passwords.HashBackupCode(code));
        }).ToList();
        db.BackupCodes.RemoveRange(db.BackupCodes.Where(c => c.UserId == userId));
        db.BackupCodes.AddRange(codes.Select(c => new BackupCode
        {
            UserId = userId, CodeHash = c.hash, CreatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, userId, "user.mfa.backup_codes_regenerated");
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
        await audit.RecordAsync(OrgId, null, Claims.ParsedUserId, "user.sessions_revoked");
        return Ok(new { message = "all_sessions_revoked" });
    }

    [HttpDelete("sessions/{clientId}")]
    public async Task<IActionResult> RevokeSession(string clientId)
    {
        var subject = string.IsNullOrEmpty(Claims.OrgId) ? Claims.UserId : $"{Claims.OrgId}:{Claims.ParsedUserId}";
        await hydra.RevokeConsentSessionAsync(subject, clientId);
        await audit.RecordAsync(OrgId, null, Claims.ParsedUserId, "user.session_revoked", "oauth2_client", clientId);
        return Ok(new { message = "session_revoked" });
    }

    // ── Phone / SMS MFA setup ─────────────────────────────────────────────────

    [HttpPost("mfa/phone/setup")]
    public async Task<IActionResult> SetupPhone([FromBody] PhoneSetupRequest body)
    {
        // The stub provider drops the message. Enrolling anyway makes SMS a factor the account
        // can never satisfy — and on a project with RequireMfa that is a lockout, not an
        // inconvenience. The login and registration paths already refuse for the same reason.
        if (!smsService.IsConfigured)
            return StatusCode(503, new { error = "sms_provider_not_configured" });

        await otpCache.EnforceSmsRateLimitAsync(Claims.ParsedUserId);
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        await otpCache.StorePendingAsync(PhoneSetupPrefix, Claims.UserId, body.Phone,
            OtpCacheService.EnrolmentTtlSeconds);
        await otpCache.StoreSessionOtpAsync("phone_setup", Claims.UserId, code);
        await smsService.SendOtpAsync(body.Phone, code, "phone_setup");
        return Ok(new { sent = true });
    }

    [HttpPost("mfa/phone/verify")]
    public async Task<IActionResult> VerifyPhone([FromBody] PhoneVerifyRequest body)
    {
        var phone = await otpCache.PeekPendingAsync(PhoneSetupPrefix, Claims.UserId);
        if (phone == null) return BadRequest(new { error = "no_setup_session" });
        if (!await otpCache.VerifySessionOtpAsync("phone_setup", Claims.UserId, body.Code))
            return BadRequest(new { error = "invalid_code" });
        var user = await db.Users.FindAsync(Claims.ParsedUserId);
        if (user == null) return NotFound();
        // Adding the attacker's number to an account that already has a factor — see HasAnyFactorAsync.
        if (await HasAnyFactorAsync(user) && await RequireReauthAsync(user, body.Reauth) is { } reauthErr)
            return reauthErr;
        await otpCache.DeletePendingAsync(PhoneSetupPrefix, Claims.UserId);
        user.Phone = phone;
        user.PhoneVerified = true;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, user.Id, "user.mfa.phone_verified");
        return Ok(new { message = "phone_verified" });
    }

    [HttpDelete("mfa/phone")]
    public async Task<IActionResult> RemovePhone([FromBody] MfaReauth? body = null)
    {
        var user = await db.Users.FindAsync(Claims.ParsedUserId);
        if (user == null) return NotFound();
        if (user.PhoneVerified && await RequireReauthAsync(user, body) is { } reauthErr) return reauthErr;
        user.Phone = null;
        user.PhoneVerified = false;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, user.Id, "user.mfa.phone_removed");
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
            User               = fido2User,
            ExcludeCredentials = existingKeys,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                // AuthenticatorSelection.Default emits userVerification=discouraged, while the
                // assertion path demands Required. A credential registered under it is a factor
                // the user can never actually use, and an authenticator that verifies anyway does
                // so by luck of its own configuration. As a second factor, possession of the key
                // is not the point — the PIN or biometric is.
                UserVerification = UserVerificationRequirement.Required,
                // Left at the library default: nothing here consumes a discoverable credential
                // (the assertion always supplies allowCredentials), but platform authenticators
                // create and sync them anyway and refusing would degrade passkey enrolment.
                ResidentKey = ResidentKeyRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None
        });
        await otpCache.StorePendingAsync(WebAuthnSetupPrefix, Claims.UserId, options.ToJson(),
            OtpCacheService.EnrolmentTtlSeconds);
        return Ok(options);
    }

    [HttpPost("mfa/webauthn/register/complete")]
    public async Task<IActionResult> WebAuthnRegisterComplete([FromBody] WebAuthnCompleteRequest body)
    {
        var userId = Claims.ParsedUserId;
        var registrant = await db.Users.FindAsync(userId);
        if (registrant == null) return NotFound();
        // Enrolling the attacker's own authenticator on an account that already has a factor — see
        // HasAnyFactorAsync. Checked before the pending options are consumed so a refused attempt
        // does not force the user to restart a legitimate registration.
        if (await HasAnyFactorAsync(registrant) && await RequireReauthAsync(registrant, body.Reauth) is { } reauthErr)
            return reauthErr;
        var json = await otpCache.GetAndDeletePendingAsync(WebAuthnSetupPrefix, Claims.UserId);
        if (json == null) return BadRequest(new { error = "no_registration_session" });
        var options     = CredentialCreateOptions.FromJson(json);
        var attestation = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
            JsonSerializer.Serialize(body.Response))!;
        IsCredentialIdUniqueToUserAsyncDelegate isUnique = async (args, ct) =>
            !await db.WebAuthnCredentials.AnyAsync(c => c.CredentialId == args.CredentialId, ct);
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
        catch (Exception)
        {
            return BadRequest(new { error = "attestation_failed" });
        }
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
        registrant.WebAuthnEnabled = true;
        registrant.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, userId, "user.mfa.passkey_registered");
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
    public async Task<IActionResult> DeleteWebAuthnCredential(Guid id, [FromBody] MfaReauth? body = null)
    {
        var userId = Claims.ParsedUserId;
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        var cred = await db.WebAuthnCredentials.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (cred == null) return NotFound();
        if (await RequireReauthAsync(user, body) is { } reauthErr) return reauthErr;
        db.WebAuthnCredentials.Remove(cred);
        var remaining = await db.WebAuthnCredentials.CountAsync(c => c.UserId == userId && c.Id != id);
        if (remaining == 0) { user.WebAuthnEnabled = false; user.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, userId, "user.mfa.passkey_removed", "webauthn_credential", id.ToString());
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

        var otherSocial = await db.UserSocialAccounts.CountAsync(s => s.UserId == userId && s.Id != id);
        if (user.PasswordHash == null && otherSocial == 0)
            return BadRequest(new { error = "cannot_remove_last_auth_method" });

        db.UserSocialAccounts.Remove(account);
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, userId, "user.social_account_unlinked", "social_account", id.ToString(),
            new() { ["provider"] = account.Provider });
        return NoContent();
    }
}

public record UpdateMeRequest(string? DisplayName, bool? NewDeviceAlertsEnabled);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Proof that the caller still holds an existing authentication factor. Required by every
/// endpoint that replaces or removes an MFA factor — a bearer token alone is not proof.
/// </summary>
public record MfaReauth(string? CurrentPassword, string? TotpCode);

public record TotpConfirmRequest(string Code, MfaReauth? Reauth = null);
public record PhoneSetupRequest(string Phone);
public record PhoneVerifyRequest(string Code, MfaReauth? Reauth = null);
public record WebAuthnCompleteRequest(object Response, string? DeviceName, MfaReauth? Reauth = null);
