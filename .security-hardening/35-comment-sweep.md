# 35 — Comment sweep

Branch `security/hardening-2026-07-30`, from `54ace3d`. Scope: `src/`, `sdk/`, `frontend/`,
`tests/`, `deploy/` scripts. `docs/`, `README.md`, `CHANGELOG.md` and `.security-hardening/`
untouched — those *are* documentation.

**The rule applied:** comments that explain *what* the code does are noise; comments that explain
an *intention* or *warn of a consequence* are legitimate. Delete the first, promote the second to
`///` (or JSDoc), keep section separators as-is.

---

## Counts

_(filled in below once every area reported)_

---

## The interpretation that decided most of the sweep

The single call that shaped everything else: **what "promote to `///`" means for a comment that
sits inside a method body.**

`///` is only syntactically available on a type or a member. A large share of this codebase's
load-bearing comments are pinned to *one line inside a body* — and pinned deliberately. The
clearest case is the one the brief names first:

```csharp
// Normalise IPv4-mapped IPv6 (::ffff:10.0.0.1) BEFORE the v6 branch. Without this the
// v6 branch returns early and every private IPv4 range is reachable by writing it in
// mapped form — the address resolves to exactly the same host.
if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
```

That comment exists to stop someone reordering the two statements underneath it. Moving it up into
a `/// <summary>` on `IsPrivateIp` would preserve the words and destroy the function: the next
reader reorders the lines and never scrolls up to the doc block. **Promotion that separates a
warning from the line it warns about is a deletion with extra steps.**

So the policy was:

| Where the rationale sits | Action |
|---|---|
| Above a type or member | Convert `//` → `///`. Real promotion. |
| Inside a body, pinned to a specific line | **Keep in place.** It is category 2 — it survives, which is the point. |
| Anywhere, and it paraphrases the code | Delete. |

This is why the "promoted" count is smaller than the "kept" count by an order of magnitude. Nothing
was lost to it; the alternative would have lost the thing that mattered.

---

## Areas

### `src/` + `sdk/dotnet` — 31 deleted, 12 promoted, ~7 rewritten in place

Comment-only diff, verified mechanically:

```
git diff -U0 -- src sdk | grep -E '^[+-]' | grep -vE '^(\+\+\+|---)' \
  | grep -vE '^[+-]\s*(//|///)' | grep -vE '^[+-]\s*$'
→ (empty)
```

`sdk/dotnet` needed **no deletions at all** — every comment in `RediensIamClient.cs` and
`ServiceCollectionExtensions.cs` is already a reason, and the rest are `── section ──` rules.

#### Deleted (31)

Pure paraphrase. Representative, not exhaustive:

| File | Comment | Why it went |
|---|---|---|
| `AuthController.cs` | `// 1. Check existing social link` … `// 4. Record the social link` | Numbered narration of four statements that already read in order. |
| `AuthController.cs` | `// Already linked?` | The next line is `var alreadyLinked = …`. |
| `AuthController.cs` | `// Require MFA if the user has any factor configured` | The next line is `var hasMfa = …`. |
| `AuthController.cs` | `// Fingerprint: HMAC-SHA256 of "userAgent + /24 subnet"` | The local is already named `subnet`; the HMAC is the next line. |
| `AuthController.cs` | `// Check project password policy`, `// Verify project is still active…` | Restates the call and the guard below it. |
| `OrgController.cs` | `// Strip client secrets before exposing to caller` | The call is `TotpEncryption.StripSecretsFromTheme(…)`. |
| `OrgController.cs` | `// Delete old Keto tuple before updating` / `// Write new Keto tuple` | Restates `DeleteRelationTupleAsync` / `WriteRelationTupleAsync`. The *reason* the order matters is already stated 14 lines above and was kept. |
| `ServiceAccountController.cs` | `// Returns true if the caller has management access to the given SA.` | Restates `CanAccessAsync`. |
| `AuditLogService.cs` | `// Prometheus` | Above `IamMetrics.AuditEvents…`. |
| `AuditLogRetentionService.cs` | `// Purge per-org logs…` / `// Purge system-level logs…` | Restates the two `ExecuteDeleteAsync` blocks. |
| `SocialLoginService.cs` | `// Provider-specific hardcoded endpoints (builtin)` | Above `BuiltinEndpoints`. |
| `SocialLoginService.cs` | `// In-process cache for OIDC discovery documents` | Above `_discoveryCache`. The four lines below it — why it must be a `ConcurrentDictionary` — were kept. |
| `WebhookService.cs` | `// Retry delays: 2s, 8s, 32s` | Above `[2_000, 8_000, 32_000]`. |
| `Program.cs` | `// Admin SPA fallback (client-side routing)` / `// Login SPA fallback` | Above `MapFallback("/admin/…")` and `MapFallbackToFile`. |

