using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RediensIAM.Client.Tests;

/// <summary>
/// P-06. RediensIAM requires <c>project_id</c> on <c>/api/introspect</c> and <c>/api/authorize</c>.
///
/// These assert on the bytes the client actually writes, not on a mock it was handed: a server
/// that did not read the field would answer exactly as if it had, so nothing short of reading the
/// request proves the field is there.
/// </summary>
public class ProjectBindingTests
{
    private const string ProjectId = "proj-1";

    /// <summary>A configuration that works, for tests that vary one field of it.</summary>
    private static RediensIamOptions Options() => new()
    {
        BaseUrl             = "https://auth.example.com",
        ServiceAccountToken = "rediens_pat_x",
        ProjectId           = ProjectId,
    };

    private static (RediensIamClient Client, StubHandler Handler) ClientAnswering(
        string json, RediensIamOptions? options = null)
    {
        var handler = new StubHandler(json);
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://auth.example.com/") };
        return (new RediensIamClient(http, options ?? Options(), new MemoryCache(new MemoryCacheOptions())), handler);
    }

    // ── The project id reaches the wire ─────────────────────────────────────────

    [Fact]
    public async Task Introspect_sends_the_project_id()
    {
        var (client, handler) = ClientAnswering("""{"active":true}""");

        var info = await client.IntrospectAsync("rediens_pat_x");

        Assert.True(info.Active);
        Assert.Contains("project_id=proj-1", handler.LastBody);
    }

    [Fact]
    public async Task Authorize_sends_the_project_id()
    {
        var (client, handler) = ClientAnswering("""{"allowed":true}""");

        Assert.True(await client.AuthorizeAsync("t", "Organisations", "org-1", "org_admin"));
        Assert.Contains("\"project_id\":\"proj-1\"", handler.LastBody);
    }

    // ── A client with no project id does not exist ──────────────────────────────

    /// <summary>
    /// The server answers <c>400 project_id_required</c>. Refusing at construction turns that into
    /// a startup failure naming the fix, instead of a 400 on every request once traffic arrives —
    /// the same shape as the pre-existing https check on <c>BaseUrl</c>.
    /// </summary>
    [Fact]
    public void Client_without_a_project_id_cannot_be_constructed()
    {
        var options = Options();
        options.ProjectId = "";

        var error = Assert.Throws<ArgumentException>(() =>
            new RediensIamClient(new HttpClient(), options, new MemoryCache(new MemoryCacheOptions())));

        Assert.Contains("ProjectId", error.Message);
    }

    [Fact]
    public void Registration_without_a_project_id_fails_at_startup()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(() => services.AddRediensIam(o =>
        {
            o.BaseUrl             = "https://auth.example.com";
            o.ServiceAccountToken = "rediens_pat_x";
        }));

        Assert.Contains("ProjectId", error.Message);
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

    /// <summary>Answers every request with one canned body and records what it was sent.</summary>
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
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
    /// A delegated session: <c>act</c> names the operator and the role list is empty. A consumer
    /// that reads the roles and ignores <c>act</c> cannot tell support traffic from the customer's
    /// own, which is the one thing it must never fail to do.
    /// </summary>
    [Fact]
    public async Task Delegated_answer_surfaces_the_actor_and_carries_no_roles()
    {
        var (client, _) = ClientAnswering(
            """{"active":true,"sub":"imp_7f3","org_id":"acme","roles":[],"act":{"sub":"usr_operator","level":"super_admin","mode":"read","session":"7f3"}}""");

        var info = await client.IntrospectAsync("rediens_imp_x");

        Assert.NotNull(info.Act);
        Assert.Equal("usr_operator", info.Act!.Sub);
        Assert.Equal("read", info.Act.Mode);
        Assert.Empty(info.Roles);
        Assert.True(info.IsReadOnlyImpersonation);
    }

    /// <summary>
    /// Opening a session returns a credential shown once. The reason travels in the body — it is
    /// what the entered tenant's own audit log will show them.
    /// </summary>
    [Fact]
    public async Task Opening_an_impersonation_returns_the_session()
    {
        var (client, handler) = ClientAnswering(
            """{"access_token":"rediens_imp_abc","session_id":"7f3","expires_in":900,"sub":"imp_7f3","org_id":"acme","project_id":"p1","act":{"sub":"usr_operator","level":"super_admin","mode":"read","session":"7f3"}}""");

        var session = await client.OpenImpersonationAsync("acme", "p1", "read", "ticket #4812");

        Assert.Equal("rediens_imp_abc", session.AccessToken);
        Assert.Equal("7f3", session.SessionId);
        Assert.Equal(900, session.ExpiresIn);
        Assert.Equal("usr_operator", session.Act!.Sub);
        Assert.Contains("impersonate", handler.LastUri!.ToString());
        Assert.Contains("ticket #4812", handler.LastBody);
    }

