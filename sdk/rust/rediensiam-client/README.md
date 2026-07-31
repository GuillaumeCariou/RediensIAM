# rediensiam-client

Resource-server client for [RediensIAM](../../../README.md): live token introspection (RFC 7662)
and permission checks.

This is a **backend** SDK. It needs a service-account credential, so it must never run anywhere a
user can read it — see [`sdk/README.md`](../../README.md) for which SDK belongs where.

- Edition 2021, async, `tokio` + `reqwest`
- TLS: `rustls-tls` with compiled-in webpki roots

---

## Install

```toml
[dependencies]
rediensiam-client = "0.2"
tokio = { version = "1", features = ["rt-multi-thread", "macros"] }
```

> **The OS trust store is not consulted.** `reqwest` is built with `rustls-tls` and webpki roots
> only, so a deployment whose RediensIAM presents a certificate from a private CA will fail to
> validate. This is a known open item (ledger §9 item 18). Until it is addressed, terminate with a
> publicly-rooted certificate or vendor your own `reqwest::Client` upstream of this crate.

---

## Required options

Three fields of `Config` are required and all three are checked **in `new()`**, not on the first
call. A misconfigured service fails to start with a message naming the fix, rather than returning
400s once traffic arrives. Every failure is `Error::Config`.

| Field | Required | Notes |
|---|---|---|
| `base_url` | yes | Must be `https`. `http` is accepted **only** when the host is `localhost`, `127.0.0.1` or `::1`. There is no flag to disable the check — a flag gets set in production too. |
| `service_account_token` | yes | A service-account PAT (`rediens_pat_…`) is the simplest credential. A user token is refused by the server with `403 service_account_required`. |
| `audience` | **yes, new in 0.2.0** | The tenant *this resource server serves*: the project id it fronts, or the organisation id if it fronts a whole organisation. **No default, and there will not be one.** |
| `cache_duration` | no | Default 30 s. `Duration::ZERO` disables caching. |
| `timeout` | no | Default 5 s. |

```rust
use rediensiam_client::{Config, RediensIamClient};

let iam = RediensIamClient::new(Config {
    base_url: "https://auth.example.com".into(),
    service_account_token: std::env::var("REDIENSIAM_TOKEN")?,
    audience: std::env::var("REDIENSIAM_PROJECT_ID")?,
    ..Default::default()
})?;
```

Omit any of the three and `new` returns `Err(Error::Config(..))` — the client never exists.
`RediensIamClient` is cheap to clone (the inner state is `Arc`-shared); build one and share it
across handlers.

The type deliberately does **not** implement `Debug`: a derived `Debug` would print the
service-account token.

### Why `audience` has no default

A default would be a guess about which tenant this service belongs to, and a wrong guess is
finding P-06 exactly. Before 0.2.0, scoping came from *the caller's* organisation — and a
multi-tenant gateway must hold a deployment-level (`__system__`) service account, which has no
organisation, so it stayed deliberately unscoped. One gateway credential therefore resolved
**every** tenant's token in the deployment as `active: true`, and each resource server was
expected to compare `project_id` against its own configuration afterwards. Nothing enforced that,
and no SDK had a field for it.

`audience` is that field. The server sends `400 {"error":"audience_required","ver":1}` to any
caller that omits it.

---

## The `ver` contract check

`CONTRACT_VERSION` is `1`. Every answer this client accepts must carry `ver >= 1`; anything else
returns `Err(Error::ServerTooOld { found })` before the answer reaches your code.

This is the load-bearing half of the audience change. A RediensIAM older than contract version 1
does **not** reject the `aud` you send — it silently discards the unknown form field and answers
exactly as it always did. A client that only *sent* `aud` could therefore not tell an enforcing
server from an ignoring one, and would report success while being bound to nothing. `ver` is
present on every 200 from an upgraded server, including `{"active": false}`, so requiring it turns
that silent failure into a loud one.

