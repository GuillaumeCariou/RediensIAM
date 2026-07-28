using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace RediensIAM.Client;

/// <summary>
/// Result of introspecting a token against RediensIAM.
/// <see cref="Active"/> false means the token is not usable right now — expired, revoked,
/// belonging to a deactivated service account, or to a suspended organisation. The reason is
/// deliberately not disclosed.
/// </summary>
public sealed record TokenInfo
{
    [JsonPropertyName("active")]             public bool Active { get; init; }
    [JsonPropertyName("sub")]                public string? Subject { get; init; }
    [JsonPropertyName("user_id")]            public string? UserId { get; init; }
    [JsonPropertyName("org_id")]             public string? OrgId { get; init; }
    [JsonPropertyName("project_id")]         public string? ProjectId { get; init; }
    [JsonPropertyName("roles")]              public IReadOnlyList<string> Roles { get; init; } = [];
    [JsonPropertyName("client_id")]          public string? ClientId { get; init; }
    [JsonPropertyName("is_service_account")] public bool IsServiceAccount { get; init; }

    public static readonly TokenInfo Inactive = new() { Active = false };

    public bool HasRole(string role) => Roles.Contains(role, StringComparer.Ordinal);
}

/// <summary>Options for <see cref="RediensIamClient"/>.</summary>
public sealed class RediensIamOptions
{
    /// <summary>Base URL of the RediensIAM public API, e.g. <c>https://auth.example.com</c>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Credential this service presents to RediensIAM. A service-account personal access token
    /// (<c>rediens_pat_…</c>) is the simplest option.
    /// </summary>
    public string ServiceAccountToken { get; set; } = "";

    /// <summary>
    /// How long a positive introspection is reused. Keep it short — it is the upper bound on how
    /// long a revoked token keeps working at this gateway. Zero disables caching.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Client for RediensIAM's resource-server surface.
///
/// Prefer this over validating JWTs locally against JWKS: local validation cannot see a role
/// revoked, a service account disabled, or an organisation suspended after the token was minted.
/// </summary>
public sealed class RediensIamClient(
    HttpClient http,
    RediensIamOptions options,
    IMemoryCache cache,
    ILogger<RediensIamClient>? logger = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Introspects a token (RFC 7662). Returns <see cref="TokenInfo.Inactive"/> rather than
    /// throwing when the token is unusable; network and server faults still throw, because
    /// treating an outage as "token invalid" would silently degrade to denying everyone —
    /// callers should decide whether that is the behaviour they want.
    /// </summary>
    public async Task<TokenInfo> IntrospectAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return TokenInfo.Inactive;

        var cacheKey = CacheKey(token);
        if (options.CacheDuration > TimeSpan.Zero && cache.TryGetValue<TokenInfo>(cacheKey, out var hit) && hit is not null)
            return hit;

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/introspect")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("token", token),
                new KeyValuePair<string, string>("token_type_hint", "access_token"),
            ]),
        };
        Authorize(request);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var info = await response.Content.ReadFromJsonAsync<TokenInfo>(Json, ct) ?? TokenInfo.Inactive;

        // Only positive answers are cached. Caching "inactive" would keep denying a token that
        // has since become valid, and buys nothing.
        if (info.Active && options.CacheDuration > TimeSpan.Zero)
            cache.Set(cacheKey, info, options.CacheDuration);

        logger?.LogDebug("Introspected token: active={Active} user={UserId}", info.Active, info.UserId);
        return info;
    }

    /// <summary>
    /// Asks RediensIAM whether the bearer of <paramref name="token"/> holds
    /// <paramref name="relation"/> on the given object. Keeps the policy in RediensIAM rather
    /// than reimplementing an interpretation of the roles claim in every gateway.
    /// </summary>
    public async Task<bool> AuthorizeAsync(
        string token, string @namespace, string @object, string relation, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/authorize")
        {
            Content = JsonContent.Create(new
            {
                token,
                @namespace,
                @object,
                relation,
            }),
        };
        Authorize(request);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AuthorizeResponse>(Json, ct);
        return result?.Allowed ?? false;
    }

    /// <summary>Drops any cached decision for a token — call on logout to make it immediate.</summary>
    public void Forget(string token) => cache.Remove(CacheKey(token));

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceAccountToken);

    // Cache on a hash, never the token itself: cache keys end up in dumps and diagnostics.
    private static string CacheKey(string token)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return "rediensiam:" + Convert.ToHexString(digest);
    }

    private sealed record AuthorizeResponse(
        [property: JsonPropertyName("allowed")] bool Allowed,
        [property: JsonPropertyName("user_id")] string? UserId);
}