    /// <summary>
    /// A session with no stated reason is not auditable, so this refuses before the round trip
    /// rather than letting the server answer 400.
    /// </summary>
    [Fact]
    public async Task Opening_an_impersonation_without_a_reason_throws()
    {
        var (client, _) = ClientAnswering("""{"access_token":"x","session_id":"y","expires_in":900}""");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.OpenImpersonationAsync("acme", "p1", "read", "  "));
    }

    /// <summary>And absent on everything else — which is what gives the field its meaning.</summary>
    [Fact]
    public async Task Ordinary_answer_has_no_actor()
    {
        var (client, _) = ClientAnswering("""{"active":true,"sub":"sa:1"}""");

        var info = await client.IntrospectAsync("rediens_pat_x");

        Assert.Null(info.Act);
        Assert.False(info.IsReadOnlyImpersonation);
    }

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
            """{"active":false,"sub":null,"user_id":null,"org_id":null,"project_id":null,"roles":null,"client_id":null,"is_service_account":false,"project_id":null}""");

        var info = await client.IntrospectAsync("rediens_pat_expired");

        Assert.False(info.Active);
        Assert.NotNull(info.Roles);
        Assert.Empty(info.Roles);
        Assert.False(info.HasRole("org_admin"));
    }

    /// <summary>
    /// One token, two projects, two different answers — the whole point of the `project_id` contract.
    /// The cache key hashed the token alone, so with a shared IMemoryCache (which
    /// <c>AddRediensIam</c> resolves from the host) the first tenant's `active: true` was served
    /// to the second, roles and all, with no round trip.
    /// </summary>
    [Fact]
    public async Task Cached_answers_are_not_shared_across_projects()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());

        var clientA = new RediensIamClient(
            new HttpClient(new StubHandler("""{"active":true,"org_id":"org-a","roles":["org_admin"],"project_id":"proj-a"}"""))
            { BaseAddress = new Uri("https://auth.example.com/") },
            new RediensIamOptions { BaseUrl = "https://auth.example.com", ServiceAccountToken = "t", ProjectId = "proj-a" },
            cache);

        var clientB = new RediensIamClient(
            new HttpClient(new StubHandler("""{"active":false,"roles":[]}"""))
            { BaseAddress = new Uri("https://auth.example.com/") },
            new RediensIamOptions { BaseUrl = "https://auth.example.com", ServiceAccountToken = "t", ProjectId = "proj-b" },
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
        var http = new HttpClient(new StubHandler("""{"active":true}"""))
        {
            BaseAddress = new Uri("https://auth.example.com/"),
        };

        _ = new RediensIamClient(http, new RediensIamOptions
        {
            BaseUrl             = "https://auth.example.com",
            ServiceAccountToken = "t",
            ProjectId           = ProjectId,
            Timeout             = TimeSpan.FromSeconds(5),
        }, new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal(TimeSpan.FromSeconds(5), http.Timeout);
    }

    /// <summary>
    /// A base URL that carries a path keeps it.
    ///
    /// <para>
    /// HttpClient resolves a relative request URI against the last path segment of its
    /// BaseAddress, so the separator has to be there: with "https://host/iam", a request for
    /// "introspect" goes to "https://host/introspect" — the segment is dropped and the call lands
    /// somewhere the caller never configured. The separator used to be appended by string
    /// concatenation, which also put it after any query string a base URL happened to carry.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("https://iam.example.com", "https://iam.example.com/")]
    [InlineData("https://iam.example.com/", "https://iam.example.com/")]
    [InlineData("https://iam.example.com/iam", "https://iam.example.com/iam/")]
    [InlineData("https://iam.example.com/iam/", "https://iam.example.com/iam/")]
    public void BaseAddress_AlwaysEndsAtADirectory(string configured, string expected)
    {
        var services = new ServiceCollection();
        services.AddRediensIam(o =>
        {
            o.BaseUrl             = configured;
            o.ProjectId            = "resource-server";
            o.ServiceAccountToken = "rediens_pat_x";
        });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client  = factory.CreateClient(nameof(RediensIamClient));

        Assert.Equal(expected, client.BaseAddress?.ToString());
    }
}
