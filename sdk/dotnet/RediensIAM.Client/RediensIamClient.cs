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
    /// <summary>
    /// Roles on the token; empty when there are none. The initialiser alone is not enough — a
    /// server that sends <c>"roles": null</c> (any build before the fix, on every inactive answer)
    /// writes that null straight over it, and <see cref="HasRole"/> then throws on the documented
    /// path where an unusable token "returns TokenInfo.Inactive rather than throwing".
    /// </summary>
    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get => field ?? []; init => field = value ?? []; } = [];
    [JsonPropertyName("client_id")]          public string? ClientId { get; init; }
    [JsonPropertyName("is_service_account")] public bool IsServiceAccount { get; init; }

    /// <summary>Echo of the <c>aud</c> this client sent, on an active answer.</summary>
    [JsonPropertyName("aud")]                public string? Audience { get; init; }

    /// <summary>
    /// Contract version of the answer; 0 means the field was absent. See
    /// <see cref="RediensIamClient.RequiredContractVersion"/> — an answer below the required
    /// version never reaches a caller.
    /// </summary>
    [JsonPropertyName("ver")]                public int Ver { get; init; }

    public static readonly TokenInfo Inactive = new() { Active = false };

    /// <summary>
    /// True when the token carries a <b>management</b> role of RediensIAM itself
    /// (<c>super_admin</c>, <c>org_admin</c>, <c>project_admin</c>). Tenant roles never match
    /// here — the issuer namespaces them by project, so use <see cref="HasProjectRole"/>.
    /// </summary>
    public bool HasRole(string role) => Roles.Contains(role, StringComparer.Ordinal);

    /// <summary>
    /// True when the token carries tenant role <paramref name="role"/> <b>in project
    /// <paramref name="projectId"/></b>.
    ///
    /// Role names are chosen by each tenant, so <c>"admin"</c> on its own means nothing across
    /// tenants. RediensIAM emits them as <c>{project_id}/{name}</c> and this is the matching
    /// read; the same qualified string is what lands in <c>ClaimTypes.Role</c>, so
    /// <c>[Authorize(Roles = "admin")]</c> fails closed rather than matching every tenant.
    /// </summary>
    public bool HasProjectRole(string projectId, string role) =>
        Roles.Contains($"{projectId}/{role}", StringComparer.Ordinal);
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
    /// The tenant <b>this resource server serves</b> — the project id it fronts, or the
    /// organisation id if it fronts a whole organisation. Sent as <c>aud</c> on every
    /// introspection and authorisation call, and mandatory at the server since contract
    /// <c>ver: 1</c>.
    ///
    /// <para>Required, with deliberately no default. A default would be a guess about which
    /// tenant this service belongs to, and a wrong guess is P-06 exactly: a deployment-scoped
    /// service-account credential resolving <i>every</i> tenant's token as active, leaving the
    /// resource server to remember a <c>project_id</c> comparison nobody remembers.</para>
    /// </summary>
    public string Audience { get; set; } = "";

    /// <summary>
    /// How long a positive introspection is reused. Keep it short — it is the upper bound on how
    /// long a revoked token keeps working at this gateway. Zero disables caching.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Throws unless these options can produce a working client, and returns them.
    ///
    /// <para>Called at construction, never on the first request: a missing audience or a
    /// cleartext <see cref="BaseUrl"/> is a deployment mistake, and it should stop the process at
    /// startup rather than turn into 400s once traffic arrives.</para>
    /// </summary>
    /// <exception cref="ArgumentException">Any option is missing or unusable.</exception>
    public RediensIamOptions Validated()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new ArgumentException("RediensIamOptions.BaseUrl is required.");
        if (string.IsNullOrWhiteSpace(ServiceAccountToken))
            throw new ArgumentException("RediensIamOptions.ServiceAccountToken is required.");
        if (string.IsNullOrWhiteSpace(Audience))
            throw new ArgumentException(
                "RediensIamOptions.Audience is required: name the project id this resource server " +
                "serves, or its organisation id if it fronts a whole organisation. RediensIAM sends " +
                "it as aud and refuses a request without one (400 audience_required).");

        // The service-account credential and every token being introspected ride on BaseUrl, so
        // cleartext there hands an on-path attacker both. http is accepted only on a loopback
        // host: forbidding it outright breaks every local setup, and a flag to disable the check
        // gets set in production too.
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"RediensIamOptions.BaseUrl is not an absolute URL: {BaseUrl}");
        if (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback))
            throw new ArgumentException(
                $"RediensIamOptions.BaseUrl must be https — http is accepted only on localhost: {BaseUrl}");

        return this;
    }
}

