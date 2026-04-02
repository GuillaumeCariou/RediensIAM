using System.Text.Json;
using System.Text.Json.Serialization;
using RediensIAM.Config;
using RediensIAM.Models;

namespace RediensIAM.Services;

public class HydraLoginRequest
{
    [JsonPropertyName("skip")] public bool Skip { get; set; }
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("oidc_context")] public HydraOidcContext? OidcContext { get; set; }
    [JsonPropertyName("request_url")] public string RequestUrl { get; set; } = "";
    [JsonPropertyName("client")] public HydraClient? Client { get; set; }
}

public class HydraConsentRequest
{
    [JsonPropertyName("skip")] public bool Skip { get; set; }
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("login_session_id")] public string? LoginSessionId { get; set; }
    [JsonPropertyName("requested_scope")] public List<string> RequestedScope { get; set; } = [];
    [JsonPropertyName("context")] public Dictionary<string, object>? Context { get; set; }
    [JsonPropertyName("client")] public HydraClient? Client { get; set; }
}

public class HydraOidcContext
{
    [JsonPropertyName("login_hint")] public string? LoginHint { get; set; }
    [JsonPropertyName("extra")] public Dictionary<string, object>? Extra { get; set; }
}

public class HydraClient
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("client_name")] public string? ClientName { get; set; }
    [JsonPropertyName("metadata")] public Dictionary<string, object>? Metadata { get; set; }
}

public class HydraConsentSession
{
    [JsonPropertyName("consent_request")] public HydraConsentSessionRequest? ConsentRequest { get; set; }
    [JsonPropertyName("granted_scopes")]  public string[] GrantedScopes { get; set; } = [];
    [JsonPropertyName("granted_at")]      public DateTimeOffset? GrantedAt { get; set; }
    [JsonPropertyName("expires_at")]      public DateTimeOffset? ExpiresAt { get; set; }
}

public class HydraConsentSessionRequest
{
    [JsonPropertyName("client")]       public HydraClient? Client       { get; set; }
    [JsonPropertyName("requested_at")] public DateTimeOffset? RequestedAt { get; set; }
    [JsonPropertyName("subject")]      public string Subject { get; set; } = "";
}

public class HydraService(IHttpClientFactory http, AppConfig appConfig)
{
    private readonly string _adminUrl = appConfig.HydraAdminUrl;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _client = http.CreateClient("hydra-admin");

    private HttpClient Client => _client;

    // ── Login ─────────────────────────────────────────────────────────────────

