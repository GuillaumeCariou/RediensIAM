# rediensiam-client

Resource-server client for [RediensIAM](../../../README.md): live token introspection (RFC 7662)
and permission checks.

- Version 0.5.0, edition 2021, no optional features
- `tokio` + `reqwest`, async throughout

**Who calls this.** A Rust service holding a service-account credential and validating tokens
presented to it: an API, a gateway, a worker. It must never run anywhere a user can read the
credential. Which SDK belongs where is [`sdk/README.md`](../../README.md); creating the service
account and PAT this client needs is [`docs/INTEGRATION.md`](../../../docs/INTEGRATION.md).

---

## Install

```toml
[dependencies]
rediensiam-client = "0.5"
tokio = { version = "1", features = ["rt-multi-thread", "macros"] }
```

### Minimum working example

```rust
use rediensiam_client::{Config, RediensIamClient};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let project_id = std::env::var("REDIENSIAM_PROJECT_ID")?;

    let iam = RediensIamClient::new(Config {
        base_url: "https://auth.example.com".into(),
        service_account_token: std::env::var("REDIENSIAM_TOKEN")?,
        // The tenant this service serves. Required, no default.
        audience: project_id.clone(),
        ..Default::default()
    })?;

    let info = iam.introspect("rediens_pat_...").await?;

    if !info.active {
        return Ok(()); // unauthenticated
    }
    if !info.belongs_to_org("org-1") {
        return Ok(()); // another tenant — do not skip this check
    }
    if info.has_project_role(&project_id, "reader") {
        // serve the request
    }

    Ok(())
}
```

`RediensIamClient` is cheap to clone — the HTTP client, the config and the cache are all
`Arc`-shared — so build one and share it across handlers. It deliberately does **not** implement
`Debug`: a derived one would print the service-account token.

### TLS trust

The root store is the CA bundle compiled into the binary **plus** the host's own trust store:
`/etc/ssl/certs` (or `SSL_CERT_FILE` / `SSL_CERT_DIR`) on Unix, the Keychain on macOS, the
certificate store on Windows. That comes from `reqwest`'s `rustls-tls-native-roots` feature, which
this crate turns on.

A deployment behind a private CA therefore works by installing that CA on the host — the same act
that makes `curl` work — with nothing to configure here. Roots are only ever *added*: verification
itself is unchanged, and a certificate from a CA the host does not trust is still refused. The cost
is that the host store is load-bearing: anything an operator adds there, this client accepts. That
is the same exposure the .NET and browser SDKs have always had, and the same one `curl` has.

`tests/tls_trust.rs` proves both halves against a real rustls handshake with a real private CA.

---

## Configuration — `Config`

`Config` derives `Clone` and `Debug` and has a `Default` impl, so `..Default::default()` fills the
two optional fields.

| Field | Type | Required | Default | Meaning |
|---|---|---|---|---|
| `base_url` | `String` | yes | `""` | Base URL of the RediensIAM public API. Must be `https`, except on loopback. |
| `service_account_token` | `String` | yes | `""` | The credential this service presents. A service-account PAT (`rediens_pat_…`) is the simplest option; a plain user token is refused by the server. |
| `audience` | `String` | yes | `""` | The tenant *this resource server serves*: the project id it fronts, or the organisation id if it fronts a whole organisation. Sent as `aud` on every call. |
| `cache_duration` | `Duration` | no | 30 s | How long a positive introspection is reused. `Duration::ZERO` disables caching. |
| `timeout` | `Duration` | no | 5 s | Applied to the inner `reqwest::Client`. |

All three required fields are checked in `new()`, not on the first call, so a misconfigured service
fails at startup with a message naming the fix rather than 400-ing once traffic arrives. Each
failure is `Error::Config`.

Loopback here is the host `localhost` or `127.0.0.1`. Nothing else: not `127.0.0.2`, not
`*.localhost`, and — despite what the doc comment on `Config::base_url` says — not `[::1]`, because
the parsed host string keeps its brackets and does not match. Use `127.0.0.1` for local
development. That exemption is the whole opt-out; there is deliberately no flag to disable the
scheme check, because a flag gets set in production too.

The rule differs slightly per SDK: .NET uses `Uri.IsLoopback` (all of `127.0.0.0/8`, plus `::1`),
and the browser SDK additionally accepts any `*.localhost` host.

### Why `audience` has no default

A default would be a guess about which tenant this service belongs to, and a wrong guess is finding
P-06 exactly. Scoping used to come from *the caller's* organisation — and a multi-tenant gateway
must hold a deployment-level (`__system__`) service account, which has no organisation, so it
stayed deliberately unscoped. One gateway credential therefore resolved **every** tenant's token in
the deployment as `active: true`, and each resource server was expected to compare `project_id`
against its own configuration afterwards. Nothing enforced that, and no SDK had a field for it.

