import { test, expect, type Page } from '@playwright/test';
import { gotoConsole, shell } from '../../fixtures/console';
import { SEED } from '../../seed-dev.mjs';

/**
 * PLAN §11 — navigation, search and shell.
 *
 * The console's chrome, driven the way an operator drives it. What makes these end-to-end rather
 * than component tests is that the tree is drawn from the deployment's real contents: the tenants
 * come from `GET /admin/organizations` and a tenant's projects are fetched only when its node
 * opens. A mocked list can prove the tree renders what it was handed; only a real one can prove the
 * console asks for the right thing, at the right moment, and agrees with itself about where you are.
 *
 * Two decisions about cost, both deliberate:
 *
 *   - **Destinations are walked inside the SPA, not deep-linked one by one.** The token lives in a
 *     private field, so every `goto` throws it away and re-runs the whole OAuth2 round trip; the
 *     previous version of this file records thirteen of those in a row making Hydra reject a CSRF
 *     cookie from a flow the test before it had abandoned mid-redirect. Following a link costs no
 *     round trip and exercises the same route table. Deep-linking is asserted separately, on one
 *     URL per shape, because that is the property a fresh load actually adds.
 *   - **Names come from `SEED`.** `Acme Corporation` has to be *in* the tree for "expand a tenant"
 *     to mean anything, and a test cannot create a tenant with two projects without spending its
 *     runtime on setup.
 */

const ACME = SEED.orgs.acme.name;
const PORTAL = SEED.projects.acmePortal.name;

/** The navigation tree. Scoped to the sidebar because every name in it also appears on the page. */
const tree = (page: Page) => shell(page).getByRole('tree', { name: 'Console navigation' });

/**
 * One row of the tree, by its label.
 *
 * `.iam-treeitem` rather than a role: the same label appears at three depths — `Service accounts`
 * belongs to the deployment, to every tenant and to every project — so a text match alone is
 * ambiguous the moment two levels are open. The caller narrows by closing what it is not asking
 * about, or by using {@link destination} below.
 */
const node = (page: Page, label: string) =>
  tree(page).locator('.iam-treeitem').filter({ hasText: label });

/**
 * The destination row at a given depth. Depth is how the tree says which level a row belongs to —
 * `paddingLeft: depth * 13` — and it is the only thing that distinguishes two identically named
 * destinations of two open levels.
 */
const atDepth = (page: Page, label: string, depth: number) =>
  tree(page).locator(`.iam-trow[style*="padding-left: ${depth * 13}px"] .iam-treeitem`)
    .filter({ hasText: label });

/** Opens a collapsed node and waits for its children, so a click that missed is not a flaky assert. */
async function expand(page: Page, label: string) {
  await tree(page).getByRole('button', { name: `Expand ${label}` }).click();
  await expect(tree(page).getByRole('button', { name: `Collapse ${label}` })).toBeVisible();
}

