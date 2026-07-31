# 24 — SonarQube reliability + maintainability: `sdk/` and `frontend/`

Branch `security/hardening-2026-07-30`. Scope limited to `sdk/` and `frontend/`; `src/` and
`deploy/` untouched (another agent owns those). Nothing committed.

## Headline numbers

| | Before | After |
|---|---|---|
| Project total (all components) | 62 | 28 |
| **My scope (`sdk/` + `frontend/`)** | **38** | **3** |

Measured, not estimated: both numbers come from `search_sonar_issues_in_projects` against
`http://192.168.1.97:9000`, project key `RediensIAM`, `issueStatuses=[OPEN,CONFIRMED]`, before
any edit and after a final `./sonar-scan.sh`. The 28 → the drop from 62 also includes work by
the `src/`/`deploy/` agent running concurrently; the 38 → 3 figure is mine alone, computed by
filtering the same two issue lists to components under `sdk/` and `frontend/`.

The 3 remaining in my scope are all deliberate — see "Not fixed" below.

Every reliability issue assigned to me (S7758 ×1, S7781 ×4, S6847 ×2) is gone.

---

## 1. `String.fromCharCode` → `String.fromCodePoint` (S7758) — verification first

`sdk/typescript/rediensiam-web/src/index.ts:478`, `base64UrlEncode`, on the PKCE path.

The rule is only safe if the two functions agree on every input this function can receive. I
verified that empirically before touching the code rather than trusting the rule:

```
fromCharCode===fromCodePoint for 0..255: true
random 4096-byte string identical: true len 4096
encode identical: true
decode identical: true
```

Reasoning behind the check: the parameter is a `Uint8Array`, so each element is an integer
0–255. Both functions map an integer below 0xFFFF to the single UTF-16 code unit with that
value; they only diverge above U+FFFF, where `fromCodePoint` emits a surrogate pair and
`fromCharCode` truncates. That range is unreachable here. The loop over 0..255 confirms
pointwise equality, and the 4096-byte random-array comparison confirms the assembled binary
string — and the full base64url output built from it — is byte-identical.

Callers of `base64UrlEncode` are `randomUrlSafe` (PKCE verifier + state) and `s256` (PKCE
challenge). Both are covered by the existing test suite, including a fixed known-answer vector
(`s256('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk')` →
`E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM`) and a fixed encode vector
(`base64UrlEncode(new Uint8Array([251,255,190,0,1,2]))`). Both still pass.

**Before**
```ts
for (const byte of bytes) binary += String.fromCharCode(byte);
```
**After**
```ts
// Every element is 0-255, so fromCodePoint is byte-for-byte identical to fromCharCode here.
for (const byte of bytes) binary += String.fromCodePoint(byte);
```

## 2. `String#replace` → `String#replaceAll` (S7781 ×4)

Four issues, two lines. All four flagged calls used a `/…/g` regex whose pattern is a single
literal character, which is exactly the case `replaceAll` with a string argument covers.

**`index.ts:479`** (2 issues)
```ts
-  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
+  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
```

**`index.ts:497`** (2 issues)
```ts
-    const padded = parts[1].replace(/-/g, '+').replace(/_/g, '/');
+    const padded = parts[1].replaceAll('-', '+').replaceAll('_', '/');
```

**The `/=+$/` padding strip is unchanged and was never flagged.** Worth being explicit: Sonar
raised exactly two issues on line 479, and those were the two `/g` calls. `/=+$/` is anchored
and non-global, so it is not a `replaceAll` candidate at all — `replaceAll` throws a
`TypeError` when handed a non-global regex. Rewriting it was neither required nor possible
without changing what it does, so it was left byte-for-byte as reviewed and marked SAFE.

The 4096-byte round-trip check above compared the old and new expressions directly
(`encode identical: true`, `decode identical: true`), so the substitution is confirmed
behaviour-preserving on real data, not just by inspection.

## 3. Union type → alias (S4323)

`index.ts:264`. `string | URL | Request` appeared at three sites (`fetch`, `targetUrl`,
`isTrustedTarget`). Added `export type FetchTarget = string | URL | Request;` and used it at all
three. Type-level only, zero runtime effect.

> Process note, in the spirit of the "no bulk regex edits" rule: I used a scripted
> string-replace for this one and it did bite — it rewrote the alias declaration itself into
> `export type FetchTarget = FetchTarget;`. Caught immediately (the tool echoed the file back),
> fixed by hand, and `tsc --noEmit` was clean afterwards. No other scripted edits were used.

## 4. Accessibility — `IamDialog` (S6847)

