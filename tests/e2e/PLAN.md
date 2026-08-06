# The end-to-end test plan

What has to be driven through a browser against a real deployment, and why each item is here rather
than in a component test.

## The dividing line

The console already has **1248 component tests**. They render a page against a mocked answer and
assert what appears. They are fast, they are exhaustive about rendering, and there is exactly one
thing they cannot do: tell you the answer was ever written, or that the server would have given it.

So an item belongs in this plan when it asserts one of:

1. **A chain** — a name typed into a dialog becomes a row in Postgres, a Keto tuple, a Hydra client
   and an entry in a different page's list.
2. **A refusal the server owns** — a 403 that a mock would have to be told to produce, which makes
   the assertion circular.
3. **Persistence** — a value that survives a reload, a rollout, or another user's session.
4. **A crossing** — an OAuth2 redirect that actually round-trips between two origins.

An item does **not** belong here when it asserts which text a page renders from data it was handed.
That is a component test, it runs in 30 seconds, and duplicating it here buys a slower copy.

## Fixture

Everything below assumes `./deploy/dev-fixture.sh` has run. The named objects come from
`seed-dev.mjs`, and a spec imports `SEED` rather than repeating a literal:

| | |
|---|---|
| Organisations | `Acme Corporation`, `Globex Industries`, `Initech` (suspended) |
| Projects | Acme: `Customer Portal`, `Internal Tools` · Globex: `Globex App` |
| User lists | `Acme Staff`, `Acme Customers`, `Globex Staff` |
| Users | `admin@acme.test`, `user@acme.test`, `locked@acme.test` |
| Service accounts | `deployment-bot` (system list), `acme-ci-bot` (Acme Staff) |

A spec that needs an object nobody else may touch still creates its own with a run-unique name. A
spec that needs an object to *already exist* uses the fixture.

---

## 1 · Authentication and session (≈45)

The only area where the browser is not a convenience but the subject.

- Sign-in: correct credentials, wrong password, unknown email, empty fields, whitespace-only.
- The lockout: five failures lock, the counter is per IP and **is not cleared by a success**, the
  lock expires, a locked account says the same thing an unknown one does.
- MFA: TOTP enrolment during login, TOTP verify, a replayed code refused, backup code, WebAuthn
  registration and assertion, SMS code, the resend budget.
- MFA downgrade guard: removing the last factor from a project that requires MFA.
- Password reset: request, the closed oracle (unknown email answers identically), the emailed
  token, an expired token, a reused token.
- Invitation: accept, expired, already used.
- Social login: the provider round trip, the `state` mismatch refusal.
- SAML: the ACS endpoint, an assertion for an unknown IdP.
- SSO: a second application skips the password, `SsoSessionMinutes = 0` disables the skip.
- Sign-out: the Hydra session ends, the console token dies, the back button does not resurrect it.
- The console's own login: the redirect crossing between `iam.localhost` and `admin.iam.localhost`.

## 2 · Organisations (≈40)

- Create: appears in the list **and** in the tree, gets its `__system__`-adjacent org list.
- Create refused: duplicate slug, empty name, slug with invalid characters.
- Rename; the tree follows.
- Suspend: confirmed first, every live session of that tenant is revoked, the row shows it, the
  tenant's own admin can no longer sign in, an unsuspend restores both.
- Delete: refused while projects exist, or cascades — whichever the server does, asserted.
- The detail page: counts match the lists they link to.
- Audit retention: setting it below the floor is clamped, the audit page reflects it.
- Cross-tenant: a super-admin browsing Acme never sees Globex's rows on any Acme page.

## 3 · Projects (≈45)

- Create: the OIDC client `client_<project_id>` exists in Hydra afterwards.
- Create rolls back when Hydra refuses — the project must not survive its own failure.
- Redirect URIs: add, and the CORS origin follows into the CSP.
- Assign a user list; the members page changes with it; unassign empties it.
- `require_role_to_login`: a user without a role is refused, with a role passes.
- MFA requirement on, then the downgrade guard refuses removing the last factor.
- Allowed email domains: a matching sign-up succeeds, a non-matching one is refused.
- Scopes: add a custom scope, it reaches the Hydra client.
- Delete: the Hydra client goes, the Keto tuples go.

## 4 · Users and user lists (≈70)

- Create a list at deployment level and inside an organisation; the two URL shapes reach the same
  page for a super-admin and for the tenant's own admin.
- Add a user with a password; invite a user without one; the invitation email is issued once.
- The email uniqueness rule is **per list** — the same address in two lists is legal, twice in one
  is a 409 with a message naming the field.
- Discriminator: two users with the same username get different ones.
- Deactivate, reactivate, force sign-out, clear a lockout.
- Delete a user; their roles and grants go with them.
- Export the list as CSV; the rate limit refuses the second export inside the window.
- Search by email and by username; a search that matches nothing says so.
- Bulk selection, if the page offers it.
- The member list of a project is the assigned list's members — change the assignment, the page
  changes.

