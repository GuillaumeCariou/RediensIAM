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
}
