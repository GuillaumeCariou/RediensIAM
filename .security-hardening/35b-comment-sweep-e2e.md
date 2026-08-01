# 35b — Comment sweep: `tests/e2e`

Finishes the sweep started in `3fcaf5c`, which stopped before this directory. Scope was
`tests/e2e/` only; nothing outside it was read for edit or touched.

The rule applied, unchanged from `3fcaf5c`: **a comment explaining _what_ the code does is
noise; one explaining an intention or warning of a consequence earns its place.** Section
separators kept throughout. Where a call was genuinely close, the tie went to promotion.

## Counts

| Action | Count |
|---|---|
| Deleted | 21 |
| Promoted / rewritten as rationale | 14 |
| Kept as-is (file banners + fixture JSDoc + inline rationale) | 32 |
| Section separators kept | 78 |

Files changed: 12 of the 18 TypeScript files in the tree. `playwright.config.ts`,
`deployment-smoke.spec.ts`, `invite.spec.ts`, `register.spec.ts`, `user-lists.spec.ts` and
`org-email.spec.ts` needed no change — `register.spec.ts` had already been swept in `3fcaf5c`,
and the others carry only banners, separators and rationale.

Net: **33 insertions, 36 deletions**. The diff is comment lines only. No statement, selector
string, template literal or route pattern was altered anywhere in the sweep.

## What was deleted (21)

Pure paraphrase — the comment restated the line beneath it:

- `fixtures/auth.ts` — `// Inject sessionStorage before any page script runs` (the fixture's
  own JSDoc, four lines up, already says this and says *why*)
- `global-setup.ts` — `// 2. Wait to land on the Login SPA…`, `// 3. Fill credentials`
- `account.spec.ts` — `// Confirm if there's an alert dialog`, `// Confirm if needed`,
  `// Click "Connect" (or "Link") for any provider`
- `org-lifecycle.spec.ts` — `// Find the row for Acme Corp and trigger suspend via dropdown /
  button`, `// Confirm dialog if present`, `// Confirm if there's a confirmation dialog`
- `project-authentication.spec.ts` — `// Enable Google`, `// After enabling, client_id and
  secret fields should be visible`, `// The PATCH body should not include the secret field`
- `system-users.spec.ts` — `// Open dropdown on Alice's row (last cell)`, `// Flash message`
- `user-list-members.spec.ts` — `// After revoke-all the dialog should show empty state`
- `webhooks.spec.ts` — `// Confirm if needed`, `// Click on the first delivery row to expand
  payload`
- `login.spec.ts` — `// Try submitting with only email filled`, and one stale comment (below)
- `mfa.spec.ts` — `// Mock the WebAuthn browser API to throw NotAllowedError` (the test name
  already ends in `(NotAllowedError)` and the file banner already explains the mocking strategy)
- `password-reset.spec.ts` — `// Drive to the password step`

## What was promoted, and why each was load-bearing (14)

Every one of these records something a reader could not recover from the code, and most of it
is flakiness knowledge — the expensive kind.

**`fixtures/mock-api.ts` — `toGlob()` → JSDoc.** Was `// Turn plain path … into a glob that
ignores origin`. The `**` prefix is not decoration: `page.route()` globs are matched against
the *full* URL, so a bare `/admin/organizations` matches nothing at all. Without this recorded,
the obvious "cleanup" is to drop the prefix and every mock in the suite silently stops
intercepting — tests then hit a real backend or hang. The JSDoc also notes RegExp patterns pass
through untouched, which is why half the specs use regexes and half use strings.

**`global-setup.ts` — the consent-screen race.** Was `// 4. Handle optional consent screen`.
Now records that Hydra shows consent only on the *first* grant for a client, which is why the
step is a `Promise.race` with a swallowed `.catch(() => {})` rather than a plain click. This is
the single most likely thing in the suite to be "tidied" into a straight click, which would
then fail on every run after the first — a failure that reproduces only on a fresh database.

**`global-setup.ts` — the `networkidle` wait.** Was `// 5. Give React time to finish the OIDC
callback`. Now says the token is written by the SPA's callback handler rather than by the
navigation, so there is nothing to await but the network going quiet, and that the rejection is
swallowed deliberately because the `sessionStorage` emptiness check below is the real
assertion. Explains both the explicit wait *and* the swallow.

**`global-setup.ts` — capturing all of `sessionStorage`.** Was `// 6. Capture all sessionStorage
entries`. Now records that oidc-client-ts keys the stored user by issuer and client id, which is
why the code copies every entry instead of reading one known key. Anyone "optimising" this to a
single lookup needs to know the key is not a constant.

**`global-setup.ts` — the opening `goto`.** Kept only the why-half: the navigation exists to
trigger the OIDC redirect chain, not to load a page.

