using System.Text.Json;
using RediensIAM.Config;

namespace RediensIAM.Services;

/// <summary>
/// The one authority on which origins this deployment will talk to, and to whom.
///
/// <para>
/// CSP, CORS and the server-side redirect allowlist each used to carry their own list, built from
/// two values frozen at startup — <c>App__PublicUrl</c> and <c>App__AdminSpaOrigin</c>. Three
/// lists, three chances to disagree, and all three wrong for the same reason: the authorization
/// code flow ends by sending the browser to a <c>redirect_uri</c> a client registered, and clients
/// are created in the console long after the process started. Every new project needed a values
/// file edited and a pod restarted, and until then the login failed on <c>connect-src 'self'</c>
/// forbidding the exact hop the protocol requires.
/// </para>
///
/// <para>
/// Hydra holds those URIs and, in each client's <c>metadata.project_id</c>, whose they are — so one
/// read of the client list yields both the deployment-wide set and the per-project one. Callers ask
/// <see cref="ForRequestAsync"/>, never their own question, so a request cannot be judged by two
/// different rules depending on which middleware is looking.
/// </para>
///
/// <para>
/// On serving a set up to <see cref="Ttl"/> old: Hydra — not this cache — decides whether an
/// authorization may use a <c>redirect_uri</c>. A withdrawn origin lingering here lets the browser
/// attempt a hop Hydra has already refused; it cannot cause a token to be issued. And a change made
/// through this deployment does not wait for the TTL at all: the controllers call
/// <see cref="Invalidate"/>.
/// </para>
/// </summary>
public sealed class ClientOriginsService(
    IServiceScopeFactory scopes,
    AppConfig appConfig,
    ILogger<ClientOriginsService> logger)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly string[] RedirectFields = ["redirect_uris", "post_logout_redirect_uris"];

    /// <summary>The query parameter that lets a request name its project before it is routed.</summary>
    private const string ProjectIdParam = "project_id";

    // One loader at a time. Without the gate a cold start under load sends one Hydra request per
    // in-flight HTTP request, and the admin API is the last thing that should be stampeded.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Registered _registered = Registered.None;
    private long _staleAtTicks;

    /// <summary>What one read of Hydra's client list yields: the whole set, and it split by project.</summary>
    private sealed record Registered(string[] All, IReadOnlyDictionary<string, string[]> ByProject)
    {
        public static readonly Registered None = new([], new Dictionary<string, string[]>());
    }

    /// <summary>
    /// The origins applicable to <paramref name="context"/>.
    ///
    /// <para>
    /// When the request names a project in its query string, only that project's origins — plus
    /// this deployment's own, which serve every page — are in play. Otherwise the whole registered
    /// set is, because there is nothing narrower to be honest about.
    /// </para>
    ///
    /// <para>
    /// Only a query parameter can narrow it, and that is a property of CORS rather than a shortcut:
    /// a preflight is sent without cookies, without Authorization and without a body, so for the
    /// routes whose project lives in the MFA session cookie, a bearer token or the posted body,
    /// there is nothing to read at the moment the browser asks. Narrowing on what a preflight
    /// cannot carry would not tighten those routes, it would block them. What confines a caller to
    /// its own project is the tenant scope on the request that follows — claims, TenantScopeInterceptor,
    /// row-level security — which applies to the real request and is a far stronger boundary than an
    /// Origin header ever is.
    /// </para>
    /// </summary>
    public async ValueTask<string[]> ForRequestAsync(HttpContext context)
    {
        var registered = await LoadIfStaleAsync();
        return Narrow(registered, context.Request.Query[ProjectIdParam].ToString());
    }

    /// <summary>Every registered origin, without regard to which project registered it.</summary>
    public async ValueTask<string[]> GetAllAsync() => (await LoadIfStaleAsync()).All;

    /// <summary>
    /// One project's origins, for the login page that is serving it. Narrower than
    /// <see cref="ForRequestAsync"/> on purpose: the page knows exactly which project it renders,
    /// so it is told about that one and no other.
    /// </summary>
    public async ValueTask<string[]> ForProjectAsync(string? projectId) =>
        Narrow(await LoadIfStaleAsync(), projectId);

    /// <summary>
    /// The set already loaded, with no Hydra round trip and no await, for the one caller that
    /// cannot await: <c>SafeRedirect</c>, reached from a dozen sites that would all have to change
    /// shape. It runs inside the request pipeline, and the security-headers middleware awaits
    /// <see cref="ForRequestAsync"/> at the head of that pipeline — so by the time this is read the
    /// current request has already refreshed it. That ordering is load-bearing, and
    /// ProjectOriginPolicyTests drives the redirect path end to end rather than trusting it.
    /// </summary>
    public string[] Snapshot => _registered.All.Length > 0 ? _registered.All : ConfiguredOrigins();

    /// <summary>
    /// Forgets the cached set so the next request re-reads it. Called wherever this deployment
    /// registers, changes or deletes a client — which is what lets a project created in the console
    /// work on the next request rather than on the next TTL.
    /// </summary>
    public void Invalidate() => Volatile.Write(ref _staleAtTicks, 0);

    // ── Loading ───────────────────────────────────────────────────────────────

    private async ValueTask<Registered> LoadIfStaleAsync()
    {
        if (IsFresh()) return _registered;

        await _gate.WaitAsync();
        try
        {
            if (IsFresh()) return _registered;
            _registered = await LoadAsync();
        }
        catch (Exception ex)
        {
            // Serving the previous set beats serving none: an empty allowlist locks every front out
            // of a deployment whose only fault is that Hydra blinked.
            logger.LogWarning(ex, "Could not read the registered clients from Hydra; keeping the origins already loaded");
            if (_registered.All.Length == 0) _registered = _registered with { All = ConfiguredOrigins() };
        }
        finally
        {
            // Stamped on the failure path too, so an outage costs one Hydra call per TTL rather than
            // one per request, each of which would otherwise wait out the whole timeout.
            Volatile.Write(ref _staleAtTicks, DateTimeOffset.UtcNow.Add(Ttl).Ticks);
            _gate.Release();
        }
        return _registered;
    }

    private bool IsFresh() => DateTimeOffset.UtcNow.Ticks < Volatile.Read(ref _staleAtTicks);

    private async Task<Registered> LoadAsync()
    {
        // HydraService is scoped; this one is not, because the set it caches belongs to the
        // deployment rather than to a request.
        using var scope = scopes.CreateScope();
        var hydra = scope.ServiceProvider.GetRequiredService<HydraService>();

        var configured = ConfiguredOrigins();
        var all = new SortedSet<string>(configured, StringComparer.OrdinalIgnoreCase);
        var byProject = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var client in await hydra.ListOAuth2ClientsAsync())
        {
            var mine = OriginsOf(client);
            if (mine.Count == 0) continue;
            all.UnionWith(mine);

            // Written by both project-creation paths as metadata.project_id. A client without one
            // is not a project's — the admin SPA's own, or a service account's — and contributes to
            // the deployment-wide set without ever narrowing to anything.
            if (ProjectIdOf(client) is not { } projectId) continue;
            if (!byProject.TryGetValue(projectId, out var set))
                byProject[projectId] = set = new SortedSet<string>(configured, StringComparer.OrdinalIgnoreCase);
            set.UnionWith(mine);
        }

        return new Registered(
            [.. all],
            byProject.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
    }

    private static SortedSet<string> OriginsOf(JsonElement client)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in RedirectFields)
        {
            if (!client.TryGetProperty(field, out var uris) || uris.ValueKind != JsonValueKind.Array) continue;
            foreach (var uri in uris.EnumerateArray())
            {
                if (uri.ValueKind != JsonValueKind.String) continue;
                if (ToPolicyOrigin(uri.GetString()) is { } origin) found.Add(origin);
            }
        }
        return found;
    }

    private static string? ProjectIdOf(JsonElement client) =>
        client.TryGetProperty("metadata", out var metadata)
        && metadata.ValueKind == JsonValueKind.Object
        && metadata.TryGetProperty("project_id", out var id)
        && id.ValueKind == JsonValueKind.String
        && Guid.TryParse(id.GetString(), out _)
            ? id.GetString()
            : null;

    private static string[] Narrow(Registered registered, string? projectId) =>
        !string.IsNullOrEmpty(projectId) && registered.ByProject.TryGetValue(projectId, out var mine)
            ? mine
            : registered.All;

    /// <summary>
    /// What this deployment trusts on its own account, before a single project exists: the issuer,
    /// the console, Hydra, and whatever App__AdminCorsOrigins names. Present in every answer this
    /// class gives, including the per-project ones — a project's pages are still served from here.
    /// </summary>
    private string[] ConfiguredOrigins() =>
        [.. new[] { appConfig.PublicUrl, appConfig.AdminSpaOrigin, appConfig.HydraPublicUrl }
            .Concat(appConfig.AdminCorsOrigins)
            .Select(ToPolicyOrigin)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The <c>allowed_cors_origins</c> a Hydra client should carry, derived from its own redirect
    /// URIs.
    ///
    /// <para>
    /// Hydra applies CORS on its public port — the SPA's discovery, token and userinfo calls go
    /// there, not here — and that was configured as one static list in the chart, which is why the
    /// three fronts had to be written into <c>apps/iam/values.yaml</c> by hand and why a fourth
    /// project would have needed a fourth edit. A wildcard would only have made the list stop
    /// growing, not stop being one list for every tenant. Hydra takes the origins per client
    /// instead: "If set, these origins are appended to the server's configuration." Setting it at
    /// registration is what makes a project's CORS arrive with the project.
    /// </para>
    ///
    /// <para>
    /// Same parser as the CSP header uses, deliberately: two derivations of "the origins of this
    /// client" that could disagree is the defect this whole class exists to remove.
    /// </para>
    /// </summary>
    public static string[] CorsOriginsFor(IEnumerable<string>? redirectUris, IEnumerable<string>? postLogoutUris) =>
        [.. (redirectUris ?? []).Concat(postLogoutUris ?? [])
            .Select(ToPolicyOrigin)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Turns a registered URI into an origin fit to appear in a response header, or null.
    ///
    /// <para>
    /// This is the one place a value supplied by whoever administers a tenant becomes a token in a
    /// CSP header, so it is the one place that has to be certain of what it emits. Two rules carry
    /// that:
    /// </para>
    ///
    /// <list type="number">
    /// <item>Only absolute http(s) survives. Native clients register custom schemes
    /// (<c>myapp://callback</c>); a CSP source list is not a redirect allowlist, and a scheme the
    /// parser does not expect there is meaningless at best.</item>
    /// <item>The origin is <b>rebuilt</b> from the parsed scheme, host and port — never sliced out
    /// of the input. A source list is separated by spaces and terminated by a semicolon, and both
    /// are ordinary characters inside a URL: without this, a redirect_uri ending in
    /// <c>;script-src 'unsafe-inline'</c> would not widen a directive, it would close one and open
    /// another. The character check afterwards is the belt to that pair of braces.</item>
    /// </list>
    /// </summary>
    public static string? ToPolicyOrigin(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(parsed.Host)) return null;

        var origin = parsed.IsDefaultPort
            ? $"{parsed.Scheme}://{parsed.Host}"
            : $"{parsed.Scheme}://{parsed.Host}:{parsed.Port}";
        return origin.All(IsOriginChar) ? origin : null;
    }

    // Everything a scheme, a host (including a bracketed IPv6 literal) and a port can contain.
    // Notably absent: space and semicolon, the two separators of a CSP source list, and CR/LF.
    private static bool IsOriginChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '/' or '[' or ']';
}