    public async Task<HydraLoginRequest> GetLoginRequestAsync(string challenge)
    {
        var resp = await Client.GetAsync($"{_adminUrl}/admin/oauth2/auth/requests/login?login_challenge={challenge}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<HydraLoginRequest>(_json) ?? throw new Exception("Invalid Hydra response");
    }

    public async Task<string> AcceptLoginAsync(string challenge, string subject, Dictionary<string, object> context)
    {
        var body = new { subject, context, remember = false, remember_for = 0 };
        var resp = await Client.PutAsJsonAsync(
            $"{_adminUrl}/admin/oauth2/auth/requests/login/accept?login_challenge={challenge}", body);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.GetProperty("redirect_to").GetString()!;
    }

    public async Task<string> RejectLoginAsync(string challenge, string error, string description)
    {
        var body = new { error, error_description = description };
        var resp = await Client.PutAsJsonAsync(
            $"{_adminUrl}/admin/oauth2/auth/requests/login/reject?login_challenge={challenge}", body);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.GetProperty("redirect_to").GetString()!;
    }

    // ── Consent ───────────────────────────────────────────────────────────────

    public async Task<HydraConsentRequest> GetConsentRequestAsync(string challenge)
    {
        var resp = await Client.GetAsync($"{_adminUrl}/admin/oauth2/auth/requests/consent?consent_challenge={challenge}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<HydraConsentRequest>(_json) ?? throw new Exception("Invalid Hydra response");
    }

    public async Task<string> AcceptConsentAsync(string challenge, object session, List<string> grantScope)
    {
        var body = new { grant_scope = grantScope, session, remember = false };
        var resp = await Client.PutAsJsonAsync(
            $"{_adminUrl}/admin/oauth2/auth/requests/consent/accept?consent_challenge={challenge}", body);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.GetProperty("redirect_to").GetString()!;
    }

    public async Task<string> RejectConsentAsync(string challenge, string error, string description)
    {
        var body = new { error, error_description = description };
        var resp = await Client.PutAsJsonAsync(
            $"{_adminUrl}/admin/oauth2/auth/requests/consent/reject?consent_challenge={challenge}", body);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.GetProperty("redirect_to").GetString()!;
    }

    // ── Logout ────────────────────────────────────────────────────────────────

    public async Task<string> GetLogoutRequestAsync(string challenge)
    {
        var resp = await Client.GetAsync($"{_adminUrl}/admin/oauth2/auth/requests/logout?logout_challenge={challenge}");
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.GetProperty("request_url").GetString() ?? "";
    }

    public async Task<string> AcceptLogoutAsync(string challenge)
    {
        var resp = await Client.PutAsJsonAsync(
            $"{_adminUrl}/admin/oauth2/auth/requests/logout/accept?logout_challenge={challenge}", new { });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
        return result.GetProperty("redirect_to").GetString()!;
    }

    // ── Clients ───────────────────────────────────────────────────────────────

    public async Task<JsonElement> ListOAuth2ClientsAsync()
    {
        var resp = await Client.GetAsync($"{_adminUrl}/admin/clients?page_size=500");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
    }

    public async Task<JsonElement> CreateOAuth2ClientAsync(object client)
    {
        var resp = await Client.PostAsJsonAsync($"{_adminUrl}/admin/clients", client);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
    }

    public async Task UpdateOAuth2ClientScopeAsync(string clientId, string[] allowedScopes)
    {
        var scope = allowedScopes.Length > 0
            ? string.Join(" ", new[] { "openid", "profile", "offline_access" }.Concat(allowedScopes).Distinct())
            : "openid profile offline_access";
        var resp = await Client.PatchAsJsonAsync(
            $"{_adminUrl}/admin/clients/{Uri.EscapeDataString(clientId)}",
            new { scope });
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteOAuth2ClientAsync(string clientId)
    {
        var resp = await Client.DeleteAsync($"{_adminUrl}/admin/clients/{clientId}");
        if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            resp.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement?> GetOAuth2ClientAsync(string clientId)
    {
        var resp = await Client.GetAsync($"{_adminUrl}/admin/clients/{Uri.EscapeDataString(clientId)}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<JsonElement>(_json);
    }

    public async Task CreateOrUpdateServiceAccountClientAsync(string clientId, string saName, JsonElement jwk)
    {
        var body = new
        {
            client_id   = clientId,
            client_name = saName,
            grant_types = new[] { "client_credentials" },
            token_endpoint_auth_method = "private_key_jwt",
            jwks = new { keys = new[] { jwk } }
        };
        var exists = await Client.GetAsync($"{_adminUrl}/admin/clients/{Uri.EscapeDataString(clientId)}");
        HttpResponseMessage resp;
        if (exists.IsSuccessStatusCode)
            resp = await Client.PutAsJsonAsync($"{_adminUrl}/admin/clients/{Uri.EscapeDataString(clientId)}", body);
        else
            resp = await Client.PostAsJsonAsync($"{_adminUrl}/admin/clients", body);
        resp.EnsureSuccessStatusCode();
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    public async Task RevokeSessionsAsync(string subject, string? clientId = null)
    {
        var url = $"{_adminUrl}/admin/oauth2/auth/sessions/consent?subject={Uri.EscapeDataString(subject)}";
        if (clientId != null) url += $"&client={Uri.EscapeDataString(clientId)}";
        await Client.DeleteAsync(url);
    }

    public async Task<List<HydraConsentSession>> ListConsentSessionsAsync(string subject)
    {
        var resp = await Client.GetAsync($"{_adminUrl}/admin/oauth2/auth/sessions/consent?subject={Uri.EscapeDataString(subject)}");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<HydraConsentSession>>(_json) ?? [];
    }

    public async Task RevokeConsentSessionAsync(string subject, string clientId)
    {
        await Client.DeleteAsync(
            $"{_adminUrl}/admin/oauth2/auth/sessions/consent?subject={Uri.EscapeDataString(subject)}&client={Uri.EscapeDataString(clientId)}");
    }

    public async Task RevokeAllConsentSessionsAsync(string subject)
    {
        await Client.DeleteAsync(
            $"{_adminUrl}/admin/oauth2/auth/sessions/consent?subject={Uri.EscapeDataString(subject)}");
    }

    // ── Token validation ──────────────────────────────────────────────────────

    // Validates tokens via Hydra's admin introspection endpoint (port 4445).
    // Avoids fetching JWKS from the public port (4444) which may not be reachable pod-to-pod.
    public async Task<TokenClaims?> ValidateJwtAsync(string token)
    {
        var form = new FormUrlEncodedContent([new("token", token)]);
        var resp = await Client.PostAsync($"{_adminUrl}/admin/oauth2/introspect", form);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadFromJsonAsync<IntrospectResult>(_json);
        if (body is not { Active: true }) return null;

        var ext = body.Ext;
        var userId    = ext?.GetString("user_id")    ?? body.Sub ?? "";
        var orgId     = ext?.GetString("org_id")     ?? "";
        var projectId = ext?.GetString("project_id") ?? "";
        var roles     = ext?.GetRoles("roles")        ?? [];

        return new TokenClaims
        {
            UserId = userId, OrgId = orgId, ProjectId = projectId,
            Roles = roles, IsServiceAccount = false
        };
    }

    private record IntrospectResult(
        bool Active, string? Sub,
        [property: JsonPropertyName("ext")] ExtClaims? Ext);

    private class ExtClaims : Dictionary<string, JsonElement>
    {
        public string? GetString(string key) =>
            TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public List<string> GetRoles(string key)
        {
            if (!TryGetValue(key, out var v)) return [];
            if (v.ValueKind == JsonValueKind.Array)
                return v.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToList();
            if (v.ValueKind == JsonValueKind.String)
            {
                var raw = v.GetString() ?? "";
                if (raw.StartsWith('['))
                    try { return JsonSerializer.Deserialize<List<string>>(raw) ?? []; } catch { }
                return raw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            return [];
        }
    }
}
