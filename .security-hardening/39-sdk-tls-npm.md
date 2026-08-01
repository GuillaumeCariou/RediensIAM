# 39 — Rust SDK trust store, and the npm advisories in both SPAs

Scope: `sdk/` and `frontend/` only. Nothing under `src/`, `tests/`, `deploy/` or `docs/` was
touched. Nothing was committed.

Two rows of `docs/SECURITY.md` §8 are addressed. Both are now closable, but **one of them was
already stale before I started** and the report says so rather than claiming credit for it.

---

## 1. The Rust SDK ignored the OS trust store

### What was true

`sdk/rust/rediensiam-client/Cargo.toml` had:

```toml
reqwest = { version = "0.12", default-features = false, features = ["json", "rustls-tls"] }
```

In reqwest 0.12, `rustls-tls` is an alias for `rustls-tls-webpki-roots` (`reqwest-0.12.28/Cargo.toml:148`).
The root store was therefore the `webpki-roots` bundle compiled into the binary and nothing else.
The finding is accurate, and I reproduced the exact failure it predicts — see "the test actually
discriminates" below:

```
invalid peer certificate: UnknownIssuer
```

against a server holding a perfectly valid certificate from a CA installed on the host. A
certificate error for what is a configuration fact, exactly as §8 says.

### The change

One line of `Cargo.toml`:

```toml
reqwest = { version = "0.12", default-features = false, features = [
    "json",
    "rustls-tls",
    "rustls-tls-native-roots",
] }
```

**Route chosen: reqwest's `rustls-tls-native-roots` feature, not a direct `rustls-native-certs`
dependency.** The feature *is* `rustls-native-certs` — it pulls in `dep:rustls-native-certs` and
calls `load_native_certs()` (`reqwest-0.12.28/src/async_impl/client.rs:697-731`). Taking it as a
feature rather than a dependency means no hand-written root-store assembly in this crate, so
nothing here can get the merge wrong, and reqwest's own handling of malformed certificates in
native stores (skip and count, fail only if *every* entry was invalid) comes along for free. Writing
that by hand would be more code doing the same thing slightly worse.

### Exactly what the client now trusts that it did not

Both feature flags are on, and reqwest defaults both root sources to enabled
(`client.rs:319-323`). The store assembled at `Client::build()` is, in order:

1. any `add_root_certificate()` roots — none here;
2. the compiled-in `webpki-roots` bundle (`client.rs:692-695`) — **unchanged, still there**;
3. the host's own trust store (`client.rs:697-731`).

So the delta is precisely **item 3, and nothing else**:

| | Before | After |
|---|---|---|
| Mozilla/`webpki-roots` bundle | trusted | trusted |
| `/etc/ssl/certs`, `SSL_CERT_FILE`, `SSL_CERT_DIR` (Unix) | **not consulted** | trusted |
| macOS Keychain, Windows certificate store | **not consulted** | trusted |
| A certificate from a CA in none of the above | refused | **still refused** |

Verification itself is untouched: no `danger_accept_invalid_certs`, no custom verifier, no
hostname-check bypass. Chain building, expiry, hostname and signature checks are the same rustls
code as before, over a larger set of anchors.

**The honest cost.** The host trust store is now load-bearing for this client. Anything an operator
adds there, the SDK will accept — including a corporate MITM proxy CA, which is usually the intent
and occasionally is not. This is the same exposure `curl`, the .NET runtime and every browser have
always had on that host; it is new *for this SDK*. There is no new way to weaken verification and no
new flag that turns it off; the widening is bounded by what the host administrator installs.

`src/lib.rs` gained a `# What this client trusts` section in the module docs saying the above in
four sentences, because an integrator reading rustdoc should not have to read this file.

### Consistency across the three SDKs

I checked rather than assumed. `grep` across all of `sdk/` for
`ServerCertificate|DangerousAccept|HttpClientHandler|SslOptions|RemoteCertificate|rejectUnauthorized|NODE_TLS`
returns **nothing** — no SDK overrides certificate validation anywhere.

