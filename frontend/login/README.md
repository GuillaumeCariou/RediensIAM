# RediensIAM — Login SPA

The end-user authentication surface. Everything a person sees while proving who they are: sign-in,
registration, the MFA challenge, MFA enrolment, password reset and set-password.

React 19 + TypeScript + Vite, `react-hook-form` + `zod` for the forms, `@tanstack/react-query` for
the fetches. No component library — the styling is the project's own CSS, because this page renders
tenant branding and every extra dependency is another thing loaded on the most-visited unauthenticated
page in the deployment.

> **This SPA has no tests.** See [Testing](#testing) — the tooling is installed and unused.

---

## Where it runs

The bundle is served **by the backend**, not by a separate web server:

| | |
|---|---|
| Vite `base` | default (`/`) |
| Built into | `wwwroot/` in the container image (`Dockerfile`, stage 1 → stage 3) |
| Reached at | `https://<host>/login`, `/mfa`, `/register`, … |

This is the **public** surface. Unlike `/admin`, none of these paths are in the chart's
`ingress.public.adminOnlyPaths`, and they are reachable from the internet by design.

### Routes

`/login` · `/mfa` · `/mfa-setup` · `/password-reset` · `/register` · `/set-password` ·
`/preview` (theme preview for the admin console) · `/auth/oauth2/error`. Anything else redirects
to `/login`.

---

## The CSP arrangement

Two policies apply and a request must satisfy **both** — browsers enforce the intersection.

| | Where | What |
|---|---|---|
| **Header** (enforcing copy) | `src/Program.cs`, `AddSecurityHeaders`, non-`/admin` branch | `connect-src 'self'`, `img-src 'self' data: https:`, `frame-ancestors 'none'` |
| **Meta** | `index.html` | the same directives, mirrored |

Unlike the admin console, this SPA's meta policy **can** mirror the header almost exactly: it only
ever calls the origin that served it, so `connect-src 'self'` is correct everywhere and needs no
deployment value baked in. What the meta tag still cannot carry is `frame-ancestors` — browsers
ignore that directive in a meta tag — so the header carries it, alongside `X-Frame-Options: DENY`.

`img-src` allows any HTTPS origin on both copies: the login page renders tenant branding — a
project logo and social-provider icons, both remote URLs the operator does not control. Images
execute nothing.

`style-src` carries `'unsafe-inline'` and cannot do without it: this page renders the tenant's
`custom_css` into a `<style>` element. `script-src` stays `'self'` with no inline escape, so
script injection is still refused. The CSS sink itself is guarded server-side by
`LoginThemeValidator` (a real parser), which is what makes widening `style-src` acceptable (C-6).

`src/lib/sanitizeCss.ts` is a **second, best-effort** client-side pass over the same value — it
strips comments and hex escapes, drops every `@`-rule, rewrites every `url(...)` to
`url(about:blank)`, and drops rules mentioning `password`/`attr(`. It exists to shrink the blast
radius of a server-side regression. It is regex-based and explicitly not sufficient on its own;
the server validation is the control.

---

## Redirects

`src/safeNavigate.ts`. Every navigation driven by a `redirect_to` in an API response goes through
`isSafeRedirect` first, which allows only the SPA's own origin plus the Hydra public origin when
`VITE_HYDRA_PUBLIC_ORIGIN` is set. Backslashes are rejected outright (browsers normalise
`/\evil.com` into a protocol-relative `//evil.com`), and non-`http(s)` schemes are rejected.

A refused redirect returns `false` and the caller shows a generic "could not complete sign-in"
message rather than navigating. Do not add a `location.assign` that bypasses this.

---

## The MFA flow

Two distinct things live here, and they are not the same as the admin console's re-authentication
dialog:

**`/mfa` — the challenge (`MfaChallenge.tsx`).** Presented after a password succeeds when the
account or project requires a second factor. Four methods, offered only if the account has them:
authenticator app (TOTP), security key (WebAuthn), text message (SMS OTP), and a backup code. On
success the page follows the server's `redirect_to` through `safeNavigate` back into the OAuth2
flow.

**`/mfa-setup` — enrolment (`MfaSetup.tsx`).** Reached when a project requires MFA and the account
has none yet.

Two backend behaviours worth knowing when working on either:

- **`POST /account/mfa/phone/setup` answers `503 {"error":"sms_provider_not_configured"}`** where
  the deployment has no SMS provider wired up — the provider is a stub. Offering SMS in the UI
  does not mean the deployment can send one; handle the 503 rather than treating it as a
  transient failure.
- A TOTP code that verifies is burned by the anti-replay cache and can never be sent again, and
  failed attempts charge a rate limiter (429). Nothing should retry by itself.

The admin console's *re-authentication* prompt — proving you still hold a factor before changing
one — is a different flow in a different SPA; see [`../admin/README.md`](../admin/README.md).

---

## Run and build

```bash
npm ci
npm run build      # tsc -b && vite build → dist/
npm run lint       # eslint .
npm run dev        # vite — see below
```

`npm run dev` is usable if you point it at a backend: `src/api.ts` reads
`VITE_API_BASE_URL` (defaulting to `''`, i.e. same-origin), so

```bash
VITE_API_BASE_URL=https://auth.example.com \
VITE_HYDRA_PUBLIC_ORIGIN=https://auth.example.com \
npm run dev
```

will drive a remote backend — subject to that backend's CORS configuration
(`App__AdminSpaOrigin` is what `Program.cs` allows). Without those variables the dev server serves
the SPA and every API call 404s against Vite itself.

The supported loop is the built bundle: `deploy/deploy.sh` runs `npm ci && npm run build` here and
in `frontend/admin`, then `docker build` copies both `dist/` trees into `wwwroot/`. Use
`./deploy/setup.sh --dev` for a first install, or `./deploy/deploy.sh --dev` to rebuild and
redeploy an existing one.

---

## Layout

| Path | What |
|---|---|
| `src/api.ts` | every backend call; `BASE` comes from `VITE_API_BASE_URL` |
| `src/safeNavigate.ts` | the open-redirect guard — all navigation goes through it |
| `src/lib/sanitizeCss.ts` | client-side second pass over tenant `custom_css` |
| `src/useTheme.ts` | applies the project's theme (colours, logo, custom CSS) |
| `src/pages/` | one file per route |

---

## Testing

```bash
npm test         # vitest run — 79 tests, ~3s
npm run test:watch
```

Vitest + jsdom + Testing Library. Config lives in the `test` block of `vite.config.ts`; shared
setup is `src/test/setup.ts`. Tests sit next to the code they cover and are typechecked by the
`tsc -b` in `npm run build`.

| File | Tests | Covers |
|---|--:|---|
| `src/lib/sanitizeCss.test.ts` | 36 | The P-03 control: keylogger selectors, `url()`, `attr(`, at-rules, nesting, malformed input, `safeCssValue`'s refusals and its 120-character ceiling — **and a timing bound** |
| `src/pages/Login.test.tsx` | 29 | The form's happy path, every error state, the MFA handoffs, hostile `redirect_to`, and tenant theming end to end |
| `src/safeNavigate.test.ts` | 14 | Backslash smuggles, protocol-relative URLs, non-http schemes, userinfo and lookalike hosts, and both halves of `safeNavigate`'s contract |

Two things worth knowing before changing these:

- **`sanitizeCss` has a cost budget, not just a correctness one.** Six 16 KB hostile payloads must
  each finish in under 250 ms, and growing the input tenfold must not cost more than 60x. The four
  regexes replaced by the current linear scan were cubic on input containing no `{` — SonarQube
  measured 69 s of CPU for 4 KB, on the unauthenticated login page. If a regex goes back into that
  file, these are the tests that will tell you.
- **The keylogger tests do not grep the output string.** They apply the sanitiser's result to a
  real `<style>` node and ask a real `<input type="password">` whether any surviving rule matches
  it. That is the property that matters; a string assertion is a proxy for it and a leaky one.

`Login.test.tsx` deliberately does **not** mock `safeNavigate` — refusing a hostile `redirect_to`
is part of what the login form has to do, so four hostile values are asserted to produce an error
message and no navigation at all.

Not covered: `MfaChallenge`, `Register`, `PasswordReset`, `SetPassword`, `MfaSetup` — none were
touched by the security audit. Browser-level coverage is the Playwright suite in `tests/e2e/`,
which is not run in CI and is itself unverified in the current tree (`node_modules` absent — see
[`docs/TESTING.md`](../../docs/TESTING.md)).

See [`.security-hardening/28-frontend-tests.md`](../../.security-hardening/28-frontend-tests.md)
for the full rationale, and for two known deviations in `sanitizeCss.ts` that are pinned by tests
rather than fixed.

---

## Known dependency advisories

`npm audit` reports **7 high** advisories in this SPA (down from 8 high + 1 low), `react-router`
among them. The remaining fixes need `npm audit fix --force`, i.e. breaking major bumps, which was
judged riskier than the advisories as reached by this SPA. That judgement has not been re-tested
since the SPA was rewritten. Tracked as R-21 / R-03 / T-06 in
[`.security-hardening/14-finding-ledger.md`](../../.security-hardening/14-finding-ledger.md) §9.
