using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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
[Route("auth")]
public class AuthController(
    RediensIamDbContext db,
    AuthControllerServices svc,
    AppConfig appConfig,
    Microsoft.Extensions.Caching.Distributed.IDistributedCache cache,
    ILogger<AuthController> logger) : ControllerBase
{
    // Unwrap bundle — keeps method bodies unchanged while satisfying S107
    private HydraService hydra            => svc.Hydra;
    private PasswordService passwords      => svc.Passwords;
    private OtpCacheService otp            => svc.Otp;
    private LoginRateLimiter rateLimiter   => svc.RateLimiter;
    private AuditLogService audit          => svc.Audit;
    private KetoService keto               => svc.Keto;
    private IEmailService emailService     => svc.Email;
    private ISmsService smsService         => svc.Sms;
    private IFido2 fido2                   => svc.Fido2;
    private SocialLoginService socialLogin  => svc.SocialLogin;
    private BreachCheckService breachCheck  => svc.BreachCheck;
    private const string MfaPendingUser      = "mfa_pending_user";
    private const string MfaPendingProject   = "mfa_pending_project";
    private const string MfaPendingChallenge = "mfa_pending_challenge";
    private const string ErrRateLimited      = "rate_limited";
    private const string ErrMissingProjectId = "missing_project_id";
    private const string ErrInvalidChallenge = "invalid_challenge";
    private const string ErrAccessDenied     = "access_denied";
    private const string ErrProjectNotReady  = "project_not_ready";
    private const string ErrInvalidCreds     = "invalid_credentials";
    private const string ErrNoMfaSession     = "no_mfa_session";
    private const string ErrInvalidCode      = "invalid_code";
    private const string ErrReset            = "reset";
    private const string CtxOrgId            = "org_id";
    private const string CtxProjectId        = "project_id";
    private const string CtxUserId           = "user_id";

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    [HttpGet("login")]
    public async Task<IActionResult> GetLogin([FromQuery] string login_challenge)
    {
        try
        {
            var req = await hydra.GetLoginRequestAsync(login_challenge);
            if (req.Skip)
            {
                var redirect = await hydra.AcceptLoginAsync(login_challenge, req.Subject, []);
                return Redirect(redirect);
            }

            if (req.Client?.ClientId == Roles.AdminClientId)
                return Ok(new { project_name = "RediensIAM Admin", is_admin_login = true });

            var projectId = ExtractProjectId(req);
            if (projectId == null) return BadRequest(new { error = ErrMissingProjectId });

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == Guid.Parse(projectId) && p.Active);
            if (project == null) return BadRequest(new { error = "invalid_project" });

            return Ok(new
            {
                project_id = projectId,
                project_name = project.Name,
                theme = StripSecretsFromTheme(project.LoginTheme),
                has_custom_template = project.LoginTemplate != null,
                require_role = project.RequireRoleToLogin,
                allow_self_registration = project.AllowSelfRegistration,
                email_verification_enabled = project.EmailVerificationEnabled,
                sms_verification_enabled = project.SmsVerificationEnabled,
                min_password_length          = project.MinPasswordLength,
                password_require_uppercase   = project.PasswordRequireUppercase,
                password_require_lowercase   = project.PasswordRequireLowercase,
                password_require_digit       = project.PasswordRequireDigit,
                password_require_special     = project.PasswordRequireSpecial,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetLogin failed for challenge {Challenge}", login_challenge);
            return BadRequest(new { error = ErrInvalidChallenge });
        }
    }

    [HttpGet("login/theme")]
    public async Task<IActionResult> GetTheme([FromQuery] string login_challenge)
    {
        try
        {
            var req = await hydra.GetLoginRequestAsync(login_challenge);
            var projectId = ExtractProjectId(req);
            if (projectId == null) return BadRequest();

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == Guid.Parse(projectId));
            if (project == null) return NotFound();

            return Ok(new
            {
                login_theme = StripSecretsFromTheme(project.LoginTheme),
                has_custom_template = project.LoginTemplate != null,
                project.Name
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetTheme failed for challenge {Challenge}", login_challenge);
            return BadRequest();
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest body)
    {
        if (await rateLimiter.IsBlockedAsync(Ip))
            return StatusCode(429, new { error = ErrRateLimited });

        HydraLoginRequest req;
        try { req = await hydra.GetLoginRequestAsync(body.LoginChallenge); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Login: invalid challenge {Challenge}", body.LoginChallenge);
            return BadRequest(new { error = ErrInvalidChallenge });
        }

        if (req.Client?.ClientId == Roles.AdminClientId)
            return await AdminLogin(body);

        var projectId = ExtractProjectId(req);
        if (projectId == null) return BadRequest(new { error = ErrMissingProjectId });

        var registeredProjectId = req.Client?.Metadata?.GetValueOrDefault(CtxProjectId)?.ToString();
        if (registeredProjectId != null && registeredProjectId != projectId)
        {
            var rejectUrl = await hydra.RejectLoginAsync(body.LoginChallenge, ErrAccessDenied, "project_id mismatch");
            return Ok(new { redirect_to = rejectUrl, error = "project_id_mismatch" });
        }

        var project = await db.Projects
            .Include(p => p.AssignedUserList)
            .FirstOrDefaultAsync(p => p.Id == Guid.Parse(projectId) && p.Active);

        if (project?.AssignedUserListId == null)
            return BadRequest(new { error = ErrProjectNotReady });

        var user = await LookupUserByCredentialsAsync(project, body);
        if (user == null || !user.Active)
        {
            await rateLimiter.RecordFailureAsync(Ip, null);
            IamMetrics.LoginAttempts.WithLabels("failure").Inc();
            return Unauthorized(new { error = ErrInvalidCreds });
        }

        var credErr = await CheckUserCredentialsAsync(user, body);
        if (credErr != null) return credErr;

        var accessErr = await CheckProjectAccessAsync(user, project, body.LoginChallenge);
        if (accessErr != null) return accessErr;

        var mfaResult = await InitiateMfaAsync(user, project, projectId, body.LoginChallenge);
        if (mfaResult != null) return mfaResult;

        return await CompleteLoginAsync(user, project, body.LoginChallenge);
    }

    private async Task<User?> LookupUserByCredentialsAsync(Project project, LoginRequest body)
    {
        if (body.Email != null)
        {
            var emailLower = body.Email.ToLowerInvariant();
            return await db.Users.FirstOrDefaultAsync(u =>
                u.UserListId == project.AssignedUserListId && u.Email == emailLower);
        }

        if (body.Username != null)
        {
            var parts = body.Username.Split('#');
            if (parts.Length == 2)
                return await db.Users.FirstOrDefaultAsync(u =>
                    u.UserListId == project.AssignedUserListId && u.Username == parts[0] && u.Discriminator == parts[1]);
        }
        return null;
    }

    private async Task<IActionResult?> CheckUserCredentialsAsync(User user, LoginRequest body)
    {
        if (user.LockedUntil.HasValue && user.LockedUntil > DateTimeOffset.UtcNow)
        {
            IamMetrics.LoginAttempts.WithLabels("locked").Inc();
            return Unauthorized(new { error = "account_locked", locked_until = user.LockedUntil });
        }

        if (user.PasswordHash == null || !passwords.Verify(body.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= appConfig.MaxLoginAttempts)
                user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(appConfig.LockoutMinutes);
            await db.SaveChangesAsync();
            await rateLimiter.RecordFailureAsync(Ip, user.Id);
            IamMetrics.LoginAttempts.WithLabels("failure").Inc();
            return Unauthorized(new { error = ErrInvalidCreds });
        }
        return null;
    }

    private async Task<IActionResult?> CheckProjectAccessAsync(User user, Project project, string loginChallenge)
    {
        if (project.IpAllowlist.Length > 0 &&
            (!System.Net.IPAddress.TryParse(Ip, out var clientIp) ||
             !project.IpAllowlist.Any(cidr => IpInRange(clientIp, cidr))))
        {
            await audit.RecordAsync(project.OrgId, project.Id, user.Id, "user.login.failure");
            IamMetrics.LoginAttempts.WithLabels("ip_blocked").Inc();
            return Unauthorized(new { error = "ip_not_allowed" });
        }

        if (project.RequireRoleToLogin)
        {
            var hasRole = await db.UserProjectRoles.AnyAsync(r => r.UserId == user.Id && r.ProjectId == project.Id);
            if (!hasRole)
            {
                var rejectUrl = await hydra.RejectLoginAsync(loginChallenge, ErrAccessDenied, "no_role_assigned");
                return Redirect(rejectUrl);
            }
        }
        return null;
    }

    private async Task<IActionResult?> InitiateMfaAsync(User user, Project project, string projectId, string loginChallenge)
    {
        if (project.RequireMfa)
        {
            var hasMfa = user.TotpEnabled || user.PhoneVerified ||
                         await db.WebAuthnCredentials.AnyAsync(w => w.UserId == user.Id);
            if (!hasMfa)
            {
                HttpContext.Session.SetString("mfa_setup_required", "true");
                SetMfaSession(user.Id.ToString(), loginChallenge, projectId);
                IamMetrics.LoginAttempts.WithLabels("mfa_setup_required").Inc();
                return Ok(new { requires_mfa_setup = true });
            }
        }

        if (user.TotpEnabled)
        {
            SetMfaSession(user.Id.ToString(), loginChallenge, projectId);
            return Ok(new { requires_mfa = true, mfa_type = "totp" });
        }

        if (user.PhoneVerified && !string.IsNullOrEmpty(user.Phone))
        {
            SetMfaSession(user.Id.ToString(), loginChallenge, projectId);
            var smsCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
            await otp.StoreSessionOtpAsync("sms_mfa", user.Id.ToString(), smsCode);
            await smsService.SendOtpAsync(user.Phone, smsCode, "login");
            var masked = user.Phone.Length > 4
                ? new string('*', user.Phone.Length - 4) + user.Phone[^4..]
                : "****";
            return Ok(new { requires_mfa = true, mfa_type = "sms", phone_hint = masked });
        }

        if (user.WebAuthnEnabled)
        {
            SetMfaSession(user.Id.ToString(), loginChallenge, projectId);
            return Ok(new { requires_mfa = true, mfa_type = "webauthn" });
        }

        return null;
    }

    private void SetMfaSession(string userId, string loginChallenge, string? projectId)
    {
        HttpContext.Session.SetString(MfaPendingUser, userId);
        HttpContext.Session.SetString(MfaPendingChallenge, loginChallenge);
        HttpContext.Session.SetString(MfaPendingProject, projectId ?? "");
    }

    private async Task<IActionResult> CompleteLoginAsync(User user, Project project, string loginChallenge)
    {
        user.FailedLoginCount = 0;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await rateLimiter.ResetAsync(Ip, user.Id);

        var subject = $"{project.OrgId}:{user.Id}";
        var context = new Dictionary<string, object>
        {
            [CtxOrgId]     = project.OrgId.ToString(),
            [CtxProjectId] = project.Id.ToString(),
            [CtxUserId]    = user.Id.ToString()
        };

        var redirectUrl = await hydra.AcceptLoginAsync(loginChallenge, subject, context);
        await audit.RecordAsync(project.OrgId, project.Id, user.Id, "user.login");
        IamMetrics.LoginAttempts.WithLabels("success").Inc();
        _ = Task.Run(() => CheckNewDeviceAsync(user, Ip, Request.Headers.UserAgent.ToString()));
        return Ok(new { redirect_to = redirectUrl });
    }

    [HttpPost("mfa/backup-codes/verify")]
    public async Task<IActionResult> VerifyBackupCode([FromBody] BackupCodeVerifyRequest body)
    {
        var userId    = HttpContext.Session.GetString(MfaPendingUser);
        var challenge = HttpContext.Session.GetString(MfaPendingChallenge);
        var projectId = HttpContext.Session.GetString(MfaPendingProject);

        if (userId == null || challenge == null || projectId == null)
            return BadRequest(new { error = ErrNoMfaSession });
        if (!Guid.TryParse(userId, out var userGuid))
            return BadRequest(new { error = ErrNoMfaSession });

        if (await rateLimiter.IsBlockedAsync(Ip, userGuid))
            return StatusCode(429, new { error = ErrRateLimited });

        var allCodes = await db.BackupCodes
            .Where(c => c.UserId == userGuid && c.UsedAt == null)
            .ToListAsync();
        var submitted = body.Code.ToUpperInvariant();
        var code = allCodes.FirstOrDefault(bc => passwords.Verify(submitted, bc.CodeHash));
        if (code == null)
        {
            await rateLimiter.RecordFailureAsync(Ip, userGuid);
            return Unauthorized(new { error = ErrInvalidCode });
        }

        code.UsedAt = DateTimeOffset.UtcNow;
        var user = await db.Users.FindAsync(userGuid);
        return await CompleteMfaLoginAsync(user!, userGuid, challenge, projectId, "user.login.backup_code");
    }

    [HttpPost("mfa/phone/send")]
    public async Task<IActionResult> SendSmsOtp()
    {
        var userId = HttpContext.Session.GetString(MfaPendingUser);
        if (userId == null) return BadRequest(new { error = ErrNoMfaSession });
        var userGuid = Guid.Parse(userId);
        if (await rateLimiter.IsBlockedAsync(Ip, userGuid))
            return StatusCode(429, new { error = ErrRateLimited });
        var user = await db.Users.FindAsync(userGuid);
        if (user == null || !user.PhoneVerified || string.IsNullOrEmpty(user.Phone))
            return BadRequest(new { error = "phone_not_configured" });
        await otp.EnforceSmsRateLimitAsync(userGuid);
        var smsCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        await otp.StoreSessionOtpAsync("sms_mfa", userId, smsCode);
        await smsService.SendOtpAsync(user.Phone, smsCode, "login");
        return Ok(new { sent = true });
    }

    [HttpPost("mfa/phone/verify")]
    public async Task<IActionResult> VerifySmsOtp([FromBody] SmsOtpVerifyRequest body)
    {
        var userId    = HttpContext.Session.GetString(MfaPendingUser);
        var challenge = HttpContext.Session.GetString(MfaPendingChallenge);
        var projectId = HttpContext.Session.GetString(MfaPendingProject);
        if (userId == null || challenge == null || projectId == null)
            return BadRequest(new { error = ErrNoMfaSession });
        if (!Guid.TryParse(userId, out var userGuid))
            return BadRequest(new { error = ErrNoMfaSession });

        if (await rateLimiter.IsBlockedAsync(Ip, userGuid))
            return StatusCode(429, new { error = ErrRateLimited });

        if (!await otp.VerifySessionOtpAsync("sms_mfa", userId, body.Code))
        {
            await rateLimiter.RecordFailureAsync(Ip, userGuid);
            return Unauthorized(new { error = ErrInvalidCode });
        }

        var user = await db.Users.FindAsync(userGuid);
        if (user == null) return NotFound();
        return await CompleteMfaLoginAsync(user, userGuid, challenge, projectId, "user.login.sms");
    }

    [HttpPost("mfa/totp/verify")]
    public async Task<IActionResult> VerifyTotp([FromBody] TotpVerifyRequest body)
    {
        var userId = HttpContext.Session.GetString(MfaPendingUser);
        var challenge = HttpContext.Session.GetString(MfaPendingChallenge);
        var projectId = HttpContext.Session.GetString(MfaPendingProject);

        if (userId == null || challenge == null || projectId == null)
            return BadRequest(new { error = ErrNoMfaSession });
        if (!Guid.TryParse(userId, out var userGuid))
            return BadRequest(new { error = ErrNoMfaSession });

        if (await rateLimiter.IsBlockedAsync(Ip, userGuid))
            return StatusCode(429, new { error = ErrRateLimited });

        var user = await db.Users.FindAsync(userGuid);
        if (user?.TotpSecret == null) return BadRequest(new { error = "totp_not_configured" });

        if (await otp.IsTotpUsedAsync(user.Id, body.Code))
        {
            await rateLimiter.RecordFailureAsync(Ip, userGuid);
            return Unauthorized(new { error = "code_already_used" });
        }

        var secret = TotpEncryption.Decrypt(appConfig.TotpEncKey, user.TotpSecret);
        var totp = new Totp(secret);
        if (!totp.VerifyTotp(body.Code, out _, new VerificationWindow(1, 1)))
        {
            await rateLimiter.RecordFailureAsync(Ip, userGuid);
            return Unauthorized(new { error = "invalid_totp" });
        }

        await otp.StoreTotpUsedAsync(user.Id, body.Code);
        return await CompleteMfaLoginAsync(user, userGuid, challenge, projectId, "user.login.mfa");
    }

    [HttpGet("consent")]
    public async Task<IActionResult> GetConsent([FromQuery] string consent_challenge)
    {
        var req = await hydra.GetConsentRequestAsync(consent_challenge);

        var context = req.Context;
        var userIdStr = context?.GetValueOrDefault(CtxUserId)?.ToString();

        if (userIdStr == null) return BadRequest(new { error = "missing_context" });
        var userId = Guid.Parse(userIdStr);

        if (req.Client?.ClientId == Roles.AdminClientId)
        {
            var adminRoles = new List<string>();
            if (await keto.CheckAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{userId}"))
                adminRoles.Add(Roles.SuperAdmin);
            if (await keto.HasAnyRelationAsync(Roles.KetoOrgsNamespace, Roles.KetoOrgAdminRelation, $"user:{userId}"))
                adminRoles.Add(Roles.OrgAdmin);
            if (await keto.HasAnyRelationAsync(Roles.KetoProjectsNamespace, Roles.KetoManagerRelation, $"user:{userId}"))
                adminRoles.Add(Roles.ProjectAdmin);

            if (adminRoles.Count == 0)
            {
                var rejectUrl = await hydra.RejectConsentAsync(consent_challenge, ErrAccessDenied, "insufficient_role");
                return Redirect(rejectUrl);
            }

            // Resolve the org and project scopes so the token carries them
            var orgRole = await db.OrgRoles
                .Where(r => r.UserId == userId && r.Role == Roles.OrgAdmin)
                .OrderBy(r => r.GrantedAt)
                .FirstOrDefaultAsync();
            var projectRole = await db.OrgRoles
                .Where(r => r.UserId == userId && r.Role == Roles.ProjectAdmin)
                .OrderBy(r => r.GrantedAt)
                .FirstOrDefaultAsync();

            var adminSession = new
            {
                access_token = new
                {
                    user_id = userIdStr,
                    roles = adminRoles,
                    org_id = orgRole?.OrgId.ToString() ?? "",
                    project_id = projectRole?.ScopeId?.ToString() ?? ""
                }
            };
            var adminRedirect = await hydra.AcceptConsentAsync(consent_challenge, adminSession, req.RequestedScope);
            return Redirect(adminRedirect);
        }

        var projectIdStr = context?.GetValueOrDefault(CtxProjectId)?.ToString();
        var orgIdStr = context?.GetValueOrDefault(CtxOrgId)?.ToString();

        if (projectIdStr == null) return BadRequest(new { error = "missing_context" });

        var projectId = Guid.Parse(projectIdStr);

        var roles = await db.UserProjectRoles
            .Include(r => r.Role)
            .Where(r => r.UserId == userId && r.ProjectId == projectId)
            .Select(r => r.Role.Name)
            .ToListAsync();

        var session = new
        {
            access_token = new
            {
                org_id = orgIdStr,
                project_id = projectIdStr,
                user_id = userIdStr,
                roles
            },
            id_token = new
            {
                email = (await db.Users.FindAsync(userId))?.Email,
                org_id = orgIdStr,
                project_id = projectIdStr
            }
        };

        var redirectUrl = await hydra.AcceptConsentAsync(consent_challenge, session, req.RequestedScope);
        return Redirect(redirectUrl);
    }

    [HttpGet("logout")]
    public async Task<IActionResult> GetLogout([FromQuery] string logout_challenge)
    {
        try
        {
            await hydra.GetLogoutRequestAsync(logout_challenge);
            return Ok(new { logout_challenge });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetLogout: invalid challenge {Challenge}", logout_challenge);
            return BadRequest();
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> AcceptLogout([FromBody] LogoutRequest body)
    {
        var redirectUrl = await hydra.AcceptLogoutAsync(body.LogoutChallenge);
        return Ok(new { redirect_to = redirectUrl });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest body)
    {
        if (await rateLimiter.IsBlockedAsync(Ip, null, "register"))
            return StatusCode(429, new { error = ErrRateLimited });

        HydraLoginRequest req;
        try { req = await hydra.GetLoginRequestAsync(body.LoginChallenge); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Register: invalid challenge {Challenge}", body.LoginChallenge);
            return BadRequest(new { error = ErrInvalidChallenge });
        }

        var projectId = ExtractProjectId(req);
        if (projectId == null) return BadRequest(new { error = ErrMissingProjectId });

        var project = await db.Projects
            .Include(p => p.AssignedUserList)
            .FirstOrDefaultAsync(p => p.Id == Guid.Parse(projectId) && p.Active);

        if (project == null) return NotFound(new { error = "project_not_found" });
        if (!project.AllowSelfRegistration) return StatusCode(403, new { error = "registration_not_allowed" });
        if (project.AssignedUserListId == null) return BadRequest(new { error = ErrProjectNotReady });

        var policyErr = await ValidatePasswordPolicyAsync(project, body.Password);
        if (policyErr != null) return policyErr;

        var email = body.Email.ToLowerInvariant();
        var emailErr = await ValidateEmailForRegistrationAsync(project, email);
        if (emailErr != null) return emailErr;

        var verificationEnabled = project.EmailVerificationEnabled || project.SmsVerificationEnabled;

        if (!verificationEnabled)
            return await RegisterDirectAsync(project, email, body);

        return await RegisterWithVerificationAsync(project, projectId, email, body);
    }

    private async Task<IActionResult?> ValidatePasswordPolicyAsync(Project project, string password)
    {
        if (project.MinPasswordLength > 0 && password.Length < project.MinPasswordLength)
            return BadRequest(new { error = "password_too_short", min_length = project.MinPasswordLength });
        if (project.PasswordRequireUppercase && !password.Any(char.IsUpper))
            return BadRequest(new { error = "password_requires_uppercase" });
        if (project.PasswordRequireLowercase && !password.Any(char.IsLower))
            return BadRequest(new { error = "password_requires_lowercase" });
        if (project.PasswordRequireDigit && !password.Any(char.IsDigit))
            return BadRequest(new { error = "password_requires_digit" });
        if (project.PasswordRequireSpecial && !password.Any(c => !char.IsLetterOrDigit(c)))
            return BadRequest(new { error = "password_requires_special" });

        if (project.CheckBreachedPasswords)
        {
            var count = await breachCheck.GetBreachCountAsync(password);
            if (count > 0) return BadRequest(new { error = "password_breached", count });
        }
        return null;
    }

    private async Task<IActionResult?> ValidateEmailForRegistrationAsync(Project project, string email)
    {
        if (project.AllowedEmailDomains.Length > 0)
        {
            var domain = email.Split('@').LastOrDefault() ?? "";
            if (!project.AllowedEmailDomains.Contains(domain))
            {
                await rateLimiter.RecordFailureAsync(Ip, null, "register");
                return StatusCode(403, new { error = "domain_not_allowed" });
            }
        }

        if (await db.Users.AnyAsync(u => u.UserListId == project.AssignedUserListId && u.Email == email))
        {
            await rateLimiter.RecordFailureAsync(Ip, null, "register");
            return Conflict(new { error = "email_already_exists" });
        }
        return null;
    }

    private async Task<IActionResult> RegisterDirectAsync(Project project, string email, RegisterRequest body)
    {
        var user = await BuildUserAsync(project.AssignedUserListId!.Value, email, body.Username, body.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await keto.WriteRelationTupleAsync(Roles.KetoUserListsNamespace, project.AssignedUserListId!.Value.ToString(), "member", $"user:{user.Id}");
        await audit.RecordAsync(project.OrgId, project.Id, user.Id, "user.registered");
        await keto.AssignDefaultRoleAsync(project, user);

        var subject = $"{project.OrgId}:{user.Id}";
        var ctx = new Dictionary<string, object>
        {
            [CtxOrgId]     = project.OrgId.ToString(),
            [CtxProjectId] = project.Id.ToString(),
            [CtxUserId]    = user.Id.ToString()
        };
        var redirectUrl = await hydra.AcceptLoginAsync(body.LoginChallenge, subject, ctx);
        return Ok(new { redirect_to = redirectUrl });
    }

    private async Task<IActionResult> RegisterWithVerificationAsync(Project project, string projectId, string email, RegisterRequest body)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        var pending = System.Text.Json.JsonSerializer.Serialize(new
        {
            email,
            username      = body.Username ?? email.Split('@')[0],
            password_hash = passwords.Hash(body.Password),
            project_id    = projectId,
            user_list_id  = project.AssignedUserListId!.Value.ToString(),
            org_id        = project.OrgId.ToString(),
            login_challenge = body.LoginChallenge
        });

        await otp.StorePendingAsync("reg", sessionId, pending);
        await otp.StoreSessionOtpAsync("reg", sessionId, code);

        if (project.EmailVerificationEnabled)
            await emailService.SendOtpAsync(email, code, "registration", project.OrgId, project.Id);
        else if (project.SmsVerificationEnabled && body.Phone != null)
            await smsService.SendOtpAsync(body.Phone, code, "registration");

        return Ok(new { requires_verification = true, session_id = sessionId });
    }

    [HttpPost("register/verify")]
    public async Task<IActionResult> VerifyRegistration([FromBody] VerifyOtpRequest body)
    {
        if (!await otp.VerifySessionOtpAsync("reg", body.SessionId, body.Code))
            return BadRequest(new { error = ErrInvalidCode });

        var pendingJson = await otp.GetAndDeletePendingAsync("reg", body.SessionId);
        if (pendingJson == null) return BadRequest(new { error = "session_expired" });

        using var doc = System.Text.Json.JsonDocument.Parse(pendingJson);
        var root = doc.RootElement;

        var email = root.GetProperty("email").GetString()!;
        var username = root.GetProperty("username").GetString()!;
        var passwordHash = root.GetProperty("password_hash").GetString()!;
        var userListId = Guid.Parse(root.GetProperty("user_list_id").GetString()!);
        var orgId = Guid.Parse(root.GetProperty(CtxOrgId).GetString()!);
        var projId = Guid.Parse(root.GetProperty(CtxProjectId).GetString()!);
        var loginChallenge = root.GetProperty("login_challenge").GetString()!;

        // Verify project is still active and configured before creating user
        var regProject = await db.Projects.FindAsync(projId);
        if (regProject == null || !regProject.Active || regProject.AssignedUserListId == null)
            return BadRequest(new { error = "project_inactive" });

        if (await db.Users.AnyAsync(u => u.UserListId == userListId && u.Email == email))
            return Conflict(new { error = "email_already_exists" });

        var discriminator = await UserHelpers.GenerateDiscriminatorAsync(db, userListId, username);
        var user = new User
        {
            UserListId = userListId, Username = username,
            Discriminator = discriminator, Email = email,
            PasswordHash = passwordHash,
            EmailVerified = true, EmailVerifiedAt = DateTimeOffset.UtcNow,
            Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await keto.WriteRelationTupleAsync(Roles.KetoUserListsNamespace, userListId.ToString(), "member", $"user:{user.Id}");
        await audit.RecordAsync(orgId, projId, user.Id, "user.registered");
        await keto.AssignDefaultRoleAsync(regProject, user);

        var subject = $"{orgId}:{user.Id}";
        var ctx = new Dictionary<string, object>
        {
            [CtxOrgId] = orgId.ToString(),
            [CtxProjectId] = projId.ToString(),
            [CtxUserId] = user.Id.ToString()
        };
        var redirectUrl = await hydra.AcceptLoginAsync(loginChallenge, subject, ctx);
        return Ok(new { redirect_to = redirectUrl });
    }

    [HttpPost("invite/complete")]
    public async Task<IActionResult> CompleteInvite([FromBody] InviteCompleteRequest body)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(body.Token)));
        var token = await db.EmailTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == "invite");

        if (token == null || token.ExpiresAt < DateTimeOffset.UtcNow || token.UsedAt != null)
            return BadRequest(new { error = "invalid_or_expired_token" });

        // Check project password policy
        var userList = await db.UserLists.Include(ul => ul.Projects).FirstOrDefaultAsync(ul => ul.Id == token.User.UserListId);
        var inviteProject = userList?.Projects.FirstOrDefault();
        if (inviteProject != null)
        {
            var policyErr = await ValidatePasswordPolicyAsync(inviteProject, body.Password);
            if (policyErr != null) return policyErr;
        }
        if (inviteProject?.CheckBreachedPasswords == true)
        {
            var count = await breachCheck.GetBreachCountAsync(body.Password);
            if (count > 0) return BadRequest(new { error = "password_breached", count });
        }

        token.User.PasswordHash     = passwords.Hash(body.Password);
        token.User.Active           = true;
        token.User.EmailVerified    = true;
        token.User.EmailVerifiedAt  = DateTimeOffset.UtcNow;
        token.User.UpdatedAt        = DateTimeOffset.UtcNow;
        token.UsedAt                = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { message = "invite_accepted" });
    }

    [HttpGet("verify-email")]
    public async Task<IActionResult> VerifyEmail([FromQuery] string token)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        var emailToken = await db.EmailTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == "verify_email");

        if (emailToken == null || emailToken.ExpiresAt < DateTimeOffset.UtcNow || emailToken.UsedAt != null)
            return BadRequest(new { error = "invalid_or_expired_token" });

        emailToken.User.EmailVerified = true;
        emailToken.User.EmailVerifiedAt = DateTimeOffset.UtcNow;
        emailToken.UsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { message = "email_verified" });
    }

    [HttpPost("password-reset/request")]
    public async Task<IActionResult> RequestPasswordReset([FromBody] PasswordResetRequestBody body)
    {
        if (await rateLimiter.IsBlockedAsync(Ip, null, ErrReset))
            return StatusCode(429, new { error = ErrRateLimited });

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == body.ProjectId && p.Active);
        if (project?.AssignedUserListId == null || (!project.EmailVerificationEnabled && !project.SmsVerificationEnabled))
            return BadRequest(new { error = "verification_not_configured" });

        var emailLower = body.Email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.UserListId == project.AssignedUserListId && u.Email == emailLower);

        // Always generate code to keep compute time constant
        var sessionId = Guid.NewGuid().ToString("N");
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");

        if (user != null)
        {
            await otp.StorePendingAsync(ErrReset, sessionId, user.Id.ToString());
            await otp.StoreSessionOtpAsync(ErrReset, sessionId, code);

            if (project.EmailVerificationEnabled)
                await emailService.SendOtpAsync(user.Email, code, "password_reset", project.OrgId, project.Id);
            else if (project.SmsVerificationEnabled)
                await smsService.SendOtpAsync(body.Phone ?? user.Email, code, "password_reset");

            return Ok(new { session_id = sessionId });
        }

        // Constant-time: perform equivalent Redis writes to prevent timing-based email enumeration
        await otp.StorePendingAsync("reset:void", sessionId, "void");
        await otp.StoreSessionOtpAsync("reset:void", sessionId, code);
        await rateLimiter.RecordFailureAsync(Ip, null, ErrReset);
        return Ok(new { });
    }

    [HttpPost("password-reset/verify")]
    public async Task<IActionResult> VerifyPasswordReset([FromBody] VerifyOtpRequest body)
    {
        if (!await otp.VerifySessionOtpAsync(ErrReset, body.SessionId, body.Code))
            return Unauthorized(new { error = ErrInvalidCode });

        var userIdStr = await otp.GetAndDeletePendingAsync(ErrReset, body.SessionId);
        if (userIdStr == null) return BadRequest(new { error = "session_expired" });

        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        db.EmailTokens.Add(new EmailToken
        {
            UserId = Guid.Parse(userIdStr),
            Kind = "reset_password",
            TokenHash = hash,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return Ok(new { reset_token = raw });
    }

    [HttpPost("password-reset/confirm")]
    public async Task<IActionResult> ConfirmPasswordReset([FromBody] PasswordResetConfirmBody body)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body.Token)));
        var token = await db.EmailTokens
            .Include(t => t.User).ThenInclude(u => u.UserList).ThenInclude(ul => ul.Projects)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == "reset_password");

        if (token == null || token.ExpiresAt < DateTimeOffset.UtcNow || token.UsedAt != null)
            return BadRequest(new { error = "invalid_or_expired_token" });

        var resetProject = token.User.UserList.Projects.FirstOrDefault();
        if (resetProject != null)
        {
            var policyErr = await ValidatePasswordPolicyAsync(resetProject, body.NewPassword);
            if (policyErr != null) return policyErr;
        }

        token.User.PasswordHash = passwords.Hash(body.NewPassword);
        token.User.FailedLoginCount = 0;
        token.User.LockedUntil = null;
        token.UsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var subject = token.User.UserList.OrgId.HasValue ? $"{token.User.UserList.OrgId}:{token.User.Id}" : token.User.Id.ToString();
        await hydra.RevokeSessionsAsync(subject);
        return Ok(new { message = "password_reset" });
    }

    private async Task<bool> HasManagementRoleAsync(Guid userId)
    {
        var subject = $"user:{userId}";
        if (await keto.CheckAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, subject)) return true;
        if (await keto.HasAnyRelationAsync(Roles.KetoOrgsNamespace, Roles.KetoOrgAdminRelation, subject)) return true;
        return await keto.HasAnyRelationAsync(Roles.KetoProjectsNamespace, Roles.KetoManagerRelation, subject);
    }

    private async Task<IActionResult> AdminLogin(LoginRequest body)
    {
        if (body.Email == null) return BadRequest(new { error = "email_required" });

        // Admin console users must belong to the system user list (OrgId == null, Immovable).
        var emailLower = body.Email.ToLowerInvariant();
        var user = await db.Users
            .Include(u => u.UserList)
            .FirstOrDefaultAsync(u =>
                u.Email == emailLower &&
                u.UserList.OrgId == null &&
                u.UserList.Immovable);

        if (user == null || !user.Active)
        {
            await rateLimiter.RecordFailureAsync(Ip, null);
            return Unauthorized(new { error = ErrInvalidCreds });
        }

        if (user.LockedUntil.HasValue && user.LockedUntil > DateTimeOffset.UtcNow)
            return Unauthorized(new { error = "account_locked", locked_until = user.LockedUntil });

        if (user.PasswordHash == null || !passwords.Verify(body.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            user.LockedUntil = user.FailedLoginCount >= appConfig.MaxLoginAttempts
                ? DateTimeOffset.UtcNow.AddMinutes(appConfig.LockoutMinutes)
                : user.LockedUntil;
            await db.SaveChangesAsync();
            await rateLimiter.RecordFailureAsync(Ip, user.Id);
            return Unauthorized(new { error = ErrInvalidCreds });
        }

        if (!await HasManagementRoleAsync(user.Id))
            return Unauthorized(new { error = "insufficient_role" });

        user.FailedLoginCount = 0;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await rateLimiter.ResetAsync(Ip, user.Id);

        // Require MFA if the user has any factor configured
        var hasMfa = user.TotpEnabled || user.PhoneVerified ||
                     await db.WebAuthnCredentials.AnyAsync(w => w.UserId == user.Id);
        if (hasMfa)
        {
            SetMfaSession(user.Id.ToString(), body.LoginChallenge, null);
            return Ok(new { requires_mfa = true });
        }

        var context = new Dictionary<string, object> { [CtxUserId] = user.Id.ToString() };
        var redirectUrl = await hydra.AcceptLoginAsync(body.LoginChallenge, user.Id.ToString(), context);
        return Ok(new { redirect_to = redirectUrl });
    }

    private async Task CheckNewDeviceAsync(User user, string ip, string userAgent)
    {
        if (!user.NewDeviceAlertsEnabled) return;
        try
        {
            // Fingerprint: HMAC-SHA256 of "userAgent + /24 subnet"
            var subnet = ip.Contains('.') && System.Net.IPAddress.TryParse(ip, out var parsed)
                ? string.Join(".", parsed.GetAddressBytes().Take(3)) + ".0"
                : ip;
            var raw = $"{userAgent}|{subnet}";
            using var hmac = new HMACSHA256(appConfig.DeviceFpKey);
            var fingerprint = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw)));

            var cacheKey = $"device:{user.Id}:{fingerprint}";
            var known = await cache.GetAsync(cacheKey);
            await cache.SetAsync(cacheKey, [1], new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(appConfig.NewDeviceCacheDays)
            });

            if (known == null)
                await emailService.SendNewDeviceAlertAsync(user.Email, ip, userAgent, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "New device check failed for user {UserId}", user.Id);
        }
    }

    private static System.Net.IPAddress NormalizeIp(System.Net.IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4();
        if (ip.Equals(System.Net.IPAddress.IPv6Loopback)) return System.Net.IPAddress.Loopback;
        return ip;
    }

    private static bool IpInRange(System.Net.IPAddress ip, string cidr)
    {
        ip = NormalizeIp(ip);
        var parts = cidr.Split('/');
        if (!System.Net.IPAddress.TryParse(parts[0], out var network)) return false;
        network = NormalizeIp(network);

        if (ip.AddressFamily != network.AddressFamily) return false;
        if (parts.Length == 1) return ip.Equals(network);
        if (!int.TryParse(parts[1], out var prefixLen)) return false;

        var ipBytes  = ip.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (ipBytes.Length != netBytes.Length) return false;

        return ipBytes.Length == 4
            ? Ipv4InRange(ipBytes, netBytes, prefixLen)
            : Ipv6InRange(ipBytes, netBytes, prefixLen);
    }

    private static bool Ipv4InRange(byte[] ipBytes, byte[] netBytes, int prefixLen)
    {
        var mask   = prefixLen == 0 ? 0u : ~((1u << (32 - prefixLen)) - 1);
        var ipInt  = (uint)(ipBytes[0]  << 24 | ipBytes[1]  << 16 | ipBytes[2]  << 8 | ipBytes[3]);
        var netInt = (uint)(netBytes[0] << 24 | netBytes[1] << 16 | netBytes[2] << 8 | netBytes[3]);
        return (ipInt & mask) == (netInt & mask);
    }

    private static bool Ipv6InRange(byte[] ipBytes, byte[] netBytes, int prefixLen)
    {
        var fullBytes = prefixLen / 8;
        var remBits   = prefixLen % 8;
        for (var i = 0; i < fullBytes; i++)
            if (ipBytes[i] != netBytes[i]) return false;
        if (remBits > 0)
        {
            var byteMask = (byte)(0xFF << (8 - remBits));
            if ((ipBytes[fullBytes] & byteMask) != (netBytes[fullBytes] & byteMask)) return false;
        }
        return true;
    }

    private async Task<User> BuildUserAsync(Guid userListId, string email, string? username, string password)
    {
        var uname = username ?? email.Split('@')[0];
        string discriminator;
        var discIter = 0;
        do
        {
            if (++discIter > 100) throw new InvalidOperationException("discriminator_space_exhausted");
            discriminator = Random.Shared.Next(1000, 9999).ToString();
        }
        while (await db.Users.AnyAsync(u => u.UserListId == userListId && u.Username == uname && u.Discriminator == discriminator));
        return new User
        {
            UserListId = userListId,
            Username = uname,
            Discriminator = discriminator,
            Email = email,
            PasswordHash = passwords.Hash(password),
            Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    // ── OAuth2 social login ───────────────────────────────────────────────────

    [HttpGet("oauth2/start")]
    public async Task<IActionResult> OAuthStart(
        [FromQuery] string login_challenge,
        [FromQuery] string provider_id)
    {
        HydraLoginRequest req;
        try { req = await hydra.GetLoginRequestAsync(login_challenge); }
        catch { return BadRequest(new { error = ErrInvalidChallenge }); }

        var projectId = ExtractProjectId(req);
        if (projectId == null) return BadRequest(new { error = ErrMissingProjectId });

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == Guid.Parse(projectId) && p.Active);
        if (project?.AssignedUserListId == null) return BadRequest(new { error = ErrProjectNotReady });

        var providerCfg = GetProviderConfig(project.LoginTheme, provider_id);
        if (providerCfg == null) return BadRequest(new { error = "provider_not_found" });
        if (string.IsNullOrEmpty(providerCfg.ClientId)) return BadRequest(new { error = "provider_not_configured" });

        var (url, _) = await socialLogin.BuildAuthorizationUrlAsync(providerCfg, login_challenge, projectId);
        return Redirect(url);
    }

    // ── Link additional social provider to an already-authenticated user ──────

    [HttpGet("oauth2/link/start")]
    public async Task<IActionResult> OAuthLinkStart([FromQuery] string provider_id)
    {
        var claims = HttpContext.GetClaims();
        if (claims == null) return Unauthorized();

        var projectId = claims.ProjectId;
        if (string.IsNullOrEmpty(projectId)) return BadRequest(new { error = ErrMissingProjectId });

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == Guid.Parse(projectId) && p.Active);
        if (project?.AssignedUserListId == null) return BadRequest(new { error = ErrProjectNotReady });

        var providerCfg = GetProviderConfig(project.LoginTheme, provider_id);
        if (providerCfg == null) return BadRequest(new { error = "provider_not_found" });
        if (string.IsNullOrEmpty(providerCfg.ClientId)) return BadRequest(new { error = "provider_not_configured" });

        // Already linked?
        var alreadyLinked = await db.UserSocialAccounts.AnyAsync(s =>
            s.UserId == claims.ParsedUserId && s.Provider == provider_id);
        if (alreadyLinked) return BadRequest(new { error = "provider_already_linked" });

        var stateData = new OAuthStateData("", projectId, provider_id,
            LinkMode: true, LinkUserId: claims.UserId, LinkProjectId: projectId);
        var (url, _) = await socialLogin.BuildLinkAuthorizationUrlAsync(providerCfg, stateData);
        return Redirect(url);
    }

    [HttpGet("oauth2/callback")]
    public async Task<IActionResult> OAuthCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        if (state == null) return BadRequest(new { error = "missing_state" });

        var stateData = await socialLogin.ConsumeStateAsync(state);
        if (stateData == null) return BadRequest(new { error = "invalid_or_expired_state" });

        var errorRedirect = $"oauth2/error?login_challenge={Uri.EscapeDataString(stateData.LoginChallenge)}";

        if (error != null || code == null)
        {
            logger.LogWarning("OAuth2 callback error for provider {Provider}: {Error}", stateData.ProviderId, error);
            return Redirect(errorRedirect);
        }

        var project = await db.Projects
            .Include(p => p.AssignedUserList)
            .FirstOrDefaultAsync(p => p.Id == Guid.Parse(stateData.ProjectId) && p.Active);

        if (project?.AssignedUserListId == null) return Redirect(errorRedirect);

        var providerCfg = GetProviderConfig(project.LoginTheme, stateData.ProviderId);
        if (providerCfg == null) return Redirect(errorRedirect);

        var profile = await socialLogin.ExchangeAndGetProfileAsync(providerCfg, code);
        if (profile == null) return Redirect(errorRedirect);

        if (stateData.LinkMode && stateData.LinkUserId != null)
            return await HandleOAuthLinkModeAsync(stateData, profile);

        var user = await FindOrCreateSocialUserAsync(profile, stateData.ProviderId, project);
        if (user == null) return Redirect(errorRedirect);

        if (project.RequireRoleToLogin)
        {
            var hasRole = await db.UserProjectRoles.AnyAsync(r => r.UserId == user.Id && r.ProjectId == project.Id);
            if (!hasRole)
            {
                var rejectUrl = await hydra.RejectLoginAsync(stateData.LoginChallenge, ErrAccessDenied, "no_role_assigned");
                return Redirect(rejectUrl);
            }
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var subject = $"{project.OrgId}:{user.Id}";
        var ctx = new Dictionary<string, object>
        {
            [CtxOrgId]     = project.OrgId.ToString(),
            [CtxProjectId] = project.Id.ToString(),
            [CtxUserId]    = user.Id.ToString(),
        };

        var redirectTo = await hydra.AcceptLoginAsync(stateData.LoginChallenge, subject, ctx);
        await audit.RecordAsync(project.OrgId, project.Id, user.Id, $"user.login.social.{stateData.ProviderId}");
        return Redirect(redirectTo);
    }

    private async Task<IActionResult> HandleOAuthLinkModeAsync(OAuthStateData stateData, SocialUserProfile profile)
    {
        if (!Guid.TryParse(stateData.LinkUserId, out var linkUserId))
            return Redirect("/account?link_error=invalid_user");

        var existing = await db.UserSocialAccounts.AnyAsync(s =>
            s.Provider == stateData.ProviderId && s.ProviderUserId == profile.ProviderUserId);
        if (existing) return Redirect("/account?link_error=already_linked");

        db.UserSocialAccounts.Add(new UserSocialAccount
        {
            UserId         = linkUserId,
            Provider       = stateData.ProviderId,
            ProviderUserId = profile.ProviderUserId,
            Email          = profile.Email,
            LinkedAt       = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        if (Guid.TryParse(stateData.LinkProjectId, out var lpId))
        {
            var lpProject = await db.Projects.FindAsync(lpId);
            if (lpProject != null)
                await audit.RecordAsync(lpProject.OrgId, lpId, linkUserId,
                    $"user.social_linked.{stateData.ProviderId}", "user", linkUserId.ToString());
        }
        return Redirect("/account?link_success=1");
    }

    private async Task<User?> FindOrCreateSocialUserAsync(SocialUserProfile profile, string provider, Project project)
    {
        // Check allowed email domains before any provisioning
        if (project.AllowedEmailDomains.Length > 0 && !string.IsNullOrEmpty(profile.Email))
        {
            var domain = profile.Email.Split('@').LastOrDefault()?.ToLowerInvariant() ?? "";
            if (!project.AllowedEmailDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
                return null;
        }

        // 1. Check existing social link
        var social = await db.UserSocialAccounts
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Provider == provider && s.ProviderUserId == profile.ProviderUserId);

        if (social != null) return social.User;

        // 2. Try to link to existing user by email — only when BOTH sides have verified the email.
        // Linking on unverified email allows account takeover via attacker-controlled OAuth providers.
        User? user = null;
        if (!string.IsNullOrEmpty(profile.Email) && profile.IsEmailVerified)
        {
            var emailLower = profile.Email.ToLowerInvariant();
            user = await db.Users.FirstOrDefaultAsync(u =>
                u.UserListId == project.AssignedUserListId &&
                u.Email == emailLower &&
                u.EmailVerified &&
                u.Active);
        }

        // 3. Create new user if not found
        if (user == null)
        {
            user = await CreateSocialUserAsync(profile, project);
            if (user == null) return null;
        }

        // 4. Record the social link
        db.UserSocialAccounts.Add(new UserSocialAccount
        {
            UserId         = user.Id,
            Provider       = provider,
            ProviderUserId = profile.ProviderUserId,
            Email          = profile.Email,
            LinkedAt       = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return user;
    }

    private async Task<User?> CreateSocialUserAsync(SocialUserProfile profile, Project project)
    {
        if (string.IsNullOrEmpty(profile.Email)) return null;

        var email = profile.Email.ToLowerInvariant();
        var uname = Regex.Replace(
            profile.Name?.Split(' ')[0]?.ToLower() ?? email.Split('@')[0], @"[^a-z0-9_]", "",
            RegexOptions.None, TimeSpan.FromMilliseconds(100));
        if (string.IsNullOrEmpty(uname)) uname = "user";

        string discriminator;
        var discIter = 0;
        do
        {
            if (++discIter > 100) throw new InvalidOperationException("discriminator_space_exhausted");
            discriminator = Random.Shared.Next(1000, 9999).ToString();
        }
        while (await db.Users.AnyAsync(u =>
            u.UserListId == project.AssignedUserListId &&
            u.Username == uname &&
            u.Discriminator == discriminator));

        var user = new User
        {
            UserListId      = project.AssignedUserListId!.Value,
            Username        = uname,
            Discriminator   = discriminator,
            Email           = email,
            PasswordHash    = null,
            EmailVerified   = true,
            EmailVerifiedAt = DateTimeOffset.UtcNow,
            Active          = true,
            CreatedAt       = DateTimeOffset.UtcNow,
            UpdatedAt       = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await keto.WriteRelationTupleAsync(
            Roles.KetoUserListsNamespace,
            project.AssignedUserListId!.Value.ToString(),
            Roles.KetoMemberRelation,
            $"user:{user.Id}");

        await audit.RecordAsync(project.OrgId, project.Id, user.Id, "user.registered.social");
        await keto.AssignDefaultRoleAsync(project, user);
        return user;
    }

    private ProviderConfig? GetProviderConfig(Dictionary<string, object>? theme, string providerId)
    {
        if (theme == null || !theme.TryGetValue("providers", out var raw)) return null;
        if (raw is not JsonElement el || el.ValueKind != JsonValueKind.Array) return null;

        var encKey = appConfig.ThemeEncKey;
        foreach (var p in el.EnumerateArray())
        {
            var cfg = TryBuildProviderConfig(p, providerId, encKey);
            if (cfg != null) return cfg;
        }
        return null;
    }

    private static ProviderConfig? TryBuildProviderConfig(JsonElement p, string providerId, byte[] encKey)
    {
        if (!p.TryGetProperty("id", out var idProp) || idProp.GetString() != providerId) return null;
        if (p.TryGetProperty("enabled", out var enProp) && !enProp.GetBoolean()) return null;

        var type      = p.TryGetProperty("type",       out var t)  ? t.GetString()  ?? "" : "";
        var clientId  = p.TryGetProperty("client_id",  out var ci) ? ci.GetString() ?? "" : "";
        var issuerUrl = p.TryGetProperty("issuer_url", out var iu) ? iu.GetString() : null;
        return new ProviderConfig(providerId, type, clientId, ResolveProviderSecret(p, encKey), issuerUrl);
    }

    private static string ResolveProviderSecret(JsonElement p, byte[] encKey)
    {
        if (p.TryGetProperty("client_secret_enc", out var csEnc) && !string.IsNullOrEmpty(csEnc.GetString()))
        {
            try { return TotpEncryption.DecryptString(encKey, csEnc.GetString()!); }
            catch { /* corrupt/mismatched key — treat as no secret */ }
        }
        if (p.TryGetProperty("client_secret", out var cs))
            return cs.GetString() ?? "";
        return "";
    }

    private static Dictionary<string, object>? StripSecretsFromTheme(Dictionary<string, object>? theme)
        => TotpEncryption.StripSecretsFromTheme(theme);

    private static string? ExtractProjectId(HydraLoginRequest req)
    {
        var extra = req.OidcContext?.Extra;
        if (extra?.TryGetValue(CtxProjectId, out var v) == true) return v?.ToString();

        var url = req.RequestUrl;
        var idx = url.IndexOf("project_id=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + "project_id=".Length;
        var end = url.IndexOf('&', start);
        return end < 0 ? url[start..] : url[start..end];
    }

    // ── WebAuthn assertion ────────────────────────────────────────────────────

    [HttpGet("mfa/webauthn/options")]
    public async Task<IActionResult> WebAuthnOptions()
    {
        var userId = HttpContext.Session.GetString(MfaPendingUser);
        if (userId == null) return BadRequest(new { error = ErrNoMfaSession });
        if (!Guid.TryParse(userId, out var uid))
            return BadRequest(new { error = ErrNoMfaSession });

        var allowedCreds = await db.WebAuthnCredentials
            .Where(c => c.UserId == uid)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync();

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowedCreds,
            UserVerification   = UserVerificationRequirement.Preferred
        });

        HttpContext.Session.SetString("fido2.assertionOptions", options.ToJson());
        return Ok(options);
    }

    [HttpPost("mfa/webauthn/verify")]
    public async Task<IActionResult> WebAuthnVerify([FromBody] JsonElement body)
    {
        var userId    = HttpContext.Session.GetString(MfaPendingUser);
        var challenge = HttpContext.Session.GetString(MfaPendingChallenge);
        var projectId = HttpContext.Session.GetString(MfaPendingProject);
        if (userId == null || challenge == null || projectId == null)
            return BadRequest(new { error = ErrNoMfaSession });
        if (!Guid.TryParse(userId, out var uid))
            return BadRequest(new { error = ErrNoMfaSession });

        var json = HttpContext.Session.GetString("fido2.assertionOptions");
        if (json == null) return BadRequest(new { error = "no_assertion_options" });
        HttpContext.Session.Remove("fido2.assertionOptions");

        var options  = AssertionOptions.FromJson(json);
        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(body.GetRawText())!;

        var cred = await db.WebAuthnCredentials.FirstOrDefaultAsync(c => c.CredentialId == response.RawId);
        if (cred == null) return Unauthorized(new { error = "unknown_credential" });
        IsUserHandleOwnerOfCredentialIdAsync isOwner = async (args, ct) =>
            await db.WebAuthnCredentials.AnyAsync(c => c.CredentialId == args.CredentialId && c.UserId == uid, ct);

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse                   = response,
                OriginalOptions                     = options,
                StoredPublicKey                     = cred.PublicKey,
                StoredSignatureCounter              = (uint)cred.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = isOwner
            });
        }
        catch (Exception)
        {
            return Unauthorized(new { error = "assertion_failed" });
        }

        cred.SignCount  = (long)result.SignCount;
        cred.LastUsedAt = DateTimeOffset.UtcNow;

        var user = await db.Users.FindAsync(uid);
        if (user == null) return NotFound();
        return await CompleteMfaLoginAsync(user, uid, challenge, projectId, "user.login.webauthn", resetRateLimit: false);
    }

    private async Task<IActionResult> CompleteMfaLoginAsync(User user, Guid userGuid, string challenge, string projectId, string auditEvent, bool resetRateLimit = true)
    {
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        if (resetRateLimit) await rateLimiter.ResetAsync(Ip, userGuid);

        HttpContext.Session.Remove(MfaPendingUser);
        HttpContext.Session.Remove(MfaPendingChallenge);
        HttpContext.Session.Remove(MfaPendingProject);
        // Rotate session to prevent session fixation: clearing all data invalidates the pre-MFA session state.
        HttpContext.Session.Clear();

        string redirectUrl;
        if (projectId == "")
        {
            var ctx = new Dictionary<string, object> { [CtxUserId] = user.Id.ToString() };
            redirectUrl = await hydra.AcceptLoginAsync(challenge, user.Id.ToString(), ctx);
            await audit.RecordAsync(null, null, user.Id, auditEvent);
        }
        else
        {
            var project = await db.Projects.FindAsync(Guid.Parse(projectId));
            var subject  = $"{project!.OrgId}:{user.Id}";
            var context  = new Dictionary<string, object>
            {
                [CtxOrgId] = project.OrgId.ToString(), [CtxProjectId] = projectId, [CtxUserId] = user.Id.ToString()
            };
            redirectUrl = await hydra.AcceptLoginAsync(challenge, subject, context);
            await audit.RecordAsync(project.OrgId, project.Id, user.Id, auditEvent);
        }
        return Ok(new { redirect_to = redirectUrl });
    }
}

public record BackupCodeVerifyRequest(string Code);
public record LoginRequest(string LoginChallenge, string? Email, string? Username, string Password);
public record TotpVerifyRequest(string Code);
public record SmsOtpVerifyRequest(string Code);
public record LogoutRequest(string LogoutChallenge);
public record RegisterRequest(string LoginChallenge, string Email, string Password, string? Username, string? Phone);
public record VerifyOtpRequest(string SessionId, string Code);
public record PasswordResetRequestBody([property: System.Text.Json.Serialization.JsonRequired] Guid ProjectId, string Email, string? Phone);
public record PasswordResetConfirmBody(string Token, string NewPassword);
public record InviteCompleteRequest(string Token, string Password);
