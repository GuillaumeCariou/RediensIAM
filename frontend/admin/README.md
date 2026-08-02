# RediensIAM — Admin console

The operator SPA. Everything an administrator does to RediensIAM itself happens here:
organisations, projects, user lists, users, roles, service accounts and their PATs, webhooks,
SMTP, the audit log, metrics and health.

React 19 + TypeScript + Vite and Tailwind. Login runs on `rediensiam-web`, the browser SDK this
repo ships. There is no component library and no charting library: the shared pieces live in
`src/components/iam/` and are styled with the `iam-*` classes in `src/index.css`.

---

## Where it runs

The bundle is served **by the backend**, not by a separate web server:

| | |
|---|---|
| Vite `base` | `/admin/` |
| Built into | `wwwroot/admin/` in the container image (`Dockerfile`, stage 2 → stage 3) |
| Reached at | `https://<host>/admin` |

`/admin/*` is in the chart's `ingress.public.adminOnlyPaths`, so on a public host it answers 403.
The console is reachable on the admin host only. The machine-to-machine equivalent of the same
controllers is `/api/manage/*`, which is deliberately **not** in `adminOnlyPaths` — see
[`docs/API.md`](../../docs/API.md).

---

## Authentication

`src/auth.ts`. Authorization code + PKCE against the RediensIAM-managed Hydra, through
`rediensiam-web` — the console runs on the SDK it ships to integrators, so a defect in that login
fails here first.

- Configuration comes from `GET /admin/config` at runtime (`hydra_url`, `client_id`,
  `redirect_uri`) — the bundle is built once and carries no deployment values.
- The `redirect_uri` the server returns is checked against `location.origin` before it is used.
  Hydra validates its registered list too; this is the second lock, so a compromised config
  endpoint cannot hand the authorization code to another origin.
- Tokens live in the SDK's private field and a module-level variable — nothing in `localStorage`
  or `sessionStorage`, nothing that outlives the tab.
- A single `signinRedirectInFlight` guard means concurrent 401s fire one redirect, not several
  racing ones that would each overwrite the stored PKCE state.

**`App__AdminSpaOrigin` must equal the origin the browser actually loads this SPA from**, and
Hydra's registered `redirect_uri` must match it. A mismatch fails at the origin check above before
the OIDC round-trip even starts.

---

## The CSP arrangement

Two policies apply and a request must satisfy **both** — browsers enforce the intersection.

| | Where | What it pins |
|---|---|---|
| **Header** (enforcing copy) | `src/Program.cs`, `AddSecurityHeaders`, `/admin` branch | `connect-src 'self' <issuerOrigin>` — the *exact* issuer origin, computed at runtime; plus `frame-ancestors 'none'` |
| **Meta** | `index.html` | everything else. `connect-src` is left broad (`'self' https: http:`) |

The meta tag **cannot** pin the issuer: the issuer is a deployment value and the bundle is built
once, so a meta policy that named one would be wrong everywhere else. The header narrows it. The
meta tag deliberately omits `frame-ancestors`, because browsers ignore that directive in a meta
tag — the header carries it, along with `X-Frame-Options: DENY`.

`style-src` carries `'unsafe-inline'` on both copies and cannot do without it: the pages use
React `style={{…}}` props, which browsers govern under `style-src`. `script-src` stays `'self'`
with no inline escape, so script injection is still refused.

Fonts are self-hosted (`@fontsource-variable/geist`) — there is no external font origin to allow.

If you add a call to a **third** origin, the header is what will refuse it, not the meta tag.

---

## MFA re-authentication

`src/components/ReauthDialog.tsx` (`useReauth`) plus `reauthMethods` in `src/auth.ts`.

Every MFA mutation on `/account/mfa/*` — removing a phone, deleting a passkey, confirming TOTP,
regenerating backup codes — requires proof the caller still holds an existing factor. A bearer
token alone is not enough: a stolen session must not be able to swap the second factor.

The flow is **optimistic**:

1. Run the mutation with no proof. Enrolling a *first* factor on an account that has none needs
   none, so that stays a single step.
2. The backend answers `401 {"error":"reauthentication_required","methods":[…]}`.
   `apiFetch` special-cases this 401 — every other 401 clears the token and redirects to login,
   which here would throw away a working session mid-action.
3. `useReauth` opens the dialog offering exactly the methods the **server** listed. A passwordless
   account is never asked for a password.
4. The same action is re-run with `{ current_password }` or `{ totp_code }` in the body.

Two backend behaviours the UI respects: a failed proof charges a rate limiter (429 → the dialog
blocks further attempts rather than extending the block), and a TOTP code that verifies is burned
by the anti-replay cache and can never be sent again. So nothing retries by itself and the input
is cleared after every attempt.

A related server-side guard shows up here as a `409`: turning a project's `require_mfa` off is
refused once, with the count of users it would affect, and needs `confirm_mfa_downgrade: true` on
the retry.

---

## Run and build