#### Promoted to `///` (12)

Every one records something the code cannot say for itself.

| Where | What it preserves |
|---|---|
| `PasswordService.VerifyBackupCode` | **Newly written, not merely moved** — see below. Why `FixedTimeEquals` stays in the caller. |
| `Roles.ManagementLevel` | *Lower value = more privileged.* The enum is compared numerically, so the ordering **is** the authorisation check — renumbering a member silently changes who gets through. |
| `LoginRateLimiter._incrScript` | Why increment+expire is one Lua script: a read-then-write pair lets concurrent attempts share one slot of the budget, and a counter that fails to acquire a TTL locks the principal out for ever. |
| `ProjectController.GetProjectAsync` | H1 — the single chokepoint for tenant scoping. A handler that queries `db.Projects` directly bypasses the only cross-tenant check on the controller. |
| `ProjectController.GetUser` | H2 — the user id is caller-supplied and is matched against *this project's* list, else any project admin reads any user by guessing an id. |
| `ProjectController.AssignRole` | Keto does the authorisation; the project load is what stops the response distinguishing "not allowed" from "no such project" across tenants. |
| `AccountController.Claims` | Why the null-forgiving `!` is safe: `GatewayAuthMiddleware` 401s before the action runs. |
| `HydraService.ValidateJwtAsync` | Why introspection goes to the admin port 4445 rather than JWKS on 4444 (not reachable pod-to-pod), and why the answer is cached. |
| `IamMetrics` ×3 | The `result` label value domains. Dashboards and alert rules match these strings, so adding a value is a contract change. |
| `Program.partial class Program` | It exists only so `WebApplicationFactory<Program>` can name it — otherwise it reads as dead and gets deleted. |

#### Rewritten in place — narration dropped, reason kept

- **`// Unwrap bundle (S107)`** ×4 (`Account`, `Auth`, `Org`, `SystemAdmin` controllers). The brief
  names `// Unwrap bundle` as a delete example, and it is — but `(S107)` is *why the indirection
  exists at all*. Dropping the whole line invites someone to inline the bundle and re-trip the rule.
  Now: `// Bundle forwarders — the constructor takes one aggregate to satisfy S107; see ControllerServices.`
- **`// Query only list IDs`** (`SystemAdminController`) — also a named delete example, and also
  followed by the real reason. Kept the reason, dropped the restatement:
  *"Projecting to ids keeps UserList out of EF's change tracker. Materialising the entities instead
  triggers EF's own cascade, which fails on the org ↔ list circular dependency."*
- **`// Fail fast.`** (`Program.cs`) — the named example again. Deleted that sentence and the one
  after it (both restated by the `LogCritical` line and the exception message directly below), kept
  the half that explains a non-obvious choice: why the exception is *wrapped* rather than rethrown
  bare.
- **`SamlService.CertificateValidationMode`** — strengthened rather than trimmed. The old comment
  said why `None` was chosen; it did not say **not to raise it to `ChainTrust`**, which is the
  actual trap. It now names that, and points at the hand-rolled validity-window checks that
  compensate for what `None` also switches off.
- **`ServiceAccountController`** — `// SuperAdmin may use any list…` documents an *absence* (a
  deliberate fall-through past both guards). Reworded to say so explicitly, because a comment
  describing a branch that is not there reads as debris and gets tidied away.

