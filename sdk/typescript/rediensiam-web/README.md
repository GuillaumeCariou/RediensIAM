# rediensiam-web

Browser SDK for [RediensIAM](../../../README.md): OpenID Connect authorization-code login with
PKCE, tokens held in memory only, and an authenticated `fetch`.

Zero runtime dependencies — Web Crypto and `fetch` cover the whole flow. TypeScript is the only
devDependency.

This is a **frontend** SDK. It logs the user in; it does not decide what they may do — see
[`sdk/README.md`](../../README.md).

---

## Install

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

---

## Required options

Three are required and all three are checked **in the constructor**. Every failure throws
`RediensIamError` with `code === 'config_invalid'`.

| Option | Required | Notes |
|---|---|---|
| `issuer` | yes | Must be `https:`. `http:` is accepted **only** on `localhost`, `127.0.0.1` or `[::1]`. There is no flag to disable the check — a flag gets set in production too. |
| `clientId` | yes | The OAuth2 client registered for this application. |
| `redirectUri` | yes | Must resolve to **this app's own origin**, checked against `location.origin`. Hydra also enforces its registered list; this is the second lock. |
| `scope` | no | Defaults to `openid profile offline_access`. |
| `postLogoutRedirectUri` | no | Defaults to the app origin. |
| `projectId` | no | RediensIAM uses the client's registered project regardless; passing it only makes the login page render the right theme sooner. |
| `apiOrigins` | no | Extra origins `iam.fetch()` may send the bearer to. Same `https`/loopback rule as `issuer`. |

There is **no audience option**, deliberately — see below.

---

## No introspection, by design

This SDK never calls `/api/introspect` or `/api/authorize`.

Those endpoints require a service-account credential, and a credential shipped to a browser
belongs to anyone who opens devtools. That is also why the `aud` option that became mandatory on
the backend SDKs in 0.2.0 has no counterpart here: this SDK declares no audience because it never
calls the endpoints that demand one, and there is nothing to migrate.

**`rediensiam-web` is unaffected by the 0.2.0 wire-contract changes** except for the role helpers
below.

Everything `claims` exposes is decoded from the access token **without verifying the signature**,
and roles may have been revoked since it was issued. Use it to render — show a menu, hide a button
— never to protect. Every privileged decision is re-made server-side by your API, using
`RediensIAM.Client` (C#) or `rediensiam-client` (Rust).

If you find yourself wanting to check a role in the browser to protect data, that check belongs in
your API.

---

## Role helpers

Tenant role names are chosen by tenant admins. Since 0.2.0 RediensIAM emits them qualified by the
project that defined them, and reserves the management names.

```ts
// Management roles of RediensIAM itself — bare names.
if (iam.hasRole('org_admin')) showAdminMenu();

// Tenant roles are `{project_id}/{name}`, so they need a project to match against.
// Defaults to the project the token was issued for, which is what a single-tenant app wants.
if (iam.hasProjectRole('editor')) showEditorTools();
if (iam.hasProjectRole('editor', someOtherProjectId)) { /* explicit project */ }

const { orgId, userId, projectId, roles, expiresAt } = iam.claims;
```

| Method | Matches | Does not match |
|---|---|---|
| `hasRole(name)` | `super_admin`, `org_admin`, `project_admin` — the only bare names RediensIAM emits | any tenant role, whatever its name |
| `hasProjectRole(name, projectId?)` | the exact string `{projectId}/{name}` | the same role name in another project |

Note the argument order: **role first** in the browser SDK (`hasProjectRole(role, projectId?)`),
project first in the backend SDKs (`HasProjectRole(projectId, role)` / `has_project_role`). The
browser form takes the project last because it is optional.

`hasRole('admin')` returns `false` even when the user holds a tenant role called `admin`, and
`hasProjectRole` with no project available at all returns `false` rather than matching loosely —
both fail closed.

---

## What it does for you

- Authorization code + **PKCE S256** (verified against the RFC 7636 test vector)
- `state` generated and checked — an attacker cannot feed you their code and log your user into
  their account
- Access token held **in memory only**: not `localStorage`, not `sessionStorage`. Injected
  JavaScript cannot read it and it does not outlive the tab. The cost is a silent
  re-authentication after a reload, which `handleRedirect()` covers.
- Refresh 30 s before expiry, **single-flight**, so concurrent 401s cause one refresh and not ten.
  Hydra rotates refresh tokens; the new one is kept.
- `redirectUri` origin checked against the app origin at construction
- `issuer` must be `https:`, **and** every endpoint the discovery document names must sit on the
  issuer's own origin — otherwise whoever answers for the issuer chooses where the PKCE verifier
  and the refresh token get sent. Enforced for `authorization_endpoint`, `token_endpoint` and, if
  present, `end_session_endpoint`.
- `fetch()` attaches the bearer only to the app's own origin or to an origin declared in
  `apiOrigins`. Anything else throws `RediensIamError` with `code === 'untrusted_target'` rather
  than leaking the token:

  ```ts
  const iam = createRediensIam({ /* … */, apiOrigins: ['https://api.example.com'] });
  ```

Logout ends the SSO session too:

```ts
await iam.logout();
```

### Error codes

`RediensIamError.code` is one of `not_authenticated`, `state_mismatch`, `token_exchange_failed`,
`discovery_failed`, `config_invalid`, `untrusted_target`.

---

## Caching and storage

There is no answer cache here — this SDK asks RediensIAM nothing that would need one.

| What | Where | Lifetime |
|---|---|---|
| Access token, refresh token, expiry | in-memory fields | the tab |
| Discovery document | in-memory field | the client instance; fetched once |
| PKCE verifier + `state` | `sessionStorage` under `rediensiam:pkce` | one redirect — it must survive the navigation and nothing beyond it |

The PKCE state is the only thing written to browser storage, and it is worthless once the
callback has been handled.

---

## Local development

`http://` is accepted on `localhost`, `127.0.0.1` and `[::1]` only — for `issuer` and for each
`apiOrigins` entry. That is the whole opt-out; there is deliberately no flag to disable the check.

---

## Build and test

```bash
npm run build      # tsc -p tsconfig.json → dist/
npm run typecheck  # tsc --noEmit
npm test           # node --test src/*.test.ts
```

Tests run on Node's built-in test runner against the TypeScript sources directly (Node strips
types), so there is no test framework and no build step before testing. `RediensIamError` uses a
plain field rather than a constructor parameter property for the same reason: type-stripping is
strip-only and a parameter property would need a transform.