test.describe('the tree', () => {
  test('expanding a tenant reveals its destinations and its projects', async ({ page }) => {
    await gotoConsole(page, '/console/system/organisations');

    // Closed on arrival: the tree opens the node you are on, and /system/organisations is the
    // deployment's, not a tenant's.
    await expect(tree(page).getByRole('button', { name: `Expand ${ACME}` })).toBeVisible();
    await expand(page, ACME);

    // A destination only an organisation has — the deployment level offers no Webhooks, so this
    // one word is enough to say the org's own list was drawn.
    await expect(node(page, 'Webhooks')).toBeVisible();
    // And its projects, fetched when the node opens rather than up front: a deployment with fifty
    // tenants would otherwise make fifty requests to draw a sidebar.
    await expect(node(page, PORTAL)).toBeVisible();
    await expect(node(page, SEED.projects.acmeInternal.name)).toBeVisible();
  });

  test('clicking a tenant destination moves the URL and lights the node', async ({ page }) => {
    await gotoConsole(page, '/console/system/organisations');
    await expand(page, ACME);

    // Depth 2 is the tenant's own destinations; the deployment's sit at the same label and depth 2
    // under a different parent, so the click is aimed by the row that follows the tenant.
    await atDepth(page, 'User lists', 2).nth(1).click();

    await expect(page).toHaveURL(/\/console\/system\/organisations\/[^/]+\/userlists$/);
    // `.active` is what the tree calls "you are here", and it is derived from the pathname rather
    // than from the click — a URL reached any other way must light the same row.
    await expect(tree(page).locator('.iam-treeitem.active')).toHaveText(/User lists/);
  });

  test('a project node opens to the project level', async ({ page }) => {
    await gotoConsole(page, '/console/system/organisations');
    await expand(page, ACME);
    await expand(page, PORTAL);

    // Roles exists at the project level and nowhere else, so it needs no disambiguation.
    await node(page, 'Roles').click();

    await expect(page).toHaveURL(/\/console\/system\/organisations\/[^/]+\/projects\/[^/]+\/roles$/);
    await expect(tree(page).locator('.iam-treeitem.active')).toHaveText(/Roles/);
  });

  test('the filter narrows to what matches, and nothing matches nothing', async ({ page }) => {
    await gotoConsole(page, '/console/system/organisations');
    const filter = shell(page).getByLabel('Filter the tree');

    await filter.fill('webh');
    // A destination label, so every tenant stays visible: the match is *inside* them, and hiding
    // the tenant would hide the thing that matched.
    await expect(node(page, ACME)).toBeVisible();
    await expect(node(page, 'Organisations')).toHaveCount(0);

    await filter.fill(ACME);
    await expect(node(page, ACME)).toBeVisible();
    await expect(node(page, SEED.orgs.globex.name)).toHaveCount(0);

    await filter.fill('zzz-nothing-matches-this-zzz');
    await expect(node(page, ACME)).toHaveCount(0);

    await filter.fill('');
    await expect(node(page, SEED.orgs.globex.name)).toBeVisible();
  });
});

test.describe('destinations', () => {
  /**
   * Every destination of every level, in one session.
   *
   * `scope.ts` is the single description of what the console has, and `App.tsx` generates its
   * routes from the same list — so a destination the tree offers and the router does not is
   * exactly the defect this catches, for all of them at once. The page title is the assertion: a
   * route that matched nothing renders the catch-all, which navigates away, and a page still
   * fetching renders a spinner with no title.
   *
   * One level open at a time. Two open levels put two `Service accounts` rows in the tree, and a
   * walk that cannot say which one it means is a walk that proves nothing about either.
   */
  const walk = async (page: Page, labels: string[], depth: number) => {
    for (const label of labels) {
      await atDepth(page, label, depth).first().click();
      await expect(shell(page)).toBeVisible();
      await expect(page.locator('.iam-page-title').first(), `${label} rendered no page`)
        .toBeVisible({ timeout: 20_000 });
    }
  };

  test('every deployment destination renders a page', async ({ page }) => {
    await gotoConsole(page, '/console/system');

    await walk(page, ['Organisations', 'Admins', 'Users', 'Projects', 'User lists',
                      'Service accounts', 'Email', 'Impersonation', 'Audit log', 'Metrics',
                      'Health', 'Settings'], 2);
  });

  test('every organisation destination renders a page', async ({ page }) => {
    await gotoConsole(page, '/console/system/organisations');
    await expand(page, ACME);
    // Collapse the deployment so its identically named destinations leave the tree entirely.
    await tree(page).getByRole('button', { name: 'Collapse Deployment' }).click();

    await walk(page, ['Projects', 'User lists', 'Admins', 'Service accounts', 'Email',
                      'Audit log', 'Webhooks', 'Settings'], 2);
  });

  test('every project destination renders a page', async ({ page }) => {
    await gotoConsole(page, '/console/system/organisations');
    await tree(page).getByRole('button', { name: 'Collapse Deployment' }).click();
    await expand(page, ACME);
    await expand(page, PORTAL);

    await walk(page, ['Users', 'Roles', 'Service accounts', 'Authentication', 'Settings'], 4);
  });

  test('each URL shape resolves on a fresh load, not only from a link', async ({ page }) => {
    // One per shape rather than per destination: what a fresh load adds over an in-SPA click is the
    // OAuth2 round trip and the router's read of the path, and both are per-shape properties. Doing
    // it for all of them would re-prove the same two things twenty-odd times, at the price of
    // twenty-odd sign-ins.
    await gotoConsole(page, '/console/system/audit-log');
    await expect(page).toHaveURL(/\/console\/system\/audit-log$/);
    await expect(page.locator('.iam-page-title').first()).toBeVisible();

    await expand(page, ACME);
    await node(page, 'Webhooks').click();
    const orgPath = new URL(page.url()).pathname;
    expect(orgPath).toMatch(/\/console\/system\/organisations\/[^/]+\/webhooks$/);

    await gotoConsole(page, orgPath);
    await expect(page).toHaveURL(new RegExp(`${orgPath}$`));
    await expect(page.locator('.iam-page-title').first()).toBeVisible();
  });

  test('an unknown console route lands somewhere real rather than nowhere', async ({ page }) => {
    // The catch-all sends an unrecognised path to the scope's home. A blank page here would mean
    // the router matched nothing — which is what a wrong `basename` produces, with one console
    // warning as its only symptom.
    await gotoConsole(page, '/console/this-route-does-not-exist');

    await expect(shell(page)).toBeVisible();
    await expect(page).not.toHaveURL(/this-route-does-not-exist/);
  });
});

