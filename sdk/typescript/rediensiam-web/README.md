# rediensiam-web

Browser SDK for [RediensIAM](../../../README.md): OpenID Connect authorization-code login with
PKCE S256, tokens held in memory only, and an authenticated `fetch`.

Version 0.5.0. Zero runtime dependencies — Web Crypto and `fetch` cover the whole flow;
TypeScript is the only devDependency.

**Who calls this.** A browser application: a SPA, a mobile web app, anything running as JavaScript
on a user's machine. This repository's own admin console runs on it
(`frontend/admin/src/auth.ts`), deliberately — the login this SDK gives integrators is the login
the console itself uses, so a defect in it fails here first.

This is a **frontend** SDK. It logs the user in; it does not decide what they may do. The server
does that, with `RediensIAM.Client` (C#) or `rediensiam-client` (Rust). Which SDK belongs where is
[`sdk/README.md`](../../README.md); setting up the project and OAuth2 client this SDK needs is
[`docs/INTEGRATION.md`](../../../docs/INTEGRATION.md).

---

## Install

```bash
npm install rediensiam-web
```

Inside this repository the package is consumed from source through a path alias rather than the
registry — `frontend/admin/vite.config.ts` and `frontend/admin/tsconfig.app.json` both map
`rediensiam-web` to `sdk/typescript/rediensiam-web/src/index.ts`.

The published entry points are `dist/index.js` (ESM) and `dist/index.d.ts`. Run `npm run build`
before packing; `dist/` is generated.

### Minimum working example

```ts
import { createRediensIam } from 'rediensiam-web';

const iam = createRediensIam({
  issuer: 'https://auth.example.com',
  clientId: 'client_<your-project-id>',
  redirectUri: `${location.origin}/callback`,
});

// On page load. Completes the login if this is the callback; otherwise starts one.
if (!(await iam.handleRedirect()) && !iam.isAuthenticated) {
  await iam.login(); // navigates away; the returned promise never settles
}

// Call your API. The bearer is attached, and a 401 triggers one refresh and one retry.
const orders = await iam.fetch('/api/orders').then((r) => r.json());

// Rendering decisions only — see "Claims are unverified" below.
if (iam.hasRole('org_admin')) showAdminMenu();

await iam.logout();
```

`clientId` is `client_<project_id>` for a project-created client — the id is deterministic and
derivable in every environment. `issuer` must be the origin the **browser** reaches and must equal
the issuer Hydra is configured with; the SDK fetches `{issuer}/.well-known/openid-configuration`
before it redirects, and a mismatch is the usual cause of "login does nothing".

### What the environment must provide

`fetch`, `crypto.getRandomValues`, `crypto.subtle`, `sessionStorage`, `location`, `history`,
`btoa`/`atob`. `crypto.subtle` exists only in a secure context, which is the same set of origins
the `https`-or-loopback rule below already allows.

---

## Configuration — `RediensIamConfig`

Every field is validated in the constructor, so a misconfigured client fails where it is built
rather than mid-flow. Every failure throws `RediensIamError` with `code === 'config_invalid'`.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `issuer` | `string` | yes | Issuer URL of the RediensIAM-managed Hydra. Must be `https:`, except on loopback (below). |
| `clientId` | `string` | yes | The OAuth2 client registered for this application. |
| `redirectUri` | `string` | yes | Must resolve to **this app's own origin**, and must match a redirect URI registered for the client. |
| `scope` | `string` | no | Defaults to `openid profile offline_access`. `offline_access` is what yields the refresh token; drop it and every expiry becomes a full redirect. |
| `postLogoutRedirectUri` | `string` | no | Defaults to `location.origin`. Hydra refuses a value the client has not registered, so this must be one it has. |
| `projectId` | `string` | no | Sent as `project_id` on the authorization request. RediensIAM uses the client's *registered* project regardless; this only makes the login page render the right theme sooner. |
| `apiOrigins` | `string[]` | no | Extra origins `iam.fetch()` may send the bearer to. Same scheme rule as `issuer`. |

There is deliberately **no audience option** — see [What this SDK does not do](#what-this-sdk-does-not-do).

Construction throws when:

- `issuer`, `clientId` or `redirectUri` is empty;
- `issuer`, or any `apiOrigins` entry, is not an absolute URL, or is `http:` on a non-loopback host;
- the origin of `redirectUri`, resolved against `location.origin`, is not `location.origin`.

The last check is skipped when `globalThis.location` is absent — outside a browser there is no app
origin to compare against. `fetch()` fails closed in that same situation.

### The loopback exemption

`http:` is accepted on `localhost`, `127.0.0.1`, `[::1]`, and on any host ending in `.localhost`.
RFC 6761 §6.3 reserves the whole `.localhost` TLD for loopback and browsers treat
`http://*.localhost` as a secure context, so a development deployment that names its services —
`iam.localhost`, `admin.iam.localhost` — is as safe as bare `localhost`. The suffix is matched at a
label boundary: `notlocalhost` and `localhost.attacker.test` are not loopback.

That exemption is the whole opt-out. There is no flag to disable the scheme check, because a flag
gets set in production too.

Note that the rule differs slightly per SDK: .NET uses `Uri.IsLoopback` (all of `127.0.0.0/8`, no
`.localhost` suffix) and Rust accepts `localhost` and `127.0.0.1` only.

---

## API

### `createRediensIam(config: RediensIamConfig): RediensIam`

Factory for `new RediensIam(config)`. Same validation, same throws.

### `class RediensIam`

| Member | Signature | Returns / throws |
|---|---|---|
| constructor | `new RediensIam(config: RediensIamConfig)` | Throws `RediensIamError` `config_invalid`. |
| `isAuthenticated` | `get (): boolean` | True while a token is held **and** its adjusted expiry is in the future. Never throws, never refreshes. |
| `login` | `(): Promise<never>` | Runs discovery, generates the PKCE verifier and `state`, writes them to `sessionStorage`, and navigates to `authorization_endpoint`. The promise never settles. Throws `discovery_failed`. |
| `handleRedirect` | `(): Promise<boolean>` | `false` unless the URL carries **both** `code` and `state` — the ordinary "this is not a callback" case, and the only non-throwing false. `true` after a completed exchange, with `code` and `state` stripped from the address bar via `history.replaceState`. Throws `state_mismatch`, `discovery_failed`, `token_exchange_failed`. |
| `getToken` | `(): Promise<string \| null>` | The current access token; refreshes first if it has expired and a refresh token is held. `null` when there is no usable session. Does not throw on a failed refresh — it returns `null`. |
| `logout` | `(): Promise<void>` | Clears the access, id and refresh tokens and the expiry, then navigates to `end_session_endpoint` with `post_logout_redirect_uri` and `id_token_hint`. Does not throw. |
| `fetch` | `(input: FetchTarget, init?: RequestInit): Promise<Response>` | `fetch` with the bearer attached. Throws `untrusted_target` and `not_authenticated`. |
| `claims` | `get (): Claims` | Decoded from the access token **without verifying the signature**. `{ roles: [] }` when there is no token or it does not parse. Never throws. |
| `hasRole` | `(role: string): boolean` | Management roles of RediensIAM itself. |
| `hasProjectRole` | `(role: string, projectId?: string): boolean` | Tenant roles. `projectId` defaults to the project the token was issued for. |

`fetch` returns the response unchanged for every status except 401. On a 401 it refreshes once and
retries once; if the refresh yields nothing it throws `not_authenticated`, so a caller can treat
that one error as "send them to login". A 401 on the retry is returned to the caller as a
`Response`, not thrown.

`logout` is best-effort about the SSO session. If discovery fails, or the issuer advertises no
`end_session_endpoint`, it degrades to a **local** sign-out and navigates to
`postLogoutRedirectUri` (or the app origin) anyway. Local state is gone either way, but the user is
still signed in at the IdP — a subsequent `login()` will complete without a prompt.

### `interface Claims`

```ts
interface Claims {
  userId?: string;    // ext.user_id, falling back to the token's `sub`
  orgId?: string;     // ext.org_id
  projectId?: string; // ext.project_id
  roles: string[];    // ext.roles — always an array, empty when absent
  expiresAt?: Date;   // from `exp`, seconds since epoch
}
```

Claims are read from `payload.ext` when Hydra emitted one, otherwise from the payload itself.
`roles` is normalised from the three shapes Hydra may serialise it in: an array, a JSON array in a
string, or a comma-separated string.

### `class RediensIamError extends Error`

`name` is `'RediensIamError'`; `code` is a `RediensIamErrorCode`:

| Code | Thrown by | Meaning |
|---|---|---|
| `config_invalid` | constructor | See the validation list above. |
| `discovery_failed` | `login`, `handleRedirect`, `getToken` (via refresh) | Discovery returned a non-2xx, or named an endpoint outside the issuer origin. |
| `state_mismatch` | `handleRedirect` | No stored PKCE state for this callback, or the returned `state` is not the one issued. |
| `token_exchange_failed` | `handleRedirect` | The token endpoint answered non-2xx. The status is in the message. |
| `not_authenticated` | `fetch` | No valid session, or the refresh after a 401 produced nothing. |
| `untrusted_target` | `fetch` | The target origin is neither the app origin nor a declared `apiOrigins` entry. |

### `type FetchTarget = string | URL | Request`

Anything `fetch` accepts as its first argument.

### Helpers

Exported because the tests drive them directly. They are stable, but an application normally needs
none of them.

| Export | Signature | Notes |
|---|---|---|
| `isTrustedTarget` | `(target: FetchTarget, appOrigin: string \| undefined, apiOrigins: ReadonlySet<string>) => boolean` | The rule `fetch()` applies. Fails closed: an unparseable target, or no app origin, matches nothing. |
| `base64UrlEncode` | `(bytes: Uint8Array) => string` | base64url without padding, per RFC 7636. |
| `randomUrlSafe` | `(byteLength: number) => string` | `crypto.getRandomValues` + `base64UrlEncode`. |
| `s256` | `(verifier: string) => Promise<string>` | The PKCE challenge. Pinned against the RFC 7636 Appendix B test vector. |
| `decodeJwtPayload` | `(token: string) => Record<string, unknown> \| null` | Payload only, **no signature check**. `null` for anything malformed rather than throwing. |
| `matchProjectRole` | `(roles: string[], projectId: string \| undefined, role: string) => boolean` | Matches the exact string `{projectId}/{role}`. No project means no match. |

---

## Role helpers

Tenant role names are chosen by tenant admins, so `admin` on its own means nothing across tenants.
RediensIAM emits tenant roles as `{project_id}/{name}` and reserves the management names.

```ts
// Management roles of RediensIAM itself — bare names.
if (iam.hasRole('org_admin')) showAdminMenu();

// Tenant roles need a project to match against. Defaults to the token's own project,
// which is what a single-tenant app wants.
if (iam.hasProjectRole('editor')) showEditorTools();
if (iam.hasProjectRole('editor', someOtherProjectId)) { /* explicit project */ }

const { userId, orgId, projectId, roles, expiresAt } = iam.claims;
```

| Method | Matches | Does not match |
|---|---|---|
| `hasRole(name)` | `super_admin`, `org_admin`, `project_admin` — the only bare names RediensIAM emits | any tenant role, whatever its name |
| `hasProjectRole(name, projectId?)` | the exact string `{projectId}/{name}` | the same role name in another project |

Note the argument order: **role first** here (`hasProjectRole(role, projectId?)`), project first in
the backend SDKs (`HasProjectRole(projectId, role)`, `has_project_role(project_id, role)`). The
browser form takes the project last because it is optional.

`hasRole('admin')` is `false` even when the user holds a tenant role called `admin`, and
`hasProjectRole` with no project available at all is `false` rather than matching loosely. Both
fail closed.

---

## What this SDK enforces, and why

Each of these is a control, not tidiness. They are pinned by `src/index.test.ts`.

**The issuer must be `https:`, except on loopback.** The PKCE verifier, the refresh token and the
bearer all ride on the issuer origin, so cleartext there hands anyone on the network path the whole
session. Every `apiOrigins` entry is held to the same rule, for the same reason. Loopback is the
only exemption, and there is no flag — a flag to relax it gets set in production too.

**The `redirectUri` origin must equal the app origin.** A `redirect_uri` pointing elsewhere hands
the authorization code to another origin. Hydra also enforces its registered list; this is the
second lock, and the one that still holds if that list is ever loosened. The admin console adds a
third, on the value it reads from `/console/config`, before it ever reaches the SDK.

**Every discovery endpoint must sit on the issuer's origin.** An unvalidated discovery document
redirects the whole flow: whoever answers for the issuer names the token endpoint, and that is
where the PKCE verifier and the refresh token go. `authorization_endpoint` and `token_endpoint`
must be present and on the issuer origin; `end_session_endpoint` is optional but validated when
present. The check runs before the document is cached.

**The bearer only goes to the app origin or a declared `apiOrigins`.** Without this, one
caller-supplied or user-influenced URL through `iam.fetch()` ships the access token off-origin.
A subdomain of the app origin is still another origin and is refused. Anything else throws
`untrusted_target` rather than attaching the token.

```ts
const iam = createRediensIam({ /* … */, apiOrigins: ['https://api.example.com'] });
```

**Tokens live in a private field — never `localStorage`, never `sessionStorage`.** Anything
readable by JavaScript is readable by injected JavaScript, and a stored token outlives the tab that
earned it. The cost is that a page reload starts a fresh login redirect; with a live IdP session
that completes without user interaction.

**PKCE S256, with `state`.** The verifier is 64 random bytes, the challenge is SHA-256, and `s256`
is pinned against the RFC 7636 test vector. The `state` comparison is the CSRF control for the
authorization-code flow: rejecting a mismatch is what stops an attacker feeding you their own
authorization code and logging your user into the attacker's account. The stored state is removed
*before* the comparison, so a replayed callback finds nothing and throws.

**Logout sends the id token, never the access token.** `id_token_hint` must be an ID token. Sending
the access token there would put it in a query string — browser history, the IdP's access logs,
every intermediary — which is exactly what the in-memory-only design exists to prevent, and Hydra
would not have honoured the post-logout redirect with it anyway. The id token is kept for this one
purpose and is presented nowhere else.

**Headers are merged through `Headers`.** `iam.fetch()` builds the outgoing headers with
`new Headers(init.headers)` rather than by spreading. Spreading a `Headers` instance yields `{}`,
and spreading the `[[k, v]]` array form yields `{"0": [...]}`; either way the caller's
`Content-Type` disappears silently and the API answers 415.

**Refresh is single-flight, and early.** Concurrent 401s cause one refresh, not ten. The recorded
lifetime is `expires_in` minus 30 seconds (default 3600 when the server omits it), so a request
already in flight cannot land on a token that expired between the check and the server reading it.
Hydra rotates refresh tokens; a response carrying a new one replaces the old, and a response
without one leaves the still-valid token in place.

---

## Claims are unverified

`claims`, `hasRole` and `hasProjectRole` read the access token payload **without checking the
signature**, and roles may have been revoked since it was issued. Use them to render — show a menu,
hide a button. Never gate anything that matters on them; the server re-checks every request.

If you find yourself wanting to check a role in the browser to protect data, that check belongs in
your API.

---

## What this SDK does not do

Listed so you do not go looking.

- **No introspection and no authorisation calls.** It never calls `/api/introspect` or
  `/api/authorize`. Those need a service-account credential, and a credential shipped to a browser
  belongs to anyone who opens devtools. That is also why the `aud` option the backend SDKs require
  has no counterpart here: this SDK declares no audience because it never calls the endpoints that
  demand one, and there is nothing to migrate. Token validation belongs on your server.
- **No signature verification, no JWKS, no `nonce` or `at_hash` check.** The id token is stored for
  `id_token_hint` and is never parsed.
- **No persistence.** Nothing survives a reload except the PKCE state mid-redirect, and no session
  is shared between tabs — each tab builds its own client and does its own login.
- **No silent renew via a hidden iframe, no `prompt=none`, no session monitoring**
  (`check_session_iframe`), no front-channel or back-channel logout handling.
- **No token revocation.** `logout()` drops the local tokens and ends the SSO session through
  `end_session_endpoint`; it does not call a revocation endpoint, so an already-issued access token
  remains valid at your API until it expires or your backend's introspection cache turns over.
- **No automatic `handleRedirect()`.** The application decides when to run it; nothing is
  registered on load.
- **No userinfo request**, no profile fetching, and no refresh of `claims` from the server.
- **No framework bindings.** No React hooks, no router integration, no interceptor for third-party
  HTTP clients — `iam.fetch` is the whole surface, and `getToken()` is there if you need the bearer
  for something else.
- **No retries or backoff** beyond the one refresh-and-retry on a 401.

---

## Storage

There is no answer cache — this SDK asks RediensIAM nothing that would need one.

| What | Where | Lifetime |
|---|---|---|
| Access, id and refresh tokens, expiry | private fields on the instance | the tab |
| Discovery document | private field on the instance | the instance; fetched once, and not re-fetched after logout |
| PKCE verifier + `state` | `sessionStorage`, key `rediensiam:pkce` | one redirect — it must survive the navigation and nothing beyond it |

The PKCE state is the only thing written to browser storage, and it is worthless once the callback
has been handled: `handleRedirect()` removes it before it compares anything.

---

## Build and test

```bash
npm run build      # tsc -p tsconfig.json → dist/
npm run typecheck  # tsc -p tsconfig.json --noEmit
npm test           # node --test src/*.test.ts
```

Tests run on Node's built-in runner against the TypeScript sources directly (Node strips types), so
there is no test framework and no build step before testing. `RediensIamError` assigns `code` as a
plain field rather than using a constructor parameter property for the same reason: type-stripping
is strip-only, and a parameter property would need a transform.