| SDK | Transport | Root store | Consulted the platform store before this change? |
|---|---|---|---|
| .NET | `AddHttpClient<RediensIamClient>` (`ServiceCollectionExtensions.cs:38`), default `SocketsHttpHandler`, no handler customisation | OS: OpenSSL store on Linux (which honours `SSL_CERT_FILE`/`SSL_CERT_DIR`), Keychain on macOS, CryptoAPI on Windows | **yes** |
| TypeScript | browser `fetch`, zero dependencies (`src/index.ts:7`) | whatever the browser trusts — the OS store on Windows/macOS/Chrome-on-Linux, NSS on Firefox | **yes** |
| Rust | reqwest + rustls | was: compiled-in bundle only. now: compiled-in bundle **+** host store | **no → yes** |

Rust was the odd one out and no longer is. Note the residual asymmetry, which is real and which I
did not try to erase: .NET and TypeScript trust the platform store *only*, so removing a CA from the
host store removes it from those clients. The Rust client additionally carries the `webpki-roots`
bundle, so a CA that an operator deliberately *removes* from the host store is still trusted here if
Mozilla ships it. Dropping `rustls-tls` to make the three identical would have made the Rust client
fail on a host with no CA bundle at all — a distroless or scratch container, which is a normal way
to ship a Rust service and not a normal way to ship .NET or a browser. I took the floor over the
symmetry, deliberately. Anyone who wants CA-removal to bite in Rust should build with
`default-features = false` and only `rustls-tls-native-roots`.

### Tests

`sdk/rust/rediensiam-client/tests/tls_trust.rs`, one test, both halves, **a real TLS handshake in
both** — a `tokio-rustls` server holding a leaf issued by an `rcgen` CA, and the actual
`RediensIamClient::introspect` on the other end:

- **a client built against a private root connects** — the CA is placed in the host trust store,
  `introspect` returns `active: true` over TLS.
- **an untrusted certificate is still refused** — same server, host store holding a *different*
  valid CA. The call fails, and the assertion requires the failure to be a certificate decision
  (`Error::Transport` whose source chain contains `certificate`/`UnknownIssuer`), not merely any
  error. A connect-refused or a timeout does not pass this test.

The one honest caveat, and it is stated in the test's own module doc rather than only here: the
"host trust store" is reached via `SSL_CERT_FILE`, which is what `rustls-native-certs` reads on Unix
through `openssl-probe` (`rustls-native-certs-0.8.3/src/lib.rs:52,361`, `src/unix.rs:4`) — the same
variable OpenSSL, curl and .NET honour, and the standard way an operator injects a CA into a
container. It exercises the real `load_native_certs()` path without a test needing root to write
`/etc/ssl/certs`. It does **not** exercise the macOS Keychain or the Windows certificate store;
those go through the same `rustls-native-certs` entry point but different platform code, and this
test says nothing about them.

**The test actually discriminates.** I reverted the `Cargo.toml` line and re-ran it:

```
---- private_ca_is_trusted_and_an_unknown_one_is_not stdout ----
thread 'private_ca_is_trusted_and_an_unknown_one_is_not' panicked at tests/tls_trust.rs:129:29:
a host-trusted private CA must validate: transport error talking to RediensIAM: error sending
request for url (https://127.0.0.1:32777/api/introspect) <- error sending request for url
(https://127.0.0.1:32777/api/introspect) <- client error (Connect) <- invalid peer certificate: UnknownIssuer

test result: FAILED. 0 passed; 1 failed
```

That failure text is the reported bug, reproduced. A test that passes both before and after a fix
proves nothing; this one does not.

New dev-dependencies: `rcgen`, `rustls`, `tokio-rustls`, all pinned to the `ring` provider reqwest
already compiles in, so there is no ambiguous-provider panic. `rustls` and `tokio-rustls` were
already in the dependency tree via `hyper-rustls`; only `rcgen` is genuinely new, and only for tests.

### One unrelated fix

`cargo clippy -- -D warnings` failed on a **pre-existing** `manual_contains` lint in
`has_project_role` (`self.roles.iter().any(|r| *r == qualified)` → `self.roles.contains(&qualified)`).
Same semantics, and the reported clippy run below could not otherwise be clean. Flagged because it
is not part of either finding.

---

## 2. npm advisories in both SPAs

### The finding was stale on arrival

§8 says **7 high per SPA**. `npm audit` on the committed lockfiles, before I changed anything:

```
# npm audit report

react-router  7.12.0 - 8.2.0
Severity: high
React Router: RSC Mode CSRF Bypass Allows Action Execution Before 400 Response - https://github.com/advisories/GHSA-qwww-vcr4-c8h2
fix available via `npm audit fix --force`
Will install react-router-dom@7.11.0, which is a breaking change
node_modules/react-router
  react-router-dom  >=7.12.0-pre.0
  Depends on vulnerable versions of react-router
  node_modules/react-router-dom

2 high severity vulnerabilities
```

