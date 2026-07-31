# 28 — Frontend test suites for `frontend/admin` and `frontend/login`

Both SPAs went from zero tests to **150 passing tests** across 7 files. `npm test` is wired in
both. Both builds still pass.

Scope was `frontend/admin` and `frontend/login` only. Nothing under `deploy/`, `src/` or `sdk/`
was read or touched.

---

## Headline: two defects found, one fixed

### 1. A failed MFA mutation was reported to nobody (fixed)

**`frontend/admin/src/components/ReauthDialog.tsx`.** This is the one that matters.

`useReauth().guard()` resolved the moment the re-authentication prompt opened — not when the
mutation finished. So by the time the user typed their password and the mutation actually ran,
the caller's `await guard(...)` had long since returned and its `catch` was out of scope.

`submit()` handled that case by rethrowing:

```ts
} else {
  // The proof worked; the mutation itself failed. Close and let the page report it.
  setPending(null);
  throw e;
}
```

There was no page left to report it. The throw propagated into the dialog's `<form onSubmit>`
handler, which is a React event handler returning a promise — i.e. straight into an **unhandled
promise rejection**. Vitest flags it as one; a browser logs it to the console and nothing else.

The user-visible behaviour: you enter your password correctly, the prompt disappears, and no
error is shown. That is indistinguishable from success. On these endpoints — regenerate backup
codes, confirm TOTP, verify phone, remove phone, delete a passkey — believing a change happened
when it did not is exactly the kind of thing you find out about when you are locked out.

Reachable whenever the mutation fails *after* a good proof: a 500, a 409, a dropped connection,
a validation error the endpoint raises after the re-auth check. Not reachable on a bad proof
(that path is handled) which is why reading it did not catch it.

**Fix**, in `ReauthDialog.tsx` — `guard` now awaits the whole flow:

```ts
await new Promise<void>((resolve, reject) => {
  setPending({ methods, run: action, settle: failure => failure ? reject(failure) : resolve() });
});
```

`submit` calls `pending.settle()` on success and `pending.settle(e)` on a non-re-auth failure;
cancelling calls `pending.settle()` with no argument, because a cancel is not a failure the page
should report. Every existing call site in `AccountPage.tsx` already wraps `await guard(...)` in
`try/catch` with its own message, so they all start working with no change. The only behavioural
side effect is that a caller's `finally { setSaving(false) }` now runs when the prompt closes
rather than when it opens — which is more accurate, since the operation really is still in flight.

Covered by `ReauthDialog.test.tsx` → `a mutation that fails after a good proof` (3 tests).

### 2. `sanitizeCss` replaces hex escapes with NUL, not a space (reported, not changed)

**`frontend/login/src/lib/sanitizeCss.ts:24`.** The line reads, in an editor:

```ts
out = out.replaceAll(/\\[0-9a-fA-F]{1,6}\s?/g, ' ');
```

Under `cat -v` the replacement is `'^@'` — a literal **U+0000 NUL**, not U+0020. The comment two
lines above says "replacing them with a space".

**Not exploitable.** CSS preprocessing (Syntax L3 §3.3) rewrites U+0000 to U+FFFD, which is a
valid ident code point but is still not the character the escape encoded. So
`input[t\79 pe=password]` comes out as `input[t<NUL>pe=password]`, the browser reads
`input[t�pe=password]`, the attribute name is not `type`, and the selector cannot match a
password field. The neutralisation holds. I verified this by applying the sanitiser's output to a
real stylesheet and asking a real `<input type="password">` whether any surviving rule matches it
— no escaped variant does.

**Still worth fixing**, for two reasons: it writes NUL bytes into a live `<style>` node on the
login page, and it makes the code read as though escapes produce token boundaries. They do not —
a space terminates an ident, U+FFFD continues one. Anyone extending this file will reason from the
comment and be wrong. One character.

Left alone deliberately: changing the substitution character in a security control is a semantic
change to someone else's P-03 fix, and it is not exploitable today. Pinned by
`sanitizeCss.test.ts` → `KNOWN DEVIATION: hex escapes are replaced with NUL, not the space the
comment claims`, which fails the moment it is corrected — at which point delete the test.

