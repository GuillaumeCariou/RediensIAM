# RediensIAM.Client

Resource-server client for [RediensIAM](../../../README.md): live token introspection (RFC 7662),
permission checks, and an ASP.NET Core authentication handler.

- Package id `RediensIAM.Client`, version 0.5.0
- Target framework `net10.0`, nullable enabled

**Who calls this.** A server-side .NET service: an API, a gateway, a worker — anything holding a
service-account credential and validating tokens presented to it. It must never run in a browser;
the credential it needs belongs to anyone who can read it. Which SDK belongs where is
[`sdk/README.md`](../../README.md); creating the service account and PAT this SDK needs is
[`docs/INTEGRATION.md`](../../../docs/INTEGRATION.md).

---

## Install

```bash
dotnet add package RediensIAM.Client
```

The package carries a `FrameworkReference` to `Microsoft.AspNetCore.App`, which supplies
`IMemoryCache` and `IHttpClientFactory` alongside the authentication types, so it expects to be
consumed from an ASP.NET Core app. `RediensIamClient` itself needs nothing from ASP.NET Core beyond
that; only `AddRediensIam()` and the authentication handler do.

### Minimum working example

```csharp
using RediensIAM.Client;

var builder = WebApplication.CreateBuilder(args);

// The tenant this service serves — the same value goes in Audience and in every role check.
var projectId = builder.Configuration["RediensIAM:ProjectId"]!;

builder.Services.AddRediensIam(o =>
{
    o.BaseUrl             = "https://auth.example.com";
    o.ServiceAccountToken = builder.Configuration["RediensIAM:Token"]!;
    o.Audience            = projectId;   // required, no default
});

var app = builder.Build();

app.MapGet("/orders/{orgId}", async (string orgId, HttpRequest req, RediensIamClient iam) =>
{
    var header = req.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();

    var info = await iam.IntrospectAsync(header["Bearer ".Length..].Trim());

    if (!info.Active) return Results.Unauthorized();
    if (info.OrgId != orgId) return Results.StatusCode(403);              // tenant check — do not skip this
    if (!info.HasProjectRole(projectId, "reader")) return Results.StatusCode(403);

    return Results.Ok(/* … */);
});

app.Run();
```

Or plug it into ASP.NET Core authentication and use `[Authorize]` as usual:

```csharp
builder.Services.AddRediensIam(o => { /* as above */ });
builder.Services.AddAuthentication(RediensIamDefaults.Scheme).AddRediensIam();
```

`AddRediensIam(IServiceCollection, …)` is a prerequisite for the authentication overload: the
handler resolves `RediensIamClient` from the container.

---

## Configuration — `RediensIamOptions`

| Property | Type | Required | Default | Meaning |
|---|---|---|---|---|
| `BaseUrl` | `string` | yes | `""` | Base URL of the RediensIAM public API. Must be `https`, except on a loopback host. |
| `ServiceAccountToken` | `string` | yes | `""` | The credential this service presents. A service-account PAT (`rediens_pat_…`) is the simplest option; a plain user token is refused by the server. |
| `Audience` | `string` | yes | `""` | The tenant *this resource server serves*: the project id it fronts, or the organisation id if it fronts a whole organisation. Sent as `aud` on every call. |
| `CacheDuration` | `TimeSpan` | no | 30 s | How long a positive introspection is reused. `TimeSpan.Zero` disables caching. |
| `Timeout` | `TimeSpan` | no | 5 s | Applied to the `HttpClient`. |

`RediensIamOptions.Validated()` is public: it throws `ArgumentException` unless the options can
produce a working client, and returns the same instance. `AddRediensIam` calls it during
registration so the failure lands at startup with the registration in the stack trace, and the
`RediensIamClient` constructor calls it again so direct construction runs the same checks.

| Message names | Cause |
|---|---|
| `BaseUrl is required` | unset or whitespace |
| `BaseUrl is not an absolute URL` | relative |
| `BaseUrl must be https — http is accepted only on localhost` | cleartext to a non-loopback host |
| `ServiceAccountToken is required` | unset or whitespace |
| `Audience is required: name the project id this resource server serves…` | unset or whitespace |

Loopback is `Uri.IsLoopback`: `localhost`, all of `127.0.0.0/8`, and `::1`. That is the whole
opt-out; there is deliberately no flag to disable the scheme check, because a flag gets set in
production too. The rule differs slightly per SDK — the browser SDK additionally accepts any
`*.localhost` host, and the Rust client accepts `localhost` and `127.0.0.1` only.