Identical in both SPAs. **2, not 7.** No lockfile changed between the last report and this one
(`git status` was clean); what changed is the advisory database.

The missing five were the `brace-expansion` cluster (`brace-expansion`, `minimatch`,
`@eslint/config-array`, `@eslint/eslintrc`, `eslint`) under GHSA-mh99-v99m-4gvg. That package is
still in the tree at the same version — `eslint@9.39.4 → minimatch@3.1.5 → brace-expansion@1.1.18` —
and npm no longer flags it. Confirmed directly against the registry rather than inferred from
`npm audit`'s silence:

```
$ curl -s -X POST https://registry.npmjs.org/-/npm/v1/security/advisories/bulk \
    -d '{"brace-expansion":["1.1.18","5.0.9"],"minimatch":["3.1.5"]}'
{}
```

No advisory applies to those versions any more. And dev dependencies *are* being audited — the
audit metadata reports `"dev": 342` of 417 dependencies scanned — so this is not `npm audit`
skipping the devDependency tree.

**So the eslint 9→10 major was not taken, because there is no longer anything to take it for.**
The task's own framing was right that it is dev tooling and cheap to force; the reason not to is
simpler than the risk calculation — the advisory it would close does not exist. Bumping a major and
re-triaging 21 lint errors to fix nothing is cost with no benefit. If GHSA-mh99-v99m-4gvg is
reinstated or re-scoped, this becomes a 20-minute job with a known cost.

### Re-check: is `react-router` reachable in these SPAs?

Re-established from the current source. Marked below is what I re-verified versus what I took from
`15b-sdk-frontend-residuals.md §3`.

**Mode. Re-checked, not cited.** Both SPAs are `createRoot(<App/>)` in `main.tsx`, and `App.tsx`
mounts `<BrowserRouter>` (`admin/src/App.tsx:160` with `basename="/admin"`, `login/src/App.tsx:35`).
Zero hits across `admin/src` and `login/src` for `createBrowserRouter`, `createHashRouter`,
`createMemoryRouter`, `RouterProvider`, `HydratedRouter`, `useLoaderData`, `useActionData`,
`useFetcher`, `useSubmit`, route `loader:`/`action:`, `StaticRouter`, `renderToString`,
`hydrateRoot`, `prerender`, `ScrollRestoration`, `unstable_`, `@react-router/*` or
`react-router.config`. The five `action:` hits are an audit-log field, a `ReauthDialog` callback
prop and a `CommandPalette` icon key; the one `Form` hit is a local `FormState`. Declarative mode,
client-only, no SSR, no data router, no framework mode, no RSC — the earlier report's premise still
holds after everything that has landed since.

**The one advisory that remains** — GHSA-qwww-vcr4-c8h2, RSC-mode CSRF bypass — needs RSC. There is
no RSC. **Not reachable**, and that is a property of the architecture, which the whole-file greps
above re-establish rather than assume.

**GHSA-wrjc-x8rr-h8h6 (backslash open redirect) — re-checked, and the earlier report's wording is
now wrong in one place.** It is fixed by version in 7.18.2 and again in 8.3.0, so it is moot for the
lockfile; I re-walked it anyway because the earlier report explicitly flagged its reasoning as
call-site-dependent and therefore perishable. Every navigation target in both SPAs:

- **admin** — every `navigate()` and every `<Link to=>` begins with a literal absolute segment.
  Bases resolve to literals: `orgBase` is `` `/system/organisations/${orgId}` `` or `/org`
  (`hooks/useOrgContext.ts:18`), `userListBase` and `projectBase` derive from it, `projectUrl()` is
  `/system/organisations/…` or `/project?project_id=…` (`:21-24`), `home` is `defaultPath()` in
  `App.tsx:49`. `CommandPalette`'s `item.to` (`:98`) comes from a hardcoded list of 24 literals
  (`:40-73`). Ids from `useParams` are interpolated only *after* a literal prefix, so a backslash in
  one can never occupy position 0 or 1 — which is what the advisory needs.
