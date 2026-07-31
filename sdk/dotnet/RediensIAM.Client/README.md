# RediensIAM.Client

Resource-server client for [RediensIAM](../../../README.md): live token introspection (RFC 7662),
permission checks, and an ASP.NET Core authentication handler.

This is a **backend** SDK. It needs a service-account credential, so it must never run in a
browser — see [`sdk/README.md`](../../README.md) for which SDK belongs where.

- Target framework: `net10.0`
- Package id: `RediensIAM.Client`

---

## Install

```bash
dotnet add package RediensIAM.Client
```

The package references `Microsoft.AspNetCore.App` as a framework reference, so it expects to be
consumed from an ASP.NET Core app. The `RediensIamClient` type itself has no ASP.NET Core
dependency beyond that; only `AddRediensIam()` and the authentication handler do.

---

## Required options

Three options are required and all three are checked **at construction**, not on the first
request. A misconfigured service fails to start with a message naming the fix, rather than
returning 400s once traffic arrives.

| Option | Required | Notes |
|---|---|---|
| `BaseUrl` | yes | Must be `https`. `http` is accepted **only on a loopback host** (`Uri.IsLoopback`: `localhost`, all of `127.0.0.0/8`, `::1`). There is no flag to disable the check — a flag gets set in production too. |
| `ServiceAccountToken` | yes | A service-account PAT (`rediens_pat_…`) is the simplest credential. A user token is refused by the server with `403 service_account_required`. |
| `Audience` | **yes, new in 0.2.0** | The tenant *this resource server serves*: the project id it fronts, or the organisation id if it fronts a whole organisation. **No default, and there will not be one.** |
| `CacheDuration` | no | Default 30 s. `TimeSpan.Zero` disables caching. |
| `Timeout` | no | Default 5 s. |

```csharp
builder.Services.AddRediensIam(o =>
{
    o.BaseUrl             = "https://auth.example.com";
    o.ServiceAccountToken = builder.Configuration["RediensIAM:Token"]!;
    o.Audience            = builder.Configuration["RediensIAM:ProjectId"]!;
    o.CacheDuration       = TimeSpan.FromSeconds(30);
});
```

`AddRediensIam` calls `RediensIamOptions.Validated()` during registration, so the failure lands at
startup with the registration in the stack trace. Constructing `RediensIamClient` directly runs the
same checks in its constructor.

Every failure throws `ArgumentException`:

| Message names | Cause |
|---|---|
| `BaseUrl is required` / `is not an absolute URL` | unset or relative |
| `BaseUrl must be https — http is accepted only on localhost` | cleartext to a non-loopback host |
| `ServiceAccountToken is required` | unset |
| `Audience is required: name the project id this resource server serves…` | unset — see below |

### Why `Audience` has no default

A default would be a guess about which tenant this service belongs to, and a wrong guess is
finding P-06 exactly. Before 0.2.0, scoping came from *the caller's* organisation — and a
multi-tenant gateway must hold a deployment-level (`__system__`) service account, which has no
organisation, so it stayed deliberately unscoped. One gateway credential therefore resolved
**every** tenant's token in the deployment as `active: true`, and each resource server was
expected to compare `project_id` against its own configuration afterwards. Nothing enforced that,
and no SDK had a field for it.

`Audience` is that field. The server sends `400 {"error":"audience_required","ver":1}` to any
caller that omits it.

---

## The `ver` contract check

`RediensIamClient.RequiredContractVersion` is `1`. Every answer this client accepts must carry
`ver >= 1`; anything else throws `InvalidOperationException` before the answer reaches your code.

This is the load-bearing half of the audience change. A RediensIAM older than contract version 1
does **not** reject the `aud` you send — it silently discards the unknown form field and answers
exactly as it always did. An SDK that only *sent* `aud` could therefore not tell an enforcing
server from an ignoring one, and would report success while being bound to nothing. `ver` is
present on every 200 from an upgraded server, including `{"active": false}`, so requiring it turns
that silent failure into a loud one.

**Consequence: deploy order is load-bearing.** An upgraded SDK against an un-upgraded server
refuses *every* answer, by design:

```
RediensIAM answered with ver=0, expected at least 1: this server predates mandatory
audience binding and silently ignored the aud this client sent. Upgrade RediensIAM
before trusting its answers.
```

