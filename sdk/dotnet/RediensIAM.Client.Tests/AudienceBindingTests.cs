using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RediensIAM.Client.Tests;

/// <summary>
/// P-06. RediensIAM requires <c>aud</c> on <c>/api/introspect</c> and <c>/api/authorize</c>, and
/// stamps <c>ver</c> on every answer so a client can tell an enforcing server from one that
/// silently ignored the field.
///
/// These assert on the bytes the client actually writes, not on a mock it was handed: an
/// un-upgraded server discards the unknown <c>aud</c> without complaint, so nothing short of
/// reading the request proves the field is there.
/// </summary>
public class AudienceBindingTests
{
    private const string Audience = "proj-1";

    /// <summary>A configuration that works, for tests that vary one field of it.</summary>
    private static RediensIamOptions Options() => new()
    {
        BaseUrl             = "https://auth.example.com",
        ServiceAccountToken = "rediens_pat_x",
        Audience            = Audience,
    };

    private static (RediensIamClient Client, StubHandler Handler) ClientAnswering(
        string json, RediensIamOptions? options = null)
    {
        var handler = new StubHandler(json);
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://auth.example.com/") };
        return (new RediensIamClient(http, options ?? Options(), new MemoryCache(new MemoryCacheOptions())), handler);
    }

    // ── The audience reaches the wire ─────────────────────────────────────────

    [Fact]
    public async Task Introspect_sends_the_audience()
    {
        var (client, handler) = ClientAnswering("""{"active":true,"ver":1}""");

        var info = await client.IntrospectAsync("rediens_pat_x");

        Assert.True(info.Active);
        Assert.Contains("aud=proj-1", handler.LastBody);
    }

    [Fact]
    public async Task Authorize_sends_the_audience()
    {
        var (client, handler) = ClientAnswering("""{"allowed":true,"ver":1}""");

        Assert.True(await client.AuthorizeAsync("t", "Organisations", "org-1", "org_admin"));
        Assert.Contains("\"aud\":\"proj-1\"", handler.LastBody);
    }

    // ── A client with no audience does not exist ──────────────────────────────

    /// <summary>
    /// The server answers <c>400 audience_required</c>. Refusing at construction turns that into
    /// a startup failure naming the fix, instead of a 400 on every request once traffic arrives —
    /// the same shape as the pre-existing https check on <c>BaseUrl</c>.
    /// </summary>
    [Fact]
    public void Client_without_an_audience_cannot_be_constructed()
    {
        var options = Options();
        options.Audience = "";

        var error = Assert.Throws<ArgumentException>(() =>
            new RediensIamClient(new HttpClient(), options, new MemoryCache(new MemoryCacheOptions())));

        Assert.Contains("Audience", error.Message);
    }

    [Fact]
    public void Registration_without_an_audience_fails_at_startup()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(() => services.AddRediensIam(o =>
        {
            o.BaseUrl             = "https://auth.example.com";
            o.ServiceAccountToken = "rediens_pat_x";
        }));

