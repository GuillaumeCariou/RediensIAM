using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RediensIAM.Config;
using RediensIAM.Models;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// Step 32 — the login path resolves its tenant before it resolves its user.
///
/// <para>
/// Until now every RLS discussion had to restate the same limit: login looked a user up by
/// e-mail before any organisation was known, so the whole authentication surface ran as
/// <c>'system'</c> and row-level security enforced nothing on it. It turns out the tenant is
/// knowable much earlier — the Hydra login challenge names an OAuth2 client, and RediensIAM
/// writes <c>org_id</c> into that client's metadata at project creation. These tests hold the
/// three things that must all be true for that to be an improvement rather than a hole:
/// the scope comes from the challenge, a mismatched challenge is refused, and scoping the
/// lookup does not turn it into an oracle for which tenant an address belongs to.
/// </para>
///
/// <para>
/// <b>Why some of these talk to Postgres directly.</b> The fixture's container runs the
/// application as its bootstrap superuser, and a superuser bypasses row-level security even
/// under <c>FORCE</c>. Asserting isolation through the application's own <c>DbContext</c> would
/// therefore assert nothing. The policy-level tests below create an ordinary role, apply the
/// real predicates from <c>deploy/rediensiam/files/rls.sql</c>, and probe as that role.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class LoginScopeRegressionTests(TestFixture fixture) : IAsyncLifetime
{
    // POST /auth/login is rate limited per source address, and every test here shares one.
    public Task InitializeAsync() => fixture.FlushCacheAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string ReadScope = "SELECT current_setting('rediensiam.org_id', true)";
    private const string Password = SeedData.DefaultPassword;

    // ── The challenge carries the tenant ──────────────────────────────────────

    [Fact]
    public async Task Login_Runs_Under_The_Organisation_Its_Challenge_Names()
    {
        var tenant = await CreateTenantAsync();
        var user = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, tenant.Project.HydraClientId, tenant.Project.Id.ToString(), tenant.Org.Id.ToString());
        fixture.Hydra.ResetLog();

        var res = await PostLoginAsync(challenge, user.Email);

        res.Status.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(res.Body)!["redirect_to"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        fixture.Hydra.LoginWasAccepted(challenge).Should().BeTrue();
    }

    [Fact]
    public async Task Login_Is_Refused_When_The_Challenges_Client_Names_A_Different_Organisation()
    {
        // The proof that the organisation actually comes from client.metadata and is actually
        // enforced: the project is real and active, the user is real, the password is right, and
        // the only wrong thing is the org the challenge's client is registered to. With RLS on,
        // the project is already invisible under that scope; the explicit check is what makes
        // this hold with the chart flag off too.
        var tenant = await CreateTenantAsync();
        var user = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var (stranger, _) = await fixture.Seed.CreateOrgAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, tenant.Project.HydraClientId, tenant.Project.Id.ToString(), stranger.Id.ToString());
        fixture.Hydra.ResetLog();

        var res = await PostLoginAsync(challenge, user.Email);

        res.Status.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(res.Body)!["error"]!.GetValue<string>().Should().Be("project_org_mismatch");
        fixture.Hydra.LoginWasAccepted(challenge).Should().BeFalse();
    }

    [Fact]
    public async Task Login_Still_Works_For_A_Client_Registered_Before_Org_Id_Was_Recorded()
    {
        // SetupLoginChallenge writes project_id into client.metadata and no org_id — the shape
        // of every project client minted before the organisation was recorded there. The scope
        // then comes from the project row instead, one unscoped read later. Falling back must
        // not be a failure: it is the difference between a narrower window and an outage.
        var tenant = await CreateTenantAsync();
        var user = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallenge(
            challenge, tenant.Project.HydraClientId, projectId: tenant.Project.Id.ToString());
        fixture.Hydra.ResetLog();

        var res = await PostLoginAsync(challenge, user.Email);

        res.Status.Should().Be(HttpStatusCode.OK);
        fixture.Hydra.LoginWasAccepted(challenge).Should().BeTrue();
    }

    // ── The enumeration surface ───────────────────────────────────────────────

    [Fact]
    public async Task A_User_In_Another_Tenant_Is_Indistinguishable_From_One_That_Does_Not_Exist()
    {
        // The rule this whole change is held to: scoping the credential lookup must not turn it
        // into a probe for which tenant an address belongs to. It does not, because the lookup
        // was already keyed by the project's assigned user list — the scope narrows it to
        // exactly the rows that predicate already allowed. Both requests must come back byte for
        // byte the same.
        var tenant = await CreateTenantAsync();
        var stranger = await CreateTenantAsync();
        var foreigner = await fixture.Seed.CreateUserAsync(stranger.List.Id);

        var foreignAttempt = await AttemptLoginAsync(tenant, foreigner.Email);
        var absentAttempt = await AttemptLoginAsync(tenant, SeedData.UniqueEmail());

        foreignAttempt.Status.Should().Be(HttpStatusCode.Unauthorized);
        foreignAttempt.Status.Should().Be(absentAttempt.Status);
        foreignAttempt.Body.Should().Be(absentAttempt.Body,
            "a foreign tenant's address must not be distinguishable from one nobody has registered");
    }

    [Fact]
    public async Task A_Foreign_Tenants_Password_Is_Not_A_Signal_Either()
    {
        // The complement: the *correct* password for a user in another tenant must be as wrong
        // as any other string. If the scope were derived from the address rather than from the
        // challenge, this is where that would show.
        var tenant = await CreateTenantAsync();
        var stranger = await CreateTenantAsync();
        var foreigner = await fixture.Seed.CreateUserAsync(stranger.List.Id);

        var rightPassword = await AttemptLoginAsync(tenant, foreigner.Email, Password);
        var wrongPassword = await AttemptLoginAsync(tenant, foreigner.Email, "not-the-password");

        rightPassword.Status.Should().Be(HttpStatusCode.Unauthorized);
        rightPassword.Body.Should().Be(wrongPassword.Body);
    }

    // ── The pin itself ────────────────────────────────────────────────────────

    [Fact]
    public async Task Pinned_Scope_Reaches_The_Connection_The_Request_Goes_On_To_Use()
    {
        var orgId = Guid.NewGuid();
        var accessor = fixture.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        try
        {
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
            var interceptor = fixture.Services.GetRequiredService<TenantScopeInterceptor>();

            (await ReadScopeAsync(db)).Should().Be(TenantScopeInterceptor.SystemScope, "nothing is pinned yet");

            await interceptor.PinToOrganisationAsync(db, orgId);

            (await ReadScopeAsync(db)).Should().Be(orgId.ToString());
            await db.Users.AsNoTracking().Take(1).ToListAsync();
            (await ReadScopeAsync(db)).Should().Be(orgId.ToString(), "and it survives the next checkout in this request");
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public async Task Pin_Refuses_To_Move_A_Request_Already_Scoped_By_Another_Organisations_Token()
    {
        var accessor = fixture.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = ContextWithOrgClaim(Guid.NewGuid().ToString());
        try
        {
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
            var interceptor = fixture.Services.GetRequiredService<TenantScopeInterceptor>();

            var act = () => interceptor.PinToOrganisationAsync(db, Guid.NewGuid());

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another organisation*");
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public async Task Pin_Is_Allowed_When_The_Token_Names_No_Organisation()
    {
        // A stray Authorization header on an ordinary login must not become a 500. Only a token
        // that names a *different* organisation is a conflict.
        var orgId = Guid.NewGuid();
        var accessor = fixture.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = ContextWithOrgClaim("");
        try
        {
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
            var interceptor = fixture.Services.GetRequiredService<TenantScopeInterceptor>();

            await interceptor.PinToOrganisationAsync(db, orgId);

            (await ReadScopeAsync(db)).Should().Be(orgId.ToString());
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    // ── What the pin actually buys, at the policy layer ───────────────────────

    [Fact]
    public async Task Under_A_Pinned_Scope_Another_Tenants_User_Is_Invisible_To_The_Policies()
    {
        var tenant = await CreateTenantAsync();
        var stranger = await CreateTenantAsync();
        var mine = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var theirs = await fixture.Seed.CreateUserAsync(stranger.List.Id);

        await WithRealPoliciesAsync(async probe =>
        {
            var underMyScope = await VisibleUserIdsAsync(probe, tenant.Org.Id.ToString());
            underMyScope.Should().Contain(mine.Id);
            underMyScope.Should().NotContain(theirs.Id,
                "this is the isolation the pin exists to obtain — without it the login path saw every tenant's users");

            var unscoped = await VisibleUserIdsAsync(probe, TenantScopeInterceptor.SystemScope);
            unscoped.Should().Contain(mine.Id).And.Contain(theirs.Id);
        });
    }

    [Fact]
    public async Task The_Admin_User_List_Is_Invisible_Under_Every_Tenant_Scope()
    {
        // Why AdminLogin cannot be scoped and is not a gap that was overlooked. The console's
        // users live in a list whose OrgId IS NULL; NULL = rls_org() is NULL, never true, so no
        // organisation scope can ever see them. Running that lookup as 'system' is the only
        // thing that works, and it is structural rather than a shortcut.
        var systemList = new UserList
        {
            Id = Guid.NewGuid(), Name = $"__system__{Guid.NewGuid():N}", OrgId = null,
            Immovable = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();
        var admin = await fixture.Seed.CreateUserAsync(systemList.Id);
        var tenant = await CreateTenantAsync();

        await WithRealPoliciesAsync(async probe =>
        {
            (await VisibleUserIdsAsync(probe, tenant.Org.Id.ToString())).Should().NotContain(admin.Id);
            (await VisibleUserIdsAsync(probe, Guid.NewGuid().ToString())).Should().NotContain(admin.Id);
            (await VisibleUserIdsAsync(probe, TenantScopeInterceptor.SystemScope)).Should().Contain(admin.Id);
        });
    }

    // ── The paths that stay unscoped must keep working ────────────────────────

    [Fact]
    public async Task The_Admin_Console_Login_Still_Resolves_Its_User_Unscoped()
    {
        var systemList = new UserList
        {
            Id = Guid.NewGuid(), Name = $"__system__{Guid.NewGuid():N}", OrgId = null,
            Immovable = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();
        var admin = await fixture.Seed.CreateUserAsync(systemList.Id);

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallenge(challenge, Roles.AdminClientId);
        fixture.Keto.AllowAll();

        var res = await PostLoginAsync(challenge, admin.Email);

        // Whatever the console decides about a second factor, it must have FOUND the user — an
        // org-scoped connection would have returned invalid_credentials here.
        res.Status.Should().Be(HttpStatusCode.OK);
        res.Body.Should().NotContain("invalid_credentials");
    }

    [Fact]
    public async Task Token_Keyed_Email_Verification_Still_Works_Unscoped()
    {
        // No organisation is knowable here: the subject is named by a random token. It runs as
        // 'system' by necessity, and must keep doing so.
        var tenant = await CreateTenantAsync();
        var user = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        user.EmailVerified = false;
        var raw = Guid.NewGuid().ToString("N");
        fixture.Db.EmailTokens.Add(new EmailToken
        {
            UserId = user.Id,
            Kind = "verify_email",
            TokenHash = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(
                global::System.Text.Encoding.UTF8.GetBytes(raw))),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var res = await fixture.Client.GetAsync($"/auth/verify-email?token={raw}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── The greppable artefact ────────────────────────────────────────────────

    [Fact]
    public void The_Unscoped_Path_List_No_Longer_Claims_The_Whole_Login_Path()
    {
        var paths = TenantScopeInterceptor.LegitimatelyUnscopedPaths;

        paths.Should().NotBeEmpty("something always remains, and hiding it is worse than listing it");
        paths.Should().Contain(p => p.Contains("AdminLogin", StringComparison.Ordinal),
            "the console login is the irreducible case and must stay named");
        paths.Should().NotContain(p =>
            p.Contains("login, password reset, e-mail verification, social/SAML callbacks", StringComparison.Ordinal),
            "the blanket pre-authentication entry is no longer true — most of that surface is now pinned");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private sealed record Tenant(Organisation Org, Project Project, UserList List);

    private async Task<Tenant> CreateTenantAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        project.RequireMfa = false;
        await fixture.Db.SaveChangesAsync();
        return new Tenant(org, project, list);
    }

    private sealed record Attempt(HttpStatusCode Status, string Body);

    private async Task<Attempt> PostLoginAsync(string challenge, string email, string password = Password)
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge, email, password,
        });
        return new Attempt(res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>One login attempt against <paramref name="tenant"/> on its own fresh challenge.</summary>
    private async Task<Attempt> AttemptLoginAsync(Tenant tenant, string email, string password = "wrong-password")
    {
        await fixture.FlushCacheAsync();
        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, tenant.Project.HydraClientId, tenant.Project.Id.ToString(), tenant.Org.Id.ToString());
        return await PostLoginAsync(challenge, email, password);
    }

    private static DefaultHttpContext ContextWithOrgClaim(string orgId)
    {
        var context = new DefaultHttpContext();
        context.Items["Claims"] = new TokenClaims
        {
            UserId = Guid.NewGuid().ToString(), OrgId = orgId, ProjectId = "", Roles = [],
        };
        return context;
    }

    private static async Task<string?> ReadScopeAsync(RediensIamDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            return await ScalarAsync(db.Database.GetDbConnection(), ReadScope);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : value.ToString();
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Applies the real <c>users</c> / <c>user_lists</c> predicates from
    /// <c>deploy/rediensiam/files/rls.sql</c>, runs <paramref name="probe"/> as an ordinary
    /// (non-superuser, non-owner) role that the policies actually bind to, and removes both
    /// again. The application's own connection is a superuser and is unaffected either way,
    /// which is exactly why the probe has to exist.
    /// </summary>
    private async Task WithRealPoliciesAsync(Func<NpgsqlConnection, Task> probe)
    {
        var role = $"rls_probe_{Guid.NewGuid():N}"[..24];
        const string probePassword = "probe-not-a-secret";

        var admin = new NpgsqlConnection(fixture.PostgresConnectionString);
        await admin.OpenAsync();
        try
        {
            await ExecuteAsync(admin, $$"""
                CREATE ROLE "{{role}}" LOGIN PASSWORD '{{probePassword}}';
                GRANT USAGE ON SCHEMA public TO "{{role}}";
                GRANT SELECT ON public.users, public.user_lists TO "{{role}}";

                CREATE OR REPLACE FUNCTION rls_unscoped() RETURNS boolean LANGUAGE sql STABLE AS $fn$
                  SELECT coalesce(current_setting('rediensiam.org_id', true), '') = 'system'
                $fn$;
                CREATE OR REPLACE FUNCTION rls_org() RETURNS uuid LANGUAGE sql STABLE AS $fn$
                  SELECT CASE
                    WHEN coalesce(current_setting('rediensiam.org_id', true), '') ~*
                         '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                    THEN current_setting('rediensiam.org_id', true)::uuid
                  END
                $fn$;

                ALTER TABLE public.user_lists ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS rediensiam_tenant_probe ON public.user_lists;
                CREATE POLICY rediensiam_tenant_probe ON public.user_lists FOR ALL
                  USING (rls_unscoped() OR ("OrgId" = rls_org()));

                ALTER TABLE public.users ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS rediensiam_tenant_probe ON public.users;
                CREATE POLICY rediensiam_tenant_probe ON public.users FOR ALL
                  USING (rls_unscoped() OR EXISTS (
                    SELECT 1 FROM public.user_lists ul
                     WHERE ul."Id" = users."UserListId" AND ul."OrgId" = rls_org()));
                """);

            var probeDsn = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
            {
                Username = role, Password = probePassword, Pooling = false,
            }.ConnectionString;

            await using var connection = new NpgsqlConnection(probeDsn);
            await connection.OpenAsync();
            await probe(connection);
        }
        finally
        {
            // Leaving policies behind would be invisible to every other test — the application
            // role is a superuser and bypasses them — right up until someone enables RLS for
            // real. Removed here rather than trusted to the next run.
            await ExecuteAsync(admin, $"""
                DROP POLICY IF EXISTS rediensiam_tenant_probe ON public.users;
                DROP POLICY IF EXISTS rediensiam_tenant_probe ON public.user_lists;
                ALTER TABLE public.users      DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public.user_lists DISABLE ROW LEVEL SECURITY;
                REVOKE ALL ON public.users, public.user_lists FROM "{role}";
                REVOKE ALL ON SCHEMA public FROM "{role}";
                DROP ROLE IF EXISTS "{role}";
                """);
            await admin.DisposeAsync();
        }
    }

    private static async Task<List<Guid>> VisibleUserIdsAsync(NpgsqlConnection probe, string scope)
    {
        await using (var set = probe.CreateCommand())
        {
            set.CommandText = "SELECT set_config('rediensiam.org_id', @scope, false)";
            set.Parameters.AddWithValue("scope", scope);
            await set.ExecuteNonQueryAsync();
        }

        var ids = new List<Guid>();
        await using var read = probe.CreateCommand();
        read.CommandText = "SELECT \"Id\" FROM public.users";
        await using var reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }
}