```bash
npm ci
npm run build      # tsc -b && vite build → dist/
npm run lint       # eslint .
npm run dev        # vite — see the caveat below
```

**`npm run dev` does not stand alone.** There is no `server.proxy` in `vite.config.ts` and no
`VITE_API_BASE_URL` escape hatch in `src/api.ts`; every call is a same-origin relative path
(`/admin/config`, `/admin/…`, `/org/…`, `/account/…`). Against the Vite dev server on
`localhost:5173` those 404. To work on this SPA against a live backend you need either a
`server.proxy` entry of your own or a reverse proxy putting both on one origin.

The supported loop is the built bundle: `deploy/deploy.sh` runs `npm ci && npm run build` here and
in `frontend/login`, then `docker build` copies both `dist/` trees into `wwwroot/`. Use
`./deploy/setup.sh --dev` for a first install, or `./deploy/deploy.sh --dev` to rebuild and
redeploy an existing one.

---

## Layout

| Path | What |
|---|---|
| `src/auth.ts` | OIDC session, `apiFetch`, `ApiError`, re-auth types |
| `src/api.ts` | every backend call, one function each |
| `src/context/` | `AuthContext`, `ScopeContext` (system / org / project scope), `ThemeContext` |
| `src/components/iam/` | the project's own presentational pieces (`StatCard`, `IamTuple`, `Spark`, …) |
| `src/components/layout/` | `Shell`, `Sidebar`, `Topbar`, `CommandPalette`, the tweaks panel |
| `src/pages/system/` | super-admin scope |
| `src/pages/org/` | organisation-admin scope |
| `src/pages/project/` | project-admin scope |
| `src/pages/account/` | the signed-in user's own account, including MFA |

The three page scopes mirror the three management levels; `ScopeContext` decides which sidebar and
which API prefix a page uses.

---

## Testing

```bash
npm test         # vitest run — 88 tests, ~3s
npm run test:watch
```

Vitest + jsdom + Testing Library. Config lives in the `test` block of `vite.config.ts`; shared
setup is `src/test/setup.ts`. Tests sit next to the code they cover and are typechecked by the
`tsc -b` in `npm run build` (they are not bundled — `vite build` only follows `index.html`).

| File | Tests | Covers |
|---|--:|---|
| `src/components/ReauthDialog.test.tsx` | 23 | The MFA re-authentication contract: no proof on the first attempt, prompt only on `401 reauthentication_required`, `methods` is authoritative, **no auto-retry**, input cleared between attempts, 429 locks the form, focus containment |
| `src/components/layout/CommandPalette.test.tsx` | 19 | Opens via `showModal()` (not `show()`), Escape closes, combobox/listbox keyboard behaviour — `aria-activedescendant`, arrows, Enter — and role gating |
| `src/pages/org/OrgEmail.test.tsx` | 17 | The five SMTP 400 codes and their fallback, plus the assertion that no server-supplied text reaches the screen (the port-scanner defence) |
| `src/auth.test.ts` | 12 | The 401 split — a re-auth 401 must not destroy the session — the one-shot signin-redirect guard, and the `/admin/config` `redirect_uri` origin check |
| `src/contracts.test.ts` | 11 | Markup defects no type checker sees: fabricated chart data, toggles with no switch semantics, an editor that PATCHes defaults after a failed load, unguarded `Number(e.target.value)` |
| `src/theme.test.ts` | 6 | Light and dark declare the same variables, `color-scheme` is set, no colour is pinned outside the two blocks |

Two constraints these tests exist to hold, both from the backend:

- **a failed proof charges a rate limiter**, so the dialog must never retry by itself — the
  re-auth tests assert the call count, not just the rendered output;
- **a verified TOTP code is burned** by the anti-replay cache, so the field is cleared after every
  attempt and the same characters can never be resubmitted.

Two things jsdom cannot do, so they are not claimed here: real Tab containment for a native
`<dialog>` (no top layer, no `inert` — `src/test/setup.ts` shims the missing `HTMLDialogElement`
methods and the tests assert only that the dialog is opened *modally*), and anything needing
layout. Browser-level coverage is the Playwright suite in `tests/e2e/`, which is not run in CI and
is itself unverified in the current tree (`node_modules` absent — see
[`docs/TESTING.md`](../../docs/TESTING.md)).

`AccountPage.tsx` is not rendered directly; its MFA handlers are thin wrappers over `useReauth`,
which is covered through a harness of the same shape. See
`SECURITY-AUDIT-LOG.md` step 28 for
what else was left out and why — and for the bug these tests found in `ReauthDialog.tsx`.

---

## Known dependency advisories

`npm audit` reports **7 high** advisories in this SPA (down from 8 high + 1 low), `react-router`
among them. The remaining fixes need `npm audit fix --force`, i.e. breaking major bumps, which was
judged riskier than the advisories as reached by this SPA. That judgement has not been re-tested
since the SPA was rewritten. Tracked as R-21 / R-03 / T-06 in
[`docs/SECURITY.md`](../../docs/SECURITY.md) §8, the status of record.
