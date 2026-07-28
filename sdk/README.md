# RediensIAM SDKs

Clients for services that sit **behind** RediensIAM — a gateway, an API, a worker — and need to
answer two questions about an incoming bearer token:

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

## Getting a service-account token

```
Admin console → Service Accounts → New → Tokens → Generate
```

Or `POST /service-accounts/{id}/pat`. Grant the account only the roles the service actually
needs — a gateway that just validates tokens needs none.
