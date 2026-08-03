import { test, expect } from '../../fixtures/console';
import { gotoConsole, shell } from '../../fixtures/console';

/**
 * That a console page opened by URL renders, rather than a spinner nothing can resolve.
 *
 * The division of labour matters here. `ConsoleRoutingTests` on the server side asks for all
 * forty-six console routes without an Authorization header and refuses a 401 — that is the check
 * that would have caught the `/admin` collision, and it is cheap enough to cover every route. What
 * it cannot see is whether a human then gets a page: the SPA has to boot, complete an OAuth2 round
 * trip and mount its shell.
 *
 * So this file walks a representative route rather than all forty-nine. Each one is a full
 * sign-in — the token lives in memory, so a fresh load re-runs the whole flow — and thirteen of
 * those in a row made Hydra reject a CSRF cookie belonging to a flow the previous test had
 * abandoned mid-redirect. Repeating the same assertion thirteen times bought nothing and cost the
 * run its reliability.
 */

test('the system scope renders when opened by URL', async ({ console: page }) => {
  await gotoConsole(page, '/console/system/organisations');

  await expect(shell(page)).toBeVisible();
  await expect(page).toHaveURL(/\/console\/system\/organisations$/);
});

test('a page two levels deep renders too', async ({ console: page }) => {
  // The depth is the point: /console/system/* is exactly the prefix the management API owns, and
  // every one of those pages answered a bare 401 before the console had a namespace of its own.
  await gotoConsole(page, '/console/system/audit-log');

  await expect(shell(page)).toBeVisible();
  await expect(page).toHaveURL(/\/console\/system\/audit-log$/);
});

test('an unknown console route lands somewhere real rather than nowhere', async ({ console: page }) => {
  // The catch-all sends an unrecognised path to the scope's home. A blank page here would mean the
  // router matched nothing — which is what a wrong `basename` produces, with one console warning
  // as its only symptom.
  await gotoConsole(page, '/console/this-route-does-not-exist');

  await expect(shell(page)).toBeVisible();
  await expect(page).not.toHaveURL(/this-route-does-not-exist/);
});

test('the sidebar reaches the scope pages without a reload', async ({ console: page }) => {
  // How an operator actually moves. The token lives in memory for the life of the tab, so
  // following a link costs no round trip; anything reachable this way is reachable.
  // `.iam-nav-item`, not every anchor: the brand, the version and the account row are links too,
  // so counting anchors measured the chrome rather than the navigation.
  const items = page.locator('.iam-sidebar .iam-nav-item');
  expect(await items.count(), 'the sidebar renders no navigation at all').toBeGreaterThan(5);

  for (const name of ['Organisations', 'Users', 'Audit Log']) {
    await page.getByRole('link', { name, exact: true }).first().click();
    await expect(shell(page)).toBeVisible();
    await expect(page.locator('.iam-page-title').first()).toBeVisible();
  }
});
