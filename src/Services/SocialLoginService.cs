using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
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
    string? LinkProjectId = null,
    string? CodeVerifier = null
);

public record SocialUserProfile(
    string ProviderUserId,
    string? Email,
    string? Name,
    bool IsEmailVerified = false
);

// ── Service ──────────────────────────────────────────────────────────────────

public class SocialLoginService(
    IHttpClientFactory httpClientFactory,
    IDistributedCache cache,
    AppConfig appConfig,
    IWebHostEnvironment env,
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

    private const string BearerScheme = "Bearer";

    private static readonly Dictionary<string, string> DefaultScopes = new()
    {
        ["google"]   = "openid email profile",
        ["github"]   = "read:user user:email",
        ["gitlab"]   = "read_user",
        ["facebook"] = Email,
    };

    private const string Email = "email";

    // In-process cache for OIDC discovery documents
    private readonly Dictionary<string, JsonDocument> _discoveryCache = new(64);
    private const int DiscoveryCacheMaxSize = 64;

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

    // ── PKCE ─────────────────────────────────────────────────────────────────

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (verifier, challenge);
    }

    private static Dictionary<string, string> AddPkce(Dictionary<string, string> query, string challenge)
    {
        query["code_challenge"]        = challenge;
        query["code_challenge_method"] = "S256";
        return query;
    }

    // ── Authorization URL ────────────────────────────────────────────────────

    public async Task<(string Url, string State)> BuildAuthorizationUrlAsync(
        ProviderConfig provider, string loginChallenge, string projectId)
    {
        var (verifier, challenge) = GeneratePkce();
        var stateData = new OAuthStateData(loginChallenge, projectId, provider.Id, CodeVerifier: verifier);
        var state = await StoreStateAsync(stateData);

        var (authEndpoint, scope) = await GetAuthEndpointAndScopeAsync(provider);

        var query = AddPkce(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"]     = provider.ClientId,
            ["redirect_uri"]  = CallbackUrl,
            ["scope"]         = scope,
            ["state"]         = state,
        }, challenge);

        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return ($"{authEndpoint}?{qs}", state);
    }

    public async Task<(string Url, string State)> BuildLinkAuthorizationUrlAsync(
        ProviderConfig provider, OAuthStateData stateData)
    {
        var (verifier, challenge) = GeneratePkce();
        stateData = stateData with { CodeVerifier = verifier };
        var state = await StoreStateAsync(stateData);
        var (authEndpoint, scope) = await GetAuthEndpointAndScopeAsync(provider);

        var query = AddPkce(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"]     = provider.ClientId,
            ["redirect_uri"]  = CallbackUrl,
            ["scope"]         = scope,
            ["state"]         = state,
        }, challenge);

        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return ($"{authEndpoint}?{qs}", state);
    }

    // ── Code exchange + user profile ─────────────────────────────────────────

    public async Task<SocialUserProfile?> ExchangeAndGetProfileAsync(ProviderConfig provider, string code, string? codeVerifier = null)
    {
        try
        {
            var accessToken = await ExchangeCodeAsync(provider, code, codeVerifier);
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

    private async Task<string?> ExchangeCodeAsync(ProviderConfig provider, string code, string? codeVerifier = null)
    {
        var tokenEndpoint = await GetTokenEndpointAsync(provider);
        var http = httpClientFactory.CreateClient();

        var fields = new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["redirect_uri"]  = CallbackUrl,
            ["client_id"]     = provider.ClientId,
            ["client_secret"] = provider.ClientSecret,
        };
        if (codeVerifier != null)
            fields["code_verifier"] = codeVerifier;

        var body = new FormUrlEncodedContent(fields);

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

    private async Task<JsonDocument?> GetBearerJsonAsync(string url, string accessToken)
    {
        var http = httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(BearerScheme, accessToken);
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private async Task<SocialUserProfile?> GetStandardProfileAsync(ProviderConfig provider, string accessToken)
    {
        var userInfoUrl = await GetUserInfoEndpointAsync(provider);
        using var doc = await GetBearerJsonAsync(userInfoUrl, accessToken);
        if (doc == null) return null;
        var sub           = TryGet(doc, "sub", "id");
        var email         = TryGet(doc, Email);
        var name          = TryGet(doc, "name", "display_name", "username");
        var emailVerified = doc.RootElement.TryGetProperty("email_verified", out var ev) && ev.GetBoolean();
        return sub == null ? null : new SocialUserProfile(sub, email, name, emailVerified);
    }

    private async Task<SocialUserProfile?> GetGithubProfileAsync(string accessToken)
    {
        var http = httpClientFactory.CreateClient();

        async Task<JsonDocument?> CallAsync(string url)
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(BearerScheme, accessToken);
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

        // Always fetch from emails API to get verified status. Public profile email is unverified.
        string? email = null;
        var emailVerified = false;
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
                    emailVerified = true;
                    break;
                }
            }
        }
        // Fallback to profile email if emails API unavailable; treat as unverified
        if (string.IsNullOrEmpty(email))
            email = TryGet(user, Email);

        return new SocialUserProfile(id, email, name, emailVerified);
    }

    private async Task<SocialUserProfile?> GetFacebookProfileAsync(string accessToken)
    {
        using var doc = await GetBearerJsonAsync(BuiltinEndpoints["facebook"].UserInfo, accessToken);
        if (doc == null) return null;
        var id    = TryGet(doc, "id");
        var email = TryGet(doc, Email);
        var name  = TryGet(doc, "name");
        // Facebook does not expose email_verified; treat emails as unverified for account linking
        return id == null ? null : new SocialUserProfile(id, email, name, IsEmailVerified: false);
    }

    private async Task<SocialUserProfile?> GetOidcProfileAsync(ProviderConfig provider, string accessToken)
    {
        var disco       = await GetDiscoveryAsync(provider.IssuerUrl!);
        var userInfoUrl = disco.RootElement.GetProperty("userinfo_endpoint").GetString()!;
        using var doc   = await GetBearerJsonAsync(userInfoUrl, accessToken);
        if (doc == null) return null;
        var sub           = TryGet(doc, "sub");
        var email         = TryGet(doc, Email);
        var name          = TryGet(doc, "name", "preferred_username");
        var emailVerified = doc.RootElement.TryGetProperty("email_verified", out var ev) && ev.GetBoolean();
        return sub == null ? null : new SocialUserProfile(sub, email, name, emailVerified);
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
        if (!Uri.TryCreate(issuerUrl, UriKind.Absolute, out var parsed))
            throw new ArgumentException($"OIDC issuer_url must be an absolute URL: {issuerUrl}");
        var httpsRequired = env.IsProduction() || parsed.Host != "localhost";
        if (parsed.Scheme != "https" && httpsRequired)
            throw new ArgumentException($"OIDC issuer_url must be an absolute HTTPS URL: {issuerUrl}");

        if (_discoveryCache.TryGetValue(issuerUrl, out var cached)) return cached;

        if (_discoveryCache.Count >= DiscoveryCacheMaxSize)
            _discoveryCache.Remove(_discoveryCache.Keys.First());

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
