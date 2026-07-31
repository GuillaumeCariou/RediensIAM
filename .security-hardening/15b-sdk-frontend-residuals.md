# Step 15b — SDK and frontend residuals (R-30, R-31, T-06/R-21/R-03)

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30`
**Scope:** `sdk/` and `frontend/` only. `src/`, `tests/` and `deploy/` were not touched — other
agents own those. Nothing was committed.

These three findings are the ones §10 of the ledger calls "findings no step ever owned": steps 4
and 6 edited `sdk/typescript/rediensiam-web/src/index.ts` (R-28 cache key, R-29 logout,
`hasProjectRole`) and left the adjacent lines alone, and step 6 ran `npm run build`, not
`npm audit`.

## Files changed

| File | Finding |
|---|---|
| `sdk/typescript/rediensiam-web/src/index.ts` | R-30, R-31 |
| `sdk/typescript/rediensiam-web/src/index.test.ts` | R-30, R-31 — 4 new tests |
| `sdk/rust/rediensiam-client/src/lib.rs` | R-30 — 1 new test |
| `sdk/dotnet/RediensIAM.Client/ServiceCollectionExtensions.cs` | R-30 |
| `sdk/README.md` | documents the HTTPS rule, the loopback opt-out and `apiOrigins` |
| `frontend/admin/package-lock.json` | T-06 / R-21 / R-03 (`npm audit fix`) |
| `frontend/login/package-lock.json` | T-06 / R-21 / R-03 (`npm audit fix`) |

Neither SPA's `package.json` changed — every fixed version already fell inside the declared `^`
range, so the lockfiles are the whole diff. No SPA source file was modified.

---

## 1. R-30 — HTTPS required, discovered endpoints validated

### The shape of the finding differs per SDK

The ledger states R-30 as one finding across three SDKs, but only one of them does OIDC discovery.
`rediensiam-client` (Rust) and `RediensIAM.Client` (.NET) are resource-server clients: they POST to
`{base_url}/api/introspect` and `/api/authorize` with a service-account credential. There is no
discovery document to validate. So R-30 splits:

- **all three** — refuse a cleartext transport;
- **browser SDK only** — refuse a discovery document that points off the issuer's origin.

### The loopback opt-out

`http:` is accepted only when the host is `localhost`, `127.0.0.1` or `[::1]`/`::1`. There is
deliberately **no configuration flag** to disable the check. A flag is what gets set in production
"temporarily"; a loopback host is something an attacker on the network path cannot be, so the
exemption cannot be abused off-box. This is stated in each SDK's doc comment and in
`sdk/README.md`.

### Browser SDK — `sdk/typescript/rediensiam-web/src/index.ts`

Constructor now resolves `issuer` (and each `apiOrigins` entry) through a `secureOrigin()` helper
that parses the URL, rejects anything not https-or-loopback-http with `config_invalid`, and keeps
the resulting origin. The issuer origin is stored in `#issuerOrigin`.

`#discover()` no longer stores the parsed document as-is. It walks
`authorization_endpoint`, `token_endpoint` and `end_session_endpoint` and requires each present one
to have exactly `#issuerOrigin`; a missing `authorization_endpoint`/`token_endpoint`, or any
endpoint elsewhere, throws `discovery_failed`. `end_session_endpoint` stays optional (it already
was — `logout()` degrades to a local sign-out without it).

This is the leg the threat model's C-8 argues over: with discovery unvalidated, whoever answers for
the issuer names the token endpoint, and the PKCE `code_verifier` and the refresh token are posted
there. Requiring https closes the downgrade path; requiring the issuer's origin closes the
"answered correctly once, redirected the flow" path.

### Rust — `sdk/rust/rediensiam-client/src/lib.rs`

`RediensIamClient::new` calls a new `require_secure_url(&config.base_url)` after the existing
non-empty checks. It parses with `reqwest::Url` (so a non-absolute `base_url` is now also rejected,
which it previously was not) and matches on scheme + `host_str()`.

### .NET — `sdk/dotnet/RediensIAM.Client/ServiceCollectionExtensions.cs`