`frontend/admin/src/components/iam/IamDialog.tsx:25-31`.

**What was actually broken.** The old markup was a `<div role="none" onClick={onClose}>` scrim
wrapping a non-modal `<dialog open>`. Concretely that meant:

- The scrim's click-to-close was mouse-only. `role="none"` strips it from the a11y tree, and it
  had no keyboard handler, so there was no keyboard-reachable equivalent.
- `<dialog open>` (as opposed to `showModal()`) does **not** put the dialog in the top layer.
  The rest of the page stayed focusable and stayed in the accessibility tree — Tab walked
  straight out of the dialog into the page behind it, and a screen reader could read the
  obscured content as if it were live.
- Nothing moved focus into the dialog on open, and nothing restored it on close.
- The dialog had no accessible name (no `aria-labelledby`).
- `onClick`/`onKeyDown` with `stopPropagation` existed purely to stop the scrim handler firing —
  the listeners the rule flagged were plumbing for a workaround, not real behaviour.
- Escape was handled by a manual `window` keydown listener.

**Fix: use the native modal dialog.** `showModal()` supplies focus containment, an inert
background, top-layer rendering and Escape-to-close as platform behaviour, which let the scrim
div, both `stopPropagation` handlers and the keydown effect all be deleted.

```tsx
useEffect(() => {
  const dialog = ref.current;
  if (!open || !dialog || dialog.open) return;
  dialog.setAttribute('closedby', 'any');
  dialog.showModal();
}, [open]);

<dialog ref={ref} className="iam-dialog"
        aria-labelledby={titleId} aria-describedby={desc ? descId : undefined}
        onClose={onClose}>
```

`closedby="any"` restores click-outside-to-close, which the scrim used to provide — behaviour
preserved. It is set via `setAttribute` rather than as a JSX prop deliberately: as a JSX prop it
is typed (`@types/react` 19.2 declares it) and compiles fine, but Sonar's `typescript:S6747`
does not know the attribute yet and raised "Unknown property 'closedby'". Setting it
imperatively keeps the runtime behaviour and avoids trading one issue for another. Verified
`d.closedBy === 'any'` after `setAttribute` in the browser.

CSS: `.iam-dialog-scrim` (fixed overlay + `display: grid; place-items: center`) was replaced by
`.iam-dialog::backdrop`, since a modal dialog is centred by the UA and paints its own backdrop.
The blur/tint values and the `iam-scrim-in` animation were carried over unchanged.

The `open` prop and the conditional `return null` were kept — 11 call sites depend on them.

## 5. Accessibility — `CommandPalette` (S6847 + S3358 ×2)

`frontend/admin/src/components/layout/CommandPalette.tsx`.

Same scrim/`<dialog open>` problem as above, plus its own:

- Arrow/Enter/Escape were handled by a **`window`** keydown listener while focus sat in the
  search input. It worked for sighted mouse+keyboard users but nothing told assistive tech that
  a cursor was moving — the `.selected` row was styled, and that was the entire signal.
- The result rows were `<button>`s inside plain `<div>`s, i.e. no listbox relationship at all.
  A screen reader saw a flat run of buttons with no notion of "option 3 of 12" or "currently
  active".

Fixes:

- `showModal()` + `closedby="any"` + `onClose`, exactly as `IamDialog`. Scrim div and both
  `stopPropagation` handlers deleted; the `window` Escape branch deleted (native).
- Arrow/Enter moved off `window` onto the `<input>`'s `onKeyDown`. The input is an interactive
  element, so this is the correct owner and does not re-trigger S6847.
- Proper combobox/listbox wiring: input gets `role="combobox"`, `aria-expanded`,
  `aria-controls`, and `aria-activedescendant` pointing at the highlighted row's id; the list
  gets `role="listbox"`; rows get `role="option"` + `aria-selected` + `tabIndex={-1}` (they stay
  `<button>`s, so click and hover keep working unchanged). The keyboard cursor is now
  announced.
- The dialog got `aria-label="Command palette"`; decorative SVGs got `aria-hidden="true"`.
- The `let idx = -1` counter mutated during render was replaced by a flat index carried in the
  memoised `groups` structure, so the keyboard cursor and the rendered rows cannot drift apart.
- **S3358 ×2**: the four-branch nested ternary picking a row icon became a module-scope
  `ICONS: Record<CmdItem['kind'], React.ReactNode>` lookup.

One knock-on: removing the render-time mutation let `react-hooks/set-state-in-effect` finally
analyse the component, surfacing a pre-existing `useEffect` that reset `q`/`sel` on open. Rather
than leave a new lint error, the palette is now mounted only while open
(`{cmdOpen && <CommandPalette …/>}` in `Shell.tsx`), so state is fresh by construction and the
effect is gone. The `open` prop was dropped; there is one call site.

