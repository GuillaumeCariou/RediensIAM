using Fido2NetLib;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Services;

namespace RediensIAM.Services;

/// <summary>Core auth dependencies (hydra, passwords, otp, rate limiter, audit, keto).</summary>
public sealed class AuthCoreServices(
    HydraService hydra,
    PasswordService passwords,
    OtpCacheService otp,
    LoginRateLimiter rateLimiter,
    AuditLogService audit,
    KetoService keto)
{
    public HydraService Hydra           => hydra;
    public PasswordService Passwords    => passwords;
    public OtpCacheService Otp          => otp;
    public LoginRateLimiter RateLimiter => rateLimiter;
    public AuditLogService Audit        => audit;
    public KetoService Keto             => keto;
}

/// <summary>Extended auth dependencies (email, sms, fido2, social login, breach check).</summary>
public sealed class AuthExtServices(
    IEmailService email,
    ISmsService sms,
    IFido2 fido2,
    SocialLoginService socialLogin,
    BreachCheckService breachCheck)
{
    public IEmailService Email              => email;
    public ISmsService Sms                  => sms;
    public IFido2 Fido2                     => fido2;
    public SocialLoginService SocialLogin   => socialLogin;
    public BreachCheckService BreachCheck   => breachCheck;
}

/// <summary>Service bundle for AuthController — composes AuthCoreServices + AuthExtServices (S107).</summary>
public sealed class AuthControllerServices(AuthCoreServices core, AuthExtServices ext)
{
    public HydraService Hydra             => core.Hydra;
    public PasswordService Passwords      => core.Passwords;
    public OtpCacheService Otp            => core.Otp;
    public LoginRateLimiter RateLimiter   => core.RateLimiter;
    public AuditLogService Audit          => core.Audit;
    public KetoService Keto               => core.Keto;
    public IEmailService Email            => ext.Email;
    public ISmsService Sms                => ext.Sms;
    public IFido2 Fido2                   => ext.Fido2;
    public SocialLoginService SocialLogin => ext.SocialLogin;
    public BreachCheckService BreachCheck => ext.BreachCheck;
}

/// <summary>Service bundle for AccountController — groups 5 dependencies to keep constructor ≤ 7 params (S107).</summary>
public sealed class AccountControllerServices(
    PasswordService passwords,
    HydraService hydra,
    ISmsService sms,
    OtpCacheService otp,
    IFido2 fido2)
{
    public PasswordService Passwords => passwords;
    public HydraService Hydra        => hydra;
    public ISmsService Sms           => sms;
    public OtpCacheService Otp       => otp;
    public IFido2 Fido2              => fido2;
}

/// <summary>Service bundle for OrgController / SystemAdminController — groups 6 dependencies (S107).</summary>
public sealed class OrgAdminServices(
    HydraService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    IEmailService email,
    IDistributedCache cache)
{
    public HydraService Hydra      => hydra;
    public KetoService Keto        => keto;
    public PasswordService Passwords => passwords;
    public AuditLogService Audit   => audit;
    public IEmailService Email     => email;
    public IDistributedCache Cache => cache;
}

/// <summary>Service bundle for ManagedApiController — groups 5 dependencies (S107).</summary>
public sealed class ManagedApiServices(
    HydraService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    IEmailService email)
{
    public HydraService Hydra        => hydra;
    public KetoService Keto          => keto;
    public PasswordService Passwords  => passwords;
    public AuditLogService Audit     => audit;
    public IEmailService Email       => email;
}