---

## Smaller findings (documented in tests, not fixed)

**`sanitizeCss`: a leading `@import` takes the next rule down with it.** The at-rule sweep
(`replaceAll(/@[^;{}]*;?/g, '')`) runs *after* `dropUnsafeRules`, so `@import url(x); .a { … }`
still has the `@import` glued to the front of `.a`'s selector when the rule filter reads it. The
selector contains `@`, so `.a` is dropped as collateral. Fails closed — the tenant loses a rule,
nothing unsafe gets through — so it is documented rather than fixed. Test: `KNOWN QUIRK: a
leading @import takes the next rule down with it`.

**`OrgEmail`: a failed load looks identical to no configuration.** `fetchConfig`'s catch sets
`Failed to load SMTP configuration.`, but that error state is only rendered inside the edit form,
which is closed at that point. A 500 from `GET /org/smtp` therefore shows the same "Using global
SMTP" card as an org that genuinely has no relay — and the admin may then configure one on top of
a state they could not read. Test: `KNOWN GAP: a failed load is indistinguishable from having no
relay`.

**`Login.tsx`: the identifier field had no accessible label** (fixed, see below).

**`MfaChallenge.tsx:269` has the same unlabelled-input defect** — `<label className="label">` with
no `htmlFor` and no `id` on the input it describes. Not fixed: it is outside what I tested, and I
will not change a file I have no coverage for. Every other form in the SPA (`Register`,
`PasswordReset`, `SetPassword`, and now `Login`) associates its labels correctly, so these two are
oversights rather than a convention.

---

## Application code changed

Two files, both deliberate, both described above:

| File | Change | Why |
|---|---|---|
| `frontend/admin/src/components/ReauthDialog.tsx` | `guard` awaits the full flow via a `settle` callback on `Pending`; `submit` and `onCancel` settle it | Bug fix — a failed mutation after a good proof was an unhandled rejection and the user saw nothing |
| `frontend/login/src/pages/Login.tsx` | Added `htmlFor="login-identifier"` / `id="login-identifier"` to the email-or-username field | The primary field on the primary form had no accessible name; a screen reader announced an unlabelled text box. Also the only way to reach it by role in a test |

No other application file was modified. No test was written around a change made to make the test
pass.

---

## What is covered, and why those cases

### `frontend/admin` — 71 tests

**`src/components/ReauthDialog.test.tsx` — 23 tests.** The MFA re-authentication contract, driven
through a harness that uses `useReauth` exactly the way `AccountPage` does.

- *The first attempt sends no proof.* Asserted as `action.mock.calls[0]` being an empty argument
  list, not `[undefined]` — first enrolment must stay one step.
- *The prompt appears only on `401 reauthentication_required`.* A 500 and a plain
  `401 invalid_token` both go to the page's own error, not to a password prompt. Telling a user
  their password is wrong when the request never got that far is its own kind of harm.
- *`methods` is authoritative.* A passwordless account is never offered a password field; a
  password-only account is never offered a code field.
- *No auto-retry.* After a rejected proof the test waits 50 ms and re-asserts the call count is
  still 2. This is the test that would catch a retry loop walking the account into a lockout.
- *The input is cleared after every attempt*, so a burned TOTP code cannot be resubmitted — the
  anti-replay cache would reject it and it would cost another rate-limiter slot.
- *429 locks the form.* Both inputs and the submit button go disabled, so the UI cannot generate
  another attempt while blocked.
- *A failed re-auth reads differently from a failed mutation*, and a burned TOTP code reads
  differently from a wrong password.
- *Focus containment.* Tab is pressed 12 times and focus is asserted to still be inside the
  dialog on every one, with a link and the trigger button sitting outside it as bait. Plus: the
  page behind is out of the accessibility tree while it is open (`queryByRole` for the trigger
  returns nothing). Escape and Cancel both close without re-running the mutation.

**`src/auth.test.ts` — 12 tests.** The 401 split, which is the load-bearing half of the
re-auth flow and pure logic.

- `reauthMethods` returns `[]` (not `null`) when the server names no methods — `null` means "not
  a re-authentication demand" and would let the error through as an ordinary failure.
