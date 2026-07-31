# 34 — Dead code sweep

Branch `security/hardening-2026-07-30`, from `d4cfb31`. Scope: `src/`, `sdk/`, `frontend/`, `tests/`.
`deploy/` and `docs/` untouched (owned by another agent — the `README.md` / `docs/*.md` /
`docs/DIAGRAMS.md` / `.security-hardening/30-diagrams.md` entries in `git status` are theirs, not mine).

**Result: 39 files touched, ~1340 lines removed, 0 build warnings, suite green after every batch.**

---

## Method

The brief was right that a name-grep sweep is useless here, and I confirmed it rather than
assuming it — see [§5.1](#51-the-naive-type-sweep-23-hits-0-real). Everything below rests on a
tool that models the language, not on a text match:

| Surface | Tool | How it was run |
|---|---|---|
| `src/`, `tests/`, `sdk/dotnet` | Roslyn IDE analyzers | temporary root `.editorconfig` promoting IDE0005/0051/0052/0059/0060, CA1823, CS0169/0414/0649 to `warning`; `dotnet build -p:EnforceCodeStyleInBuild=true -p:GenerateDocumentationFile=true`. **The `.editorconfig` was deleted before the final build** — it is not in the diff. |
| `frontend/admin`, `frontend/login` | `knip` via `npx` | `npx knip --no-exit-code`, before and after |
| all TS | `tsc` | both frontends already build with `noUnusedLocals`; it caught an orphaned import mid-edit |
| `sdk/rust` | `cargo clippy --all-targets` | Rust's `dead_code` lint |
| `sdk/typescript` | `tsc --noUnusedLocals --noUnusedParameters` | |
| config keys | grep for readers of each key declared in `appsettings.json` | |

Deletions went out in three batches; the .NET suite ran to completion after each.

| Batch | Content | .NET suite |
|---|---|---|
| baseline | — | **1346 + 11 passed, 0 failed** |
| 1 | `src/` — 18 unused usings, 1 redundant assignment | **1346 + 11 passed, 0 failed** |
| 2 | `tests/` — 4 dead members, 2 dead parameters, 1 dead package | **1346 + 11 passed, 0 failed** |
| 3 | `frontend/`, `tests/e2e` (no .NET impact) | **1346 + 11 passed, 0 failed** |
| final | full rebuild + full suite | **1346 + 11 passed, 0 failed — 0 warnings** |

Frontend verification for batch 3: `admin` build ✔, 71 tests ✔, lint unchanged (26 pre-existing
problems, none in a file I touched); `login` build ✔, 79 tests ✔, knip now reports **nothing at all**.

---

## 1. Removed from `src/` (batch 1)

### 1.1 Unused `using` directives — 18 across 16 files

Compiler-verified: an unused import cannot change semantics, and removing a *used* one fails the
build. All were reported by IDE0005.

| File | Removed |
|---|---|
| `src/GlobalUsings.cs` | `global using Microsoft.AspNetCore.Authorization;` |
| `src/Controllers/IntrospectionController.cs` | `RediensIAM.Models` |
| `src/Controllers/OrgController.cs` | `System.Text`, `System.Text.Json` |
| `src/Middleware/GatewayAuthMiddleware.cs` | `System.Text.Json`, `RediensIAM.Models` |
| `src/Data/Entities/AuditLog.cs` | `System.Net` |
| `src/Data/Entities/Organisation.cs` | `System.Text.Json` |
| `src/Data/Entities/Project.cs` | `System.Text.Json` |
| `src/Services/ControllerServices.cs` | `RediensIAM.Services` (self-import) |
| `src/Services/GrantedLevel.cs` | `RediensIAM.Models` |
| `src/Services/HydraService.cs` | `RediensIAM.Models` |
| `src/Services/LiveAuthorizationService.cs` | `RediensIAM.Models` |
| `src/Services/LoginRateLimiter.cs` | `RediensIAM.Exceptions` |
| `src/Services/PatService.cs` | `RediensIAM.Models` |
| `src/Services/SamlService.cs` | `System.Net` |
| `src/Services/SocialLoginService.cs` | `Microsoft.AspNetCore.Hosting` |
| `src/Services/WebhookService.cs` | `System.Net` |

The `global using Microsoft.AspNetCore.Authorization` deserves a note, because `[Authorize]`
appears all over the controllers and it *looks* load-bearing. It is not: ASP.NET Core's
`Microsoft.NET.Sdk.Web` implicit-usings set already contributes that namespace, so the explicit
global using was a duplicate. This is the one case where I did not trust the analyzer alone —
but removal is self-verifying (a genuinely needed global using produces a wall of CS0246), and
the build stayed at 0 errors.

The eight per-file `using RediensIAM.Models;` were redundant against the surviving
`global using RediensIAM.Models;` on line 1 of `GlobalUsings.cs`.

### 1.2 `src/Services/PasswordService.cs` — redundant out-param initialisation

In `TryResolveBackupCodeKey`, `storedHex = "";` (IDE0059) was dead: every path that returns —
including the fail-closed `return false` inside the `colon > 0` branch — assigns `storedHex`
first. Removed. The sibling `keyForHash = [];` was **not** removed and was **not** flagged: it
genuinely is needed, because `keyForHash` is *not* assigned before that same early `return false`.
The asymmetry is meaningful, not sloppiness, and it is now the only initialiser left.

---

## 2. Removed from `tests/` (batch 2)

All six were reported by IDE0051/IDE0052/IDE0060 — the categories the brief called reliable.

| Item | File | Evidence |
|---|---|---|
| `private static readonly Faker Faker` | `Infrastructure/SeedData.cs` | IDE0052 (assigned, never read) |
| `using Bogus;` | `Infrastructure/SeedData.cs` | only Bogus reference in the project |
| `<PackageReference Include="Bogus" Version="35.*" />` | `RediensIAM.IntegrationTests.csproj` | ditto — the dependency existed solely for that one dead field |
| `private static TokenClaims Claims(...)` | `Tests/Regression/StructuralDebtTests.cs` | IDE0051 |
| `private async Task<(User, HttpClient)> ScaffoldAsync()` | `Tests/Account/AccountBranchCoverageTests.cs` | IDE0051 |
| `private readonly IDistributedCache _cache` | `Tests/Services/SocialLoginServiceTests.cs` | IDE0052; `BuildSvc` constructs its own local cache, and `Dispose` only disposes `_server` |

Two dead parameters on private test helpers, removed with their call sites (compiler-verified,
each contained to one file):

- `SamlTests.BuildSignedResponseForm(string challenge, …)` — `challenge` unused; the method
  already takes `authnReqId` for the `InResponseTo` value. Dropped at the declaration and 3 call sites.
- `SocialLoginCoverageTests.GetOAuthStateAsync(Project project, …)` — `project` unused; the URL is
  built from `challengeId` alone. Dropped at the declaration and 5 call sites.

---

## 3. Removed from `frontend/` and `tests/e2e` (batch 3)

### 3.1 `frontend/admin` — 8 dead files

knip "Unused files", each independently confirmed to have zero importers by grep before deletion:

```
src/App.css                        src/components/iam/HealthRow.tsx
src/components/ui/avatar.tsx       src/components/iam/IamTuple.tsx
src/components/ui/scroll-area.tsx  src/components/iam/Spark.tsx
src/components/ui/textarea.tsx
src/components/ui/tooltip.tsx
```

The three `iam/` components were reachable only through `components/iam/index.ts`, which nothing
consumed for those names — so their `index.ts` re-export lines went too. Also removed
`IamAvatarStack` from `IamAvatar.tsx` (the default export `IamAvatar` **is** live — used by
`pages/system/Organisations.tsx` and `pages/system/Users.tsx` — only the named `Stack` export was
dead), plus the `ReactNode` type import that it alone used. That last one I did not spot myself:
`tsc` failed with `TS6133: 'ReactNode' is declared but its value is never read`, which is exactly
the kind of evidence the brief asked me to prefer over my own reasoning.

### 3.2 `frontend/admin/src/api.ts` — 13 dead endpoint wrappers

Each was a 3-line `apiFetch` wrapper whose name appeared exactly once in the whole app (its own
declaration):

```
disableUser  enableUser  forceLogoutUser  deleteUserList  getProject
createProjectUser  forceLogoutProjectUser  listSaRoles  updateOrgListManager
listAdminOrgProjects  adminListRoles  adminCreateRole  adminDeleteRole
```

`api.ts` is an internal module of a private app, not a published package, so this is not a
contract change. The backend endpoints themselves are untouched — only the unused client wrappers
went. The now-inaccurate `// ── Admin-scoped role management ──` section header went with the
three `adminRole` functions it described.

### 3.3 Dead dependencies — 12 packages

Zero references anywhere in `src/`, config or HTML. Worth calling out in a *security* audit
specifically: these are supply-chain surface that bought nothing.

- `frontend/admin` (7 deps + 1 devDep): `@radix-ui/react-avatar`, `@radix-ui/react-popover`,
  `@radix-ui/react-scroll-area`, `@radix-ui/react-tooltip`, `@tanstack/react-table`, `date-fns`,
  `recharts`, `@tailwindcss/forms`
- `frontend/login` (4): `@hookform/resolvers`, `@tanstack/react-query`, `react-hook-form`, `zod`

`recharts` is the notable one — `iam/ActivityChart.tsx` renders a chart by hand and imports only
`react`. Both lockfiles were regenerated with `npm install`; both apps build and test green.

### 3.4 `frontend/login/src/api.ts`

`getLoginTheme` — exported, imported by nothing, not used in its own file either.

### 3.5 `tests/e2e/fixtures/mock-api.ts`

`mockAdminConfig` and `mockError` — exported helpers with zero consumers across the entire e2e
tree. Removing them orphaned nothing: `toGlob`, `RoutePattern` and the `Page` import all remain in
use by the four live `mockGet`/`mockPost`/`mockPatch`/`mockDelete` helpers.

**Caveat, stated plainly:** the Playwright suite needs a live deployment and could not be executed
here (its `node_modules` are not even installed, so `tsc` could not be run either). Verification for
these two was static only — an exhaustive grep of the e2e tree, which is conclusive for static ESM
imports but is weaker evidence than everything else in this report. They are test fixtures, so the
blast radius is a red e2e run, never production.

---

## 4. Left deliberately

### 4.1 `LoginRateLimiter.ResetAsync(string ipAddress, …)` — IDE0060, **left**

`src/Services/LoginRateLimiter.cs:57`. The parameter is genuinely never read. It is also
**deliberately** never read, and that is the point of the method:

> The per-IP counter is deliberately NOT cleared: it is shared across every account targeted from
> that address, so clearing it would let an attacker holding one valid account reset the budget at
> will and brute-force other accounts indefinitely from the same IP.

That XML doc is a security fix's rationale, and `AuthHardeningRegressionTests.cs:154` asserts the
behaviour. Removing the parameter would make three call sites read `ResetAsync(user.Id)` — losing
the visible signal that the IP is in hand and consciously not acted on, and making a future
"cleanup" that clears the IP counter look harmless. The one-line cost of a dead parameter buys a
standing reminder. **Left; recommend suppressing IDE0060 here rather than removing it.**

### 4.2 `SamlService.BuildConfigAsync(…, Uri acsUrl)` — IDE0060, **left, and worth a look**

`src/Services/SamlService.cs:19`. `acsUrl` is unused; the method builds a `Saml2Configuration` from
`spEntityId` and the IdP config and never touches it. Both live callers
(`SamlController.cs:57` and `:153`) pass a real `AcsUrl`.

I did not remove it because I cannot tell whether it is dead code or **a missing check**. In SAML,
the assertion's `Destination` / `SubjectConfirmationData@Recipient` are supposed to be validated
against the SP's ACS URL; a config object built without it may simply not be enforcing that. If so,
deleting the parameter erases the only remaining evidence of the gap. This is a security question,
not a tidiness one — **flagged for whoever owns the SAML surface**, not deleted.

### 4.3 `src/Data/Migrations/20260531100400_AddInstanceConfig.cs` — unused `using System;`

IDE0005, real, but the file is EF-generated and is a historical record. `dotnet ef` will re-add it
on the next scaffold. Not worth fighting the generator. **Left.**

### 4.4 ~44 × IDE0059 in the test project — **left**

Every one is a tuple deconstruction where one element is unused:

```csharp
var (org, orgList) = await fixture.Seed.CreateOrgAsync();   // 'org' unused here
```

The fix is `var (_, orgList) = …` across ~30 files. That is a 40-file diff of pure cosmetics with
no dead *code* removed — the call still has to happen, only the name changes. It is normal test-helper
usage, not residue left by the audit. **Left; if it is ever wanted, it is mechanical and safe.**

### 4.5 27 unused `shadcn/ui` sub-exports + 2 exported types — **left**

`AlertDialogPortal`, `DialogOverlay`, `DropdownMenuSub…`, `SelectScrollUpButton`, `TableCaption`,
`BadgeProps`, `ButtonProps`, and 20 more. These live inside files that *are* used, and they are the
standard vendored shadcn component surface — deleting individual members makes the files diverge
from upstream, so the next `shadcn add` or manual sync silently reintroduces them. Churn with a
negative half-life. **Left.**

(The four whole `ui/` files I *did* delete are a different case: nothing in them was used at all.)

### 4.6 Four over-exported admin symbols — **left**

`ROLE_SUPER_ADMIN`, `ROLE_ORG_ADMIN`, `ROLE_PROJECT_ADMIN` (`context/AuthContext.tsx`) and
`resolveDataTheme` (`context/ThemeContext.tsx`). knip reports them as unused *exports*, which is
accurate but misleading: all four are used inside their own file. The change would be dropping the
`export` keyword, not deleting code. Zero dead code recovered. **Left.**

### 4.7 Public SDK surface — **not touched, by rule**

`sdk/typescript/rediensiam-web`, `sdk/dotnet/RediensIAM.Client`, `sdk/rust/rediensiam-client` all
published at 0.2.1. Their exports are the contract, so "nothing in this repo imports it" is not
evidence of death. I checked them for *internal* dead code instead, and found none:

- **Rust**: `cargo clippy --all-targets` → **zero `dead_code` warnings**. One style suggestion only
  (`using contains() instead of iter().any() is more efficient`) — a performance nit, not dead code,
  and out of scope for this sweep.
- **TypeScript**: `tsc --noEmit --noUnusedLocals --noUnusedParameters` → clean, exit 0.
- **.NET**: covered by the solution-wide analyzer run; no IDE0051/0052/0060/CA1823 findings.

### 4.8 OTP grid duplication — **characterised, not extracted**

The audit note is accurate. `frontend/login` has the six-digit OTP entry grid three times:

| File | Form |
|---|---|
| `src/pages/PasswordReset.tsx:35-80` | extracted as a local `OtpGrid` component (not exported) |
| `src/pages/MfaChallenge.tsx:43-44, 293-305` | inlined — own `OTP_CELL_IDS`, own change/keydown/paste handlers |
| `src/pages/MfaSetup.tsx:10-11, 227-240` | inlined — likewise |

But this is **duplicated live code, not dead code** — no copy is superseded; all three render. The
extraction is a real improvement and the target already exists (lift `OtpGrid` to a shared
component and import it in all three). I did not do it here for two reasons: it is a refactor
rather than a deletion, and — decisively — `frontend/login` has tests for `Login.tsx` only.
`MfaChallenge.tsx`, `MfaSetup.tsx` and `PasswordReset.tsx` have **no test coverage at all**, so
rewriting focus-juggling, backspace and paste handling in three auth flows would be entirely
unverified. That is precisely the change that "only fails at runtime, on someone else's
deployment". **Recommend doing it as its own step, behind tests for those three pages.**

---

## 5. Suspicious but left

### 5.1 The naive type sweep: 23 hits, 0 real

For the record, since the brief asked me not to repeat it — I ran it once, as a control. Every
`public`/`internal` type declared in `src/` (202 of them), counted against mentions across
`src/`, `tests/` and `sdk/`. Twenty-three had exactly one mention (their own declaration):

- **19 × `IEntityTypeConfiguration<T>`** — `UserConfiguration`, `ProjectConfiguration`,
  `AuditLogConfiguration`, `WebhookConfiguration`, … found by `ApplyConfigurationsFromAssembly`
- **2 × controllers** — `AdminWebhookController`, `OrgWebhookController` — found by routing
- **`RediensIamDbContextFactory`** — EF design-time tooling
- **`InstanceConfigurationExtensions`** — the configuration provider

**23 hits, 23 false positives, 0 genuine dead types.** The `src/` type surface is clean, and this
technique has a 0% precision rate on this codebase. Noted so nobody re-runs it and starts deleting.

I also specifically re-confirmed that `EncryptedOnlyXmlRepository` and the DataProtection
encryptor/decryptor pair were never candidates — nothing in this sweep touched
`src/Config/KeyRingProtection.cs`.

### 5.2 Dead configuration keys in `src/appsettings.json` — **flagged, not removed**

Five keys are declared and read by **nothing** — no `AppConfig` property, no `IConfiguration`
access anywhere in `src/`:

| Key | Declared value | Read by |
|---|---|---|
| `Database:MigrateOnStartup` | `true` | **nothing** |
| `App:FrontendUrl` | `http://localhost:3000` | **nothing** |
| `App:LoginPath` | `/login` | **nothing** |
| `Cache:ProjectTtlMinutes` | `5` | **nothing** |
| `Cache:JwksTtlMinutes` | `60` | **nothing** |

**`Database:MigrateOnStartup` is the one that matters.** It reads as a safety switch, and it is
inert. `Program.cs:211` calls `EnsureDbSchemaAsync` unconditionally, which calls
`db.Database.MigrateAsync()` inside a 12-attempt retry loop; `InstanceConfiguration.cs:42` calls
`db.Database.Migrate()` as well. Setting `Database__MigrateOnStartup=false` — which an operator
reading `appsettings.json` would reasonably believe stops schema mutation at boot — **does
nothing**. Migrations always run.

This is the shape `26-documentation.md` found with `RECONFIGURE_FROM_ENV` / `INSTANCE_ID`, inverted:
there, the app read a key the chart could not set; here, `appsettings.json` advertises a key the app
never reads.

I did **not** delete these, per rule 3. Deleting a key is only cosmetic if nobody is setting it, and
I can read `deploy/` but cannot change it — I confirmed the chart does not currently reference any
of the five, but a values override or an operator's `Database__MigrateOnStartup` env var is exactly
the kind of thing that does not appear in the repo. The right fix for `MigrateOnStartup` is a
decision — honour it or delete it — and honouring it is a behaviour change, out of scope for a
dead-code sweep. **Owner: whoever owns `deploy/` and `src/Program.cs`.**

### 5.3 Recursive `bin/` nesting — pre-existing build cruft, **left**

`src/bin/Debug/net10.0/bin/Debug/net10.0/bin/…` and the same under
`tests/RediensIAM.IntegrationTests/bin/` nest deep enough to hit ripgrep's 100-level recursion
limit. It predates this task (it showed up in tool output before my first edit) and it breaks
`dotnet build --no-incremental`, which dies with 23 × `MSB3030 Could not copy … because it was not
found`. It is gitignored build output, so it is invisible to the repo and harmless to a normal
build — the standard `dotnet build` used for the baseline and the final check gives 0/0. Worth a
`rm -rf src/bin tests/*/bin` by someone, but it is not source and not mine to clean.

### 5.4 Unreachable branches / always-true conditions — **not attempted**

The brief listed these. Roslyn's built-in analyzers do not report them reliably, and the tool that
would (SonarQube, via the repo's `sonar-scan.sh`) needs a running server. The analyzer set I did run
found none of the adjacent categories — no CS0162 unreachable code, no CS0169/0414/0649 dead fields,
no IDE0051/0052 anywhere in `src/`. **Reporting this as not-covered rather than implying it was
checked.**

---

## 6. Final state

```
$ dotnet build RediensIAM.slnx -p:SonarQubeTargetsImported=true
    0 Warning(s)
    0 Error(s)

$ dotnet test RediensIAM.slnx -p:SonarQubeTargetsImported=true
Passed!  - Failed: 0, Passed:   11, Skipped: 0, Total:   11  RediensIAM.Client.Tests.dll
Passed!  - Failed: 0, Passed: 1346, Skipped: 0, Total: 1346  RediensIAM.IntegrationTests.dll

frontend/admin:  build ✔   71 tests ✔   knip: 0 unused files, 0 unused deps (was 5 / 8)
frontend/login:  build ✔   79 tests ✔   knip: clean, no findings at all
sdk/rust:        cargo clippy ✔ — 0 dead_code warnings
sdk/typescript:  tsc --noUnusedLocals --noUnusedParameters ✔
```

Suite and warning count both match the required baseline: **1346 passing, 0 warnings.**
Nothing committed.
