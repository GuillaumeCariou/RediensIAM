# RediensIAM SDKs

Two different jobs, two different kinds of SDK. Picking the wrong one is a security problem, so
start here.

| You are writing | Use | Path |
|---|---|---|
| A **frontend** — SPA, mobile web | `rediensiam-web` | [`typescript/rediensiam-web`](typescript/rediensiam-web) |
| A **backend** — gateway, API, worker | `RediensIAM.Client` (C#) · `rediensiam-client` (Rust) | [`dotnet/`](dotnet/RediensIAM.Client) · [`rust/`](rust/rediensiam-client) |

**The frontend logs the user in. The backend decides what they may do.** The browser SDK never
validates a token, because validating requires a service-account credential and anything shipped
to a browser is readable by anyone with devtools. Claims it exposes are for rendering only —
showing a menu, hiding a button. Every privileged decision is re-made server-side.

If you find yourself wanting to check a role in the browser to protect data, that check belongs
in your API.

Setting up the clients, projects and service accounts these SDKs need is
[`docs/INTEGRATION.md`](../docs/INTEGRATION.md). Start there if you are wiring an app up for the
first time — in particular, **every JSON body on this API is `snake_case`**, and a camelCase key
binds to nothing without erroring.

---

## Backend SDKs

Answer two questions about an incoming bearer token:

1. Is this token valid **right now**?
2. Is this subject allowed to do **X**?

| Language | Path | Package |
|---|---|---|
| C# / .NET 10 | [`dotnet/RediensIAM.Client`](dotnet/RediensIAM.Client) | `RediensIAM.Client` |
| Rust (2021) | [`rust/rediensiam-client`](rust/rediensiam-client) | `rediensiam-client` |

---

## Why not just verify the JWT locally?

Local JWKS verification proves a token was *issued*. It cannot see anything that happened since:

| Event | Local JWT check | These SDKs |
|---|---|---|
| Role revoked | still honoured until expiry | reflected within the cache window |
| Service account deactivated | still honoured | rejected |
| Organisation suspended | still honoured | rejected |
| Token revoked | still honoured | rejected |

Local verification is the right choice only when you can tolerate a stale decision for the full
token lifetime. For anything privileged, ask.

**Breaking change in `ext.roles`.** Tenant role names are now emitted qualified by the project
that defined them — `{project_id}/{name}` — and the management names (`super_admin`, `org_admin`,
`project_admin`) are reserved and cannot be used for a tenant role. A bare `"admin"` therefore
matches nothing, which is deliberate: two tenants' `admin` used to be the same string here.
Use `HasProjectRole(projectId, name)` (.NET), `has_project_role(project_id, name)` (Rust) or
`hasProjectRole(name, projectId?)` (browser); `HasRole` / `has_role` / `hasRole` now match
management roles only.
See [`docs/INTEGRATION.md`](../docs/INTEGRATION.md#introspection--the-backend-path).

---

## The API these wrap

Both SDKs speak two endpoints on the public port. Callers authenticate as a **service account**
— a personal access token (`rediens_pat_…`) is the simplest credential. A plain user token is
refused, deliberately: otherwise the endpoint becomes an oracle any bearer could use to probe
token validity.

### `POST /api/introspect` — RFC 7662

Form-encoded, per the RFC.

```
token=<token>&token_type_hint=access_token
```

`token_type_hint` is sent for RFC conformance; the server does not read it, and pins its own hint
when it asks Hydra. Only `token` matters.

```json
{
  "active": true,
  "sub": "sa:0b7c…",
  "user_id": "0b7c…",
  "org_id": "3f21…",
  "project_id": "8ac4…",
  "roles": ["org_admin"],
  "client_id": "client_admin_system",
  "is_service_account": false
}
```

An unusable token answers `{"active": false}` with everything else null — never an error status,
so a caller cannot distinguish "malformed" from "revoked" from "expired". Management roles are
re-verified against Keto before being reported, so a revoked role does not appear.

### `POST /api/authorize`

```json
{ "token": "…", "namespace": "Organisations", "object": "3f21…", "relation": "org_admin" }
```

```json
{ "allowed": true, "user_id": "0b7c…" }
```

Keeps the policy in RediensIAM instead of every gateway reimplementing its own reading of the
roles claim.

---

## C#

```bash
dotnet add package RediensIAM.Client
```

```csharp
builder.Services.AddRediensIam(o =>
{
    o.BaseUrl             = "https://auth.example.com";
    o.ServiceAccountToken = builder.Configuration["RediensIAM:Token"]!;
    o.CacheDuration       = TimeSpan.FromSeconds(30);
});
```

Use it directly:

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

Or plug it into ASP.NET Core authentication and use `[Authorize]` as usual:

```csharp
builder.Services.AddAuthentication(RediensIamDefaults.Scheme).AddRediensIam();
```

The handler maps `user_id` to `NameIdentifier`, each role to a role claim, and `org_id` /
`project_id` to claims of the same name. An IAM outage **fails the request** rather than letting
it through unauthenticated.

## Rust

```toml
[dependencies]
rediensiam-client = "0.1"
```

```rust
use rediensiam_client::{Config, RediensIamClient};

let iam = RediensIamClient::new(Config {
    base_url: "https://auth.example.com".into(),
    service_account_token: std::env::var("REDIENSIAM_TOKEN")?,
    ..Default::default()
})?;

let info = iam.introspect(token).await?;
if !info.active {
    return Err(unauthorized());
}
if !info.belongs_to_org(&org_id) {
    return Err(forbidden());   // tenant check — do not skip this
}
```

`RediensIamClient` is cheap to clone; share one instance across handlers.

---

## Caching

Both clients cache **positive** answers for 30s by default and never cache negative ones —
caching "inactive" would keep denying a token that has since become valid. That window is the
upper bound on how long a revoked token keeps working at your service, so shorten it if that
matters more than the round-trip. `Forget(token)` / `forget(token)` drops an entry immediately,
e.g. on logout.

Cache keys are digests, never the token itself: keys surface in dumps and diagnostics.

## Frontend (`rediensiam-web`)

Zero dependencies — Web Crypto and `fetch` cover the whole flow.

```bash
npm install rediensiam-web
```

```ts
import { createRediensIam } from 'rediensiam-web';

const iam = createRediensIam({
  issuer: 'https://auth.example.com',
  clientId: 'client_<your-project-id>',
  redirectUri: `${location.origin}/callback`,
});

// On page load: completes the login if we came back from the IdP.
if (!(await iam.handleRedirect()) && !iam.isAuthenticated) {
  await iam.login();   // redirects; does not return
}

// Call your API — the bearer token is attached and refreshed on 401.
const orders = await iam.fetch('/api/orders').then((r) => r.json());
```

Rendering decisions:

```ts
// Management roles of RediensIAM itself — bare names.
if (iam.hasRole('org_admin')) showAdminMenu();
// Tenant roles are emitted as `{project_id}/{name}`, so they need the project to match against.
// It defaults to the project the token was issued for.
if (iam.hasProjectRole('editor')) showEditorTools();
const { orgId, userId, projectId } = iam.claims;
```

`claims` is decoded from the token **without verifying the signature**, and roles may have been
revoked since it was issued. Use it to render, never to protect.

Logout ends the SSO session too:

```ts
await iam.logout();
```

### What it does for you

- Authorization code + **PKCE S256** (verified against the RFC 7636 test vector)
- `state` generated and checked — an attacker cannot feed you their code and log your user into
  their account
- Token held **in memory only**: not `localStorage`, not `sessionStorage`. Injected JavaScript
  cannot read it and it does not outlive the tab
- Refresh on expiry, single-flight so concurrent 401s cause one refresh, not ten
- `redirectUri` origin checked against the app origin at construction
- `issuer` must be `https:`, and every endpoint the discovery document names must sit on the
  issuer's own origin — otherwise whoever answers for the issuer chooses where the PKCE verifier
  and the refresh token are sent
- `fetch()` attaches the bearer only to the app's own origin, or to an origin you declared in
  `apiOrigins`. Anything else throws `untrusted_target` rather than leaking the token:

  ```ts
  const iam = createRediensIam({ …, apiOrigins: ['https://api.example.com'] });
  ```

**Local development:** `http://` is accepted on `localhost`, `127.0.0.1` and `[::1]` only — for
`issuer` and for `apiOrigins`, and in the C#/Rust SDKs for `BaseUrl`/`base_url`. That is the whole
opt-out; there is deliberately no flag to disable the check, because a flag gets set in production
too.

---

## Getting a service-account token

```
Admin console → Service Accounts → New → Tokens → Generate
```

Or `POST /service-accounts/{id}/pat`. Creating the account first needs
`{"name": …, "user_list_id": "<guid>"}` — `user_list_id` is required and a request without it is
rejected. Grant the account only the roles the service actually needs: a gateway that just
validates tokens needs **none**, since introspection asks only that the caller be a service
account. Full walkthrough in [`docs/INTEGRATION.md`](../docs/INTEGRATION.md).