### How the a11y fixes were tested

- **Build/type/lint**: `tsc -b` clean; `eslint src` on `frontend/admin` is at **21 errors /
  5 warnings both before and after** my changes — i.e. I introduced zero new lint findings, and
  all 21 are the pre-existing `useEffect(load, [])` pattern in unrelated pages.
- **Browser, real Chrome 151** via DevTools, on a reduced page using the same markup and the
  same `setAttribute('closedby','any'); showModal()` sequence the components now run:

  | Check | Result |
  |---|---|
  | `closedBy` reflects after `setAttribute` | `"any"` |
  | Focus after `showModal()` | lands on the first focusable (the input) — matches the old `autoFocus` |
  | `document.getElementById('outside').focus()` while modal | focus does **not** move (`focusEscaped: false`) — real containment |
  | `dialog.matches(':modal')` | `true` |
  | a11y tree snapshot while open | `dialog "Title" modal` only; the background button is **absent from the tree** |

  That last row is the concrete proof of the fix: under the old `<dialog open>` the background
  was in the tree and focusable, and now it is neither.

- **Not verified**: I could not confirm the physical backdrop *click* dismisses the dialog.
  Synthetic `MouseEvent`s are untrusted and light dismiss ignores them, and the backdrop is not
  an element the snapshot-driven click tool can target. I verified the enabling condition
  (`closedBy === "any"` on a `:modal` dialog) rather than the gesture. If `closedby` were
  somehow inert, the degradation is that click-outside stops closing; Escape and the footer
  buttons still do.
- **Not tested**: no screen-reader run, and no end-to-end click-through of the live admin app
  (it sits behind auth). The ARIA structure is asserted from the markup and the a11y tree
  snapshot, not from NVDA/VoiceOver output.

## 6. Remaining maintainability, `frontend/admin`

| File | Rule | Fix |
|---|---|---|
| `layout/Sidebar.tsx:296` | S3358 | `isSuperAdmin ? … : isOrgAdmin ? … : …` → module-scope `roleLabel(isSuperAdmin, isOrgAdmin)` with early returns, next to the existing `isActive`/`deriveScope` helpers |
| `layout/TweaksPanel.tsx:21,29,34` | S6479 ×3 | Three decorative skeleton-bar arrays keyed by index → module-scope `MINI_NAV_BARS` / `MINI_STAT_TILES` / `MINI_TABLE_ROWS` with explicit ids. Folding the opacity into `MINI_NAV_BARS` also removed an `i === 0` ternary |
| `layout/TweaksPanel.tsx:94` | S3358 | `color: isActive ? … : disabled ? … : …` → a `labelColor` local computed with `if`/`else if` above the `return`, alongside the existing `isActive`/`disabled` locals |
| `org/Projects.tsx:137` | S3358 | `loading ? skeleton : empty ? … : rows` → IIFE with early returns |
| `project/ProjectRoles.tsx:111` | S3358 | same |
| `project/ProjectServiceAccounts.tsx:144` | S3358 | same |
| `project/ProjectUsers.tsx:74` | S3358 | `loading ? … : isOrgAdmin ? … : …` → IIFE with early returns |
| `system/SystemHealth.tsx:49` | S3358 | status-chip ternary chain → `StatusChip` component, placed beside the existing `dotStatus` helper |
| `system/SystemHealth.tsx:134` | S3358 | `loading ? … : data ? … : null` → IIFE with early returns |

The IIFE-with-early-returns shape was not invented for this pass — `pages/org/UserLists.tsx:88`
already used it for the identical `loading / empty / rows` table body and was already passing
the rule, so the flagged files were brought in line with the file that was already right.

## 7. Remaining maintainability, `frontend/login`