---

### `deploy/` — 2 deleted, 1 rewritten, ~70 kept, 47 separators

This area was **already compliant**. Almost every `#` in these scripts records a finding ID or a
failure mode, so the sweep was a verification pass rather than a deletion pass. One file of eight
was edited.

- Deleted: `# Resolve internal cluster IP for the public service` — paraphrase, and mildly wrong
  (the block also resolves the admin IP and host).
- Rewritten: the registry-rebind note in `deploy.sh` was attached to `registry_bind_of()`, which
  only *reads* the bind. Its load-bearing half — that `docker rm -f registry` does not lose the
  image layers, because they live in the named volume — was moved onto the branch it actually
  explains.

Kept because they would cost a pentest to rediscover: `R-16` registry loopback bind, `R-06` default
credentials, `R-07` unconditional chmod on the reuse path, `R-15` DSN/`requireSsl` agreement (and
why `requireSsl` is grepped rather than `tls: enabled:` — there are three `tls:` blocks, and
"matching the wrong one is how this kind of check becomes a lie"), `T-04` role split, `P-04` Traefik
deny, `V-02`'s jsonpath-not-jq reasoning ("a security assertion must never pass by accident"),
`V-25`/`V-26` and the note that a future `TrustServerCertificate` "fix" would pass every check and
be worse than the plaintext it replaced, every `D-xx` detection rationale, and the note in
`audit-detections.sh` recording that removing `trust` from `pg_hba.conf` once broke every query
while the script still printed the all-clear.

Not touched: the header blocks in five scripts are **not comments** — each `-h|--help` prints them
with `sed -n '2,Np' "$0"`. Editing above the cut line silently corrupts `--help`.

---

### `frontend/` + `sdk/typescript` — 68 deleted, 41 promoted, ~50 kept

Baselines held: **admin 71 passed, login 79 passed, TS SDK 12 passed**, all three builds clean,
lint counts unchanged (26 pre-existing in admin, 1 in login).

The same body-vs-declaration line was drawn here, arrived at independently: rationale attached to a
declaration became `/** */`; a per-line warning inside a body stayed where it points. `sanitizeCss.ts`
got the most conservative treatment of any file in the repo — its header became JSDoc, but the six
inline `// (n)` step notes stayed in the body, because every one of them records an *ordering*
constraint and hoisting them is precisely how the next person reorders two lines by accident.

Promotions that matter most:

- **`login/src/safeNavigate.ts`** — the open-redirect guard. The backslash check's ordering is now
  stated as a numbered constraint (`/\evil.com` normalises to `//evil.com`, so it must be rejected
  *before* anything reasons about a leading slash), plus a new warning to compare full origins and
  never a hostname prefix or suffix.
