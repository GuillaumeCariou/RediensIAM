using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Config;

namespace RediensIAM.Services;

// ── Data records ─────────────────────────────────────────────────────────────

public record ProviderConfig(
    string Id,
    string Type,        // google | github | gitlab | facebook | oidc
    string ClientId,
    string ClientSecret,
    string? IssuerUrl   // required for oidc
);

public record OAuthStateData(
    string LoginChallenge,
    string ProjectId,
    string ProviderId,
    bool LinkMode = false,
    string? LinkUserId = null,
    string? LinkProjectId = null
);

public record SocialUserProfile(
    string ProviderUserId,
    string? Email,
    string? Name
);

// ── Service ──────────────────────────────────────────────────────────────────

public class SocialLoginService(
    IHttpClientFactory httpClientFactory,
    IDistributedCache cache,
    AppConfig appConfig,
    ILogger<SocialLoginService> logger)
{
    // Provider-specific hardcoded endpoints (builtin)
    private static readonly Dictionary<string, (string Auth, string Token, string UserInfo)> BuiltinEndpoints = new()
    {
        ["google"]   = ("https://accounts.google.com/o/oauth2/v2/auth",
                        "https://oauth2.googleapis.com/token",
                        "https://www.googleapis.com/oauth2/v3/userinfo"),
        ["github"]   = ("https://github.com/login/oauth/authorize",
                        "https://github.com/login/oauth/access_token",
                        "https://api.github.com/user"),
        ["gitlab"]   = ("https://gitlab.com/oauth/authorize",
                        "https://gitlab.com/oauth/token",
                        "https://gitlab.com/api/v4/user"),
        ["facebook"] = ("https://www.facebook.com/v18.0/dialog/oauth",
                        "https://graph.facebook.com/v18.0/oauth/access_token",
                        "https://graph.facebook.com/v18.0/me?fields=id,email,name"),
    };

    private static readonly Dictionary<string, string> DefaultScopes = new()
    {
        ["google"]   = "openid email profile",
        ["github"]   = "read:user user:email",
        ["gitlab"]   = "read_user",
        ["facebook"] = Email,
    };

    private const string Email = "email";

    // In-process cache for OIDC discovery documents
    private readonly Dictionary<string, JsonDocument> _discoveryCache = [];

    public string CallbackUrl => $"{appConfig.PublicUrl}/auth/oauth2/callback";

    // ── State (Redis) ────────────────────────────────────────────────────────

    public async Task<string> StoreStateAsync(OAuthStateData data)
    {
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var json = JsonSerializer.Serialize(data);
        await cache.SetStringAsync(
            $"oauth2:state:{state}",
            json,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });

        return state;
    }

    public async Task<OAuthStateData?> ConsumeStateAsync(string state)
    {
        var key = $"oauth2:state:{state}";
        var json = await cache.GetStringAsync(key);
        if (json == null) return null;
        await cache.RemoveAsync(key);
        return JsonSerializer.Deserialize<OAuthStateData>(json);
    }

    // ── Authorization URL ────────────────────────────────────────────────────

    public async Task<(string Url, string State)> BuildAuthorizationUrlAsync(
        ProviderConfig provider, string loginChallenge, string projectId)
    {
        var stateData = new OAuthStateData(loginChallenge, projectId, provider.Id);
        var state = await StoreStateAsync(stateData);

        var (authEndpoint, scope) = await GetAuthEndpointAndScopeAsync(provider);

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"]     = provider.ClientId,
            ["redirect_uri"]  = CallbackUrl,
            ["scope"]         = scope,
            ["state"]         = state,
        };

        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return ($"{authEndpoint}?{qs}", state);
    }

    public async Task<(string Url, string State)> BuildLinkAuthorizationUrlAsync(
        ProviderConfig provider, OAuthStateData stateData)
    {
        var state = await StoreStateAsync(stateData);
        var (authEndpoint, scope) = await GetAuthEndpointAndScopeAsync(provider);

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"]     = provider.ClientId,
            ["redirect_uri"]  = CallbackUrl,
            ["scope"]         = scope,
            ["state"]         = state,
        };

        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return ($"{authEndpoint}?{qs}", state);
    }

    // ── Code exchange + user profile ─────────────────────────────────────────

    public async Task<SocialUserProfile?> ExchangeAndGetProfileAsync(ProviderConfig provider, string code)
    {
        try
        {
            var accessToken = await ExchangeCodeAsync(provider, code);
            if (accessToken == null) return null;
            return await GetUserProfileAsync(provider, accessToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OAuth2 exchange failed for provider {Provider}", provider.Type);
            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(string AuthEndpoint, string Scope)> GetAuthEndpointAndScopeAsync(ProviderConfig provider)
    {
        if (BuiltinEndpoints.TryGetValue(provider.Type, out var ep))
            return (ep.Auth, DefaultScopes.GetValueOrDefault(provider.Type, "openid email"));

        // Generic OIDC — discover
        var disco = await GetDiscoveryAsync(provider.IssuerUrl!);
        var authEndpoint = disco.RootElement.GetProperty("authorization_endpoint").GetString()!;
        return (authEndpoint, "openid email profile");
    }

    private async Task<string?> ExchangeCodeAsync(ProviderConfig provider, string code)
    {
        var tokenEndpoint = await GetTokenEndpointAsync(provider);
        var http = httpClientFactory.CreateClient();

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["redirect_uri"]  = CallbackUrl,
            ["client_id"]     = provider.ClientId,
            ["client_secret"] = provider.ClientSecret,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = body };
        req.Headers.Accept.ParseAdd("application/json");
        // GitHub returns form-encoded by default; Accept: application/json fixes that
        using var resp = await http.SendAsync(req);
        var content = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Token exchange failed for {Provider}: {Status} {Body}", provider.Type, resp.StatusCode, content);
            return null;
        }

        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
    }

    private async Task<SocialUserProfile?> GetUserProfileAsync(ProviderConfig provider, string accessToken)
    {
        return provider.Type switch
        {
            "github"   => await GetGithubProfileAsync(accessToken),
            "facebook" => await GetFacebookProfileAsync(accessToken),
            "oidc"     => await GetOidcProfileAsync(provider, accessToken),
            _          => await GetStandardProfileAsync(provider, accessToken),
        };
    }

    private async Task<SocialUserProfile?> GetStandardProfileAsync(ProviderConfig provider, string accessToken)
    {
        var userInfoUrl = await GetUserInfoEndpointAsync(provider);
        var http = httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, userInfoUrl);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var sub   = TryGet(doc, "sub", "id");
        var email = TryGet(doc, Email);
        var name  = TryGet(doc, "name", "display_name", "username");
        return sub == null ? null : new SocialUserProfile(sub, email, name);
    }

    private async Task<SocialUserProfile?> GetGithubProfileAsync(string accessToken)
    {
        var http = httpClientFactory.CreateClient();

        async Task<JsonDocument?> CallAsync(string url)
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            r.Headers.UserAgent.ParseAdd("RediensIAM/1.0");
            r.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(r);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }

        using var user = await CallAsync(appConfig.GithubUserApiUrl);
        if (user == null) return null;

        var id   = user.RootElement.GetProperty("id").GetInt64().ToString();
        var name = TryGet(user, "name", "login");

        // email may be null if private — fetch separately
        var email = TryGet(user, Email);
        if (string.IsNullOrEmpty(email))
        {
            using var emails = await CallAsync(appConfig.GithubEmailsApiUrl);
            if (emails != null)
            {
                foreach (var e in emails.RootElement.EnumerateArray())
                {
                    if (e.TryGetProperty("primary", out var primary) && primary.GetBoolean() &&
                        e.TryGetProperty("verified", out var verified) && verified.GetBoolean() &&
                        e.TryGetProperty(Email, out var em))
                    {
                        email = em.GetString();
                        break;
                    }
                }
            }
        }

        return new SocialUserProfile(id, email, name);
    }

    private async Task<SocialUserProfile?> GetFacebookProfileAsync(string accessToken)
    {
        var url = $"https://graph.facebook.com/v18.0/me?fields=id,email,name&access_token={Uri.EscapeDataString(accessToken)}";
        var http = httpClientFactory.CreateClient();
        using var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var id    = TryGet(doc, "id");
        var email = TryGet(doc, Email);
        var name  = TryGet(doc, "name");
        return id == null ? null : new SocialUserProfile(id, email, name);
    }

    private async Task<SocialUserProfile?> GetOidcProfileAsync(ProviderConfig provider, string accessToken)
    {
        var disco       = await GetDiscoveryAsync(provider.IssuerUrl!);
        var userInfoUrl = disco.RootElement.GetProperty("userinfo_endpoint").GetString()!;

        var http = httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, userInfoUrl);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var sub   = TryGet(doc, "sub");
        var email = TryGet(doc, Email);
        var name  = TryGet(doc, "name", "preferred_username");
        return sub == null ? null : new SocialUserProfile(sub, email, name);
    }

    private async Task<string> GetTokenEndpointAsync(ProviderConfig provider)
    {
        if (BuiltinEndpoints.TryGetValue(provider.Type, out var ep)) return ep.Token;
        var disco = await GetDiscoveryAsync(provider.IssuerUrl!);
        return disco.RootElement.GetProperty("token_endpoint").GetString()!;
    }

    private async Task<string> GetUserInfoEndpointAsync(ProviderConfig provider)
    {
        if (BuiltinEndpoints.TryGetValue(provider.Type, out var ep)) return ep.UserInfo;
        var disco = await GetDiscoveryAsync(provider.IssuerUrl!);
        return disco.RootElement.GetProperty("userinfo_endpoint").GetString()!;
    }

    private async Task<JsonDocument> GetDiscoveryAsync(string issuerUrl)
    {
        if (_discoveryCache.TryGetValue(issuerUrl, out var cached)) return cached;

        var url  = issuerUrl.TrimEnd('/') + "/.well-known/openid-configuration";
        var http = httpClientFactory.CreateClient();
        var json = await http.GetStringAsync(url);
        var doc  = JsonDocument.Parse(json);
        _discoveryCache[issuerUrl] = doc;
        return doc;
    }

    private static string? TryGet(JsonDocument doc, params string[] keys)
    {
        foreach (var k in keys)
            if (doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }
}
