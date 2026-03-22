using System.Text.Json;
using RediensIAM.Models;
using RediensIAM.Services;

namespace RediensIAM.Middleware;

public class GatewayAuthMiddleware(
    RequestDelegate next,
    HydraJwksCache jwksCache)
{
    private const string PatPrefix = "rediens_pat_";
    private const string ImpersonationPrefix = "rediens_imp_";

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

        if (token.StartsWith(ImpersonationPrefix, StringComparison.Ordinal))
        {
            var impService = ctx.RequestServices.GetRequiredService<ImpersonationService>();
            var imp = await impService.ResolveAsync(token);
            claims = imp is not null
                ? new TokenClaims { UserId = imp.UserId, OrgId = imp.OrgId, ProjectId = imp.ProjectId, Roles = imp.Roles, IsImpersonation = true }
                : null;
        }
        else if (token.StartsWith(PatPrefix, StringComparison.Ordinal))
        {
            var patService = ctx.RequestServices.GetRequiredService<PatIntrospectionService>();
            var result = await patService.IntrospectAsync(token);
            claims = result is { Active: true }
                ? new TokenClaims { UserId = result.Sub, OrgId = result.OrgId, ProjectId = result.ProjectId, Roles = result.Roles, IsServiceAccount = true }
                : null;
        }
        else
        {
            claims = await jwksCache.ValidateJwtAsync(token);
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
}
