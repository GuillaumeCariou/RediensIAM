namespace RediensIAM.IntegrationTests.Tests.System;

/// <summary>
/// <c>GET /admin/users</c>, filter by filter.
///
/// The point of every test here is that the narrowing happened in the DATABASE. The console shows
/// fifty rows at a time; a filter applied to those fifty would answer "2 disabled accounts" for a
/// list that holds forty, and nothing on the page would look wrong. So each test seeds a
/// population, asks for a slice of it, and asserts on <c>total</c> — the number over the whole
/// filtered set, which only a server-side <c>WHERE</c> can produce.
///
/// Every request carries <c>org_id</c> of this test's own organisation. The collection shares one
/// database, so without it a count would be a count of everyone else's fixtures too.
/// </summary>
[Collection("RediensIAM")]
public class UserSearchFilterTests(TestFixture fixture)
{
    private async Task<(Organisation org, UserList list, HttpClient client)> ScaffoldAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        return (org, list, fixture.ClientWithToken(token));
    }

    private static async Task<JsonElement> BodyAsync(HttpClient client, string query)
    {
        var res = await client.GetAsync(query);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static int Total(JsonElement body) => body.GetProperty("total").GetInt32();

    private static string[] Emails(JsonElement body) =>
        [.. body.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()!)];

    /// <summary>Mutates a seeded account in place — the seeder only knows email and active.</summary>
    private async Task PatchAsync(User user, Action<User> change)
    {
        var tracked = await fixture.Db.Users.FirstAsync(u => u.Id == user.Id);
        change(tracked);
        await fixture.Db.SaveChangesAsync();
    }

    // ── q ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Q_MatchesTheDisplayName_WhichThePlaceholderPromises()
    {
        var (org, list, client) = await ScaffoldAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var needle = $"Zaz{Guid.NewGuid():N}"[..12];
        await PatchAsync(user, u => u.DisplayName = $"{needle} Lefevre");

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}&q={needle}");

        Total(body).Should().Be(1);
        Emails(body).Should().ContainSingle().Which.Should().Be(user.Email);
    }

    [Fact]
    public async Task Q_MatchesTheAccountIdItself()
    {
        var (org, list, client) = await ScaffoldAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}&q={user.Id}");

        Total(body).Should().Be(1);
        Emails(body).Should().ContainSingle().Which.Should().Be(user.Email);
    }

    [Fact]
    public async Task Q_StillRefusesTwoCharacters()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync("/admin/users?q=ab");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("query_too_short");
    }

    // ── Tenant and list ───────────────────────────────────────────────────────

    [Fact]
    public async Task OrgId_KeepsOnlyThatTenantsAccounts()
    {
        var (org, list, client) = await ScaffoldAsync();
        await fixture.Seed.CreateUserAsync(list.Id);
        var (otherOrg, otherList) = await fixture.Seed.CreateOrgAsync();
        var stranger = await fixture.Seed.CreateUserAsync(otherList.Id);

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}");

        Emails(body).Should().NotContain(stranger.Email);
        Total(body).Should().Be(await fixture.Db.Users.CountAsync(u => u.UserList.OrgId == org.Id));
        otherOrg.Id.Should().NotBe(org.Id);
    }

    [Fact]
    public async Task UserListId_KeepsOnlyThatListsAccounts()
    {
        var (org, list, client) = await ScaffoldAsync();
        var inList = await fixture.Seed.CreateUserAsync(list.Id);
        var other  = await fixture.Seed.CreateUserListAsync(org.Id);
        var elsewhere = await fixture.Seed.CreateUserAsync(other.Id);

        var body = await BodyAsync(client, $"/admin/users?user_list_id={list.Id}");

        Total(body).Should().Be(1);
        Emails(body).Should().Contain(inList.Email).And.NotContain(elsewhere.Email);
    }

    // ── status ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_Disabled_KeepsOnlyTheDisabledOnes()
    {
        var (org, list, client) = await ScaffoldAsync();
        await fixture.Seed.CreateUserAsync(list.Id);
        var off = await fixture.Seed.CreateUserAsync(list.Id, active: false);

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}&status=disabled");

        Total(body).Should().Be(1);
        Emails(body).Should().ContainSingle().Which.Should().Be(off.Email);
    }

    [Fact]
    public async Task Status_Locked_ReadsTheLockAsATime_NotAFlag()
    {
        var (org, list, client) = await ScaffoldAsync();
        var locked = await fixture.Seed.CreateUserAsync(list.Id);
        var expired = await fixture.Seed.CreateUserAsync(list.Id);
        await PatchAsync(locked,  u => u.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(10));
        await PatchAsync(expired, u => u.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-10));

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}&status=locked");

        Total(body).Should().Be(1);
        Emails(body).Should().ContainSingle().Which.Should().Be(locked.Email);
    }

    /// <summary>
    /// "Active" means what an operator means by it. An account still serving a lockout is enabled
    /// in the column and unable to sign in in fact, and listing it under Active hides the one row
    /// somebody is looking for.
    /// </summary>
    [Fact]
    public async Task Status_Active_ExcludesAnAccountServingALockout()
    {
        var (org, list, client) = await ScaffoldAsync();
        var fine   = await fixture.Seed.CreateUserAsync(list.Id);
        var locked = await fixture.Seed.CreateUserAsync(list.Id);
        await fixture.Seed.CreateUserAsync(list.Id, active: false);
        await PatchAsync(locked, u => u.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(10));

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}&status=active");

        Emails(body).Should().Contain(fine.Email).And.NotContain(locked.Email);
    }

    // ── mfa ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public async Task Mfa_SplitsOnEitherFactor(string want)
    {
        var (org, list, client) = await ScaffoldAsync();
        var totp = await fixture.Seed.CreateUserAsync(list.Id);
        var key  = await fixture.Seed.CreateUserAsync(list.Id);
        var none = await fixture.Seed.CreateUserAsync(list.Id);
        await PatchAsync(totp, u => u.TotpEnabled = true);
        await PatchAsync(key,  u => u.WebAuthnEnabled = true);

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}&mfa={want}");

        if (want == "yes")
        {
            Total(body).Should().Be(2);
            Emails(body).Should().Contain([totp.Email, key.Email]).And.NotContain(none.Email);
        }
        else
        {
            Emails(body).Should().Contain(none.Email).And.NotContain(totp.Email).And.NotContain(key.Email);
        }
    }

    // ── signed_in ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignedIn_Never_FindsTheAccountsThatNeverDid()
    {
        var (org, list, client) = await ScaffoldAsync();
        var never = await fixture.Seed.CreateUserAsync(list.Id);
        var once  = await fixture.Seed.CreateUserAsync(list.Id);
        await PatchAsync(once, u => u.LastLoginAt = DateTimeOffset.UtcNow.AddDays(-2));

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}&signed_in=never");

        Emails(body).Should().Contain(never.Email).And.NotContain(once.Email);
    }

    [Fact]
    public async Task SignedIn_Windows_CutWhereTheyClaimTo()
    {
        var (org, list, client) = await ScaffoldAsync();
        var recent = await fixture.Seed.CreateUserAsync(list.Id);
        var older  = await fixture.Seed.CreateUserAsync(list.Id);
        await PatchAsync(recent, u => u.LastLoginAt = DateTimeOffset.UtcNow.AddDays(-2));
        await PatchAsync(older,  u => u.LastLoginAt = DateTimeOffset.UtcNow.AddDays(-20));

        var week  = await BodyAsync(client, $"/admin/users?org_id={org.Id}&signed_in=7d");
        var month = await BodyAsync(client, $"/admin/users?org_id={org.Id}&signed_in=30d");

        Emails(week).Should().Contain(recent.Email).And.NotContain(older.Email);
        Emails(month).Should().Contain([recent.Email, older.Email]);
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A filter the server does not know is refused, not dropped. Dropped, it would return the
    /// whole population under the heading of a narrowed one — the page would look right and the
    /// number would be wrong.
    /// </summary>
    [Theory]
    [InlineData("status=enabled")]
    [InlineData("mfa=totp")]
    [InlineData("signed_in=today")]
    public async Task UnknownFilterValue_IsRefused_NotIgnored(string pair)
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/admin/users?{pair}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_filter");
        body.GetProperty("filter").GetString().Should().Be(pair.Split('=')[0]);
    }

    // ── Combined, and paged ───────────────────────────────────────────────────

    [Fact]
    public async Task FiltersCombine_AndTheTotalIsTheirIntersection()
    {
        var (org, list, client) = await ScaffoldAsync();
        var wanted = await fixture.Seed.CreateUserAsync(list.Id, active: false);
        var otherList = await fixture.Seed.CreateUserListAsync(org.Id);
        var disabledElsewhere = await fixture.Seed.CreateUserAsync(otherList.Id, active: false);
        var enabledHere = await fixture.Seed.CreateUserAsync(list.Id);
        await PatchAsync(wanted, u => u.TotpEnabled = true);
        await PatchAsync(disabledElsewhere, u => u.TotpEnabled = true);
        await PatchAsync(enabledHere, u => u.TotpEnabled = true);

        var body = await BodyAsync(client,
            $"/admin/users?org_id={org.Id}&user_list_id={list.Id}&status=disabled&mfa=yes");

        Total(body).Should().Be(1);
        Emails(body).Should().ContainSingle().Which.Should().Be(wanted.Email);
    }

    /// <summary>
    /// The page is a window on the filtered set, never the other way round: <c>total</c> stays the
    /// same across pages, the pages do not overlap, and together they are the whole set.
    /// </summary>
    [Fact]
    public async Task PaginationUnderAFilter_WindowsTheFilteredSet()
    {
        var (org, list, client) = await ScaffoldAsync();
        for (var i = 0; i < 3; i++)
            await fixture.Seed.CreateUserAsync(list.Id, active: false);
        await fixture.Seed.CreateUserAsync(list.Id);

        var first  = await BodyAsync(client, $"/admin/users?org_id={org.Id}&status=disabled&page=1&pageSize=2");
        var second = await BodyAsync(client, $"/admin/users?org_id={org.Id}&status=disabled&page=2&pageSize=2");

        Total(first).Should().Be(3);
        Total(second).Should().Be(3, "the total counts the filtered set, not the page");
        Emails(first).Should().HaveCount(2);
        Emails(second).Should().HaveCount(1);
        Emails(first).Should().NotIntersectWith(Emails(second));
        second.GetProperty("page").GetInt32().Should().Be(2);
        second.GetProperty("page_size").GetInt32().Should().Be(2);
    }

    // ── The banner's counts ───────────────────────────────────────────────────

    [Fact]
    public async Task ListsAndTenants_CountTheFilteredSet_NotThePage()
    {
        var (org, list, client) = await ScaffoldAsync();
        var second = await fixture.Seed.CreateUserListAsync(org.Id);
        await fixture.Seed.CreateUserAsync(list.Id);
        await fixture.Seed.CreateUserAsync(list.Id);
        await fixture.Seed.CreateUserAsync(second.Id);

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}&pageSize=1");

        body.GetProperty("users").GetArrayLength().Should().Be(1);
        Total(body).Should().Be(4, "the organisation's own list holds the admin too");
        body.GetProperty("lists").GetInt32().Should().Be(3);
        body.GetProperty("tenants").GetInt32().Should().Be(1);
    }

    // ── The roles column ──────────────────────────────────────────────────────

    /// <summary>
    /// Read from the grant table, and named per project: the emitted claim is qualified
    /// <c>{projectId}/{name}</c>, and a project may flag several roles as default, so "the role"
    /// of an account is a list even inside one project.
    /// </summary>
    [Fact]
    public async Task Roles_NameTheProjectEachOneBelongsTo()
    {
        var (org, list, client) = await ScaffoldAsync();
        var user    = await fixture.Seed.CreateUserAsync(list.Id);
        var project = await fixture.Seed.CreateProjectAsync(org.Id);
        var role    = await fixture.Seed.CreateRoleAsync(project.Id, "editor");
        fixture.Db.UserProjectRoles.Add(new UserProjectRole
        {
            Id = Guid.NewGuid(), UserId = user.Id, ProjectId = project.Id,
            RoleId = role.Id, GrantedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var body = await BodyAsync(client, $"/admin/users?org_id={org.Id}&q={user.Email}");

        var granted = body.GetProperty("users").EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == user.Email)
            .GetProperty("roles").EnumerateArray().Single();
        granted.GetProperty("name").GetString().Should().Be("editor");
        granted.GetProperty("project_id").GetString().Should().Be(project.Id.ToString());
        granted.GetProperty("project_name").GetString().Should().Be(project.Name);
    }
}
