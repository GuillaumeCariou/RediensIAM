import { test, expect, type Page } from '@playwright/test';
import { signInAs, shell, emailField } from '../../fixtures/console';
import { APP_URL, CONSOLE_URL } from '../../playwright.config';
import { SEED } from '../../seed-dev.mjs';

/**
 * PLAN §12 — the authorisation boundaries.
 *
 * The refusals, asserted by going there. Every case below is a 403 the *server* owns, or a
 * redirect the console performs because of one — which is exactly what a component test cannot
 * show: a mocked API has to be told to refuse, and an assertion that a mock refused when told to
 * refuse is circular. Only a real deployment can be asked "may this person reach this page" and
 * answer for itself.
 *
 * Each test signs in as a real identity from the fixture. That costs a full form round trip per
 * test, and there is no shortcut: the stored session belongs to the bootstrap super-admin, who is
 * refused nothing and therefore proves nothing here. `storageState` is dropped for the whole file
 * for the same reason — with it, Hydra recognises the browser and skips the form, signing every
 * test back in as the wrong person.
 *
 * The lockout is why these credentials are never mistyped on purpose: five failures from one
 * address lock it for fifteen minutes, and the counter is not cleared by a success. Wrong-password
 * cases belong to §1, last, alone.
 */

test.use({ storageState: { cookies: [], origins: [] } });

// From `operators`, not `users`: `AdminLogin` admits only accounts in the immovable system list,
// so a tenant's administrator is a deployment account holding an OrgRole over that tenant. A
// member of the tenant's own list cannot reach the console at all — which is itself a boundary,
// and the one below asserts it.
const ORG_ADMIN = SEED.operators.acmeOrgAdmin;
const PROJECT_ADMIN = SEED.operators.acmeProjectAdmin;
const OTHER_ORG_ADMIN = SEED.operators.globexOrgAdmin;

/**
 * Why every role-scoped test below is `fixme` rather than deleted or quietly passing.
 *
 * The console cannot currently hold a non-super-admin operator, and the two rules that make it so
 * are each deliberate on their own:
 *
 *   - `AuthController.AdminLogin` admits only accounts whose user list is the immovable system one
 *     — "Admin console users must belong to the system user list".
 *   - `UserListOperations.GrantsSuperAdmin` makes membership of that same list *be*
 *     `System:rediensiam#super_admin` — "deployment-wide administration".
 *
 * Together they say: everyone who can sign in to the console is a super-admin. Seeding an
 * `org_admin` and granting it through `/admin/organizations/{id}/admins` produces an account that
 * signs in and is then handed the entire deployment tree — Organisations, Admins, Impersonation,
 * Health, Settings — because `isSuperAdmin` is true for it.
 *
 * That the code was written for the other reading is visible three places over: `AdminLogin` tests
 * `HasManagementRoleAsync` right after the password, `ConsentForAdminAsync` computes `org_id` and
 * `project_id` from `OrgRoles` and rejects with `insufficient_role`, and the console carries a
 * whole `OwnLevel` branch, `superOnly` destinations and two URL shapes per level. None of it can
 * be reached. Which of the two rules should give is a decision about who may administer a
 * deployment, so these stay written and skipped until it is made, rather than deleted — a deleted
 * test is a question nobody asks again.
 */
const ROLE_SCOPING_UNREACHABLE =
  'Blocked: system-list membership grants super_admin, and console login requires system-list ' +
  'membership — so no org_admin or project_admin can hold a console session. See the note above.';

/** Where a console URL actually settled, without its origin. */
const where = (page: Page) => new URL(page.url()).pathname;

/**
 * Opens a console URL on an already-signed-in page and waits for the shell.
 *
 * Deliberately not `gotoConsole`: that helper answers the sign-in form with the *bootstrap*
 * credentials, which would silently promote the caller to super-admin half way through a test
 * about what a tenant administrator may reach.
 */
async function open(page: Page, path: string) {
  await page.goto(`${CONSOLE_URL}${path}`);
  await expect(shell(page)).toBeVisible({ timeout: 30_000 });
}