`AddRediensIam` now runs `Uri.TryCreate(..., UriKind.Absolute, ...)` and checks
`Scheme != UriSchemeHttps && !(Scheme == UriSchemeHttp && IsLoopback)`, throwing `ArgumentException`
like the two checks above it. `Uri.IsLoopback` already covers `localhost`, the whole `127.0.0.0/8`
range and `::1`.

### Verification

`npm test` (browser SDK) — three new tests:

- `issuer must be https, except on loopback` — `http://auth.example.com` and a bare
  `auth.example.com` throw `config_invalid`; `http://localhost:4444` and `http://127.0.0.1:4444` do
  not.
- `apiOrigins are held to the same scheme rule`.
- `discovery endpoints must sit on the issuer origin` — stubs `fetch`/`location`/`sessionStorage`,
  feeds a document whose `token_endpoint` is `https://evil.example/oauth2/token`, asserts
  `login()` rejects with `discovery_failed`; then feeds a same-origin document and asserts the
  navigation still happens, so the check does not break an ordinary deployment.

`cargo test` — `base_url_must_be_https_except_on_loopback`.

.NET has no test project in this repo (`grep` finds zero references to `RediensIAM.Client` from
`src/` or `tests/`, and no `AddRediensIam` caller outside the SDK's own doc comment). It is verified
by compilation only — see §5.

---

## 2. R-31 — the browser SDK's bearer no longer follows any URL

`RediensIam.fetch()` now resolves the target before doing anything else and throws
`untrusted_target` unless the origin is the app's own or one the app declared in the new
`apiOrigins` config field. The check is a pure exported helper, `isTrustedTarget(target, appOrigin,
apiOrigins)`, matching the file's existing "helpers exported for testing" convention.

It fails closed in three ways that matter: an unparseable target matches nothing; a relative target
with no app origin (outside a browser) matches nothing; and a `Request` object is read through its
own `.url` rather than being trusted as opaque.

`https://cdn.app.example.com` is **not** trusted by `https://app.example.com` — the comparison is on
origin, not on suffix. That is deliberate: a subdomain is a different security boundary, and a
takeover of one is exactly the scenario this stops.

**Verified:** `the bearer only goes to the app origin or a declared api origin` covers same-origin
absolute and relative, a declared api origin, an off-origin URL, the protocol-relative
`//evil.example/collect` form, a subdomain, a `Request`, and the no-app-origin case.

---

## 3. T-06 / R-21 / R-03 — npm advisories

### Before

Both SPAs, identically: **9 vulnerabilities (1 low, 8 high)**. Highs: `react-router`,
`react-router-dom`, `vite`, `undici`, `postcss`, `js-yaml`, `picomatch`, `brace-expansion`. Low:
`@babel/core`.

### `npm audit fix` — what it changed

No `--force`, no major bump, no `package.json` edit. 44 lockfile entries moved in `frontend/admin`;
the relevant ones (identical in `frontend/login`):

| Package | From | To |
|---|---|---|
| `react-router` / `react-router-dom` | 7.13.1 | 7.18.2 |
| `vite` | 8.0.1 | 8.2.0 |
| `undici` | 7.26.0 | 7.29.0 |
| `postcss` | 8.5.8 | 8.5.25 |
| `js-yaml` | 4.1.1 | 4.3.0 |
| `picomatch` | 2.3.1 / 4.0.3 | 2.3.2 / 4.0.5 |
| `brace-expansion` (top level) | 5.0.4 | 5.0.9 |
| `@babel/core` | 7.29.0 | 7.29.7 |
| `rolldown` (transitive of vite) | 1.0.0-rc.10 | 1.2.1 |

I checked every `change` line for a major-version jump; there is none. `rolldown` moving off an
`-rc` tag is inside `vite@8.2.0`'s own dependency range, not a declared dependency of either SPA.

### After

Both SPAs: **7 high, 0 low**. Two clusters remain, and **both need a breaking major bump, so I did
not force either**:

**(a) `brace-expansion` GHSA-mh99-v99m-4gvg via `eslint` → `minimatch@3.1.5` → `brace-expansion@1.1.18`.**
`npm audit fix --force` would install `eslint@10.8.0`. Counts as 5 of the 7 (brace-expansion,
minimatch, @eslint/config-array, @eslint/eslintrc, eslint). This is lint tooling: a devDependency,
not in either bundle, run only by `npm run lint`. The advisory is a DoS on a crafted glob pattern —
the patterns come from `eslint.config.js`, which is ours. **Cost of closing it: an eslint 9→10 major
across both SPAs, which will surface new/renamed rules on ~26 existing lint findings (see §6).**

**(b) `react-router` GHSA-qwww-vcr4-c8h2, "RSC Mode CSRF Bypass Allows Action Execution Before 400
Response"** (affects 7.12.0–8.2.0). The only offered fix is a **downgrade** to `react-router-dom@7.11.0`,
which npm itself labels a breaking change and which would reinstate several of the eleven advisories
just closed. RSC mode is not used — see the re-check below. Not forced.

### Re-check: is `react-router` reachable in these SPAs?

Step 1 downgraded R-03 on the grounds that no reachable sink existed, then step 6 rewrote both SPAs
and nobody re-tested the reasoning. I re-established the premise from the current source rather than
inheriting it.

**Mode both SPAs are in, verified today:** `createRoot` + `BrowserRouter` (`main.tsx`, `App.tsx` in
each). Zero hits across `frontend/{admin,login}/src` for `createBrowserRouter`, `RouterProvider`,
`createMemoryRouter`, `useLoaderData`, `useActionData`, `useFetcher`, route `loader`/`action`,
`StaticRouter`, `renderToString`, `hydrateRoot`, `prerender`, `@react-router/*` or
`react-router.config`. (Three `action=` hits are a `PageHeader` component prop, not a route action.)
So: declarative mode, client-only, no SSR, no data router, no framework mode, no RSC.

Against the twelve advisories on 7.13.1:

| Advisory | Requires | Reachable? |
|---|---|---|
| GHSA-49rj-9fvp-4h2h turbo-stream RCE | SSR / single-fetch | no |
| GHSA-8646-j5j9-6r62 RSC `javascript:` redirect XSS | RSC | no |
| GHSA-f22v-gfqf-p8f3 prerendered `Location` XSS | prerender | no |
| GHSA-8x6r-g9mw-2r78 `__manifest` DoS | framework mode | no |
| GHSA-rxv8-25v2-qmq8 single-fetch DoS | framework mode | no |
| GHSA-84g9-w2xq-vcv6 document-request CSRF | framework mode | no |
| GHSA-h8fp-f39c-q6mh RSCErrorHandler XSS | RSC | no |
| GHSA-337j-9hxr-rhxg `deserializeErrors()` injection | SSR hydration | no |
| GHSA-qwww-vcr4-c8h2 RSC CSRF bypass | RSC | no |
| GHSA-2j2x-hqr9-3h42 `//` open redirect in `redirect()` | loader/action | no |
| GHSA-chx6-hx7r-mcp5 route-matching DoS | route matching | **sink present**, but matching is client-side — the worst case is a user hanging their own tab, not a server DoS |
| GHSA-wrjc-x8rr-h8h6 open redirect via backslash in `<Link>` / `useNavigate` | `<Link>` / `useNavigate` | **sink present** |

**The conclusion holds, but not for the reason step 1 gave.** Ten of the twelve are unreachable
because the SPAs are in declarative mode — a property of the architecture, which step 6 happened not
to change. The last one is different: GHSA-wrjc-x8rr-h8h6's sink *is* present, in both SPAs, since
every page imports `useNavigate` or `<Link>`. I walked every navigation target: admin's are literals
or built from ids already inside the app's own URL space (`projectUrl(p.id)`,
`${orgBase}/service-accounts/${sa.id}`, `item.to` from a static command list); login's are literals.
The one place a redirect target genuinely comes from the server —
`login/src/safeNavigate.ts` — bypasses the router entirely and already rejects backslashes and
off-origin targets itself.

