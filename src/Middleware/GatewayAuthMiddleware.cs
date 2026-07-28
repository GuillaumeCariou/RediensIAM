using System.Text.Json;
using RediensIAM.Config;
using RediensIAM.Models;
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
            await ctx.Response.WriteAsJsonAsync(new { error = "forbidden", detail = "token_audience_not_allowed" });
            return;
        }

        ctx.Items["Claims"] = claims;

        await next(ctx);
    }

    private static readonly string[] ManagementPrefixes =
        ["/admin", "/org", "/project", "/service-accounts", "/api/manage", "/internal"];

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
    public static TokenClaims? GetClaims(this HttpContext ctx)
        => ctx.Items["Claims"] as TokenClaims;

    public static bool HasRole(this TokenClaims claims, params string[] roles)
        => roles.Any(r => claims.Roles.Contains(r));

    public static ManagementLevel GetManagementLevel(this TokenClaims claims)
    {
        if (claims.Roles.Contains(Roles.SuperAdmin))   return ManagementLevel.SuperAdmin;
        if (claims.Roles.Contains(Roles.OrgAdmin))     return ManagementLevel.OrgAdmin;
        if (claims.Roles.Contains(Roles.ProjectAdmin)) return ManagementLevel.ProjectAdmin;
        return ManagementLevel.None;
    }
}