**`org-lifecycle.spec.ts` — the post-create re-mock.** Was `// Reload mock to include new org
after create`. This is the same pattern `3fcaf5c` promoted in `webhooks.spec.ts`, so it now
carries matching wording, plus the mechanism: **Playwright runs route handlers
last-registered-first**, which is the only reason a second `mockGet` on an already-mocked path
does anything. Verified against the Playwright docs (`page.route`: "the most recently registered
route takes precedence"; handlers are `unshift`-ed). A second registration on the same path
looks like dead code or a copy-paste slip, and deleting it breaks the refetch assertion in a way
that looks like an app bug.

**`project-authentication.spec.ts` — the saved-secret placeholder.** Was `// Secret field should
show a "saved" placeholder, not expose the value`. Now states the security property: a stored
secret must never be sent back to the browser, so the placeholder is the only signal one exists
— which makes both assertions load-bearing rather than redundant. This is a security-audit
assertion; the note is what stops someone dropping the `toHaveValue('')` check as duplicative.

**`project-authentication.spec.ts` — the untouched secret field.** Was `// Only update
client_id, leave secret untouched`. Now says the omission is on purpose and that filling the
field would defeat the test. A skipped form field reads as an oversight otherwise.

**`webhooks.spec.ts` — the unselected event.** Was `// Don't select any events`. Now says the
omission *is* the condition under test. Same reasoning: an absent step invites a helpful fix.

**`login.spec.ts` — the `toBeFocused()` assertion.** Was `// Browser native validation prevents
submit — password field is required`. Kept and sharpened: the browser's own required-field
validation blocks the submit and focuses the offending input, so focus is the only observable
outcome — there is no app-rendered error message to assert on. Without this, the assertion looks
arbitrary and the natural "improvement" is to look for an error string that never appears.

**`login.spec.ts` — the navigation listener** (also a stale fix, below).

**`login.spec.ts` — the fixed 500 ms wait.** Was `// Give the navigation a moment`. Now records
*why* it cannot be an implicit wait: the navigation never completes, so there is no load state
and no URL change to await — only the outgoing request the listener is watching for. This is
exactly the knowledge that stops someone swapping in `waitForURL` and getting a timeout.

**`mfa.spec.ts` — the WebAuthn options stub** (also a stale fix, below).

**`mfa.spec.ts` — `void origGet`** (also a stale fix, below).

## Hardest judgement calls

**The `// Confirm if needed` family — deleted, and this was the closest call in the sweep.**
Six or seven tests end with the same tolerant block: grab a confirm button, click it only if
it happens to be visible within 500 ms, swallow the failure. There is real knowledge in the
*shape* of that block — that the confirmation dialog is optional, and that hardening it into an
assertion would break tests. That argues for promotion under the tie-break rule.

I deleted them anyway, on two grounds. First, the comments as written (`// Confirm if needed`,
`// Confirm dialog if present`) contain none of that reasoning — they restate `if visible, click`
and nothing more, so deleting them loses nothing that was ever written down. Promoting would
have meant *inventing* a rationale I would be guessing at, and a confidently wrong comment is
worse than no comment. Second, the identical block already appears **uncommented** in
`service-accounts.spec.ts` (three times), `project-authentication.spec.ts`, `user-list-members.spec.ts`
and `org-email.spec.ts`. The comment was on a minority of occurrences; removing it makes the
suite consistent rather than half-annotated.

I also considered extracting a `confirmIfPresent()` helper — the brief explicitly offers that
route, and it would carry one honest JSDoc for all seven sites. I did not do it: it is a code
change, not a comment change, across seven call sites with differing button regexes, and I
cannot run the suite to confirm it. That is precisely the risk the brief warned about. **If the
owner wants that rationale captured, the helper is the right home for it and I would recommend
it as a separate, runnable change.**

**File banners — kept, all 18.** These repeat the file name (`account.spec.ts — Account
self-service page tests`), which the rule nominally condemns. But `3fcaf5c` swept six of these
very files and left every banner intact, so the owner's own precedent in this directory is to
keep them; and unlike the C# class banners that were deleted, these carry route paths, mocking
strategy and prerequisites. Deleting them would have been a style change, not this sweep.

**`// locked for 1h` and `// 5 min from now` — kept.** Trailing annotations on `3_600_000` and
`5 * 60 * 1000`. Arguably arithmetic restatement, but they are unit annotations on opaque
literals and cost one line each. Tie-break applied.

**`// Drive to the password step` — deleted.** The two sibling tests immediately below do the
identical three-mock setup with no such label. Sibling consistency decided it: the comment
carried no knowledge unique to that test.

## Stale comments found (4)

All four were in the login suite and all four were actively wrong, not merely redundant.

1. **`login.spec.ts`** — `// Let the POST go to the real backend but stub it to return an error`.
   Self-contradictory: the line beneath is `page.route(...)` with `route.fulfill`, so nothing
   reaches the backend. It also contradicts the file banner's claim that these tests run against
   the real stack. **Deleted** (paraphrase *and* false).

2. **`login.spec.ts`** — `// Intercept the final navigation so the test doesn't actually follow
   it`. `page.on('request', …)` is an observer; it neither intercepts nor blocks anything, and
   the navigation does proceed. Someone trusting this comment would believe the redirect is
   suppressed. **Rewritten** to state the real reason the listener exists: `redirect_to` points
   outside the Login SPA at a target this environment does not serve, so `page.url()` afterwards
   proves nothing — recording the request captures the target whether or not it loads. The new
   text says explicitly that the listener observes and does not block.

3. **`mfa.spec.ts`** — `// Mock the options fetch and stub credentials.get to prevent a real
   prompt`, on the `renders passkey UI` test. That test stubs only the options route; it never
   touches `navigator.credentials.get` (the test three cases down does). **Rewritten**: answering
   the options fetch with an error stops the page *before* it reaches `credentials.get`, which
   is what prevents a real authenticator prompt no headless browser can dismiss. Same protective
   intent, accurately stated.

4. **`mfa.spec.ts`** — `// Restore after we've set it`, sitting above `void origGet;`. Nothing is
   restored, ever. `origGet` is captured and then discarded; `void` exists only to silence the
   unused-binding warning. **Rewritten** to say so, and to say why not restoring is safe: the
   init script is scoped to a page that is thrown away at the end of the test.

## Findings outside the sweep's remit (reported, not changed)

None of these are comment problems, so none were touched. Flagging them because they surfaced
while reading, and two are live bugs.

1. **`tests/org/org-email.spec.ts` never runs.** `playwright.config.ts` defines three projects
   — `admin`, `account`, `login` — whose `testMatch` globs cover `tests/admin/**`,
   `tests/account/**` and `tests/login/**`. Nothing matches `tests/org/**`. Confirmed
   empirically: `npx playwright test --list` reports **150 tests in 14 files**, and
   `org-email.spec.ts` is absent from the listing while present on disk. Nine SMTP tests —
   including the super-admin endpoint-scoping test — have been silently dead. The file uses the
   `adminPage` fixture, so the fix is most likely widening the `admin` project's `testMatch` or
   moving the file under `tests/admin/`; either is a behaviour change and needs a run to confirm.

2. **`project-authentication.spec.ts:203` — malformed selector.**
   `page.locator('[data-provider="google"] [role="switch"], ')` ends with a comma and an empty
   second selector. The test is written defensively (`if (await googleSwitch.count() > 0)`), so
   it passes either way — which is exactly why this has gone unnoticed and why the test may not
   be asserting what it appears to.

3. **`user-lists.spec.ts:144` — an ineffective re-mock.** Byte-identical to the registration at
   line 136 and returning the same `ORG_LISTS`. It mirrors the post-create re-mock pattern from
   `org-lifecycle.spec.ts` and `webhooks.spec.ts`, but unlike those it does not add the created
   row, so it changes nothing. Either a copy-paste leftover or an incomplete edit. I deliberately
   did **not** annotate it — documenting a bug in place would entrench it.

4. **Uncommented fixed waits.** `await page.waitForTimeout(500)` after a navigation-capture
   listener recurs in `account.spec.ts`, `register.spec.ts` and four times in `mfa.spec.ts`, in
   every case with no comment. The rationale is now recorded once, at the `login.spec.ts`
   instance. I did not add new comments to the bare sites: a sweep judges the comments that
   exist, and writing six fresh ones would be authoring documentation, not sweeping it.

## What was run

From `tests/e2e/`:

- `npm ci` — 7 packages; the directory had no `node_modules`. `node_modules/` is gitignored.
- **`npx tsc --noEmit`** — exit 0, no diagnostics. Config is `strict: true` over `./**/*.ts`,
  so this covers every fixture and spec.
- **`npx playwright test --list`** — exit 0, **150 tests in 14 files** collected. This is the
  check that matters most here: it loads and compiles every spec file through Playwright's own
  pipeline, so a comment edit that had broken a template literal, a selector string or a
  `test()` registration would fail collection. It does not run `globalSetup` and needs no
  deployment.
- Playwright's route-precedence semantics were verified against upstream documentation before
  the claim was written into `org-lifecycle.spec.ts`, rather than asserted from memory.
- Full `git diff` re-read line by line after editing; every changed line is a comment.

**The Playwright suite was NOT executed.** It requires a live deployment (`TEST_BASE_URL`, a
reachable admin SPA, Hydra, and `TEST_SUPER_ADMIN_EMAIL` / `TEST_SUPER_ADMIN_PASSWORD` for
`globalSetup`), none of which was available. Typecheck and test collection both pass, and the
diff contains no executable change, but no test in this directory was actually run.

Nothing was committed.