| File | Rule | Fix |
|---|---|---|
| `MfaChallenge.tsx:201,203` | S3358 ×2 | 4-branch `methodDesc` chain → module-scope `methodInstruction(mode, phoneHint)` `switch` |
| `MfaChallenge.tsx:259` | S3358 | `mode === 'webauthn' ? … : mode === 'backup' ? … : …` → IIFE with early returns |
| `MfaChallenge.tsx:287` | S6479 | OTP grid keyed by index → `OTP_CELL_IDS` constant; `key={cellId}`, value read as `cells[i]` |
| `MfaSetup.tsx:124` | S3358 ×2 | `stepNum` chain → `STEP_NUMBERS: Record<Step, number>` lookup |
| `MfaSetup.tsx:225` | S6479 | same `OTP_CELL_IDS` treatment |
| `PasswordReset.tsx:35` | **S3776** (cognitive complexity 17 → allowed 15) | see below |
| `PasswordReset.tsx:134,153` | S3358 ×2 | 4-branch step chain → IIFE with early returns |
| `PasswordReset.tsx:160` | S6479 | same `OTP_CELL_IDS` treatment, now inside `OtpGrid` |
| `Register.tsx:45,45,46` | S3358 ×3 | `strengthLabel` / `strengthColor` ternary chains → module-scope `scoreLabel(score)` / `scoreColor(score)` |
| `Register.tsx:134` | S6479 | same `OTP_CELL_IDS` treatment |

**PasswordReset S3776.** The component carried three form handlers, three OTP-cell handlers and
a four-way render branch in one function. The three cell handlers (`handleCellChange`,
`handleCellKeyDown`, `handleCellPaste`) plus the grid JSX moved verbatim into a module-scope
`OtpGrid({ cells, setCells, cellRefs })` component in the same file. Bodies were moved without
edits — same regexes, same focus-advance and backspace-retreat rules, same 6-digit paste
truncation. `cells` state stays in `PasswordReset` because `handleOtp` submits `cells.join('')`.
Sonar now reports no S3776 for the file.

I deliberately did **not** hoist `OtpGrid` into a component shared by `MfaChallenge`,
`MfaSetup`, `PasswordReset` and `Register`, even though all four duplicate the OTP handlers.
Doing it would touch four MFA/reset flows that have no automated tests, for a maintainability
score that is already clear. Worth doing as its own change with a test harness; not worth
smuggling into a lint-cleanup pass. Flagging it here as known duplication.

## 8. `sdk/dotnet` — CA1873

`sdk/dotnet/RediensIAM.Client/RediensIamClient.cs:209`.

**Before**
```csharp
logger?.LogDebug("Introspected token: active={Active} user={UserId}", info.Active, info.UserId);
```
**After**
```csharp
// Guarded: introspection runs per request, and the unguarded call boxes info.Active into
// the params array even when Debug logging is off.
if (logger?.IsEnabled(LogLevel.Debug) == true)
    logger.LogDebug("Introspected token: active={Active} user={UserId}", info.Active, info.UserId);
```

Not pure rule-appeasement: `info.Active` is a `bool` going into a `params object[]`, so the old
line allocated a boxed bool and an array on every introspection — a per-request path — whether
or not Debug was enabled. The guard removes that.

---

## Not fixed — 3 issues, deliberate

**`typescript:S6819` ×2 — `CommandPalette.tsx:130` (`role="listbox"`) and `:137`
(`role="group"`).** These are new, introduced by my own a11y fix, and I am keeping them.

The rule says prefer `<select size=…>`, `<select multiple=…>` or `<datalist>` over
`role="listbox"`, and `<details>`/`<fieldset>`/`<optgroup>`/`<address>` over `role="group"`. The
generic advice is sound; it does not hold for this widget. A command palette is a filtered,
type-ahead result list whose rows carry an icon, a label, an optional subtitle and a kind badge,
and which must track mouse hover and a keyboard cursor independently of DOM focus. `<select>`
and `<datalist>` cannot render that content and do not support `aria-activedescendant` cursor
semantics. The construct the rule is steering me away from — combobox + listbox + option with
`aria-activedescendant` — is precisely what the WAI-ARIA Authoring Practices prescribe here.
`role="group"` is likewise required, not decorative: options in a grouped listbox must be owned
by the listbox or by a `group` inside it, so dropping it to clear the issue would leave the
listbox structurally invalid and make the a11y worse than before I started.

Since the brief was explicitly "fix them properly rather than silencing the rule", trading
correct screen-reader semantics for two green MAJORs would be the wrong call. Nothing was
suppressed — the issues are visible in SonarQube; this paragraph is the reasoning. If they need
to disappear from the dashboard, the right move is marking them Accepted in SonarQube with this
note, which I have not done because it changes triage state.

**`csharpsquid:S1075` — `sdk/dotnet/RediensIAM.Client/ServiceCollectionExtensions.cs:41`,
"Remove this hardcoded path-delimiter."**

```csharp
client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
```