- A `reauthentication_required` 401 **does not** clear the token or start a login redirect.
  Asserted structurally: `fetch` is called exactly once, because fetching `/admin/config` is the
  first step of throwing the session away. Losing a working session mid-MFA-change is the
  regression this guards.
- A plain 401 does redirect, and three concurrent 401s redirect exactly once (the
  `signinRedirectInFlight` one-shot; without it the last redirect wins the stored PKCE state and
  breaks the callback for the others).
- The `/admin/config` `redirect_uri` origin check: refused when cross-origin, refused when not a
  URL, accepted when it matches. A compromised config endpoint could otherwise hand the
  authorization code to another origin.

**`src/components/layout/CommandPalette.test.tsx` — 19 tests.**

- *Opens modally.* Asserted as `showModal()` rather than `show()` — see the jsdom note below.
  This is precisely the defect the audit fixed: a non-modal `<dialog>` behind a scrim still let
  Tab into the page behind it.
- Escape closes it and calls `onClose` once.
- Combobox/listbox: `aria-controls` points at the listbox, `aria-expanded` is set,
  `aria-activedescendant` starts on the first option and follows ArrowDown/ArrowUp, clamps at
  both ends, and exactly one option carries `aria-selected="true"` at a time.
- `aria-activedescendant` stays on a real, present option after the list is filtered under the
  cursor — a stale index would point at an id no longer in the document, which is the classic way
  this pattern breaks for screen-reader users.
- Enter navigates to the active option and closes; Enter with no matches does nothing.
- Arrow keys move the cursor rather than the text caret (the `preventDefault`).
- Role gating: an org admin sees no System or Project group; everyone sees Account.
- Mouse: clicking navigates, hovering moves the keyboard cursor so Enter follows the pointer.

**`src/pages/org/OrgEmail.test.tsx` — 17 tests.** The new SMTP 400 codes.

- Each of the five codes renders its own message (table-driven).
- An unknown code and a non-`ApiError` failure both fall back to the generic message — a new
  server-side code must not render blank.
- The form stays open with its values intact after a rejection.
- **Nothing the server sent reaches the screen.** Two tests feed a body carrying
  `detail: 'connect ECONNREFUSED 10.0.0.7:25'` and assert the rendered document contains neither
  the address nor the phrase. This is what stops `/org/smtp/test` being usable as an internal
  port scanner, and it is the assertion most likely to catch a well-meaning "let's show the real
  error" change.
- The happy paths: save closes the form and shows the stored relay, the port goes as a number, an
  untouched password is omitted, delete asks first and does nothing on "no".

### `frontend/login` — 79 tests

**`src/lib/sanitizeCss.test.ts` — 36 tests.** The P-03 control.

Rather than assert on the output string, the keylogger cases apply the sanitiser's output to a
real `<style>` node and ask a real `<input type="password">` whether any surviving rule matches
it. That is the actual security property; asserting `not.toContain('password')` would have been a
proxy for it, and — as it turns out — a proxy that fails on the escaped variants for reasons that
have nothing to do with safety. Seven attack shapes: plain, quoted, spaced, uppercased, two hex-
escaped, and one split by a comment.

- `url(...)` becomes `url(about:blank)` everywhere, including inside rules that survive.
- `attr(` is dropped from the rule **body**, not just the selector — `content: attr(value)` is
  the usual shape of the attack and the older pattern only looked before the `{`.
- `@import`, `@charset`, `@namespace`, `@font-face` all go.
- Rules nested inside a dropped `@media` go with it instead of being promoted to the top level.
  A filter that stopped at the first `}` would leave them behind, which is how a sanitiser stops
  sanitising.
- Malformed input: unbalanced braces, 1000 `{`, an unterminated comment, empty string. The
  unterminated-comment case asserts the browser ends up with zero rules — the sanitiser passes it
  through as text, but a browser closes an unterminated comment at EOF, so the mismatch is in the
  safe direction.

