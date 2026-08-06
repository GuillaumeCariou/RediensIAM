# Impersonation

Letting a Yandee operator act **as** a customer, from `gestion.yandee.fr`, without
weakening anything for everyone else.

Status: **implemented in 0.7.0, path C.** §§1–2 and §§5–11 describe what shipped. §3 and §4
are kept as the record of how the path was chosen, with the two places they turned out to be
wrong marked inline.

**Start here if you are integrating a service:** [§12](#12-integrating-in-an-external-service).

---

## 1. The need

`yandee_gestion` administers customer organizations. Its operators must be able to see and
act as a customer to diagnose, configure and support.

Two mechanisms were considered. The iframe was **examined and set aside** — the decision
formally remains open (`yandee_entreprise/decisions/ADR-003-superadmin.md` marks its
recommendation as an unsolicited opinion). Four findings weigh against it, and none of them
is a matter of taste:

1. **`frame-ancestors` must be reopened.** The client console has to accept being framed,
   naming the operator origin in its CSP. That is exactly the control that prevents
   clickjacking, and the exception never closes again.
2. **Cookies are weakened for everyone.** A cross-origin frame only receives session cookies
   marked `SameSite=None; Secure`. That attribute sits on the application's cookie, so CSRF
   protection degrades for the entire user population to serve a feature two people use.
3. **A cross-origin frame cannot be driven.** Same-origin policy forbids reading or
   commanding it. It needs a `postMessage` protocol implemented on **both** sides — so code
   lands in the client application regardless, which is the very thing the iframe was meant
   to avoid. It does not remove the coupling, it relocates it and adds a protocol to
   maintain.
4. **It decays without being touched.** Browsers keep partitioning third-party frame
   storage. A working setup stops working with no code change, and the symptom is an
   unexplained logout.

If the iframe is chosen anyway, two things are not negotiable: `frame-ancestors` naming the
exact operator origin — never a wildcard — and a **separate session cookie** for framed mode,
so `SameSite=None` never reaches every customer's session.

What replaces it: the operator obtains a **delegated token** and opens the client console
**normally** — same origin, no frame, no CSP exception, no cookie downgrade.

Consumers are other services, so this must be reachable as a **management route**, not only
from a console UI.

### Not a replacement for OIDC — a layer above it

| | What it is | Who uses it |
|---|---|---|
| **OIDC via RediensIAM** | how anyone signs in, everywhere | everyone, always |
| **Impersonation** | a delegated session laid on top of an already-established OIDC identity | only to enter a customer organization |

An operator opening `gestion.yandee.fr`, Grafana, Argo CD, Forgejo or Harbor needs **no**
impersonation: they arrive with their own identity over OIDC. That is SSO, and it is already
the direction set in `yandee_entreprise/docs/06-portail.md` — *"the real centraliser is
identity"*.

Impersonation exists for exactly one crossing: into a customer's data inside
`client.yandee.fr`. One function, one place.

Practical consequence: signing into an admin surface opens **no new tab and shows no
screen**. Authorization Code + PKCE is a full-page redirect, and with a live Hydra SSO
session the round trip is silent. The exception already documented elsewhere is the Tauri
Suite, which runs the flow through the system browser.

---

## 2. Current state — what the code says

Not supported today. Every registered grant type is one of three:

| Location | Grant types |
|---|---|
| `src/Controllers/OrgController.cs:32` | `authorization_code`, `refresh_token` |
| `src/Services/HydraService.cs:271` | `authorization_code` |
| `src/Services/HydraService.cs:296` | `client_credentials` |
| `src/Controllers/SystemAdminController.cs:45` | `authorization_code`, `refresh_token` |

No `urn:ietf:params:oauth:grant-type:token-exchange`, no `act` claim, nothing reading one.

---

## 3. Upstream: Hydra and RFC 8693

Ory documents token exchange on the standard token endpoint, and describes it as serving
exactly this purpose — *"typically used for token delegation or impersonation"*.

### `POST /oauth2/token`

**Request body**

| Parameter | Required | Meaning |
|---|---|---|
| `grant_type` | ✅ | must be `urn:ietf:params:oauth:grant-type:token-exchange` |
| `subject_token` | ✅ | the token being exchanged |
| `subject_token_type` | ✅ | identifier for the type of `subject_token` |
| `resource` | — | location of the target service the token is for |
| `audience` | — | logical name of the target service the token is for |
| `scope` | — | space-delimited scopes requested for the new token |

**Success response (200)**

| Field | Meaning |
|---|---|
| `access_token` | the new token |
| `issued_token_type` | type of the issued token |
| `token_type` | type of the access token |
| `expires_in` | lifetime in seconds |

RFC 8693 also defines `actor_token` / `actor_token_type` for composite delegation, and the
`act` claim that records who is acting for whom. Ory's reference page above does not list
them; whether the running build accepts them is part of the verification below.

### ⚠ Verify before designing around it

The page above is Ory's general reference. It does **not** prove the grant is enabled on a
given self-hosted Hydra build and version. One command settles it:

```bash
curl -s "$HYDRA_PUBLIC/.well-known/openid-configuration" \
  | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('grant_types_supported'))"
```

If `urn:ietf:params:oauth:grant-type:token-exchange` is absent, path A below is closed and
the decision is between B and C. Do not discover this during implementation.

### The fallback Hydra does document unconditionally: OAuth2 webhooks

Hydra can call an HTTPS endpoint **before issuing a token**, and the response customises the
claims. Ory states this is supported **for all grant types**.

```json
{
  "session": {
    "access_token": {
      "act": { "sub": "operator:guillaume" },
      "org_id": "acme"
    }
  }
}
```

`src/Controllers/WebhookController.cs` already exists and already names OAuth2 token exchange
in its own documentation (`:286`). The mechanism is in place; only the delegation case is
unwired.

> ⚠ **This paragraph was wrong, and it was the estimate that made B look cheap.** Line 286
> documents `CreateSsrfSafeHandler` and redirect following; "OAuth2 token exchange" there means
> the authorization-code→token exchange, not RFC 8693. A false friend: the phrase matches, the
> meaning does not.
>
> More decisively, that file holds only **outbound** webhooks — `org/webhooks` and
> `admin/webhooks`, delivery towards tenants. Hydra's claims webhook is an **inbound** endpoint
> that Hydra calls before issuing a token, and it does not exist: `grep` for `token_hook`,
> `claims webhook` and `hydra.*webhook` returns nothing in `src/` or in the chart. B therefore
> needs a new inbound endpoint, its authentication and Hydra configuration — **more** than C, not
> less.

---

## 4. Three paths

| | Mechanism | Cost | Depends on |
|---|---|---|---|
| **A** | Native Hydra token exchange | lowest, if available | the grant being enabled on the running build |
| **B** | Normal grant + claims webhook injecting `act` | moderate | `WebhookController`, already present |
| **C** | Delegated session issued by RediensIAM, no OAuth change | moderate | nothing external |

> **What was chosen: C, and the order above was inverted.** Two reasons, both checked rather than
> assumed. First, Hydra's own client model documents the grant types it supports —
> `client_credentials`, `authorization_code`, `refresh_token`,
> `urn:ietf:params:oauth:grant-type:jwt-bearer`, `urn:ietf:params:oauth:grant-type:device_code`.
> `token-exchange` is **not among them**; the RFC 8693 page in §3 is Ory **Network**'s reference,
> and this deployment pins self-hosted Hydra `0.60.1`. Second, B costs more than §3 claimed — see
> the correction there. C depends on nothing external and reuses machinery that already exists.
>
> The `grant_types_supported` check of §10.1 remains the only proof for A and still has not been
> run: it needs the running Hydra. Nothing was built on A, so nothing depends on the answer.

**Order to try: A, then B, then C.** A is free if the verification above returns the grant. B
gives the same token shape as A and the same audit quality, using a mechanism Ory supports for
every flow. C avoids any dependency on a Hydra capability — RediensIAM mints a short-lived
delegated session with the machinery that already backs login, and returns a URL into the
client console.

The management route in §5 is **identical across all three**. Only its implementation moves.
Design the route first; the path is an internal detail callers never see.

---

## 5. The management route

### `POST /admin/impersonate`

Authority: service account **and** `RequireManagementLevel(SuperAdmin)`.

Both gates, not one. Service-account gating is the rule already applied to
`/api/introspect` (`IntrospectionController.IsServiceAccountCaller`) — a plain user token is
refused so the endpoint is not an oracle. The management level is then resolved live against
Keto, exactly as `src/Filters/RequireManagementLevelAttribute.cs` does: the claimed level
decides *which* level to re-check, it is never the answer.

**Request**

```json
{
  "org_id":      "acme",
  "project_id":  "7f3…",
  "user_id":     "usr_…",
  "mode":        "read",
  "ttl_seconds": 900,
  "reason":      "ticket #4812"
}
```

| Field | Required | Notes |
|---|---|---|
| `org_id` | ✅ | organization being entered |
| `project_id` | ✅ | the authentication boundary; mandatory everywhere else in this API, mandatory here |
| `user_id` | — | impersonate a specific user; omitted means an org-scoped identity with no user's personal data |
| `mode` | ✅ | `read` or `write`. Decided at issuance, never inferred from a role |
| `ttl_seconds` | — | default 900, hard ceiling 3600 |
| `reason` | ✅ | free text, written to the audit record. An impersonation with no stated reason is not auditable |

**Response**

```json
{
  "access_token": "…",
  "token_type":   "bearer",
  "expires_in":   900,
  "session_id":   "imp_…",
  "act":          { "sub": "usr_operator", "level": "super_admin" },
  "sub":          "usr_target",
  "redirect_url": "https://client.yandee.fr/?session=…"
}
```

### `POST /admin/impersonate/{session_id}/revoke`

Same authority. Ends the session immediately. Also callable by the operator who opened it.

### `GET /admin/impersonate`

Active sessions, for the operator console and for supervision. An impersonation nobody can
list is an impersonation nobody can stop.

---

## 6. Token shape

```
sub            the impersonated identity  ← every Keto check runs on this
act.sub        the operator
act.level      the operator's management level at issuance
act.mode       read | write
act.session    imp_…
org_id         the entered organization
project_id     the authentication boundary
exp            short
```

**`sub` is the customer, `act.sub` is the operator.** This ordering is what makes the audit
trail read *"Guillaume, acting for Acme"* rather than *"Acme"* — the whole point.

⚠ **Management roles are stripped from a delegated token.** `super_admin`, `org_admin` and
`project_admin` must not survive the exchange. Otherwise entering a customer organization
*raises* privilege inside it instead of narrowing to it, and impersonation becomes a
privilege-escalation primitive rather than a support tool.

⚠ **`ext.roles` stays namespaced.** Tenant roles are emitted as `{project_id}/{name}`
(`AuthController.cs`, `Roles.ProjectRoleClaim`). A delegated token follows the same shape —
no exception, or every consumer's role check diverges for exactly the tokens that most need
scrutiny.

---

## 7. Enforcement

**Roles still come from Keto, re-checked on every privileged request** (`CONSOLE.md:157`).
A delegated token carries *who acts for whom*, never *what they may do*. Nothing in this
feature changes that, and nothing in it may be read as a shortcut around it.

`mode: read` is enforced by the gateway of the calling repo: while `act.mode == "read"`,
mutating verbs are refused. It is a claim, so every enforcement point sees it — but a claim
enforces nothing on its own, and the enforcement point is the gateway.

Introspection must surface `act`. `POST /api/introspect` returns it alongside the existing
fields, so downstream services can render the banner and log both identities without parsing
the token themselves. **A consumer that cannot see `act` cannot tell an impersonated request
from a genuine one** — which is the one thing it must never fail to do.

---

## 8. Browser session handling

A delegated session and the operator's own session share one origin,
`client.yandee.fr`. If they share a cookie, the second overwrites the first: two customers
cannot be entered, and worse, nothing tells which one is currently active.

Two rules settle it.

**1. The delegated session lives in `sessionStorage`, never in a cookie.** `sessionStorage`
is isolated per tab by construction — it is the only browser primitive that yields *one tab,
one customer*. The token arrives in the URL fragment, the SPA stores it, the URL is scrubbed.

**2. One active impersonation at a time, enforced server-side.** Opening a session revokes
the previous one. This removes every question about which session is active, and costs one
branch instead of a concurrent-session model nobody can reason about later.

With both rules in place, the new tab is an ergonomic choice, not a security mechanism —
which is where it belongs. The non-dismissible banner carries an **exit impersonation**
button that revokes the session and closes the tab.

⚠ `sessionStorage` is exposed to XSS like any JavaScript-reachable token. The mitigation is
the short TTL and revocation already required in §9 — not the storage.

---

## 9. Non-negotiables

| | Why |
|---|---|
| Both identities in every audit record | a log that says only `Acme` is a log that hides who acted |
| Short TTL, hard ceiling | a delegated token is a loan, not a role |
| Revocable, and listable | see §5 |
| `mode` decided at issuance | inferring read/write from a role reintroduces exactly the ambiguity this feature removes |
| Permanent, non-dismissible banner in the client console while impersonating | the operator must never forget whose data is on screen |
| `reason` required | correlates the session to a ticket; makes review possible |
| Management roles stripped | see §6 |

---

## 10. Before implementing

1. Run the `grant_types_supported` check in §3. It decides path A.
2. If A is closed, confirm the claims webhook fires on the flow to be used — Ory states all
   grant types are supported; confirm it on the running build, not on the doc.
3. Decide whether `user_id` may be omitted. An org-scoped identity avoids entering a real
   person's mailbox and calendar to fix a billing setting — the weaker capability is the one
   to reach for by default.
4. Add the case to `Tests/Api/` alongside introspection and authorize, and to the
   cross-tenant refusal page `D-03` in `docs/TESTING.md`. An impersonation feature without a
   cross-tenant refusal test is the single most dangerous untested path in this codebase.

---

## 11. Consumers

`yandee_gestion` is the first, through its gateway `gestion-gw`, which holds a service
account. Any repo whose gateway carries the RediensIAM SDK can call the route under the same
two gates.

See `~/Desktop/Workspace/yandee/DEPOTS.md` §5 for where enforcement sits: RediensIAM is the
decision point, each repo's gateway is the enforcement point.

---

## 12. Integrating in an external service

Everything below happens in **your** service. RediensIAM decides who may open a session; what a
session may then see and do is yours, and it cannot be otherwise — see §7.

### 12.1 What actually shipped

An impersonation token is an **opaque RediensIAM credential** prefixed `rediens_imp_`. It is not an
OAuth2 token, there is no new flow, and no CSP or cookie attribute changed anywhere.

Your gateway already calls `POST /api/introspect` on every request. That answer now carries one
extra field:

```json
{
  "active": true,
  "sub": "imp_7f3e…",
  "user_id": null,
  "org_id": "…",
  "project_id": "…",
  "roles": [],
  "act": { "sub": "usr_operator", "level": "super_admin", "mode": "read", "session": "7f3e…" }
}
```

Four properties of that answer are the whole contract:

| | Meaning for you |
|---|---|
| `act` is present | this request is an operator acting for the tenant. On every ordinary token the field is **absent** — that is what makes its presence mean something |
| `roles` is empty | **a delegated token grants nothing.** It says who acts for whom, never what they may do |
| `user_id` is null | the session is organisation-scoped. There is no person being impersonated, so nothing personal is entered by default |
| `sub` starts with `imp_` | the subject is a session, not a user. It deliberately does not parse as a user id |

### 12.2 The three things your service must do

**1. Refuse mutations while `act.mode == "read"`.** This is the enforcement point; the claim alone
enforces nothing. One check, as early as your authorisation middleware:

```rust
if let Some(act) = &info.act {
    if act.mode == "read" && !matches!(*req.method(), Method::GET | Method::HEAD | Method::OPTIONS) {
        return Err(Forbidden("impersonation_is_read_only"));
    }
}
```

The .NET and Rust SDKs expose `IsReadOnlyImpersonation` / `is_read_only_impersonation()` for the
common case. The browser SDK is unaffected: it never introspects.

**2. Decide what a support session may see.** A delegated token carries no roles, so your usual
role checks grant it nothing — which is correct, and means *you* choose the support view. Make that
choice explicit and narrow; do not map `act` to "everything the tenant's admin can see" out of
convenience.

**3. Log both identities, always.** A record naming only the tenant hides who acted:

```
org=acme  act=usr_operator  session=7f3e…  mode=read  GET /invoices/123
```

`act.session` is the value that revokes the session and the value that correlates every line of it.

### 12.3 What your UI must do

A permanent, **non-dismissible** banner whenever `act` is present — the operator must never forget
whose data is on screen — carrying an **exit** button that calls
`POST /admin/impersonate/{session}/revoke`.

The token arrives in the URL **fragment** — never sent to a server — and the URL is scrubbed
immediately: it is the one place a credential survives into history, a screenshot or a pasted link.
`rediensiam-web` does both in one call:

```ts
const support = iam.adoptImpersonation();   // null on an ordinary page load
if (support) showBanner(support.orgId, support.operator);
if (iam.isReadOnlyImpersonation) disableEveryWriteControl();
```

**Never a cookie.** A cookie is sent on every request to the origin, including the operator's own
session on the same host, and the second one written overwrites the first — two customers cannot be
entered and nothing says which is active.

> **Amended.** §8 above called for `sessionStorage`, and the SDK keeps the token **in memory**
> instead. Memory is already per-tab, so *one tab, one customer* holds either way; memory
> additionally cannot be read back by injected script after the fact, and does not survive a
> reload. The cost is that a reload ends the session and the operator opens another — cheap, for a
> credential whose ceiling is an hour. The XSS caveat stands either way: any JavaScript-reachable
> token is exposed to injected script, and the mitigation is the short TTL and revocation, not the
> storage.

`exitImpersonation()` clears the tab and nothing more. **Revoking server-side needs a
service-account credential, which a browser must never hold** — the exit button calls your backend,
which calls `POST /api/manage/impersonate/{session}/revoke`.

### 12.4 Opening a session

Only a caller that is **both** a service account and `super_admin` may open one — your gateway's
service account, not a user's browser session.

```bash
curl -X POST https://iam.yandee.fr/api/manage/impersonate \
  -H "Authorization: Bearer $IAM_SERVICE_ACCOUNT_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"org_id":"…","project_id":"…","mode":"read","reason":"ticket #4812","ttl_seconds":900}'
```

| Field | Required | Notes |
|---|---|---|
| `org_id` | ✅ | the organisation being entered |
| `project_id` | ✅ | must belong to `org_id`; the pair is checked in the database, never taken on trust |
| `mode` | ✅ | `read` or `write`, decided here and never inferred from a role |
| `reason` | ✅ | free text, lands in the tenant's own audit log. Whitespace is not a reason |
| `ttl_seconds` | — | default 900, **hard ceiling 3600**; an over-long request is clamped, not refused |
| `user_id` | — | **refused with `user_id_not_supported`.** Sessions are organisation-scoped today. It is refused rather than ignored, because a caller that sends it believes it is entering a person's account |

The token is returned once, in `access_token`. There is no way to read it again.

**Opening a session revokes that operator's previous one.** One active impersonation per operator
removes every question about which customer is currently entered.

### 12.5 Ending it

```bash
curl -X POST https://iam.yandee.fr/api/manage/impersonate/$SESSION/revoke \
  -H "Authorization: Bearer $IAM_SERVICE_ACCOUNT_TOKEN"        # 204, or 404 if nothing was live
curl https://iam.yandee.fr/api/manage/impersonate \
  -H "Authorization: Bearer $IAM_SERVICE_ACCOUNT_TOKEN"        # active sessions
```

Revocation is immediate, not at the TTL: every introspection re-reads the row, and the liveness
predicate is `not revoked and not expired`. Expiry needs no sweeper for the same reason — a session
stops being usable the instant it expires.

### 12.6 The mistakes to expect

| Symptom | Cause |
|---|---|
| `403 service_account_required` | a user token was used. This route is deliberately unreachable from a browser session |
| `400 project_not_in_org` | the project belongs to another organisation — the cross-tenant refusal doing its job |
| `400 user_id_not_supported` | see §12.4; sessions are organisation-scoped |
| `{"active": false}` on a token you just minted | your gateway's service account belongs to a **different** organisation than the one entered. `/api/introspect` answers "inactive" rather than "not yours", by design |
| the delegated token grants nothing | correct. `roles` is empty by construction — decide the support view in your own service |

### 12.7 What is deliberately not here

- **Named-user impersonation.** The weaker capability is the default; adding `user_id` later is an
  addition to this contract, not a change to it.
- **Concurrent sessions per operator.** One at a time, server-enforced.
- **A console UI.** This is a management route first: `yandee_gestion` consumes it through its
  gateway. A console page is additive and touches nothing here.