So it was unexploitable because of the *current call sites*, not because of the mode. That is a
weaker guarantee and it needs re-checking on any frontend change that makes a navigation target
user-influenced. It is moot today: 7.18.2 fixes it.

**Recommendation for the ledger:** R-03 can move from OPEN to CLOSED (11 of 12 advisories fixed by
version, the twelfth unreachable and unfixable without a downgrade). T-06/R-21 drop from 8 high to
2 distinct residual advisories, both dev-toolchain or unreachable, both requiring a major bump.

---

## 4. `hasProjectRole` and SPA behaviour after the bump

`hasProjectRole` and `matchProjectRole` are untouched and the browser SDK has zero dependencies, so
nothing in the npm bump can reach them. Their test (`tenant roles only match when qualified by their
own project`) still passes, including the cross-project and bare-management-name cases.

For the SPAs, `npm run build` runs `tsc -b` before `vite build`, so the full route table in each
`App.tsx` is typechecked against react-router 7.18.2's types — that alone would catch a changed
signature on `useNavigate`, `useParams`, `Outlet`, `Navigate` or `BrowserRouter`.

I also ran a throwaway runtime check per SPA under `vitest --environment jsdom` (temporary files,
**deleted afterwards** — neither SPA has a `test` script and I did not add one):

- `frontend/login` — rendered the real `App`, navigated to `/auth/oauth2/error?login_challenge=abc123`,
  asserted the route resolved and `useSearchParams` produced the right `href`. **1 passed.**