/// <summary>
/// Client for RediensIAM's resource-server surface.
///
/// Prefer this over validating JWTs locally against JWKS: local validation cannot see a role
/// revoked, a service account disabled, or an organisation suspended after the token was minted.
/// </summary>
public sealed class RediensIamClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Contract version this client requires on every answer.
    ///
    /// <para>RediensIAM stamps <c>ver</c> on every answer it gives, including
    /// <c>{"active": false}</c>. A server older than this silently discards the unknown <c>aud</c>
    /// this client sends and answers exactly as it always did — so sending <c>aud</c> proves
    /// nothing on its own, and only the presence of <c>ver</c> distinguishes a server that
    /// enforced the audience from one that ignored it. Failing on an absent <c>ver</c> is the
    /// point: the alternative is believing an answer is scoped to one tenant when it was scoped
    /// to the whole deployment.</para>
    /// </summary>
    public const int RequiredContractVersion = 1;

    private readonly HttpClient http;
    private readonly RediensIamOptions options;
    private readonly IMemoryCache cache;
    private readonly ILogger<RediensIamClient>? logger;

    /// <summary>
    /// Options are validated here rather than on the first request, so a service with no declared
    /// audience — or a cleartext base URL — fails to start instead of failing under load.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="options"/> is unusable.</exception>
    public RediensIamClient(
        HttpClient http,
        RediensIamOptions options,
        IMemoryCache cache,
        ILogger<RediensIamClient>? logger = null)
    {
        this.http    = http;
        this.options = options.Validated();
        this.cache   = cache;
        this.logger  = logger;

        // Only AddRediensIam applied this, while the README blesses direct construction too — so a
        // hand-built client fell back to HttpClient's 100-second default and a hung IAM stalled
        // every authenticated request for that long. Guarded because the timeout cannot be changed
        // once a request has been sent on this HttpClient.
        if (http.Timeout != this.options.Timeout)
        {
            try { http.Timeout = this.options.Timeout; }
            catch (InvalidOperationException) { /* already in use — the caller owns it */ }
        }
    }

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
                new KeyValuePair<string, string>("aud", options.Audience),
            ]),
        };
        Authorize(request);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        // An empty body is a broken server, not an inactive token — say so rather than denying.
        var info = await response.Content.ReadFromJsonAsync<TokenInfo>(Json, ct)
                   ?? throw new InvalidOperationException("RediensIAM returned an empty introspection answer.");
        RequireContract(info.Ver);

        // Only positive answers are cached. Caching "inactive" would keep denying a token that
        // has since become valid, and buys nothing.
        if (info.Active && options.CacheDuration > TimeSpan.Zero)
            cache.Set(cacheKey, info, options.CacheDuration);

        // Guarded: introspection runs per request, and the unguarded call boxes info.Active into
        // the params array even when Debug logging is off.
        if (logger?.IsEnabled(LogLevel.Debug) == true)
            logger.LogDebug("Introspected token: active={Active} user={UserId}", info.Active, info.UserId);
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
                aud = options.Audience,
            }),
        };
        Authorize(request);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AuthorizeResponse>(Json, ct)
                     ?? throw new InvalidOperationException("RediensIAM returned an empty authorisation answer.");
        RequireContract(result.Ver);
        return result.Allowed;
    }

    /// <summary>
    /// Refuses an answer that did not come from an audience-enforcing server. See
    /// <see cref="RequiredContractVersion"/>.
    /// </summary>
    private static void RequireContract(int ver)
    {
        if (ver >= RequiredContractVersion) return;

        throw new InvalidOperationException(
            $"RediensIAM answered with ver={ver}, expected at least {RequiredContractVersion}: this " +
            "server predates mandatory audience binding and silently ignored the aud this client " +
            "sent. Upgrade RediensIAM before trusting its answers.");
    }

    /// <summary>Drops any cached decision for a token — call on logout to make it immediate.</summary>
    public void Forget(string token) => cache.Remove(CacheKey(token));

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceAccountToken);

    // Cache on a hash, never the token itself: cache keys end up in dumps and diagnostics.
    //
    // The audience and the base URL are part of the key because they are part of the question. One
    // token introspected for two audiences has two different answers — that is what `aud` is for —
    // and IMemoryCache is resolved from the host, so a multi-tenant gateway shares one instance
    // across its per-tenant clients. Keyed on the token alone, tenant A's `active: true` was served
    // to tenant B, roles and all, without a round trip.
    private string CacheKey(string token)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{options.BaseUrl}\n{options.Audience}\n{token}"));
        return "rediensiam:" + Convert.ToHexString(digest);
    }

    private sealed record AuthorizeResponse(
        [property: JsonPropertyName("allowed")] bool Allowed,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("ver")] int Ver = 0);
}