test.describe('the breadcrumb', () => {
  const crumbs = (page: Page) => page.locator('.iam-scope-breadcrumb .iam-scope-chip');

  test('it gains a level as you descend, and names what the tree names', async ({ page }) => {
    await gotoConsole(page, '/console/system');
    await expect(crumbs(page)).toHaveCount(1);

    await expand(page, ACME);
    await node(page, 'Webhooks').click();
    await expect(crumbs(page)).toHaveCount(2);
    // The name, not the id: the topbar falls back to a twelve-character slice of the id while the
    // scope is still loading, and a breadcrumb that settles on that is one that never resolved.
    await expect(crumbs(page).nth(1)).toContainText(ACME);

    await expand(page, PORTAL);
    await node(page, 'Roles').click();
    await expect(crumbs(page)).toHaveCount(3);
    await expect(crumbs(page).nth(2)).toContainText(PORTAL);
  });

  test('a crumb navigates back up', async ({ page }) => {
    await gotoConsole(page, '/console/system');
    await expand(page, ACME);
    await expand(page, PORTAL);
    await node(page, 'Roles').click();

    await crumbs(page).first().click();

    await expect(page).toHaveURL(/\/console\/system$/);
  });
});

test.describe('the command palette', () => {
  const palette = (page: Page) => page.getByRole('dialog', { name: 'Command palette' });
  const query = (page: Page) => page.getByRole('combobox', { name: 'Search pages, actions' });

  test('Control+K opens it and Escape closes it', async ({ page }) => {
    await gotoConsole(page, '/console/system');

    await page.keyboard.press('Control+k');
    await expect(palette(page)).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(palette(page)).toBeHidden();
  });

  test('Meta+K opens it too', async ({ page }) => {
    // Both bindings, because the same operator uses both machines and the handler tests
    // `metaKey || ctrlKey` — a regression that dropped one would go unnoticed on the other.
    await gotoConsole(page, '/console/system');

    await page.keyboard.press('Meta+k');

    await expect(palette(page)).toBeVisible();
  });

  test('the search button opens it as well', async ({ page }) => {
    await gotoConsole(page, '/console/system');

    await page.getByRole('button', { name: /Search anywhere/ }).click();

    await expect(palette(page)).toBeVisible();
  });

  test('it filters, and Enter goes to the selected result', async ({ page }) => {
    await gotoConsole(page, '/console/system');
    await page.keyboard.press('Control+k');

    await query(page).fill('audit');
    // Not a count: how many groups offer an "Audit log" depends on which levels the token reaches,
    // which is a fact about the operator and not about the palette. What is asserted is that the
    // list narrowed and that the first result is the one Enter will take.
    const options = palette(page).getByRole('option');
    await expect(options.first()).toContainText('Audit log');
    await expect(options.first()).toHaveAttribute('aria-selected', 'true');

    await page.keyboard.press('Enter');

    await expect(palette(page)).toBeHidden();
    await expect(page).toHaveURL(/audit-log$/);
  });

  test('a query that matches nothing says so rather than offering everything', async ({ page }) => {
    await gotoConsole(page, '/console/system');
    await page.keyboard.press('Control+k');

    await query(page).fill('zzz-no-such-page-zzz');

    await expect(palette(page).getByRole('option')).toHaveCount(0);
    await expect(palette(page).getByText('No matches')).toBeVisible();
  });

  test('the arrows move the selection and not the caret', async ({ page }) => {
    await gotoConsole(page, '/console/system');
    await page.keyboard.press('Control+k');
    // Typed rather than filled: `fill` sets the value in one shot, and what is under test is what a
    // keystroke does to the caret.
    await query(page).click();
    await page.keyboard.type('se');

    const options = palette(page).getByRole('option');
    await expect(options.first()).toHaveAttribute('aria-selected', 'true');

    await page.keyboard.press('ArrowDown');
    await expect(options.nth(1)).toHaveAttribute('aria-selected', 'true');

    await page.keyboard.press('ArrowUp');
    await expect(options.first()).toHaveAttribute('aria-selected', 'true');

    // The caret stayed at the end: the handler calls preventDefault, and without it ArrowUp would
    // jump to the start of the query and the next keystroke would land in the wrong place.
    await expect(query(page)).toHaveValue('se');
    expect(await query(page).evaluate((el: HTMLInputElement) => el.selectionStart)).toBe(2);
  });

  test('it reopens empty rather than showing the last search', async ({ page }) => {
    await gotoConsole(page, '/console/system');
    await page.keyboard.press('Control+k');
    await query(page).fill('metrics');

    await page.keyboard.press('Escape');
    await expect(palette(page)).toBeHidden();

    // Through the button, not the shortcut. Escape closes the <dialog> natively, which fires
    // onClose and sets the shell's flag to false — but the keydown handler *toggles*, and whether
    // its state has caught up with the dialog's is a race this test is not about.
    await page.getByRole('button', { name: /Search anywhere/ }).click();

    await expect(query(page)).toHaveValue('');
  });
});

