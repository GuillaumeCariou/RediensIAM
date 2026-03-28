using System.Text.Json;
using RediensIAM.Config;
using RediensIAM.Models;
using RediensIAM.Services;

namespace RediensIAM.Middleware;

public class GatewayAuthMiddleware(
    RequestDelegate next,
    HydraService hydra)
{
    private const string PatPrefix = "rediens_pat_";

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

        if (token.StartsWith(PatPrefix, StringComparison.Ordinal))
        {
            var patService = ctx.RequestServices.GetRequiredService<PatService>();
            var result = await patService.IntrospectAsync(token);
            claims = result is { Active: true }
                ? new TokenClaims { UserId = result.Sub, OrgId = result.OrgId, ProjectId = result.ProjectId, Roles = result.Roles, IsServiceAccount = true }
                : null;
        }
        else
        {
            claims = await hydra.ValidateJwtAsync(token);
        }

        if (claims is null)
        {
            ctx.Response.StatusCode = 401;
            return;
        }

        ctx.Items["Claims"] = claims;
        ctx.Request.Headers["X-User-Id"] = claims.UserId;
        ctx.Request.Headers["X-Org-Id"] = claims.OrgId;
        ctx.Request.Headers["X-Project-Id"] = claims.ProjectId;
        ctx.Request.Headers["X-User-Roles"] = string.Join(",", claims.Roles);
        if (claims.IsServiceAccount)
            ctx.Request.Headers["X-Is-Service-Account"] = "true";

        await next(ctx);
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