- `frontend/admin` — the admin `App` is behind `AuthProvider`/`oidc-client-ts` and mounting it would
  fire the whole page tree, so I exercised the exact primitives it depends on instead —
  `basename="/admin"`, an `Outlet`-based role guard, `:params`, and the `Navigate` fallback — in the
  admin's own dependency tree. **2 passed** (guard allows, guard redirects).

The admin check is therefore on the primitives rather than on the real route table; the real table's
coverage is the typecheck. Stated plainly because it is a weaker check than the login one.

---

## 5. Contract changes for integrators

Three, all in the browser SDK, all additive at the type level but **two change runtime behaviour for
existing callers**:

1. **`RediensIamConfig.apiOrigins?: string[]` — new optional field.** Purely additive.
2. **`RediensIamErrorCode` gains `'untrusted_target'`.** A union widening. Source-breaking only for
   an integrator who wrote an exhaustive `switch` over the code with no `default`.
3. **Behavioural, and the one to announce:**
   - `new RediensIam({ issuer: 'http://…' })` on a non-loopback host now **throws
     `config_invalid` at construction**. It used to work. Anyone running against a cleartext
     staging issuer must move it to https or to a loopback host.
   - `iam.fetch(url)` to any origin other than the app's own now **throws `untrusted_target`**.
     Anyone calling a separate API host must add it to `apiOrigins`. This is the intended breakage —
     it is what R-31 asks for — but it will surface as a runtime error in an integrator's app, not a
     compile error.

Rust and .NET: no signature changed. Both gain a construction-time failure —
`RediensIamClient::new` returns `Err(Error::Config(..))` and `AddRediensIam` throws
`ArgumentException` — for a `base_url`/`BaseUrl` that is cleartext off-loopback or not absolute.
Same announcement applies.

Nothing in this repo consumes any of the three SDKs (`grep` for `rediensiam-web`, `RediensIAM.Client`
and `AddRediensIam` finds only docs and the SDKs themselves), so no in-repo caller breaks.

---

## 6. Left open, with cost

| Item | Why | Cost to close |
|---|---|---|
| `brace-expansion` GHSA-mh99-v99m-4gvg under `eslint` | Needs `eslint@10` major in both SPAs. Dev tooling, not shipped; the glob patterns are ours | ~1–3 h: the major, plus re-triaging ~26 existing lint findings per SPA |
| `react-router` GHSA-qwww-vcr4-c8h2 | Only "fix" is a downgrade to 7.11.0, which reinstates eleven advisories just closed. RSC mode is not used | Nothing to do until react-router ships a forward fix. Re-check on every upgrade |
| Neither SPA has a single test | `vitest`, `@testing-library/react` and `jsdom` are installed in both and there is not one `*.test.tsx`. My smoke checks were temporary and removed. This is why "do the SPAs still work" can only be answered by a typecheck | ~2 h for a `test` script, a vitest config and a routing + auth-guard smoke suite per SPA. Would have made this dependency bump a 30-second question |
| .NET SDK has no test project | The new `BaseUrl` check is verified by compilation only, not by a test. Rust and TypeScript both got real tests for the same rule | ~1 h for an xUnit project asserting `AddRediensIam` throws on `http://` off-loopback and accepts loopback + https. Worth doing — it is the only one of the three R-30 fixes with no executable proof |
| P-06 (audience/tenant binding in the SDKs) | Out of this step's scope; still open as recorded | see ledger §9 #12 |

