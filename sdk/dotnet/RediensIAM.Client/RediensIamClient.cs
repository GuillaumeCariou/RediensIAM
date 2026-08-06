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

    /// <summary>
    /// Who is acting for whom. <c>null</c> on every ordinary token — a non-null value means an
    /// operator opened a delegated session into this tenant, and the request in front of you is
    /// support traffic rather than the customer's own.
    ///
    /// <para>
    /// A delegated token carries <b>no roles</b>: authority still comes from your own enforcement
    /// point. What this field decides is what you must show and record, and — while
    /// <c>Act.Mode</c> reads <c>read</c> — what you must refuse.
    /// </para>
    /// </summary>
    [JsonPropertyName("act")] public Actor? Act { get; init; }

    /// <summary>True when this request is an operator acting for the tenant, in read-only mode.</summary>
    public bool IsReadOnlyImpersonation => Act is { Mode: "read" };


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

/// <summary>
/// A delegated session, as returned when it is opened. <see cref="AccessToken"/> is shown once.
/// </summary>
public sealed record ImpersonationSession
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("session_id")]   public string SessionId   { get; init; } = "";
    [JsonPropertyName("expires_in")]   public int    ExpiresIn   { get; init; }
    [JsonPropertyName("sub")]          public string Sub         { get; init; } = "";
    [JsonPropertyName("org_id")]       public string OrgId       { get; init; } = "";
    [JsonPropertyName("project_id")]   public string ProjectId   { get; init; } = "";
    [JsonPropertyName("act")]          public Actor? Act         { get; init; }
}

/// <summary>
/// The operator behind a delegated session (RFC 8693 <c>act</c>).
///
/// <para>
/// <c>Mode</c> is a claim, and a claim enforces nothing on its own — refusing mutating
/// verbs while it reads <c>read</c> is your gateway's job. <c>Session</c> is what an
/// operator's own console revokes, and what your logs should carry beside the tenant id so a
/// support action is never indistinguishable from the customer's own.
/// </para>
/// </summary>
public sealed record Actor
{
    [JsonPropertyName("sub")]     public string Sub     { get; init; } = "";
    [JsonPropertyName("level")]   public string Level   { get; init; } = "";
    [JsonPropertyName("mode")]    public string Mode    { get; init; } = "";
    [JsonPropertyName("session")] public string Session { get; init; } = "";
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
    /// organisation id if it fronts a whole organisation. Sent as <c>project_id</c> on every
    /// introspection and authorisation call, and mandatory at the server since contract
    /// <c>ver: 2</c>.
    ///
    /// <para>This is the same identifier that gives the front <c>client_&lt;project_id&gt;</c>, so
    /// one value configures both halves of an integration. It was called <c>Audience</c> and
    /// travelled the wire as <c>aud</c> until contract 2 — one value, two names, and a deployment
    /// test whose whole job was to check the two agreed.</para>
    ///
    /// <para>Required, with deliberately no default. A default would be a guess about which
    /// tenant this service belongs to, and a wrong guess is P-06 exactly: a deployment-scoped
    /// service-account credential resolving <i>every</i> tenant's token as active, leaving the
    /// resource server to remember a <c>project_id</c> comparison nobody remembers.</para>
    /// </summary>
    public string ProjectId { get; set; } = "";

    /// <summary>
    /// How long a positive introspection is reused. Keep it short — it is the upper bound on how
    /// long a revoked token keeps working at this gateway. Zero disables caching.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Throws unless these options can produce a working client, and returns them.
    ///
    /// <para>Called at construction, never on the first request: a missing project id or a
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
        if (string.IsNullOrWhiteSpace(ProjectId))
            throw new ArgumentException(
                "RediensIamOptions.ProjectId is required: name the project id this resource server " +
                "serves, or its organisation id if it fronts a whole organisation. RediensIAM sends " +
                "it as project_id and refuses a request without one (400 project_id_required).");

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

    private readonly HttpClient http;
    private readonly RediensIamOptions options;
    private readonly IMemoryCache cache;
    private readonly ILogger<RediensIamClient>? logger;

