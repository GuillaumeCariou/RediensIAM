# Step 6 — Frontend Security Hardening

**Status:** complete. Written after the fact: the agent doing this step died on a 529 API error on
its last item, having corrupted one file. That corruption was repaired and the last item finished
by hand — see [Incident](#incident-during-this-step) below. Everything reported here was verified
against the tree, not taken from the agent's account.

## Verification (actual output)

| Check | Result |
|---|---|
| `dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true` | **1185 passed, 0 failed, 0 skipped**, 5 m 52 s (baseline entering this step: 1183) |
| `npm run build` in `frontend/login` | ✓ built in 234 ms — Geist woff2 emitted into `dist/assets/` |
| `npm run build` in `frontend/admin` | ✓ built in 1.09 s (pre-existing >500 kB chunk warning, unrelated) |
| `npm test` in `sdk/typescript/rediensiam-web` | **8 pass, 0 fail** |

Diff: 20 files, +310 / −72 across `frontend/`, `sdk/typescript/`, `src/Program.cs`.

---

## Priority 1 — admin console MFA re-auth (R-24 follow-through)

Step 4 required a re-authentication proof on four MFA mutations. The console sent none and got 401
on all four, so it was **broken** for the whole gap between steps 4 and 6.

**Wire contract** (`frontend/admin/src/auth.ts:84`):

```ts
export interface MfaReauth {
  current_password?: string;
  totp_code?: string;
}
```

**Flow, as implemented** (`frontend/admin/src/api.ts`, new `frontend/admin/src/components/ReauthDialog.tsx`):

The proof is **omitted on the first attempt**. The backend answers
`401 {"error":"reauthentication_required","methods":[…]}` naming the methods this account can
actually supply, and only then is the user prompted. That avoids asking for a TOTP code from an
account that has no TOTP factor, and it keeps the decision about acceptable proof on the server.

The four call sites now take an optional `reauth`:

```ts
export async function removePhone(reauth?: MfaReauth)
export async function deleteWebAuthnCredential(id: string, reauth?: MfaReauth)
export async function confirmTotp(body: { code: string }, reauth?: MfaReauth)
export async function regenerateBackupCodes(reauth?: MfaReauth)
```

Note the asymmetry, which is deliberate and matches the backend: `confirmTotp` nests the proof as
`{ ...body, reauth }` because its own payload already carries `code`; the other three send the
proof as the whole body.

**Trap avoidance.** The backend applies anti-replay and rate limiting to the proof, so a retry loop
resending the same TOTP code locks the account out. A failed re-auth is therefore surfaced
distinctly from a failed mutation (`AccountPage.tsx`), and the dialog does not auto-retry.

## Priority 2 — CSP (finding E / R-26)

Admin login was **structurally impossible** before this: `oidc-client-ts` fetches
`{issuer}/.well-known/openid-configuration` before redirecting, the console is served on a NodePort
whose origin can never equal the issuer's, and the meta CSP said `connect-src 'self'`.

The split between the two copies is the important design point. Both must be satisfied, so the
effective policy is their intersection:

- **Header** (`src/Program.cs`, `AddSecurityHeaders`) is the enforcing copy and pins `connect-src`
  to the **exact issuer origin**, because the server knows it at runtime.
- **Meta** (`frontend/admin/index.html:17`) cannot pin it — the issuer is a deployment value and
  the bundle is built once — so it permits `https: http:` and lets the header narrow it.

Admin meta:

```
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self';
img-src 'self' data:; connect-src 'self' https: http:; frame-src 'self'; base-uri 'self';
form-action 'self'; object-src 'none'
```

Login meta (`frontend/login/index.html:14`) is tighter — it has no cross-origin discovery to make,
so `connect-src 'self'`:

```
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self';
img-src 'self' data: https:; connect-src 'self'; frame-src 'self'; base-uri 'self';
form-action 'self'; object-src 'none'
```

Per-directive justification for what changed:

| Directive | Why |
|---|---|
| `connect-src` | the discovery fetch; header pins the issuer, meta cannot |
| `style-src 'unsafe-inline'` | **required by both SPAs** — Radix injects inline styles. Removing it breaks the UI, and pretending otherwise is what produced R-26 |
| `font-src 'self'` | Geist is now **self-hosted** (`package.json`, `index.css`, `tailwind.config.js`) — the Google Fonts dependency and its CSP hole are gone, not allowlisted |
| `frame-ancestors` | **removed from meta**, kept in the header. Browsers ignore it in a `<meta>`; it was noise |
| `img-src data:` | provider icons are now inline data URIs, see below |

`script-src 'self'` was also added to the **login** header branch, which previously omitted it.

## Priority 3 — remaining frontend surface

**External resources eliminated.** The last third-party request on the unauthenticated login page
was a `gstatic.com` Google icon. Both `Login.tsx:48` and `Preview.tsx` now inline all provider
icons as `data:image/svg+xml` URIs. This is why SRI is not used: there is no remaining external
resource to pin, which is strictly better than pinning one.

**Browser SDK role handling** (`sdk/typescript/rediensiam-web/src/index.ts`). Step 4 made tenant
roles `{project_id}/{name}`, so a bare `hasRole('admin')` silently matched nothing. Added:

```ts
hasProjectRole(role: string, projectId?: string): boolean
```

defaulting to the token's own `project_id`, with `hasRole` now documented as management-roles-only.
Test added: *"tenant roles only match when qualified by their own project."* This mirrors
`HasProjectRole` (.NET) and `has_project_role` (Rust); `sdk/README.md` lists all three.

**Step 5 fallout absorbed.** `OrgEmail.tsx` surfaces the five new SMTP 400 codes and no longer
expects the `detail` field step 5 removed from `/org/smtp/test`.

---

## Incident during this step

An agent scripted an edit to `frontend/login/src/pages/Preview.tsx` with a greedy regex. It pasted
a block of `Login.tsx` into the file (`export default function Login()`, `TokenVisual`,
`LoginLogo`) and dropped the `facebook` entry from `PROVIDER_ICONS` — 329 lines where HEAD had 226.
The agent recognised it and was restoring the file when it hit the 529 error, so **the corruption
was left on disk**.

Repair: the file was restored to HEAD (its only legitimate change was one line) and the icon swap
re-applied by hand, preserving the `facebook` entry. Verified identical to HEAD before re-applying,
and both builds plus the full backend suite were re-run afterwards — numbers above.

Worth recording because it is a process finding, not a code one: a scripted regex edit across a
`.tsx` file produced a silent, structurally valid corruption that no test would have caught. The
login SPA has no test suite of its own.

## Not done, and what it would take

- **R-05 is now more exposed, not less.** Chain C-7 from step 2: the console was unreachable, and
  fixing the CSP made it reachable. The admin port is still a NodePort with a self-signed cert.
  **Step 9 must treat R-05 as a prerequisite before any non-local deployment** — this step
  increased the value of attacking it.
- **No frontend test suite.** Neither SPA has tests; only the browser SDK does. The re-auth flow
  and the CSP intersection are therefore verified by build and by reading, not by test. Adding
  Vitest + React Testing Library to `frontend/admin` and covering the re-auth dialog is the
  smallest useful step, roughly half a day.
- **Admin bundle is 751 kB.** Pre-existing, not security-relevant, left alone.