**The timing assertion.** Six hostile 16 KB payloads — no braces at all, `attr(` bait, open braces
only, unclosed `url(`, hex escapes, 8000 nested blocks — each asserted to finish in under 250 ms.
Plus a shape test: growing the payload tenfold must not cost more than 60x. The regexes this
replaced were cubic on input containing no `{`, which SonarQube measured at 69 s of CPU for 4 KB,
on the unauthenticated login page. The bounds are deliberately loose; they are there to catch a
return to polynomial behaviour, not to benchmark the machine. Current actual runtime for all six
is a few milliseconds.

`safeCssValue`: legitimate colours, `rebeccapurple`, a full font stack and `0.5rem` all pass;
nine breakout attempts (`;`, `}`, `url(`, `var(`, quotes, backtick, backslash, `<`) all return
`null`; the 120-character ceiling is asserted on both sides (120 passes, 121 fails); non-strings
return `null`.

**`src/safeNavigate.test.ts` — 14 tests.** Relative paths and same-origin absolute URLs pass.
Refused: other origins, protocol-relative `//evil.test`, six backslash smuggles (`/\evil.test`
and friends — the reason the `react-router` open-redirect advisory is unreachable here), six
non-http(s) schemes including `javascript:` and `data:`, userinfo tricks
(`https://localhost:3000@evil.test`), lookalike hosts (`localhost:3000.evil.test`), same host on
a different port or scheme, and empty/null/undefined. `safeNavigate` itself is tested for both
halves of its contract: it assigns `location.href` and returns `true` for a trusted target, and
assigns nothing and returns `false` otherwise — the caller depends on that boolean to show
"could not complete sign-in" rather than leaving the user on a page that looks busy.

**`src/pages/Login.test.tsx` — 29 tests.** `safeNavigate` is deliberately *not* mocked here.

- Happy path: `redirect_to` is followed. An email identifier goes as `email`, a bare name as
  `username`, an admin identifier always as `email`.
- **Four hostile `redirect_to` values** (`https://evil.test`, `//evil.test`, `/\evil.test`,
  `javascript:`) each produce "Sign-in could not complete" and **no navigation at all**. This is
  the end-to-end version of the open-redirect defence and the test I would keep if I could keep
  only one from this file.
- MFA handoffs: `requires_mfa` navigates to `/mfa` and stashes the factor type and phone hint;
  the type defaults to `totp`; `requires_mfa_setup` navigates to `/mfa-setup` with the challenge
  and user id. Both assert no navigation away from the SPA happened.
- Errors: `no_role` and `account_locked` get their own messages; everything else gets the same
  "Invalid email or password." (distinguishing a bad password from an unknown account tells an
  attacker which addresses exist); a thrown exception is reported without its text reaching the
  page; the previous error clears on the next attempt; the button re-enables so a failure can be
  retried.
- Constraint validation: an empty form and a non-email admin identifier never reach the API.
- Theming, as an integration test of `safeCssValue` and `sanitizeCss` through the component: a
  legitimate colour and font stack are applied to the document element; a breakout attempt
  (`red; background: url(...)`) is refused while the neighbouring valid value still applies;
  `custom_css` is sanitised before it reaches the `<style>` node; and the node and the custom
  properties are both removed on unmount, so a theme cannot leak across navigations.
- Providers: disabled ones are filtered out; the OAuth start URL escapes the provider id.

---

## Deliberately not covered

- **`AccountPage.tsx` itself (911 lines).** Its MFA handlers are thin wrappers around `guard`,
  and `guard` is now covered exhaustively through a harness with the same shape. Rendering the
  real page needs `AuthContext` (which does an OIDC dance), eight API modules and WebAuthn. The
  cost is high and the marginal coverage is the wiring, not the contract. If one thing gets added
  later, make it a single test that `handleRegen` and friends pass the proof through to the right
  API function.
- **Real Tab containment and inertness for the native `<dialog>`.** jsdom has no top layer, no
  layout and no `inert`, so it cannot enforce modal focus containment no matter what the shim
  does. The palette tests assert the thing that regressed — that `showModal()` is used rather
  than `show()` — and delegate the containment itself to the platform. Verifying it for real needs
  a browser; `tests/e2e/` is where that belongs. (The Radix-based `ReauthDialog` *is* tested for
  real containment, because Radix implements the trap in JavaScript and jsdom can run it.)