- **login** — **the earlier report's "login's are literals" is no longer accurate.**
  `pages/Login.tsx:167` is `` navigate(`/mfa?login_challenge=${challenge}`) ``, and `challenge`
  comes straight off the query string (`:96`). It is still not exploitable — the target starts with
  the literal `/mfa` and the interpolation lands in the query, not the authority — but it is not the
  literal the previous pass described, and anyone re-reading that sentence would have stopped
  looking. This is exactly the perishability the earlier report warned about, arriving on schedule.
- The one genuinely server-supplied target, `redirect_to`, still goes through
  `login/src/safeNavigate.ts`, which bypasses the router entirely and rejects backslashes as its
  *first* check, before any leading-slash reasoning. Unchanged, and its tests still pass.

Conclusion: unreachable, on re-established grounds. But the advisory is fixed by version anyway, so
none of it is load-bearing today.

### What I bumped

**`react-router-dom@7.18.2` → `react-router@8.3.0`, in both SPAs.**

The earlier report closed this row with "nothing to do until react-router ships a forward fix.
Re-check on every upgrade." It has shipped. `react-router@8.3.0` is outside the advisory's
`7.12.0 - 8.2.0` range and its changelog carries "Harden RSC CSRF code paths (#15311)". npm still
offers a *downgrade* to 7.11.0 because `react-router-dom` was **removed as a package in v8** and so
stops at 7.18.2 — `npm view react-router-dom@8.3.0` is a 404. npm's resolver cannot see a forward
fix that lives under a different package name. That is the whole reason this row sat open, and it is
not a reason to accept a downgrade that reinstates eleven closed advisories.

The v8 major, against what these SPAs actually use:

| v8 breaking change | Impact here |
|---|---|
| `react-router-dom` package removed; import everything from `react-router` | The whole diff. 31 import lines, hand-edited one file at a time |
| Minimum React 19.2.7 | Was `^19.2.4` (19.2.4 installed). Floor raised to `^19.2.8`, current latest |
| Minimum Node 22.22.0 | Node 24.4.1 here |
| Packages are ESM-only | Vite 8 / Vitest 4, already ESM. Both build |
| `RouterProvider`/`HydratedRouter` move to `react-router/dom` | Neither is used |
| `future.v8_middleware`, `v8_passThroughRequests`, `v8_trailingSlashAwareDataRequests` flags removed; `hasErrorBoundary` removed; `meta` `data`→`loaderData` | All data/framework mode. None used |
| 8.3.0 changed `href()`/`generatePath()` param encoding | Neither is called. The four `href` hits are DOM property mocks in tests; `NavLink` is a *locally defined* component in `Sidebar.tsx:114`, not react-router's |

All eleven imported symbols — `BrowserRouter`, `MemoryRouter`, `Routes`, `Route`, `Link`,
`Navigate`, `Outlet`, `useNavigate`, `useParams`, `useSearchParams`, `useLocation` — are exported
from `react-router` in 8.3.0; verified by requiring the built package before starting, not by
reading docs.

Every edit was made file by file with an editor. No `sed`, no codemod, no `--force`.

### What I could not bump, and what is left

Nothing. Both SPAs report `found 0 vulnerabilities`. The row can close.

The residual worth writing down is not an advisory but a shape: `react-router` v8 has no
`react-router-dom` to fall back to, so a future advisory on v8 can only be answered by a v8 patch —
there is no sibling package for npm to offer, and `npm audit fix --force` will keep proposing the
7.11.0 downgrade for as long as it can see one. Read its suggestions here with that in mind.

---

## 3. Contract change for integrators — Rust SDK TLS

**This is a behavioural change in a published SDK. Nothing in this repository consumes it, so no
in-repo caller breaks, but an existing integrator must know:**

- **No API changed.** No signature, no type, no `Config` field. Code that compiles against the
  current release compiles against this one.
- **What changed is what the client accepts on the wire.** A TLS certificate chaining to a CA in the
  host's trust store — `/etc/ssl/certs`, `SSL_CERT_FILE`, `SSL_CERT_DIR`, the macOS Keychain, the
  Windows store — is now valid. It previously was not, unless the CA also happened to be in the
  Mozilla bundle.
- **Nothing that was rejected before is now rejected differently, and nothing accepted before is now
  rejected.** The change is strictly additive over anchors. Anyone who was working is still working.
- **An integrator relying on the old behaviour as a control loses it.** If you were treating "this
  client only trusts public CAs" as a property — a deliberately narrow trust base independent of the
  host — that property is gone. The host administrator's CA list is now in your trust path. To get
  the old behaviour back, depend on this crate with reqwest's `rustls-tls-native-roots` off.
