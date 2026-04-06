using Fido2NetLib;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Services;

namespace RediensIAM.Services;

/// <summary>Service bundle for AuthController — groups 11 dependencies to keep constructor ≤ 7 params (S107).</summary>
public sealed class AuthControllerServices(
    HydraService hydra,
    PasswordService passwords,
    OtpCacheService otp,
    LoginRateLimiter rateLimiter,
    AuditLogService audit,
    KetoService keto,
    IEmailService email,
    ISmsService sms,
    IFido2 fido2,
    SocialLoginService socialLogin,
    BreachCheckService breachCheck)
{
    public HydraService Hydra           => hydra;
    public PasswordService Passwords    => passwords;
    public OtpCacheService Otp          => otp;
    public LoginRateLimiter RateLimiter => rateLimiter;
    public AuditLogService Audit        => audit;
    public KetoService Keto             => keto;
    public IEmailService Email          => email;
    public ISmsService Sms              => sms;
    public IFido2 Fido2                 => fido2;
    public SocialLoginService SocialLogin => socialLogin;
    public BreachCheckService BreachCheck => breachCheck;
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