---

## 7. Actual command output

```
########## TS SDK: npm test ##########
✔ config is validated up front
✔ issuer must be https, except on loopback
✔ apiOrigins are held to the same scheme rule
✔ the bearer only goes to the app origin or a declared api origin
✔ discovery endpoints must sit on the issuer origin
✔ a fresh client is not authenticated
✔ base64url output has no padding or non-url-safe characters
✔ PKCE challenge matches the RFC 7636 test vector
✔ generated verifiers are unique and long enough
✔ claims are read from the ext object Hydra produces
✔ tenant roles only match when qualified by their own project
✔ malformed tokens decode to null rather than throwing
ℹ tests 12
ℹ pass 12
ℹ fail 0

########## TS SDK: tsc --noEmit ##########
TS SDK TYPECHECK EXIT: 0

########## RUST SDK: cargo test ##########
running 7 tests
test tests::cache_key_is_a_sha256_digest ... ok
test tests::inactive_has_no_roles ... ok
test tests::cache_key_is_not_the_token ... ok
test tests::requires_base_url_and_token ... ok
test tests::base_url_must_be_https_except_on_loopback ... ok
test tests::role_and_tenant_helpers ... ok
test tests::tenant_roles_do_not_match_across_projects ... ok
test result: ok. 7 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out

   Doc-tests rediensiam_client
test src/lib.rs - (line 7) - compile ... ok
test result: ok. 1 passed; 0 failed

########## .NET SDK: dotnet build ##########
Build succeeded.
    4 Warning(s)      <- all pre-existing NU1510 "PackageReference will not be pruned"
    0 Error(s)

########## ADMIN SPA: npm run build ##########
vite v8.2.0 building client environment for production...
✓ 1903 modules transformed.
dist/assets/index-B6Jexe5i.css    60.49 kB │ gzip:  12.86 kB
dist/assets/index-BEYjTxXv.js    748.92 kB │ gzip: 199.85 kB
✓ built in 870ms
(chunk-size warning only, pre-existing)

########## LOGIN SPA: npm run build ##########
vite v8.2.0 building client environment for production...
✓ 37 modules transformed.
dist/assets/index-CEXU3-Gb.css    13.90 kB │ gzip:  3.35 kB
dist/assets/index-CsOJnOCM.js    290.70 kB │ gzip: 87.46 kB
✓ built in 168ms
```

### `npm run lint` — not green, and not mine

Not part of the required verification, but I ran it so the state is on record.

- `frontend/login`: **0 errors, 1 warning** (an unused `eslint-disable` in `safeNavigate.ts`).
- `frontend/admin`: **21 errors, 5 warnings** — 10 × `react-refresh/only-export-components`,
  4 × `react-hooks/exhaustive-deps`, 3 × `@typescript-eslint/no-unused-vars`, plus
  `react-hooks/set-state-in-effect`.

These are pre-existing: I modified no file under `frontend/*/src`, and the only eslint-tree change in
either lockfile is a transitive `brace-expansion` under `@typescript-eslint/typescript-estree` — the
rule-providing plugins (`eslint-plugin-react-hooks`, `eslint-plugin-react-refresh`, `eslint`) are at
the same versions as before. Flagging them because they are real, unowned, and would become louder
under the eslint 9→10 bump the remaining advisory wants.

---

## 8. Nothing was committed

`git status` in my scope after this step:

```
 M frontend/admin/package-lock.json
 M frontend/login/package-lock.json
 M sdk/dotnet/RediensIAM.Client/ServiceCollectionExtensions.cs
 M sdk/rust/rediensiam-client/src/lib.rs
 M sdk/typescript/rediensiam-web/src/index.test.ts
 M sdk/typescript/rediensiam-web/src/index.ts
 M sdk/README.md
```

Other modified paths in the tree (`src/`, `tests/`, `deploy/`, `docs/`) belong to the other two
agents and I did not touch them. Ledger §11's outstanding item — committing
`deploy/rediensiam/values.yaml` for finding D — is still outstanding and is not mine to do.