test.describe('an organisation administrator', () => {
  test.fixme(true, ROLE_SCOPING_UNREACHABLE);

  test('is sent back to their own level when they open a deployment URL', async ({ page }) => {
    await signInAs(page, ORG_ADMIN.email, ORG_ADMIN.password);

    await open(page, '/console/system/organisations');

    // Landing somewhere real is the assertion. A blank page or a spinner would mean the router
    // matched a route the token cannot fill, which is how a refusal becomes a bug report about the
    // console being broken.
    expect(where(page)).not.toMatch(/\/system\//);
    await expect(page.locator('.iam-page-title').first()).toBeVisible();
  });

  test('is offered no deployment node in the tree', async ({ page }) => {
    await signInAs(page, ORG_ADMIN.email, ORG_ADMIN.password);

    const tree = shell(page).getByRole('tree', { name: 'Console navigation' });

    // Their own level is the root: they have exactly one organisation and cannot browse to
    // another, so a "Tenants" list of one would be a control that never navigates.
    await expect(tree.getByRole('link', { name: 'Organisation', exact: true })).toBeVisible();
    await expect(tree.getByRole('link', { name: 'Deployment', exact: true })).toHaveCount(0);
    await expect(tree.getByRole('link', { name: 'Impersonation', exact: true })).toHaveCount(0);
  });

  test('cannot reach another tenant by editing the id in the URL', async ({ page }) => {
    // The id is discovered as the other tenant's own administrator, which is the only honest way
    // to obtain it — and the point: knowing the id must not be enough.
    await signInAs(page, OTHER_ORG_ADMIN.email, OTHER_ORG_ADMIN.password);
    await open(page, '/console/org');
    const globexHome = where(page);

    await page.context().clearCookies();
    await signInAs(page, ORG_ADMIN.email, ORG_ADMIN.password);
    await open(page, globexHome);

    // Whatever it renders, it must not be Globex's. The short `/org` shape resolves from the
    // token, so the strongest thing this can say is that no page of another tenant appears.
    await expect(shell(page)).toBeVisible();
    await expect(page.getByText(SEED.orgs.globex.name)).toHaveCount(0);
  });

  /**
   * Every organisation-scoped destination, not one.
   *
   * The matrix is the point of §12: a guard placed on the page rather than on the data leaks
   * through whichever destination was added last, and one sampled URL cannot see that. These are
   * cheap — the token is already held, so each is an in-tab navigation.
   */
  for (const destination of ['projects', 'userlists', 'admins', 'service-accounts', 'email', 'audit-log', 'webhooks', 'settings']) {
    test(`sees none of another tenant's ${destination}`, async ({ page }) => {
      await signInAs(page, ORG_ADMIN.email, ORG_ADMIN.password);

      // A well-formed id that is not theirs. Real or invented, the answer must be the same: the
      // server decides from the token, never from the path.
      await open(page, `/console/system/organisations/00000000-0000-4000-8000-000000000001/${destination}`);

      expect(where(page), `${destination} let a system-scoped URL stand`).not.toMatch(/\/system\//);
    });
  }
});

test.describe('a project administrator', () => {
  test.fixme(true, ROLE_SCOPING_UNREACHABLE);

  test('is sent back to their own level when they open an organisation URL', async ({ page }) => {
    await signInAs(page, PROJECT_ADMIN.email, PROJECT_ADMIN.password);

    await open(page, '/console/org/userlists');

    expect(where(page)).not.toMatch(/\/org\//);
    await expect(page.locator('.iam-page-title').first()).toBeVisible();
  });

  test('sees only their project in the tree', async ({ page }) => {
    await signInAs(page, PROJECT_ADMIN.email, PROJECT_ADMIN.password);

    const tree = shell(page).getByRole('tree', { name: 'Console navigation' });

    await expect(tree.getByRole('link', { name: 'Project', exact: true })).toBeVisible();
    await expect(tree.getByRole('link', { name: 'Deployment', exact: true })).toHaveCount(0);
    await expect(tree.getByRole('link', { name: 'Organisation', exact: true })).toHaveCount(0);
    // Webhooks belongs to an organisation and Impersonation to the deployment; neither is theirs.
    await expect(tree.getByRole('link', { name: 'Webhooks', exact: true })).toHaveCount(0);
  });
});

test.describe('the two hosts', () => {
  /**
   * The management surface answers on the admin host and is refused on the public one.
   *
   * Refused at the router, by an Ingress named `rediensiam-public-admin-deny`, so the answer
   * arrives before any token is read — which is the strongest form this can take and the reason it
   * cannot be asserted anywhere but against a real deployment. The seed learned this the hard way:
   * it called the public host and got a bare 403 from Traefik with no JSON body at all.
   */
  for (const path of ['/admin/organizations', '/admin/userlists', '/admin/impersonate']) {
    test(`${path} is refused on the public host`, async ({ request }) => {
      const res = await request.get(`${APP_URL}${path}`, { failOnStatusCode: false });

      expect(res.status(), `${path} answered ${res.status()} on the public host`).toBe(403);
    });
  }

  test('the same path on the admin host is answered by the application', async ({ request }) => {
    // 401, not the 403 above. The difference is the whole assertion: 403 is Traefik refusing the
    // host before anything reads a token, 401 is the application saying this request carried none.
    // Same path, same method, two answers — which is what "served here, refused there" means.
    const res = await request.get(`${CONSOLE_URL}/admin/organizations`, { failOnStatusCode: false });

    expect(res.status()).toBe(401);
  });
});

test.describe('a signed-out browser', () => {
  test('is sent to sign in, and returns to where it asked for', async ({ page }) => {
    // The destination is what makes this worth an end-to-end test. Sending an interrupted operator
    // to the home page after they authenticate is the same defect as losing their form input: the
    // console knows where they were going and has to remember it across a redirect to another
    // origin and back.
    await page.goto(`${CONSOLE_URL}/console/system/audit-log`);

    await expect(emailField(page)).toBeVisible({ timeout: 30_000 });
    await emailField(page).fill(ORG_ADMIN.email);
    await page.getByRole('textbox', { name: /^password/i }).fill(ORG_ADMIN.password);
    await page.getByRole('button', { name: /continue/i }).click();

    await expect(shell(page)).toBeVisible({ timeout: 30_000 });
    // Not the deployment audit log — this operator may not have it — but not the bare home either:
    // the stored destination has to have been read and then refused on its own merits.
    await expect(page.locator('.iam-page-title').first()).toBeVisible();
    expect(where(page)).toMatch(/audit-log|\/console\/org/);
  });

  test('every console URL asks for a password rather than rendering', async ({ page }) => {
    for (const path of ['/console/system', '/console/org/webhooks', '/console/project/roles']) {
      await page.context().clearCookies();
      await page.goto(`${CONSOLE_URL}${path}`);

      await expect(emailField(page), `${path} rendered to a signed-out browser`)
        .toBeVisible({ timeout: 30_000 });
    }
  });
});