    /// <summary>
    /// Options are validated here rather than on the first request, so a service with no declared
    /// project id — or a cleartext base URL — fails to start instead of failing under load.
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
                new KeyValuePair<string, string>("project_id", options.ProjectId),
            ]),
        };
        Authorize(request);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        // An empty body is a broken server, not an inactive token — say so rather than denying.
        var info = await response.Content.ReadFromJsonAsync<TokenInfo>(Json, ct)
                   ?? throw new InvalidOperationException("RediensIAM returned an empty introspection answer.");

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
                project_id = options.ProjectId,
            }),
        };
        Authorize(request);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AuthorizeResponse>(Json, ct)
                     ?? throw new InvalidOperationException("RediensIAM returned an empty authorisation answer.");
        return result.Allowed;
    }

    /// <summary>
    /// Opens a delegated session — an operator acting <b>for</b> the organisation named here.
    ///
    /// <para>
    /// Requires a service-account credential that also holds <c>super_admin</c>; anything less is
    /// refused by the server. This is an operator console's call, never a customer-facing one.
    /// </para>
    ///
    /// <para>
    /// The returned <see cref="ImpersonationSession.AccessToken"/> is shown <b>once</b> and cannot
    /// be read back. Opening a session revokes the same operator's previous one.
    /// </para>
    ///
    /// <para>
    /// Sessions are organisation-scoped: no user is impersonated, the token carries no roles, and
    /// what a support session may see is decided by your own service. <paramref name="reason"/> is
    /// required — it lands in the entered tenant's own audit log, and an impersonation with no
    /// stated reason is not auditable.
    /// </para>
    /// </summary>
    /// <param name="orgId">The organisation being entered.</param>
    /// <param name="projectId">The authentication boundary; must belong to <paramref name="orgId"/>.</param>
    /// <param name="mode"><c>read</c> or <c>write</c>. Decided here, never inferred from a role.</param>
    /// <param name="reason">Free text, required; written to the entered tenant's audit log.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="ttlSeconds">Defaults to 900 server-side; anything above 3600 is clamped, not refused.</param>
    /// <returns>The session, whose token is shown once.</returns>
    public async Task<ImpersonationSession> OpenImpersonationAsync(
        string orgId, string projectId, string mode, string reason,
        int? ttlSeconds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A delegated session must state a reason: it is written to the tenant's audit log.", nameof(reason));

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/manage/impersonate")
        {
            Content = JsonContent.Create(new
            {
                org_id      = orgId,
                project_id  = projectId,
                mode,
                reason,
                ttl_seconds = ttlSeconds,
            }),
        };
        Authorize(request);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ImpersonationSession>(Json, ct)
               ?? throw new InvalidOperationException("RediensIAM returned an empty impersonation answer.");
    }

    /// <summary>
    /// Ends a delegated session immediately — not at its TTL. Returns false when there was nothing
    /// live to end, which is also what a second call answers.
    /// </summary>
    public async Task<bool> RevokeImpersonationAsync(string sessionId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/manage/impersonate/{sessionId}/revoke");
        Authorize(request);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Drops any cached decision for a token — call on logout to make it immediate.</summary>
    public void Forget(string token) => cache.Remove(CacheKey(token));

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceAccountToken);

    // Cache on a hash, never the token itself: cache keys end up in dumps and diagnostics.
    //
    // The project id and the base URL are part of the key because they are part of the question. One
    // token introspected for two tenants has two different answers — that is what project_id is for —
    // and IMemoryCache is resolved from the host, so a multi-tenant gateway shares one instance
    // across its per-tenant clients. Keyed on the token alone, tenant A's `active: true` was served
    // to tenant B, roles and all, without a round trip.
    private string CacheKey(string token)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{options.BaseUrl}\n{options.ProjectId}\n{token}"));
        return "rediensiam:" + Convert.ToHexString(digest);
    }

    // `user_id` is on the wire too; it is not bound because nothing here reads it, and an unread
    // property is a getter nobody calls that still has to be maintained.
    private sealed record AuthorizeResponse(
        [property: JsonPropertyName("allowed")] bool Allowed);
}