        Assert.Contains("Audience", error.Message);
    }

    /// <summary>R-30, kept passing through the move into <c>RediensIamOptions.Validated()</c>.</summary>
    [Theory]
    [InlineData("https://auth.example.com", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://auth.example.com", false)]
    [InlineData("auth.example.com", false)]
    public void BaseUrl_must_be_https_except_on_loopback(string baseUrl, bool accepted)
    {
        var options = Options();
        options.BaseUrl = baseUrl;

        if (accepted) options.Validated();
        else Assert.Throws<ArgumentException>(() => options.Validated());
    }

    // ── An answer without `ver` is not trusted ────────────────────────────────

    /// <summary>
    /// The anti-downgrade signal. An un-upgraded RediensIAM drops the unknown <c>aud</c> and
    /// answers without <c>ver</c> — byte-for-byte what it always answered. Accepting that would
    /// mean believing a deployment-wide result was scoped to one tenant.
    /// </summary>
    [Fact]
    public async Task Introspection_answer_without_ver_is_refused()
    {
        var (client, _) = ClientAnswering("""{"active":true,"org_id":"org-9"}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.IntrospectAsync("rediens_pat_x"));

        Assert.Contains("ver=0", error.Message);
    }

    [Fact]
    public async Task Authorization_answer_without_ver_is_refused()
    {
        var (client, _) = ClientAnswering("""{"allowed":true}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AuthorizeAsync("t", "Organisations", "org-1", "org_admin"));

        Assert.Contains("ver=0", error.Message);
    }

    /// <summary>Answers every request with one canned body and records what it was sent.</summary>
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    // ── The inactive answer, and who it belongs to ────────────────────────────

    /// <summary>
    /// A server older than the roles-are-never-null fix answers an inactive token with every
    /// optional field explicitly null. System.Text.Json writes that null straight over the
    /// initialiser, so <c>Roles</c> became null and <c>HasRole</c> threw — on the documented path
    /// where this client "returns TokenInfo.Inactive for an unusable token rather than throwing".
    /// </summary>
    [Fact]
    public async Task Inactive_answer_with_null_roles_still_has_an_empty_role_list()
    {
        var (client, _) = ClientAnswering(
            """{"active":false,"sub":null,"user_id":null,"org_id":null,"project_id":null,"roles":null,"client_id":null,"is_service_account":false,"aud":null,"ver":1}""");

        var info = await client.IntrospectAsync("rediens_pat_expired");

        Assert.False(info.Active);
        Assert.NotNull(info.Roles);
        Assert.Empty(info.Roles);
        Assert.False(info.HasRole("org_admin"));
    }

    /// <summary>
    /// One token, two audiences, two different answers — the whole point of the `aud` contract.
    /// The cache key hashed the token alone, so with a shared IMemoryCache (which
    /// <c>AddRediensIam</c> resolves from the host) the first tenant's `active: true` was served
    /// to the second, roles and all, with no round trip.
    /// </summary>
    [Fact]
    public async Task Cached_answers_are_not_shared_across_audiences()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());

        var clientA = new RediensIamClient(
            new HttpClient(new StubHandler("""{"active":true,"org_id":"org-a","roles":["org_admin"],"aud":"proj-a","ver":1}"""))
            { BaseAddress = new Uri("https://auth.example.com/") },
            new RediensIamOptions { BaseUrl = "https://auth.example.com", ServiceAccountToken = "t", Audience = "proj-a" },
            cache);

        var clientB = new RediensIamClient(
            new HttpClient(new StubHandler("""{"active":false,"roles":[],"ver":1}"""))
            { BaseAddress = new Uri("https://auth.example.com/") },
            new RediensIamOptions { BaseUrl = "https://auth.example.com", ServiceAccountToken = "t", Audience = "proj-b" },
            cache);

        var a = await clientA.IntrospectAsync("the-same-token");
        var b = await clientB.IntrospectAsync("the-same-token");

        Assert.True(a.Active);
        Assert.False(b.Active, "tenant B's server said inactive; a cache keyed on the token alone answered for it");
    }

    /// <summary>
    /// `Timeout` is documented as an option and honoured only by the DI extension, while the
    /// README blesses direct construction. A hung IAM then stalled every request for the
    /// HttpClient default of 100 seconds instead of the five this asks for.
    /// </summary>
    [Fact]
    public void Timeout_option_is_applied_when_the_client_is_constructed_directly()
    {
        var http = new HttpClient(new StubHandler("""{"active":true,"ver":1}"""))
        {
            BaseAddress = new Uri("https://auth.example.com/"),
        };

        _ = new RediensIamClient(http, new RediensIamOptions
        {
            BaseUrl             = "https://auth.example.com",
            ServiceAccountToken = "t",
            Audience            = Audience,
            Timeout             = TimeSpan.FromSeconds(5),
        }, new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal(TimeSpan.FromSeconds(5), http.Timeout);
    }
}