- **`ReauthDialog`'s "the page behind is inert" for the native dialog**, same reason.
- **`MfaChallenge`, `Register`, `PasswordReset`, `SetPassword`, `MfaSetup`.** Not touched by the
  audit. `MfaChallenge` is the strongest candidate next — it is the other half of the login flow
  and has the same unlabelled-input defect noted above.
- **The admin SPA's other 40-odd pages.** Out of scope; nothing in the audit changed them.
- **`useTheme`.** Three lines around `localStorage`.
- **Network-level behaviour of `login/src/api.ts`** (`X-Requested-With`, `credentials: include`).
  Worth a small file later; it is a CSRF defence-in-depth measure and it is pure function-shaped.
  Not in the audit's twelve steps, so it was left.

---

## Test infrastructure

Both SPAs already had `vitest`, `jsdom`, `@testing-library/react`, `@testing-library/jest-dom` and
`@testing-library/user-event` installed and unused. **No new dependency was added.**

- `test` config lives in the existing `vite.config.ts` in both (via `defineConfig` from
  `vitest/config`), rather than a second config file. `environment: 'jsdom'`,
  `include: ['src/**/*.test.{ts,tsx}']`.
- `src/test/setup.ts` in both: registers the `jest-dom` matchers and an explicit
  `afterEach(cleanup)` (RTL's auto-cleanup needs `globals: true`, which would need a `types` entry
  in `tsconfig.app.json` — one line of setup is cheaper).
- Tests live next to the code they test, inside `src`, so `tsc -b` typechecks them as part of
  `npm run build`. `vite build` does not bundle them: they are not reachable from `index.html`.
- Scripts added to both `package.json`: `"test": "vitest run"` and `"test:watch": "vitest"`.

### The jsdom `<dialog>` shim

jsdom 29 ships `HTMLDialogElement` with a working reflected `open` property and **none of its
methods** — no `show`, no `showModal`, no `close`. `CommandPalette` calls `showModal()` in an
effect, so it could not be rendered at all.

`frontend/admin/src/test/setup.ts` shims the three methods and Escape-to-close, and exports
`isModal(el)` so a test can tell `showModal()` from `show()`. It deliberately does **not** emulate
focus containment or inertness — that would be a shim asserting against itself. See "deliberately
not covered" above.

---

## Real output

```
$ cd frontend/admin && npm test
 Test Files  4 passed (4)
      Tests  71 passed (71)
   Duration  2.65s

   src/auth.test.ts                                12
   src/components/ReauthDialog.test.tsx            23
   src/components/layout/CommandPalette.test.tsx   19
   src/pages/org/OrgEmail.test.tsx                 17

$ cd frontend/login && npm test
 Test Files  3 passed (3)
      Tests  79 passed (79)
   Duration  3.05s

   src/lib/sanitizeCss.test.ts   36
   src/pages/Login.test.tsx      29
   src/safeNavigate.test.ts      14
```

```
$ cd frontend/admin && npm run build
> tsc -b && vite build
✓ 1903 modules transformed.
dist/assets/index-DWBigPUW.css   60.39 kB │ gzip:  12.84 kB
dist/assets/index-Bx7RCSWA.js   749.42 kB │ gzip: 200.14 kB
✓ built in 877ms
(pre-existing warning: chunks larger than 500 kB)

$ cd frontend/login && npm run build
> tsc -b && vite build
✓ 37 modules transformed.
dist/assets/index-CEXU3-Gb.css   13.90 kB │ gzip:  3.35 kB
dist/assets/index-B3MiHLbD.js   291.38 kB │ gzip: 87.83 kB
✓ built in 170ms
```

`npm run lint`: `frontend/login` is clean (1 pre-existing warning about a redundant
`eslint-disable` in `safeNavigate.ts`). `frontend/admin` reports 21 errors and 5 warnings, **all
pre-existing in application code** (`react-hooks/set-state-in-effect`, unused `_`-prefixed
parameters); the count is unchanged by this work and none of the test files contribute to it.

Nothing was committed.