- **A new build-time failure mode, narrow.** reqwest returns a builder error if the host store
  contains certificates but *none* of them parse
  (`client.rs:714-731`, "zero valid certificates found in native root store"). An empty or absent
  store is not an error — the webpki bundle carries it. In this SDK that surfaces as
  `Error::Transport` from `RediensIamClient::new`.
- **New transitive dependencies**: `rustls-native-certs`, `openssl-probe`, and per-platform
  `security-framework` / `schannel` / `core-foundation`. Relevant to anyone vendoring or running a
  dependency-licence audit.

The .NET and TypeScript SDKs are unchanged by this step, in behaviour and in API.

---

## 4. Actual command output

```
########## Rust SDK: cargo test ##########
     Running unittests src/lib.rs (target/debug/deps/rediensiam_client-a8b7de1e64f7b3f3)

running 12 tests
test tests::cache_key_is_a_sha256_digest ... ok
test tests::cache_key_is_not_the_token ... ok
test tests::requires_base_url_and_token ... ok
test tests::inactive_has_no_roles ... ok
test tests::role_and_tenant_helpers ... ok
test tests::tenant_roles_do_not_match_across_projects ... ok
test tests::audience_is_required_at_construction ... ok
test tests::an_answer_without_ver_is_refused ... ok
test tests::authorize_sends_the_audience ... ok
test tests::an_authorize_answer_without_ver_is_refused ... ok
test tests::introspect_sends_the_audience ... ok
test tests::base_url_must_be_https_except_on_loopback ... ok

test result: ok. 12 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.10s

     Running tests/tls_trust.rs (target/debug/deps/tls_trust-58fabed7bc409aed)

running 1 test
test private_ca_is_trusted_and_an_unknown_one_is_not ... ok

test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.06s

   Doc-tests rediensiam_client

running 1 test
test src/lib.rs - (line 7) - compile ... ok

test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.04s
```

```
########## Rust SDK: cargo clippy --all-targets -- -D warnings ##########
    Finished `dev` profile [unoptimized + debuginfo] target(s) in 0.10s
```

(Clean. Before the `manual_contains` fix in §1 it was
`error: using contains() instead of iter().any() is more efficient --> src/lib.rs:132:9`,
`error: could not compile rediensiam-client (lib) due to 1 previous error`.)

```
########## npm audit — BEFORE, frontend/admin (identical in frontend/login) ##########
# npm audit report

react-router  7.12.0 - 8.2.0
Severity: high
React Router: RSC Mode CSRF Bypass Allows Action Execution Before 400 Response - https://github.com/advisories/GHSA-qwww-vcr4-c8h2
fix available via `npm audit fix --force`
Will install react-router-dom@7.11.0, which is a breaking change
node_modules/react-router
  react-router-dom  >=7.12.0-pre.0
  Depends on vulnerable versions of react-router
  node_modules/react-router-dom

2 high severity vulnerabilities

To address all issues (including breaking changes), run:
  npm audit fix --force
```

```
########## npm audit — AFTER ##########
frontend/admin: found 0 vulnerabilities
frontend/login: found 0 vulnerabilities
```

```
########## frontend/admin: npm test ##########
 RUN  v4.1.7 /home/guille/Desktop/Workspace/RediensIAM/frontend/admin

 Test Files  4 passed (4)
      Tests  71 passed (71)
   Duration  2.48s
```

```
########## frontend/admin: npm run build ##########
dist/assets/index-OGPytSn1.css                                   59.99 kB │ gzip:  12.72 kB
dist/assets/index-rYUjaHC0.js                                   747.49 kB │ gzip: 199.56 kB

✓ built in 808ms
[plugin builtin:vite-reporter]
(!) Some chunks are larger than 500 kB after minification.
```

```
########## frontend/login: npm test ##########
 RUN  v4.1.7 /home/guille/Desktop/Workspace/RediensIAM/frontend/login

 Test Files  3 passed (3)
      Tests  79 passed (79)
   Duration  2.96s
```

```
########## frontend/login: npm run build ##########
dist/assets/index-CEXU3-Gb.css                                   13.90 kB │ gzip:  3.35 kB
dist/assets/index-CYNL0p5_.js                                   289.41 kB │ gzip: 87.25 kB

✓ built in 183ms
```