## 5 · Roles and grants (≈35)

- Create a project role; it is namespaced `{project_id}/{name}` in the token.
- A role named `super_admin` is refused — the reserved names.
- Grant to a user, revoke, and the token stops carrying it **without a re-login**.
- Org admin: grant, revoke, and the revoked admin's next request is refused.
- A project admin cannot grant a role above their own level.
- Keto is the source: a tuple removed out of band is reflected on the next request.

## 6 · Service accounts (≈40)

- The three levels show the right accounts and none of the others' — deployment, organisation, and
  a project whose list decides membership.
- A project with no assigned list shows nothing and offers no creation.
- Create at each level; the list a new account lands on is the level's, not a choice where there is
  none.
- Tokens: issue (shown once), copy, list, revoke, and the revoked token stops introspecting active.
- The token's PAT prefix makes it recognisable.
- Roles on a service account; the introspection answer carries them.
- Delete: the PATs go with it, and a request bearing one is refused.

## 7 · Impersonation (≈25)

- The page lists a live session opened out of band, with operator, tenant, mode and reason.
- Ending one is confirmed, and the delegated token stops working immediately.
- The console offers no way to open one — opening is service-account-only.
- A delegated token introspects with `act`, no roles, and a null `user_id`.
- While `mode: read`, a consuming gateway refuses a mutation (needs a consumer; skip until one
  exists).
- The audit log of the entered tenant shows the session, named by both identities.

## 8 · Webhooks (≈25)

- Create, with a URL the SSRF validator accepts; a private-range URL is refused.
- The signing secret is shown once; rotating it invalidates the old one.
- Test delivery succeeds against a reachable endpoint and reports its status.
- The delivery list shows attempts, and a failed delivery is retried.
- Delete.

## 9 · Audit and integrity (≈25)

- The deployment log shows every tenant's rows; an organisation's shows only its own.
- Paging forward and back asks for the right offsets.
- Export as CSV; a refused export names the failure rather than re-enabling the button silently.
- An action performed in another tab appears after a reload.
- The hash chain: the integrity monitor's verdict is on the page.
- Severity colouring is the same at both levels.

## 10 · Deployment settings (≈25)

- A saved setting survives a reload **and** a pod restart — the second is what `EnvSnapshot` exists
  for, and no other test can see it.
- Out of range is clamped and re-read, not refused.
- `stored` and `in force` are shown apart when the environment overrides one.
- The environment-only settings have no field.
- The SMTP block: save, then a test send uses the new relay.
- Two browser tabs: the second sees the first's change after a reload, and `config_version` moved.

## 11 · Navigation, search and shell (≈35)

- The tree: expand a tenant, expand a project, the URL follows, the node lights.
- The filter narrows; a filter matching a destination keeps its tenant visible.
- Deep-link every destination of every level and get a page, not a spinner.
- The breadcrumb agrees with the tree at every level.
- The command palette: opens on Ctrl+K and Cmd+K, filters, Enter navigates, Escape closes, arrows
  do not move the caret.
- A tenant admin sees no deployment node; a project admin sees only their project.
- The theme toggle survives a reload.
- The account menu: navigate, sign out, close on outside click and on Escape.

## 12 · Authorisation boundaries (≈40)

The refusals. Each one is a page a role must not reach, asserted by going there:

- An org admin opening a deployment URL lands back on their own home, not on a blank page.
- A project admin opening an organisation URL, same.
- A tenant admin cannot reach another tenant by editing the id in the URL — for every
  organisation-scoped destination.
- The management API surface is refused on the public host and served on the admin host.
- A signed-out browser opening any console URL is sent to sign in and **returns to where it asked
  for** afterwards.

## 13 · Resilience (≈20)

- The API refusing (500) leaves a page that says so rather than an empty table.
- A slow answer shows the skeleton and then the content.
- A dialog whose submit fails keeps the operator's input.
- Two tabs writing the same object: the second sees the first's result after a reload.
- A pod restart mid-session: the token in memory dies, the console re-authenticates silently.

---

**≈470 tests**, and the count is a consequence rather than a target: each line above is one
assertion about one behaviour. Sections 1, 12 and 13 are where the number could grow past 500,
because each refusal is per-role and per-destination and the matrix multiplies.

## Order to write them

1. §11 and §12 — navigation and boundaries. They need only the fixture, they cover every page
   cheaply, and a broken route fails them all at once.
2. §2, §3, §4 — the object lifecycles. The most valuable chains.
3. §6, §7, §10 — the surfaces built this week, whose component tests are newest and least proven
   against a server.
4. §5, §8, §9 — grants, webhooks, audit.
5. §1 — authentication. Last, not because it matters least but because it is the slowest and the
   most sensitive to the lockout counter: a spec that fails five times locks the address for
   fifteen minutes and poisons everything after it.