- **`login/src/lib/sanitizeCss.ts`** ×4 — the ReDoS rationale on `dropUnsafeRules` ("~20 KB of
  `type password ` was a one-write way to freeze the main thread of every user signing in to that
  tenant — do not restore a regex here").
- **`admin/src/auth.ts`** ×3 — the redirect_uri origin check ("do not relax it to a hostname or
  suffix comparison"), the concurrent-401 PKCE race guard, and — previously undocumented — why
  tokens live in `InMemoryWebStorage` rather than `localStorage`.
- **`admin/src/api.ts`** — the R-24 reauth-proof convention, now stated to cover *every*
  `reauth?: MfaReauth` signature in the file, with "do not drop the parameter to simplify a call site".
- **`admin/src/pages/project/Authentication.tsx`** ×4 — SVG excluded from the MIME allowlist, and
  the `data:` URL branch identified as a *bypass* of the upload limits.
- **`sdk/typescript`** ×7 — constructor validation (scheme and redirect-origin locks), state
  mismatch as the CSRF control, discovery-origin validation, refresh rotation.

A JSX label reading `{/* Project — project_manager (and above) */}` was **deleted rather than
promoted**: `project_manager` is a role name the API has never emitted. Preserving it would have
preserved a lie.

---

## Stale comments found

Two were named in the brief. The sweep found **five more**.

| # | Where | What was wrong |
|---|---|---|
| 1 | `src/Middleware/GatewayAuthMiddleware.cs:58` | *(named in brief)* Referenced `ClaimsExtensions.GetManagementLevel`, deleted. Repointed to `GetGrantedLevel`, with the "answers null until a live check has run" behaviour that makes the reference useful. |
| 2 | `tests/…/Regression/StructuralDebtTests.cs` | *(named in brief)* Docstring said `GetManagementLevel` "is private". It is gone. |
| 3 | **`src/Controllers/SamlController.cs:71`** | `// Store request ID in session to validate InResponseTo on ACS`. The code stores it in **Redis** (`pending.StorePendingAsync`), and the file's own header explains that the session approach *was the bug* — SameSite=Strict meant the cookie never arrived on the IdP's cross-site POST, so every SAML login failed. The comment invited a future reader to restore the exact defect. Deleted. |
| 4 | **`src/Services/LoginRateLimiter.cs:12`** | `// Atomically increments counter and returns true if the new count >= max.` The Lua script returns the **count**, not a bool — that is the caller's interpretation. Corrected during promotion. |
| 5 | **`frontend/admin/src/context/AuthContext.tsx:118`** | `eslint-disable-line react-hooks/exhaustive-deps` suppresses nothing; ESLint now reports the directive itself as unused. Verified pre-existing. |
| 6 | **`frontend/login/src/safeNavigate.ts:52`** | `eslint-disable-next-line no-console` is dead — `no-console` is not enabled in that package's config. The `console.error` should stay; only the directive is dead. |
| 7 | **`frontend/admin/src/App.tsx`** | `{/* Project — project_manager (and above) */}` names a role the API has never emitted. |

## Suppressions that had no reason

The brief is explicit that a suppression without its reason is worse than the warning. Three were
bare; all three now carry one, and none was invented — each was derived from the code and verified.

- **`src/Data/RediensIamDbContextFactory.cs`** — `#pragma warning disable S2068` sat over a
  hardcoded `Password=postgres`. Now records that the literal is a local-only fallback for
  `dotnet ef migrations`, which needs a connection string to build the model and never connects
  with it in a deployment.
- **`deploy/setup.sh`** — `# shellcheck disable=SC2086` over `${UPGRADE_ARG}`. The unquoted
  expansion is **deliberate**: the variable is either empty or the single flag `--upgrade`, and
  quoting it would pass an empty positional argument to `deploy.sh` on every non-upgrade run. That
  intent was recorded nowhere. Now it is, with the upgrade path (make it an array before it ever
  carries a second word).
- **`sonar-scan.sh`** — `# shellcheck disable=SC1090`, non-constant source.

Both shell reasons are written on the line *above* the directive, not trailing it: shellcheck parses
the remainder of a directive line and trailing prose can trip SC1107.

## Other defects found while in there

- **`deploy/setup.sh` and `deploy/reset-dev.sh` printed a stray `set -uo pipefail` from `--help`.**
  Both used `sed -n '2,18p' "$0"`, but their header blocks end at lines 17 and 16. Fixed to `2,17p`
  and `2,16p`. (`preflight.sh`, `verify-deployment.sh` and `audit-detections.sh` were already
  correct — checked, not assumed.)
- **`frontend/login/src/lib/sanitizeCss.ts` is treated as a binary file by git.** It contains a
  literal NUL byte at line 31 — the replacement string the hex-escape neutraliser substitutes in.
  This is **pre-existing at `54ace3d`**, not caused by the sweep, and it means `git diff` on the
  CSS sanitiser shows only `Binary files … differ` unless `--text` is passed. For a security
  control whose whole value is that reviewers can read it, that is a meaningful hazard. Writing the
  byte as the escape `'\0'` would be byte-identical at runtime and restore diffability — **not
  done here**, because changing a sanitiser's replacement string is a code change that deserves its
  own review and test run, not a line in a comment sweep.
- **`frontend/admin`** still has a variable named `isProjectManager` carrying the dead
  `project_manager` role name. Renaming is a code change; flagged, not done.