71 and 79, matching the stated baselines exactly — I ran both before touching anything and got the
same numbers, so the react-router 8 migration changed no test outcome in either direction.

```
########## npm run lint — unchanged by this step ##########
frontend/admin: ✖ 26 problems (21 errors, 5 warnings)
frontend/login: ✖ 1 problem (0 errors, 1 warning)
```

Pre-existing, and none of it router-related: 10 × `react-refresh/only-export-components`,
4 × `react-hooks/exhaustive-deps`, 3 × `@typescript-eslint/no-unused-vars`, 6 × `react-hooks`
render-phase rules, and in login a single unused `eslint-disable` in `safeNavigate.ts` — a file this
step did not touch. `npm run lint | grep -i router` returns nothing in either SPA.

---

## 5. Wording for `docs/SECURITY.md` §8

Not applied — `docs/` was out of scope for this step. Both rows leave the "Open, with severity"
table entirely.

**Delete this row:**

> | **npm advisories in both SPAs** | High | **7 high per SPA**, down from 8 high + 1 low. `react-router` and `brace-expansion` among them; remaining fixes need `npm audit fix --force`, i.e. breaking major bumps | The forced upgrades were judged riskier than the advisories as reached by these SPAs. That judgement has not been re-tested since the SPAs were rewritten |

**Delete this row:**

> | **Rust SDK ignores the OS trust store** | Low | `rustls-tls`, compiled-in webpki roots | A private-CA deployment will not validate |

**And add, under "Deliberate product decisions, not defects":**

> - **The Rust SDK trusts the host's CA store *in addition to* a compiled-in bundle.**
>   `rediensiam-client` builds reqwest with both `rustls-tls` and `rustls-tls-native-roots`, so its
>   root store is the `webpki-roots` bundle **plus** `/etc/ssl/certs` / `SSL_CERT_FILE` /
>   `SSL_CERT_DIR` / the Keychain / the Windows store. A private-CA deployment — including this
>   project's own `cert-manager` issuer — validates with nothing to configure, and
>   `tests/tls_trust.rs` proves both halves against a real handshake: a host-trusted private CA
>   connects, an unknown CA is still refused. The .NET SDK (`SocketsHttpHandler`) and the browser
>   SDK (`fetch`) have always used their platform's store, so all three now agree on where trust
>   comes from. Two consequences, both accepted: the host administrator's CA list is in the trust
>   path of every RediensIAM Rust client on that host, and — unlike the other two SDKs — removing a
>   CA from the host store does not untrust it here if Mozilla still ships it. The compiled-in
>   bundle is deliberate: it is what keeps the client working in a distroless container with no CA
>   store at all.
>
> - **Neither SPA carries an npm advisory.** `npm audit` reports zero in `frontend/admin` and
>   `frontend/login` as of `39-sdk-tls-npm.md`. The last one, `react-router` GHSA-qwww-vcr4-c8h2,
>   closed by moving to `react-router@8.3.0`; the `brace-expansion` cluster under `eslint` had
>   already left the advisory database on its own, with the same versions still installed. Note for
>   whoever audits next: `react-router-dom` no longer exists as a package above 7.18.2, so
>   `npm audit fix --force` will keep proposing a **downgrade** to 7.11.0 — which reinstates eleven
>   closed advisories — whenever a v8 advisory appears. A v8 advisory can only be answered by a v8
>   patch.

---

## 6. Files changed

| File | Change |
|---|---|
| `sdk/rust/rediensiam-client/Cargo.toml` | `rustls-tls-native-roots` feature; `rcgen`/`rustls`/`tokio-rustls` dev-deps |
| `sdk/rust/rediensiam-client/Cargo.lock` | resolver output |
| `sdk/rust/rediensiam-client/src/lib.rs` | module doc: "What this client trusts"; pre-existing `manual_contains` clippy fix |
| `sdk/rust/rediensiam-client/tests/tls_trust.rs` | **new** — the two handshake cases |
| `frontend/admin/package.json`, `frontend/login/package.json` | `react-router-dom@^7.13.1` → `react-router@^8.3.0`; react/react-dom floor `^19.2.4` → `^19.2.8` |
| `frontend/{admin,login}/package-lock.json` | resolver output |
| 31 source files across both SPAs | `from 'react-router-dom'` → `from 'react-router'`, one line each |

Nothing committed.