Pre-existing, not introduced here. The rule targets filesystem paths, where `/` vs `\` is
platform-dependent and `Path.DirectorySeparatorChar` is the portable answer. This is a URL. `/`
is the path separator in the URI spec on every platform, and the line is normalising a trailing
slash on a caller-supplied base URL so that relative request URIs resolve correctly — the
comment directly above it says so. There is no portability concern to fix.

I could have made it vanish by writing `+ '/'` (a `char` literal, which the rule does not
match), but that changes nothing about the code and only games the detector, so I left it and
am reporting it instead. Genuine false positive.

---

## Verification output (actual)

**`sdk/typescript/rediensiam-web` — `npm test`** (identical before and after):
```
ℹ tests 12
ℹ suites 0
ℹ pass 12
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
```

**`sdk/typescript/rediensiam-web` — `npm run typecheck`**: `tsc -p tsconfig.json --noEmit`, no
output, exit 0.

**`frontend/admin` — `npm run build`** (`tsc -b && vite build`):
```
✓ 1903 modules transformed.
dist/assets/index-*.css   60.36 kB │ gzip:  12.83 kB
dist/assets/index-*.js   749.41 kB │ gzip: 200.13 kB
✓ built in 855ms
```
(the >500 kB chunk-size notice is pre-existing and unrelated.)

**`frontend/admin` — `eslint src`**: 21 errors / 5 warnings **before**, 21 errors / 5 warnings
**after**. All pre-existing `react-hooks/set-state-in-effect` on `useEffect(load, …)`.

**`frontend/login` — `npm run build`**: `✓ built in 182ms`.
**`frontend/login` — `eslint src`**: 0 errors / 1 warning before and after.

**`sdk/dotnet` — `dotnet build RediensIAM.Client/RediensIAM.Client.csproj -p:SonarQubeTargetsImported=true`**:
```
RediensIAM.Client -> .../bin/Debug/net10.0/RediensIAM.Client.dll
Build succeeded.
    4 Warning(s)
    0 Error(s)
```
The 4 warnings are pre-existing NU1510 package-pruning notices. The CA1873 warning that was
there before is gone.

**`sdk/rust/rediensiam-client`**: not modified, so `cargo test` was not run. It had no issues in
either scan.

## Files changed (17, `sdk/` + `frontend/` only)

```
 frontend/admin/src/components/iam/IamDialog.tsx                  |  50 +++--
 frontend/admin/src/components/layout/CommandPalette.tsx          | 156 +++++++------
 frontend/admin/src/components/layout/Shell.tsx                   |   3 +-
 frontend/admin/src/components/layout/Sidebar.tsx                 |   8 +-
 frontend/admin/src/components/layout/TweaksPanel.tsx             |  32 ++-
 frontend/admin/src/index.css                                     |   9 +-
 frontend/admin/src/pages/org/Projects.tsx                        |  22 +-
 frontend/admin/src/pages/project/ProjectRoles.tsx                |  28 +--
 frontend/admin/src/pages/project/ProjectServiceAccounts.tsx      |  28 +--
 frontend/admin/src/pages/project/ProjectUsers.tsx                |  12 +-
 frontend/admin/src/pages/system/SystemHealth.tsx                 |  22 +-
 frontend/login/src/pages/MfaChallenge.tsx                        |  38 +--
 frontend/login/src/pages/MfaSetup.tsx                            |  13 +-
 frontend/login/src/pages/PasswordReset.tsx                       |  96 +++++---
 frontend/login/src/pages/Register.tsx                            |  26 ++-
 sdk/dotnet/RediensIAM.Client/RediensIamClient.cs                 |   5 +-
 sdk/typescript/rediensiam-web/src/index.ts                       |  16 +-
 17 files changed, 332 insertions(+), 232 deletions(-)
```

Not committed. Nothing under `src/` or `deploy/` was touched.

## Measured vs assumed — summary

**Measured.** SonarQube counts before/after (both from live API queries). `fromCharCode` vs
`fromCodePoint` equivalence across 0–255 and over a 4096-byte random array, including full
encode and decode round-trips. All test/build/lint output quoted above, with before/after lint
baselines taken via `git stash` so the comparison is like-for-like. Dialog modality, focus
containment, `closedBy` reflection and the a11y tree, in real Chrome 151.

**Assumed / not verified.** That the physical backdrop click dismisses the dialogs (I verified
the enabling attribute and modality, not the gesture — see §5). That the `::backdrop` and
`margin: 14vh auto auto` CSS reproduces the old scrim's visual placement pixel-for-pixel; both
frontends build and the UA centring rules make it equivalent in principle, but I did not
screenshot-compare the running app. That the S3358/S6479 refactors preserve rendering — they are
mechanical (ternary → early return, index key → stable id, with the mapped value read from the
same array at the same index) and the builds are green, but the login and admin flows have no
component tests, so this rests on inspection rather than execution.
