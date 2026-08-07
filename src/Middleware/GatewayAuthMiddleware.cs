using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc.Controllers;
using RediensIAM.Config;
using RediensIAM.Filters;
using RediensIAM.Services;

namespace RediensIAM.Middleware;

public class GatewayAuthMiddleware(RequestDelegate next, AppConfig appConfig)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 401;
            return;
        }

        var token = header["Bearer ".Length..].Trim();
        TokenClaims? claims;

        if (token.StartsWith(appConfig.PatPrefix, StringComparison.Ordinal))
        {
            var patService = ctx.RequestServices.GetRequiredService<PatService>();
            var result = await patService.IntrospectAsync(token);
            claims = result is { Active: true }
                ? new TokenClaims { UserId = result.Sub, OrgId = result.OrgId, ProjectId = result.ProjectId, Roles = result.Roles, IsServiceAccount = true }
                : null;
        }
        else
        {
            var hydra = ctx.RequestServices.GetRequiredService<HydraService>();
            claims = await hydra.ValidateJwtAsync(token);
        }

        if (claims is null)
        {
            ctx.Response.StatusCode = 401;
            return;
        }

        // Audience gate. Introspection tells us the token is valid; it does not tell us the
        // token was meant for THIS surface. Without this check any access token issued by the
        // deployment's Hydra — including one minted for a tenant's own application — reaches
        // the management API, with only ext.roles standing in the way.
        if (IsManagementSurface(ctx.Request.Path) && !IsManagementAudience(claims, appConfig))
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { error = "forbidden", detail = "token_audience_not_allowed" }, ctx.RequestAborted);
            return;
        }

        ctx.Items["Claims"] = claims;
        // Records that this TokenClaims instance is the *caller's* identity for this request, so
        // that reading an unverified management level off it can be refused (see
        // ClaimsExtensions.GetGrantedLevel, which answers null until a live check has run). Every
        // request deserialises its own instance (HydraService.ValidateJwtAsync from cache,
        // PatService above), so the mark never outlives the request that made it.
        ClaimsExtensions.MarkCallerClaims(claims);

        // Default deny (S-1a). The management prefixes are opt-in today: an action is privileged
        // because somebody remembered [RequireManagementLevel]. ServiceAccountController did not
        // (R-22) and the /admin GET branch was safe only because every controller happened to
        // carry one (I-02). A new controller on these paths now fails closed instead.
        if (IsManagementSurface(ctx.Request.Path) && !HasManagementGate(ctx))
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { error = "forbidden", detail = "no_authorisation_gate" }, ctx.RequestAborted);
            return;
        }

        await next(ctx);
    }

    /// <summary>
    /// Controllers on a management prefix that gate themselves rather than by management level, and
    /// are therefore exempt from the default-deny above. This list is the whole exemption set — a
    /// greppable artefact a reviewer can audit, which 200 remembered attributes are not.
    /// </summary>
    private static readonly string[] SelfGatedControllers =
    [
        // /api/introspect and /api/authorize are RFC 7662 surfaces for service accounts, not for
        // administrators: IntrospectionController.IsServiceAccountCaller is their gate and a
        // management level would be the wrong question to ask of them.
        "Introspection",
    ];

    private static bool HasManagementGate(HttpContext ctx)
    {
        var endpoint = ctx.GetEndpoint();
        // No MVC action behind this path: a static asset, the SPA fallback, or a minimal endpoint.
        // UseRouting has already run for every branch this middleware is mounted on.
        if (endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action) return true;
        if (endpoint.Metadata.GetMetadata<RequireManagementLevelAttribute>() is not null) return true;
        return SelfGatedControllers.Contains(action.ControllerName, StringComparer.Ordinal);
    }

    private static readonly string[] ManagementPrefixes =
        ["/admin", "/org", "/project", "/service-accounts", "/api", "/internal"];

    private static bool IsManagementSurface(PathString path) =>
        ManagementPrefixes.Any(p => path.StartsWithSegments(p));

    private static bool IsManagementAudience(TokenClaims claims, AppConfig cfg)
    {
        // PATs are not OAuth2 tokens: they carry no client_id and are already bound to a
        // service account whose roles were checked at introspection time.
        if (claims.IsServiceAccount) return true;

        // Service accounts authenticating via client_credentials (private_key_jwt).
        if (claims.ClientId.StartsWith(Roles.ServiceAccountClientPrefix, StringComparison.Ordinal))
            return true;

        return cfg.ManagementClientIds.Contains(claims.ClientId, StringComparer.Ordinal);
    }
}

public static class ClaimsExtensions
{
    /// <summary>
    /// Caller-identity claims seen this request, and the <see cref="GrantedLevel"/> that
    /// <see cref="RequireManagementLevelAttribute"/> verified for them (null until it has).
    ///
    /// A weak table rather than <c>HttpContext.Items</c> because the check has to be reachable from
    /// an extension method on <see cref="TokenClaims"/>, which has no context. Entries are keyed by
    /// object identity and every request produces its own instance, so nothing leaks between them.
    /// </summary>
    private static readonly ConditionalWeakTable<TokenClaims, StrongBox<GrantedLevel?>> CallerGrants = new();

    public static TokenClaims? GetClaims(this HttpContext ctx)
        => ctx.Items["Claims"] as TokenClaims;

    public static bool HasRole(this TokenClaims claims, params string[] roles)
        => roles.Any(r => claims.Roles.Contains(r));

    /// <summary>
    /// Marks these claims as the caller's own identity. Public only so tests can reach it; it can
    /// never loosen a check, it only makes <see cref="GetGrantedLevel"/> stricter about them —
    /// marked-but-unverified claims read back as null rather than as a level.
    /// </summary>
    public static void MarkCallerClaims(TokenClaims claims)
        => CallerGrants.GetValue(claims, _ => new StrongBox<GrantedLevel?>(null));

    /// <summary>
    /// Records the verified level. In practice only <see cref="RequireManagementLevelAttribute"/>
    /// calls it, and nothing else can usefully do so: the argument is a
    /// <see cref="GrantedLevel"/>, which no other type can construct.
    /// </summary>
    public static void RecordGrantedLevel(TokenClaims claims, GrantedLevel granted)
        => CallerGrants.GetValue(claims, _ => new StrongBox<GrantedLevel?>(null)).Value = granted;

    /// <summary>
    /// The caller's management level as verified against the authorisation store earlier in this
    /// request. Null when no live check has run — which is the only honest answer, and is why this
    /// is the accessor an access decision should use.
    ///
    /// <para>
    /// It is also the only such accessor, deliberately (S-1). A level taken from <c>ext.roles</c>
    /// is a claim, not a grant, and one reader that returned either — depending on whether a live
    /// check happened to have run — is how R-22 and the password-floor bypass both happened. The
    /// two questions are separate members now, so the compiler enforces the choice: this one for
    /// the caller's own verified authority, and <see cref="GrantedLevel.ClaimedLevel"/> (internal)
    /// to read a presented token's claim on purpose, which is what introspection wants. The reader
    /// that conflated them carried a runtime throw for the misuse; it is deleted because the misuse
    /// no longer compiles, and <c>StructuralDebtTests</c> asserts it cannot come back.
    /// </para>
    /// </summary>
    public static GrantedLevel? GetGrantedLevel(this HttpContext ctx)
        => ctx.GetClaims() is { } claims && CallerGrants.TryGetValue(claims, out var box) ? box.Value : null;
}