### Why `Audience` has no default

A default would be a guess about which tenant this service belongs to, and a wrong guess is finding
P-06 exactly. Scoping used to come from *the caller's* organisation — and a multi-tenant gateway
must hold a deployment-level (`__system__`) service account, which has no organisation, so it
stayed deliberately unscoped. One gateway credential therefore resolved **every** tenant's token in
the deployment as `active: true`, and each resource server was expected to compare `project_id`
against its own configuration afterwards. Nothing enforced that, and no SDK had a field for it.

`Audience` is that field. The server answers `400 {"error":"audience_required","ver":1}` to any
caller that omits it. Full migration notes:
[`sdk/README.md`](../../README.md#aud-is-now-a-required-sdk-option).

---

## API

### `class RediensIamClient` (sealed)

```csharp
public RediensIamClient(
    HttpClient http,
    RediensIamOptions options,
    IMemoryCache cache,
    ILogger<RediensIamClient>? logger = null)
```

Throws `ArgumentException` when `options` is unusable. Sets `http.Timeout` from
`options.Timeout` if it differs, swallowing the `InvalidOperationException` that .NET raises once a
request has been sent on that `HttpClient` — at that point the caller owns it.

Constructing directly means **you** must set `http.BaseAddress`, with a trailing slash: request
URIs are relative (`api/introspect`, `api/authorize`) and are resolved against it.
`AddRediensIam` does this for you.

| Member | Signature | Returns / throws |
|---|---|---|
| `RequiredContractVersion` | `const int` = `1` | The `ver` every answer must carry. |
| `IntrospectAsync` | `Task<TokenInfo> IntrospectAsync(string token, CancellationToken ct = default)` | `TokenInfo`. A null, empty or whitespace token short-circuits to `TokenInfo.Inactive` with no round trip. |
| `AuthorizeAsync` | `Task<bool> AuthorizeAsync(string token, string @namespace, string @object, string relation, CancellationToken ct = default)` | `true` when RediensIAM allows it. Not cached. |
| `Forget` | `void Forget(string token)` | Drops any cached decision for that token. Returns nothing; a miss is not an error. |

Both calls throw rather than answering:

| Exception | When |
|---|---|
| `HttpRequestException` | Non-2xx from RediensIAM (`EnsureSuccessStatusCode`), including `400 audience_required` and `403 service_account_required`. |
| `InvalidOperationException` | An empty body — a broken server, not an inactive token — or an answer whose `ver` is below `RequiredContractVersion`. |
| `TaskCanceledException` | `Timeout` elapsed, or `ct` was cancelled. |
| `JsonException` | A body that is not the documented shape. |

An *unusable token* is not one of those: it comes back as `Active == false`, because treating an
outage as "token invalid" would silently degrade to denying everyone, and whether that is
acceptable is the caller's decision, not the SDK's.

### `record TokenInfo` (sealed)

| Property | Type | Wire name | Notes |
|---|---|---|---|
| `Active` | `bool` | `active` | False means unusable *right now* — expired, revoked, deactivated service account, suspended organisation, or bound to another tenant. The reason is deliberately not disclosed. |
| `Subject` | `string?` | `sub` | |
| `UserId` | `string?` | `user_id` | |
| `OrgId` | `string?` | `org_id` | |
| `ProjectId` | `string?` | `project_id` | |
| `Roles` | `IReadOnlyList<string>` | `roles` | Never null. An explicit `"roles": null` from the server is normalised to an empty list on both get and init. |
| `ClientId` | `string?` | `client_id` | |
| `IsServiceAccount` | `bool` | `is_service_account` | |
| `Audience` | `string?` | `aud` | Echo of the `aud` this client sent, on an active answer. |
| `Ver` | `int` | `ver` | Contract version; `0` means the field was absent, and such an answer never reaches you. |

| Member | Signature | Meaning |
|---|---|---|
| `TokenInfo.Inactive` | `static readonly TokenInfo` | `Active = false`, empty roles. |
| `HasRole` | `bool HasRole(string role)` | Ordinal match against the whole list. |
| `HasProjectRole` | `bool HasProjectRole(string projectId, string role)` | Ordinal match against `{projectId}/{role}`. |

### `static class RediensIamServiceCollectionExtensions`

| Method | Effect |
|---|---|
| `AddRediensIam(this IServiceCollection, Action<RediensIamOptions>)` | Validates the options, registers them as a singleton, calls `AddMemoryCache()`, and registers `RediensIamClient` as a typed `HttpClient` with `BaseAddress` and `Timeout` set. Throws `ArgumentException` at registration if the options are unusable. |
| `AddRediensIam(this AuthenticationBuilder)` | Adds the `RediensIamDefaults.Scheme` scheme backed by `RediensIamAuthenticationHandler`. |

### `static class RediensIamDefaults`

| Constant | Value |
|---|---|
| `Scheme` | `"RediensIAM"` |
| `OrgIdClaim` | `"org_id"` |
| `ProjectIdClaim` | `"project_id"` |

### `sealed class RediensIamAuthenticationHandler`

An `AuthenticationHandler<AuthenticationSchemeOptions>`. Registered by the builder extension above;
you do not construct it. Per request it:

- returns `NoResult()` when there is no `Bearer` header, leaving the request unauthenticated for
  the rest of the pipeline to reject;
- introspects the token on **every** request, subject to the cache;
- returns `Fail(ex)` if introspection threw, and `Fail("Token is not active.")` if the token is not
  active;
- otherwise builds a `ClaimsPrincipal`: `user_id` → `ClaimTypes.NameIdentifier`, each role →
  `ClaimTypes.Role`, `org_id` → `RediensIamDefaults.OrgIdClaim`, `project_id` →
  `RediensIamDefaults.ProjectIdClaim`. Empty values are omitted rather than added as empty claims.

It is deliberately not a JWT-signature handler. A valid signature proves the token was issued; it
cannot see a role revoked, a service account disabled, or an organisation suspended since.

---

## Role helpers

Tenant role names are chosen by tenant admins, so `admin` on its own means nothing across tenants.
RediensIAM emits tenant roles as `{project_id}/{name}` and reserves the management names.

```csharp
info.HasRole("org_admin");                    // management roles only — bare name
info.HasProjectRole(projectId, "editor");     // tenant roles — matches "{projectId}/editor"
```

| Method | Matches | Does not match |
|---|---|---|
| `HasRole(name)` | `super_admin`, `org_admin`, `project_admin` — the only bare names RediensIAM emits | any tenant role, whatever its name |
| `HasProjectRole(projectId, name)` | the exact string `{projectId}/{name}` | the same role name in another project |

`HasRole("admin")` returns `false` even when the user holds a tenant role called `admin`. That is
the intended direction: two tenants' `admin` used to be byte-identical in every consumer's
`ClaimsPrincipal`, and a bare match now **fails closed**.

The same applies through the authentication handler — the qualified string is what lands in
`ClaimTypes.Role`, so `[Authorize(Roles = "admin")]` stops matching every tenant at once. Use
`[Authorize(Roles = "org_admin")]` for management roles, or build the qualified string for tenant
roles.

Note the argument order: project first here and in the Rust client, role first in the browser SDK.

---

## Authorisation

```csharp
var allowed = await iam.AuthorizeAsync(token, "Organisations", orgId, "org_admin");
```

Keeps the policy in RediensIAM rather than having every gateway reimplement its own reading of the
roles claim. It returns `false` for a denial, for a token bound to another tenant, and for an
object outside the caller's scope — deliberately the same shape, so the endpoint cannot be used to
probe which objects exist. Only `Organisations`, `Projects` and `UserLists` have ownership
RediensIAM can check; **any other namespace answers `false`**, because failing open on a namespace
it writes no objects into is the same finding under a new name. Read `Roles` from introspection
instead.

---

## What this SDK enforces, and why

Pinned by `../RediensIAM.Client.Tests/AudienceBindingTests.cs`.

**`BaseUrl` must be `https`, except on loopback.** The service-account credential and every token
being introspected ride on that URL, so cleartext hands an on-path attacker both.

**The audience is required, and it is checked at construction.** A resource server with no declared
tenant is a deployment mistake; it should stop the process at startup with a message naming the
fix, not turn into a 400 on every request once traffic arrives.

**Every answer must carry `ver >= 1`.** This is the load-bearing half of the audience change. A
RediensIAM older than contract version 1 does **not** reject the `aud` you send — it silently
discards the unknown form field and answers exactly as it always did. An SDK that only *sent* `aud`
could therefore not tell an enforcing server from an ignoring one, and would report success while
being bound to nothing. `ver` is present on every answer from an upgraded server, including
`{"active": false}` and the 400, so requiring it turns that silent failure into a loud one.

Consequence: **deploy order is load-bearing.** An upgraded SDK against an un-upgraded server refuses
every answer, by design:

```
RediensIAM answered with ver=0, expected at least 1: this server predates mandatory
audience binding and silently ignored the aud this client sent. Upgrade RediensIAM
before trusting its answers.
```

Upgrade the server first, or accept a window in which this service fails closed. There is no window
of 400s in the other direction — the old server ignores the `aud` a new SDK sends.

**Cache keys are a SHA-256 digest of `BaseUrl`, `Audience` and the token — never the token
itself.** Keys surface in dumps and diagnostics, and the audience is part of the *question*: one
token introspected for two audiences has two different answers. `IMemoryCache` is resolved from the
host, so a multi-tenant gateway shares one instance across its per-tenant clients; keyed on the
token alone, tenant A's `active: true` was served to tenant B, roles and all, without a round trip.

**Negative answers are never cached.** Caching "inactive" would keep denying a token that has since
become valid, and buys nothing.

**An IAM outage fails the request.** The authentication handler returns `AuthenticateResult.Fail`,
not `NoResult()`, so an outage cannot become an authorisation bypass.

**`Timeout` is applied in the constructor, not only by the DI extension.** A hand-built client used
to fall back to `HttpClient`'s 100-second default, so a hung IAM stalled every authenticated request
for that long.

**The token is not logged.** The `Debug` line records `active` and `user_id` only, and it is guarded
by `IsEnabled` so the arguments are not boxed when `Debug` is off — introspection runs per request.

---

## Caching

Positive answers are cached in `IMemoryCache` for `CacheDuration` (30 s default). That window is the
upper bound on how long a revoked token keeps working at this service, so shorten it if that matters
more than the round trip; `TimeSpan.Zero` disables it entirely.

```csharp
iam.Forget(token);   // drop one entry immediately, e.g. on logout
```

`AuthorizeAsync` is **not** cached — a permission decision depends on the namespace, object and
relation as well as the token.

---

## What this SDK does not do

Listed so you do not go looking.

- **No login flow.** No authorization-code exchange, no PKCE, no redirect handling, no token
  issuance or refresh. That is the browser's job — `rediensiam-web`.
- **No local JWT validation.** No JWKS fetch, no signature check, no `exp` arithmetic. That is the
  point: a signature proves a token was issued, not that it is still honoured.
- **No tenant comparison on your behalf.** `IntrospectAsync` answering `Active == true` means the
  token is usable for the audience you declared. Comparing `OrgId` or `ProjectId` against the
  resource being served, and checking roles with `HasProjectRole`, is still yours to write.
- **No retries, no backoff, no circuit breaker.** One request per call. Compose
  `IHttpClientFactory` policies on the typed client if you want them.
- **No distributed cache.** `IMemoryCache` is per process; ten replicas hold ten caches, and a
  `Forget` on one does not reach the others.
- **No token revocation, no session management, no logout call.** `Forget` clears a local cache
  entry, nothing more.
- **No management-API surface.** Creating organisations, projects, users, service accounts or PATs
  is plain HTTP against the routes in [`docs/API.md`](../../../docs/API.md); this package wraps
  `/api/introspect` and `/api/authorize` only.
- **No authorisation policies or requirement types.** The handler produces claims;
  `[Authorize(Roles = …)]` and your own policies do the rest.

---

## Getting a service-account token

```
Admin console → Service Accounts → New → Tokens → Generate
```

Or `POST /service-accounts/{id}/pat`. Creating the account first needs
`{"name": …, "user_list_id": "<guid>"}`; `user_list_id` is required. Grant the account only the
roles the service actually needs — a gateway that just validates tokens needs **none**, since
introspection asks only that the caller be a service account. A multi-tenant gateway needs a
`__system__` (deployment-level) service account; an org-scoped one gets `active: false` for other
organisations' tokens.

Full walkthrough: [`docs/INTEGRATION.md`](../../../docs/INTEGRATION.md).

---

## Tests

`../RediensIAM.Client.Tests` covers the audience reaching the wire, the construction-time checks,
the `ver` refusal, the null-roles answer, per-audience cache isolation and the timeout. Run from the
repository root:

```bash
dotnet test sdk/dotnet/RediensIAM.Client.Tests/RediensIAM.Client.Tests.csproj \
  -p:SonarQubeTargetsImported=true
```

The flag is needed because a partially-completed `sonar-scan.sh` leaves a `.sonarqube/` directory at
the repository root that MSBuild imports — see [`docs/TESTING.md`](../../../docs/TESTING.md).