test.describe('the shell', () => {
  test('the theme toggle survives a reload', async ({ page }) => {
    await gotoConsole(page, '/console/system');
    const toggle = () => shell(page).getByRole('button', { name: /Switch to (dark|light) theme/ });
    const before = await toggle().getAttribute('aria-pressed');

    await toggle().click();
    const after = await toggle().getAttribute('aria-pressed');
    expect(after).not.toBe(before);

    // The reload is the assertion: a theme kept only in React state looks identical until the page
    // comes back, and this is the one place a component test cannot follow.
    await gotoConsole(page, '/console/system');

    await expect(toggle()).toHaveAttribute('aria-pressed', after!);
  });

  test('the account menu navigates, and closes on an outside click', async ({ page }) => {
    await gotoConsole(page, '/console/system');
    const opener = shell(page).getByRole('button', { name: 'Account & sign out' });
    const account = shell(page).getByRole('button', { name: /My Account/ });

    await opener.click();
    await expect(account).toBeVisible();

    // The popover listens for mousedown outside itself. Escape is *not* asserted here: the popover
    // does not answer it today — a real gap, filed rather than papered over, because a test that
    // asserted the current behaviour would make the gap look intended.
    await page.locator('.iam-main-scroll').click({ position: { x: 5, y: 5 } });
    await expect(account).toBeHidden();

    await opener.click();
    await account.click();
    await expect(page).toHaveURL(/\/console\/account$/);
  });
});
