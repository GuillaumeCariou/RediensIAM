using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RediensIAM.Exceptions;
using RediensIAM.Middleware;

namespace RediensIAM.IntegrationTests.Tests.Middleware;

/// <summary>
/// Unit tests for AppExceptionMiddleware — directly instantiated, no HTTP host required.
/// Covers the ConflictException, RateLimitException, UnauthorizedException, and
/// generic Exception catch branches (lines 33-57).
/// </summary>
public class AppExceptionMiddlewareTests
{
    private static async Task<(int StatusCode, JsonElement Body)> RunAsync(Exception toThrow)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        var mw = new AppExceptionMiddleware(
            _ => throw toThrow,
            NullLogger<AppExceptionMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        var json = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, json.RootElement);
    }

    [Fact]
    public async Task ConflictException_Returns409WithConflictError()
    {
        var (code, body) = await RunAsync(new ConflictException("already exists"));

        code.Should().Be(409);
        body.GetProperty("error").GetString().Should().Be("conflict");
        body.GetProperty("detail").GetString().Should().Be("already exists");
    }

    [Fact]
    public async Task RateLimitException_Returns429WithRateLimitedError()
    {
        var (code, body) = await RunAsync(new RateLimitException("too many requests"));

        code.Should().Be(429);
        body.GetProperty("error").GetString().Should().Be("rate_limited");
        body.GetProperty("detail").GetString().Should().Be("too many requests");
    }

    [Fact]
    public async Task UnauthorizedException_Returns401WithUnauthorizedError()
    {
        var (code, body) = await RunAsync(new UnauthorizedException("not allowed"));

        code.Should().Be(401);
        body.GetProperty("error").GetString().Should().Be("unauthorized");
        body.GetProperty("detail").GetString().Should().Be("not allowed");
    }

    [Fact]
    public async Task UnhandledException_Returns500WithInternalError()
    {
        var (code, body) = await RunAsync(new InvalidOperationException("something blew up"));

        code.Should().Be(500);
        body.GetProperty("error").GetString().Should().Be("internal_error");
    }
}