Upgrade the server first, or accept a window in which this service fails closed. The old server
ignores the `aud` a new SDK sends, so there is no window of 400s in the other direction.

---

## Introspection

```csharp
public class OrdersController(RediensIamClient iam) : ControllerBase
{
    [HttpGet("{orgId}/orders")]
    public async Task<IActionResult> List(string orgId)
    {
        var token = Request.Headers.Authorization.ToString()["Bearer ".Length..];
        var info  = await iam.IntrospectAsync(token);

        if (!info.Active) return Unauthorized();
        if (info.OrgId != orgId) return Forbid();   // tenant check — do not skip this

        return Ok(/* … */);
    }
}
```

`IntrospectAsync` returns `TokenInfo.Inactive` for an unusable token rather than throwing.
Transport and server faults **do** throw: treating an IAM outage as "token invalid" would
silently degrade to denying everyone, and that is a decision for the caller, not the SDK.

`TokenInfo` carries `Active`, `Subject`, `UserId`, `OrgId`, `ProjectId`, `Roles`, `ClientId`,
`IsServiceAccount`, `Audience` (the echo of what you sent) and `Ver`.

## Authorisation

```csharp
var allowed = await iam.AuthorizeAsync(token, "Organisations", orgId, "org_admin");
```

Keeps the policy in RediensIAM rather than having every gateway reimplement its own reading of the
roles claim. Returns `false` for a denial, for a token bound to another tenant, and for an object
outside the caller's scope — deliberately the same shape, so the endpoint cannot be used to probe
which objects exist. The `System` namespace is refused to every caller; read `Roles` from
introspection instead.

---

## Role helpers

Tenant role names are chosen by tenant admins. Since 0.2.0 RediensIAM emits them qualified by the
project that defined them, and reserves the management names.

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

The same applies to `[Authorize(Roles = …)]` through the authentication handler below — the
qualified string is what lands in `ClaimTypes.Role`, so `[Authorize(Roles = "admin")]` stops
matching every tenant at once. Use `[Authorize(Roles = "org_admin")]` for management roles, or
build the qualified string for tenant roles.

---

## ASP.NET Core authentication handler

```csharp
builder.Services.AddRediensIam(o => { /* as above */ });
builder.Services.AddAuthentication(RediensIamDefaults.Scheme).AddRediensIam();
```

Then use `[Authorize]` as usual. The handler:

- reads the `Bearer` token and introspects it on **every** request (subject to the cache below);
- maps `user_id` → `ClaimTypes.NameIdentifier`, each role → `ClaimTypes.Role`, and `org_id` /
  `project_id` → `RediensIamDefaults.OrgIdClaim` / `ProjectIdClaim` (literally `"org_id"` and
  `"project_id"`);
- **fails the request** on an IAM outage rather than letting it through unauthenticated —
  `AuthenticateResult.Fail(ex)`, not `NoResult()`.

It is deliberately not a JWT-signature handler. A valid signature proves the token was issued; it
cannot see a role revoked, a service account disabled, or an organisation suspended since.

---

## Caching

Positive answers are cached in `IMemoryCache` for `CacheDuration` (30 s default). Negative answers
are **never** cached — caching "inactive" would keep denying a token that has since become valid,
and buys nothing.

The cache window is the upper bound on how long a revoked token keeps working at this service.
Shorten it if that matters more than the round-trip; `TimeSpan.Zero` disables it entirely.

```csharp
iam.Forget(token);   // drop one entry immediately, e.g. on logout
```

Cache keys are SHA-256 digests of the token, never the token itself: keys surface in dumps and
diagnostics.

`AuthorizeAsync` is **not** cached — a permission decision depends on the namespace, object and
relation as well as the token.

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

`../RediensIAM.Client.Tests` covers the audience binding, the construction-time checks and the
`ver` refusal. Run from the repository root:

```bash
dotnet test sdk/dotnet/RediensIAM.Client.Tests/RediensIAM.Client.Tests.csproj \
  -p:SonarQubeTargetsImported=true
```

The flag is needed because a partially-completed `sonar-scan.sh` leaves a `.sonarqube/` directory
at the repository root that MSBuild imports — see [`docs/TESTING.md`](../../../docs/TESTING.md).
