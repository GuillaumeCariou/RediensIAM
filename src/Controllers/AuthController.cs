using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using RediensIAM.Data;
using RediensIAM.Entities;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
public class AuthController(
    RediensIamDbContext db,
    HydraAdminService hydra,
    PasswordService passwords,
    OtpCacheService otp,
    LoginRateLimiter rateLimiter,
    AuditLogService audit,
    TotpEncryptionService totpEncryption,
    KetoService keto,
    IEmailService emailService,
    ISmsService smsService,
    IConfiguration config,
    ILogger<AuthController> logger) : ControllerBase
{
    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    [HttpGet("/auth/login")]
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

            if (req.Client?.ClientId == "client_admin_system")
                return Ok(new { project_name = "RediensIAM Admin", is_admin_login = true });

            var projectId = ExtractProjectId(req);
            if (projectId == null) return BadRequest(new { error = "missing_project_id" });

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == Guid.Parse(projectId) && p.Active);
            if (project == null) return BadRequest(new { error = "invalid_project" });

            return Ok(new
            {
                project_id = projectId,
                project_name = project.Name,
                theme = project.LoginTheme,
                has_custom_template = project.LoginTemplate != null,
                require_role = project.RequireRoleToLogin,
                allow_self_registration = project.AllowSelfRegistration,
                email_verification_enabled = project.EmailVerificationEnabled,
                sms_verification_enabled = project.SmsVerificationEnabled
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetLogin failed for challenge {Challenge}", login_challenge);
            return BadRequest(new { error = "invalid_challenge" });
        }
    }

    [HttpGet("/auth/login/theme")]
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
                project.LoginTheme,
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

    [HttpPost("/auth/login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest body)
    {
        if (await rateLimiter.IsBlockedAsync(Ip))
            return StatusCode(429, new { error = "rate_limited" });

        HydraLoginRequest req;
        try { req = await hydra.GetLoginRequestAsync(body.LoginChallenge); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Login: invalid challenge {Challenge}", body.LoginChallenge);
            return BadRequest(new { error = "invalid_challenge" });
        }

        if (req.Client?.ClientId == "client_admin_system")
            return await AdminLogin(body, req);

        var projectId = ExtractProjectId(req);
        if (projectId == null) return BadRequest(new { error = "missing_project_id" });

        var registeredProjectId = req.Client?.Metadata?.GetValueOrDefault("project_id")?.ToString();
        if (registeredProjectId != null && registeredProjectId != projectId)
        {
            var rejectUrl = await hydra.RejectLoginAsync(body.LoginChallenge, "access_denied", "project_id mismatch");
            return Ok(new { redirect_to = rejectUrl, error = "project_id_mismatch" });
        }

        var project = await db.Projects
            .Include(p => p.AssignedUserList)
            .FirstOrDefaultAsync(p => p.Id == Guid.Parse(projectId) && p.Active);

        if (project?.AssignedUserListId == null)
            return BadRequest(new { error = "project_not_ready" });

        User? user = null;
        if (body.Email != null)
        {
            user = await db.Users.FirstOrDefaultAsync(u =>
                u.UserListId == project.AssignedUserListId && u.Email == body.Email.ToLowerInvariant());
        }
        else if (body.Username != null)
        {
            var parts = body.Username.Split('#');
            if (parts.Length == 2)
                user = await db.Users.FirstOrDefaultAsync(u =>
                    u.UserListId == project.AssignedUserListId && u.Username == parts[0] && u.Discriminator == parts[1]);
        }

        if (user == null || !user.Active)
        {
            await rateLimiter.RecordFailureAsync(Ip, null);
            return Unauthorized(new { error = "invalid_credentials" });
        }

        if (user.LockedUntil.HasValue && user.LockedUntil > DateTimeOffset.UtcNow)
            return Unauthorized(new { error = "account_locked", locked_until = user.LockedUntil });

        if (!passwords.Verify(body.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= config.GetValue<int>("Security:MaxLoginAttempts", 5))
                user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(config.GetValue<int>("Security:LockoutMinutes", 15));
            await db.SaveChangesAsync();
            await rateLimiter.RecordFailureAsync(Ip, user.Id);
            return Unauthorized(new { error = "invalid_credentials" });
        }

        if (project.RequireRoleToLogin)
        {
            var hasRole = await db.UserProjectRoles.AnyAsync(r => r.UserId == user.Id && r.ProjectId == project.Id);
            if (!hasRole)
            {
                var rejectUrl = await hydra.RejectLoginAsync(body.LoginChallenge, "access_denied", "no_role_assigned");
                return Ok(new { redirect_to = rejectUrl, error = "no_role" });
            }
        }

        if (user.TotpEnabled)
        {
            HttpContext.Session.SetString("mfa_pending_user", user.Id.ToString());
            HttpContext.Session.SetString("mfa_pending_challenge", body.LoginChallenge);
            HttpContext.Session.SetString("mfa_pending_project", projectId);
            return Ok(new { requires_mfa = true, mfa_type = "totp" });
        }

        user.FailedLoginCount = 0;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await rateLimiter.ResetAsync(Ip, user.Id);

        var subject = $"{project.OrgId}:{user.Id}";
        var context = new Dictionary<string, object>
        {
            ["org_id"] = project.OrgId.ToString(),
            ["project_id"] = project.Id.ToString(),
            ["user_id"] = user.Id.ToString()
        };

        var redirectUrl = await hydra.AcceptLoginAsync(body.LoginChallenge, subject, context);
        await audit.RecordAsync(project.OrgId, project.Id, user.Id, "user.login");
        return Ok(new { redirect_to = redirectUrl });
    }

    [HttpPost("/auth/mfa/totp/verify")]
    public async Task<IActionResult> VerifyTotp([FromBody] TotpVerifyRequest body)
    {
        var userId = HttpContext.Session.GetString("mfa_pending_user");
        var challenge = HttpContext.Session.GetString("mfa_pending_challenge");
        var projectId = HttpContext.Session.GetString("mfa_pending_project");

        if (userId == null || challenge == null || projectId == null)
            return BadRequest(new { error = "no_mfa_session" });

        var user = await db.Users.FindAsync(Guid.Parse(userId));
        if (user?.TotpSecret == null) return BadRequest(new { error = "totp_not_configured" });

        if (await otp.IsTotpUsedAsync(user.Id, body.Code))
            return Unauthorized(new { error = "code_already_used" });

        var secret = totpEncryption.Decrypt(user.TotpSecret);
        var totp = new Totp(secret);
        if (!totp.VerifyTotp(body.Code, out _, new VerificationWindow(1, 1)))
            return Unauthorized(new { error = "invalid_totp" });

        await otp.StoreTotpUsedAsync(user.Id, body.Code);

        var project = await db.Projects.FindAsync(Guid.Parse(projectId));
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        HttpContext.Session.Remove("mfa_pending_user");
        HttpContext.Session.Remove("mfa_pending_challenge");
        HttpContext.Session.Remove("mfa_pending_project");

        var subject = $"{project!.OrgId}:{user.Id}";
        var context = new Dictionary<string, object>
        {
            ["org_id"] = project.OrgId.ToString(),
            ["project_id"] = projectId,
            ["user_id"] = user.Id.ToString()
        };
        var redirectUrl = await hydra.AcceptLoginAsync(challenge, subject, context);
        await audit.RecordAsync(project!.OrgId, project.Id, user.Id, "user.login.mfa");
        return Ok(new { redirect_to = redirectUrl });
    }

    [HttpGet("/auth/consent")]
    public async Task<IActionResult> GetConsent([FromQuery] string consent_challenge)
    {
        var req = await hydra.GetConsentRequestAsync(consent_challenge);

        var context = req.Context;
        var userIdStr = context?.GetValueOrDefault("user_id")?.ToString();

        if (userIdStr == null) return BadRequest(new { error = "missing_context" });
        var userId = Guid.Parse(userIdStr);

        if (req.Client?.ClientId == "client_admin_system")
        {
            var adminRoles = new List<string>();
            if (await keto.CheckAsync("System", "rediensiam", "super_admin", $"user:{userId}"))
                adminRoles.Add("super_admin");
            if (await keto.HasAnyRelationAsync("Organisations", "org_admin", $"user:{userId}"))
                adminRoles.Add("org_admin");
            if (await keto.HasAnyRelationAsync("Projects", "manager", $"user:{userId}"))
                adminRoles.Add("project_manager");

            if (adminRoles.Count == 0)
            {
                var rejectUrl = await hydra.RejectConsentAsync(consent_challenge, "access_denied", "insufficient_role");
                return Redirect(rejectUrl);
            }

            // Resolve the org and project scopes so the token carries them
            var orgRole = await db.OrgRoles
                .Where(r => r.UserId == userId && r.Role == "org_admin")
                .OrderBy(r => r.GrantedAt)
                .FirstOrDefaultAsync();
            var projectRole = await db.OrgRoles
                .Where(r => r.UserId == userId && r.Role == "project_manager")
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

        var projectIdStr = context?.GetValueOrDefault("project_id")?.ToString();
        var orgIdStr = context?.GetValueOrDefault("org_id")?.ToString();

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

    [HttpGet("/auth/logout")]
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

    [HttpPost("/auth/logout")]
    public async Task<IActionResult> AcceptLogout([FromBody] LogoutRequest body)
    {
        var redirectUrl = await hydra.AcceptLogoutAsync(body.LogoutChallenge);
        return Ok(new { redirect_to = redirectUrl });
    }

    [HttpPost("/auth/register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest body)
    {
        HydraLoginRequest req;
        try { req = await hydra.GetLoginRequestAsync(body.LoginChallenge); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Register: invalid challenge {Challenge}", body.LoginChallenge);
            return BadRequest(new { error = "invalid_challenge" });
        }

        var projectId = ExtractProjectId(req);
        if (projectId == null) return BadRequest(new { error = "missing_project_id" });

        var project = await db.Projects
            .Include(p => p.AssignedUserList)
            .FirstOrDefaultAsync(p => p.Id == Guid.Parse(projectId) && p.Active);

        if (project == null) return NotFound(new { error = "project_not_found" });
        if (!project.AllowSelfRegistration) return StatusCode(403, new { error = "registration_not_allowed" });
        if (project.AssignedUserListId == null) return BadRequest(new { error = "project_not_ready" });

        var email = body.Email.ToLowerInvariant();

        if (project.AllowedEmailDomains.Length > 0)
        {
            var domain = email.Split('@').LastOrDefault() ?? "";
            if (!project.AllowedEmailDomains.Contains(domain))
                return StatusCode(403, new { error = "domain_not_allowed" });
        }

        if (await db.Users.AnyAsync(u => u.UserListId == project.AssignedUserListId && u.Email == email))
            return Conflict(new { error = "email_already_exists" });

        var verificationEnabled = project.EmailVerificationEnabled || project.SmsVerificationEnabled;

        if (!verificationEnabled)
        {
            var user = BuildUser(project.AssignedUserListId!.Value, email, body.Username, body.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            await keto.WriteRelationTupleAsync("UserLists", project.AssignedUserListId!.Value.ToString(), "member", $"user:{user.Id}");
            await audit.RecordAsync(project.OrgId, project.Id, user.Id, "user.registered");

            var subject = $"{project.OrgId}:{user.Id}";
            var ctx = new Dictionary<string, object>
            {
                ["org_id"] = project.OrgId.ToString(),
                ["project_id"] = project.Id.ToString(),
                ["user_id"] = user.Id.ToString()
            };
            var redirectUrl = await hydra.AcceptLoginAsync(body.LoginChallenge, subject, ctx);
            return Ok(new { redirect_to = redirectUrl });
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        var pending = System.Text.Json.JsonSerializer.Serialize(new
        {
            email,
            username = body.Username ?? email.Split('@')[0],
            password_hash = passwords.Hash(body.Password),
            project_id = projectId,
            user_list_id = project.AssignedUserListId!.Value.ToString(),
            org_id = project.OrgId.ToString(),
            login_challenge = body.LoginChallenge
        });

        await otp.StorePendingAsync("reg", sessionId, pending);
        await otp.StoreSessionOtpAsync("reg", sessionId, code);

        if (project.EmailVerificationEnabled)
            await emailService.SendOtpAsync(email, code, "registration");
        else if (project.SmsVerificationEnabled && body.Phone != null)
            await smsService.SendOtpAsync(body.Phone, code, "registration");

        return Ok(new { requires_verification = true, session_id = sessionId });
    }

    [HttpPost("/auth/register/verify")]
    public async Task<IActionResult> VerifyRegistration([FromBody] VerifyOtpRequest body)
    {
        if (!await otp.VerifySessionOtpAsync("reg", body.SessionId, body.Code))
            return BadRequest(new { error = "invalid_code" });

        var pendingJson = await otp.GetAndDeletePendingAsync("reg", body.SessionId);
        if (pendingJson == null) return BadRequest(new { error = "session_expired" });

        using var doc = System.Text.Json.JsonDocument.Parse(pendingJson);
        var root = doc.RootElement;

        var email = root.GetProperty("email").GetString()!;
        var username = root.GetProperty("username").GetString()!;
        var passwordHash = root.GetProperty("password_hash").GetString()!;
        var userListId = Guid.Parse(root.GetProperty("user_list_id").GetString()!);
        var orgId = Guid.Parse(root.GetProperty("org_id").GetString()!);
        var projId = Guid.Parse(root.GetProperty("project_id").GetString()!);
        var loginChallenge = root.GetProperty("login_challenge").GetString()!;

        if (await db.Users.AnyAsync(u => u.UserListId == userListId && u.Email == email))
            return Conflict(new { error = "email_already_exists" });

        var discriminator = Random.Shared.Next(1000, 9999).ToString();
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
        await keto.WriteRelationTupleAsync("UserLists", userListId.ToString(), "member", $"user:{user.Id}");
        await audit.RecordAsync(orgId, projId, user.Id, "user.registered");

        var subject = $"{orgId}:{user.Id}";
        var ctx = new Dictionary<string, object>
        {
            ["org_id"] = orgId.ToString(),
            ["project_id"] = projId.ToString(),
            ["user_id"] = user.Id.ToString()
        };
        var redirectUrl = await hydra.AcceptLoginAsync(loginChallenge, subject, ctx);
        return Ok(new { redirect_to = redirectUrl });
    }

    [HttpGet("/auth/verify-email")]
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

    [HttpPost("/auth/password-reset/request")]
    public async Task<IActionResult> RequestPasswordReset([FromBody] PasswordResetRequestBody body)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == body.ProjectId && p.Active);
        if (project?.AssignedUserListId == null || (!project.EmailVerificationEnabled && !project.SmsVerificationEnabled))
            return BadRequest(new { error = "verification_not_configured" });

        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.UserListId == project.AssignedUserListId && u.Email == body.Email.ToLowerInvariant());

        if (user != null)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
            await otp.StorePendingAsync("reset", sessionId, user.Id.ToString());
            await otp.StoreSessionOtpAsync("reset", sessionId, code);

            if (project.EmailVerificationEnabled)
                await emailService.SendOtpAsync(user.Email, code, "password_reset");
            else if (project.SmsVerificationEnabled)
                await smsService.SendOtpAsync(body.Phone ?? user.Email, code, "password_reset");

            return Ok(new { session_id = sessionId });
        }

        // Return 200 without session_id to prevent email enumeration
        return Ok(new { });
    }

    [HttpPost("/auth/password-reset/verify")]
    public async Task<IActionResult> VerifyPasswordReset([FromBody] VerifyOtpRequest body)
    {
        if (!await otp.VerifySessionOtpAsync("reset", body.SessionId, body.Code))
            return Unauthorized(new { error = "invalid_code" });

        var userIdStr = await otp.GetAndDeletePendingAsync("reset", body.SessionId);
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

    [HttpPost("/auth/password-reset/confirm")]
    public async Task<IActionResult> ConfirmPasswordReset([FromBody] PasswordResetConfirmBody body)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body.Token)));
        var token = await db.EmailTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == "reset_password");

        if (token == null || token.ExpiresAt < DateTimeOffset.UtcNow || token.UsedAt != null)
            return BadRequest(new { error = "invalid_or_expired_token" });

        token.User.PasswordHash = passwords.Hash(body.NewPassword);
        token.User.FailedLoginCount = 0;
        token.User.LockedUntil = null;
        token.UsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { message = "password_reset" });
    }

    private async Task<IActionResult> AdminLogin(LoginRequest body, HydraLoginRequest req)
    {
        if (body.Email == null) return BadRequest(new { error = "email_required" });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == body.Email.ToLowerInvariant());
        if (user == null || !user.Active)
        {
            await rateLimiter.RecordFailureAsync(Ip, null);
            return Unauthorized(new { error = "invalid_credentials" });
        }

        if (user.LockedUntil.HasValue && user.LockedUntil > DateTimeOffset.UtcNow)
            return Unauthorized(new { error = "account_locked", locked_until = user.LockedUntil });

        if (!passwords.Verify(body.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= config.GetValue<int>("Security:MaxLoginAttempts", 5))
                user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(config.GetValue<int>("Security:LockoutMinutes", 15));
            await db.SaveChangesAsync();
            await rateLimiter.RecordFailureAsync(Ip, user.Id);
            return Unauthorized(new { error = "invalid_credentials" });
        }

        var hasSuperAdmin = await keto.CheckAsync("System", "rediensiam", "super_admin", $"user:{user.Id}");
        var hasOrgAdmin = !hasSuperAdmin && await keto.HasAnyRelationAsync("Organisations", "org_admin", $"user:{user.Id}");
        var hasProjManager = !hasSuperAdmin && !hasOrgAdmin && await keto.HasAnyRelationAsync("Projects", "manager", $"user:{user.Id}");

        if (!hasSuperAdmin && !hasOrgAdmin && !hasProjManager)
            return Unauthorized(new { error = "insufficient_role" });

        user.FailedLoginCount = 0;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await rateLimiter.ResetAsync(Ip, user.Id);

        var context = new Dictionary<string, object> { ["user_id"] = user.Id.ToString() };
        var redirectUrl = await hydra.AcceptLoginAsync(body.LoginChallenge, user.Id.ToString(), context);
        return Ok(new { redirect_to = redirectUrl });
    }

    private User BuildUser(Guid userListId, string email, string? username, string password) => new()
    {
        UserListId = userListId,
        Username = username ?? email.Split('@')[0],
        Discriminator = Random.Shared.Next(1000, 9999).ToString(),
        Email = email,
        PasswordHash = passwords.Hash(password),
        Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static string? ExtractProjectId(HydraLoginRequest req)
    {
        var extra = req.OidcContext?.Extra;
        if (extra?.TryGetValue("project_id", out var v) == true) return v?.ToString();

        var url = req.RequestUrl;
        var idx = url.IndexOf("project_id=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + "project_id=".Length;
        var end = url.IndexOf('&', start);
        return end < 0 ? url[start..] : url[start..end];
    }
}

public record LoginRequest(string LoginChallenge, string? Email, string? Username, string Password);
public record TotpVerifyRequest(string Code);
public record LogoutRequest(string LogoutChallenge);
public record RegisterRequest(string LoginChallenge, string Email, string Password, string? Username, string? Phone);
public record VerifyOtpRequest(string SessionId, string Code);
public record PasswordResetRequestBody(Guid ProjectId, string Email, string? Phone);
public record PasswordResetConfirmBody(string Token, string NewPassword);
