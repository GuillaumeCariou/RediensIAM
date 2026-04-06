using RediensIAM.Exceptions;

namespace RediensIAM.Middleware;

public class AppExceptionMiddleware(RequestDelegate next, ILogger<AppExceptionMiddleware> logger)
{
    private const string AppJson = "application/json";

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (ForbiddenException ex)
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentType = AppJson;
            await ctx.Response.WriteAsJsonAsync(new { error = "forbidden", detail = ex.Message });
        }
        catch (NotFoundException ex)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = AppJson;
            await ctx.Response.WriteAsJsonAsync(new { error = "not_found", detail = ex.Message });
        }
        catch (BadRequestException ex)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = AppJson;
            await ctx.Response.WriteAsJsonAsync(new { error = "bad_request", detail = ex.Message });
        }
        catch (ConflictException ex)
        {
            ctx.Response.StatusCode = 409;
            ctx.Response.ContentType = AppJson;
            await ctx.Response.WriteAsJsonAsync(new { error = "conflict", detail = ex.Message });
        }
        catch (RateLimitException ex)
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.ContentType = AppJson;
            await ctx.Response.WriteAsJsonAsync(new { error = "rate_limited", detail = ex.Message });
        }
        catch (UnauthorizedException ex)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = AppJson;
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized", detail = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = AppJson;
            await ctx.Response.WriteAsJsonAsync(new { error = "internal_error" });
        }
    }
}