**Consequence: deploy order is load-bearing.** An upgraded client against an un-upgraded server
returns `ServerTooOld` for *every* call, by design. Upgrade the server first, or accept a window
in which this service fails closed. The old server ignores the `aud` a new client sends, so there
is no window of 400s in the other direction.

---

## Introspection

```rust
let info = iam.introspect(token).await?;

if !info.active {
    return Err(unauthorized());
}
if !info.belongs_to_org(&org_id) {
    return Err(forbidden());   // tenant check — do not skip this
}
```

An unusable token yields `Ok(TokenInfo { active: false, .. })` — never an error, so a caller
cannot distinguish "malformed" from "revoked" from "expired". Transport and server faults return
`Err`: treating an IAM outage as "token invalid" would silently degrade to denying everyone, and
that is a decision for the caller, not the SDK. An empty token short-circuits to
`TokenInfo::inactive()` without a round-trip.

`TokenInfo` carries `active`, `sub`, `user_id`, `org_id`, `project_id`, `roles`, `client_id`,
`is_service_account`, `aud` (the echo of what you sent) and `ver`.

## Authorisation

```rust
let allowed = iam.authorize(token, "Organisations", &org_id, "org_admin").await?;
```

Keeps the policy in RediensIAM rather than having every gateway reimplement its own reading of the
roles claim. Returns `false` for a denial, for a token bound to another tenant, and for an object
outside the caller's scope — deliberately the same shape, so the endpoint cannot be used to probe
which objects exist. The `System` namespace is refused to every caller; read `roles` from
introspection instead.

---

## Role helpers

Tenant role names are chosen by tenant admins. Since 0.2.0 RediensIAM emits them qualified by the
project that defined them, and reserves the management names.

```rust
info.has_role("org_admin");                       // management roles only — bare name
info.has_project_role(&project_id, "editor");     // tenant roles — matches "{project_id}/editor"
info.belongs_to_org(&org_id);                     // the tenant check
```

| Method | Matches | Does not match |
|---|---|---|
| `has_role(name)` | `super_admin`, `org_admin`, `project_admin` — the only bare names RediensIAM emits | any tenant role, whatever its name |
| `has_project_role(project_id, name)` | the exact string `{project_id}/{name}` | the same role name in another project |

`has_role("admin")` returns `false` even when the user holds a tenant role called `admin`. That is
the intended direction: two tenants' `admin` used to be the same string at every consumer, and a
bare match now **fails closed**. `tenant_roles_do_not_match_across_projects` in `src/lib.rs` pins
this.

---

## Caching

Positive answers are cached in-process for `cache_duration` (30 s default). Negative answers are
**never** cached — caching "inactive" would keep denying a token that has since become valid, and
buys nothing.

The cache window is the upper bound on how long a revoked token keeps working at this service.
Shorten it if that matters more than the round-trip; `Duration::ZERO` disables it entirely.

```rust
iam.forget(token).await;   // drop one entry immediately, e.g. on logout
```

Cache keys are SHA-256 digests of the token, never the token itself: keys surface in dumps and
diagnostics, and the map returns a full `TokenInfo` — roles included — before any server call, so
a collidable key is an authentication bypass. A previous non-cryptographic 64-bit hash was
replaced for that reason (R-28); `cache_key_is_a_sha256_digest` pins the algorithm to a
known-answer vector.

The cache is one `RwLock<HashMap<..>>` with an opportunistic sweep on insert — no background task.
Entries are tiny and expire within seconds; shard it only if contention shows up in a profile.

`authorize` is **not** cached: a permission decision depends on the namespace, object and relation
as well as the token.

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

```bash
cargo test --manifest-path sdk/rust/rediensiam-client/Cargo.toml
```

The `aud` field being absent is invisible to an old server by design, so the wire tests run a
one-shot loopback listener and assert on the bytes the client actually wrote, rather than on a
mock it was handed.