`project_id` is that field. The server answers `400 {"error":"project_id_required"}` to any
caller that omits it. Full migration notes:
[`sdk/README.md`](../../README.md#project_id-is-now-a-required-sdk-option).

---

## API

### `struct RediensIamClient`

Derives `Clone`. Not `Debug`, by design.

| Method | Signature | Returns |
|---|---|---|
| `new` | `fn new(config: Config) -> Result<Self, Error>` | `Err(Error::Config)` for a missing field or an insecure `base_url`; `Err(Error::Transport)` if `reqwest` cannot build its client. |
| `introspect` | `async fn introspect(&self, token: &str) -> Result<TokenInfo, Error>` | An empty token short-circuits to `TokenInfo::inactive()` with no round trip. |
| `authorize` | `async fn authorize(&self, token: &str, namespace: &str, object: &str, relation: &str) -> Result<bool, Error>` | Not cached, and no empty-token short circuit. |
| `forget` | `async fn forget(&self, token: &str)` | Drops any cached decision for that token. `async` because the cache is behind a `tokio::sync::RwLock`. |

### `struct TokenInfo`

Derives `Debug`, `Clone`, `Default`, `Deserialize`. All fields are public.

| Field | Type | Notes |
|---|---|---|
| `active` | `bool` | False means unusable *right now* — expired, revoked, deactivated service account, suspended organisation, or bound to another tenant. The reason is deliberately not disclosed. |
| `sub` | `Option<String>` | |
| `user_id` | `Option<String>` | |
| `org_id` | `Option<String>` | |
| `project_id` | `Option<String>` | |
| `roles` | `Vec<String>` | Empty rather than absent. An explicit `"roles": null` deserialises to an empty vector. |
| `client_id` | `Option<String>` | |
| `is_service_account` | `bool` | |
| `aud` | `Option<String>` | Echo of the `aud` this client sent, on an active answer. |

| Method | Signature | Meaning |
|---|---|---|
| `inactive` | `fn inactive() -> Self` | `Default::default()` — `active: false`, no roles. |
| `has_role` | `fn has_role(&self, role: &str) -> bool` | Exact match against the whole list. |
| `has_project_role` | `fn has_project_role(&self, project_id: &str, role: &str) -> bool` | Exact match against `{project_id}/{role}`. |
| `belongs_to_org` | `fn belongs_to_org(&self, org_id: &str) -> bool` | `org_id` equality — the check a multi-tenant resource server needs before serving tenant-scoped data. |

### `enum Error`

Implements `std::error::Error` through `thiserror`.

| Variant | When | Note |
|---|---|---|
| `Transport(reqwest::Error)` | Connection failure, DNS, TLS rejection, timeout — and also a response body that does not deserialise, since decoding goes through `reqwest`. | `From<reqwest::Error>` is derived. Treating this as "the IAM is unreachable" is right for the first group and optimistic for the last; check `source()` if the distinction matters to you. |
| `Api { status: u16, body: String }` | Any non-2xx from RediensIAM, with the body attached. `400 project_id_required` and `403 service_account_required` arrive here. | |
| `Config(String)` | A missing or unusable option, from `new()`. | |

An *unusable token* is none of these: it is `Ok(TokenInfo { active: false, .. })`. Transport and
server faults return `Err` so an IAM outage cannot be mistaken for "everyone is unauthenticated" —
the caller decides how to handle it.

---

## Role helpers

Tenant role names are chosen by tenant admins, so `admin` on its own means nothing across tenants.
RediensIAM emits tenant roles as `{project_id}/{name}` and reserves the management names.

```rust
info.has_role("org_admin");                    // management roles only — bare name
info.has_project_role(&project_id, "editor");  // tenant roles — matches "{project_id}/editor"
info.belongs_to_org(&org_id);                  // the tenant check
```

| Method | Matches | Does not match |
|---|---|---|
| `has_role(name)` | `super_admin`, `org_admin`, `project_admin` — the only bare names RediensIAM emits | any tenant role, whatever its name |
| `has_project_role(project_id, name)` | the exact string `{project_id}/{name}` | the same role name in another project |

`has_role("admin")` returns `false` even when the user holds a tenant role called `admin`. That is
the intended direction: two tenants' `admin` used to be the same string at every consumer, and a
bare match now **fails closed**. `tenant_roles_do_not_match_across_projects` in `src/lib.rs` pins
this.

Note the argument order: project first here and in the .NET SDK, role first in the browser SDK.

---

## Authorisation

```rust
let allowed = iam.authorize(token, "Organisations", &org_id, "org_admin").await?;
```

Keeps the policy in RediensIAM rather than having every gateway reimplement its own reading of the
roles claim. It returns `Ok(false)` for a denial, for a token bound to another tenant, and for an
object outside the caller's scope — deliberately the same shape, so the endpoint cannot be used to
probe which objects exist. Only `Organisations`, `Projects` and `UserLists` have ownership
RediensIAM can check; **any other namespace answers `false`**, because failing open on a namespace
it writes no objects into is the same finding under a new name. Read `roles` from introspection
instead.

---

## What this client enforces, and why

Pinned by the unit and wire tests in `src/lib.rs` and by `tests/tls_trust.rs`.

**`base_url` must be `https`, except on loopback.** The service-account credential and every token
being introspected ride on that URL, so cleartext hands an on-path attacker both.

**The audience is required, and it is checked in `new()`.** A resource server with no declared
tenant is a deployment mistake; it should stop the process at startup with a message naming the
fix, not return 400s under load. The wire tests assert that the field reaches the socket in both
the form body and the JSON body — the whole change is worthless if it is configured and then not
sent.

**Deploy order is load-bearing, and there is no client-side net.** A RediensIAM that predates
mandatory `project_id` does **not** reject the field — it silently discards the unknown parameter
and answers exactly as it always did. Nothing in the answer distinguishes an enforcing server from
an ignoring one, so an upgraded client pointed at an old server would report success while being
bound to nothing. **Upgrade the server first.**

**Cache keys are a SHA-256 digest of the token, never the token itself.** Keys surface in dumps and
diagnostics, and the map returns a full `TokenInfo` — roles included — before any server call, so
anything that collides with a cached privileged token is authenticated as that token. A 64-bit
non-cryptographic hash (this used FNV-1a) is trivially preimageable once a key is observed;
`cache_key_is_a_sha256_digest` pins the algorithm to a known-answer vector.

The key needs no audience in it, unlike the .NET client's: the cache lives inside the client and is
shared only with its own clones, which share its config. Two clients with different audiences hold
two caches.

**Negative answers are never cached.** Caching "inactive" would keep denying a token that has since
become valid, and buys nothing.

**An inactive answer is an answer, not an error.** A server that sends every optional field as an
explicit `null` used to deserialise into `Error::Transport` — the one error this crate tells you to
read as "the IAM is unreachable" — so a caller degrading gracefully during an outage would have
admitted every expired and revoked token. `roles` now uses a deserializer that treats `null` as
empty; `an_inactive_answer_with_null_fields_is_not_a_transport_error` pins it.

---

## Caching

Positive answers are cached in process for `cache_duration` (30 s default). That window is the upper
bound on how long a revoked token keeps working at this service, so shorten it if that matters more
than the round trip; `Duration::ZERO` disables it entirely.

```rust
iam.forget(token).await;   // drop one entry immediately, e.g. on logout
```

The cache is one `RwLock<HashMap<..>>` with an opportunistic sweep on insert — no background task.
Entries are tiny and expire within seconds; shard it only if contention shows up in a profile.

`authorize` is **not** cached: a permission decision depends on the namespace, object and relation
as well as the token.

---

## What this client does not do

Listed so you do not go looking.

- **No login flow.** No authorization-code exchange, no PKCE, no redirect handling, no token
  issuance or refresh. That is the browser's job — `rediensiam-web`.
- **No local JWT validation.** No JWKS fetch, no signature check, no `exp` arithmetic. That is the
  point: a signature proves a token was issued, not that it is still honoured.
- **No tenant comparison on your behalf.** `introspect` answering `active: true` means the token is
  usable for the audience you declared. `belongs_to_org` / `has_project_role` against the resource
  being served is still yours to call.
- **No retries, no backoff, no circuit breaker.** One request per call.
- **No framework integration.** No `axum`/`tower` layer, no extractor, no middleware — call
  `introspect` from your own handler or write the thin layer yourself.
- **No shared or distributed cache.** Ten replicas hold ten caches, and a `forget` on one does not
  reach the others.
- **No token revocation, no session management, no logout call.** `forget` clears a local cache
  entry, nothing more.
- **No management-API surface.** Creating organisations, projects, users, service accounts or PATs
  is plain HTTP against the routes in [`docs/API.md`](../../../docs/API.md); this crate wraps
  `/api/introspect` and `/api/authorize` only.
- **No `Debug` on the client**, deliberately — it would print the credential. `Config` does derive
  `Debug`, so do not log a `Config`.

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

`aud` being absent is invisible to an old server by design, so the wire tests run a one-shot
loopback listener and assert on the bytes the client actually wrote rather than on a mock it was
handed. `tests/tls_trust.rs` runs a real TLS handshake against a private CA in both directions —
trusted and not — and sets `SSL_CERT_FILE`, which is process-wide, so it is deliberately one test
rather than two.
