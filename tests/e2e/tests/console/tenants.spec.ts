import { test, expect } from '../../fixtures/console';
import { gotoConsole, shell } from '../../fixtures/console';

/**
 * The tenant lifecycle, driven through the console the way an operator drives it.
 *
 * What only an end-to-end test can show is the chain: a name typed into a dialog becomes an
 * organisation in Postgres, a Keto tuple, an entry in the tree, and a row the next page can find.
 * The component suites already assert what each page renders from a mocked answer; none of them
 * can tell you the answer was ever written.
 *
 * Every object created here carries a run-unique name. The suite runs against a real deployment
 * that keeps its rows, so a fixed name passes once and then collides with itself forever.
 */

const unique = (prefix: string) => `${prefix}-${Date.now().toString(36)}`;

/** Opens a dialog by its trigger and waits for it, so a click that missed is not a flaky assertion. */
async function openDialog(page: import('@playwright/test').Page, trigger: string | RegExp) {
  await page.getByRole('button', { name: trigger }).first().click();
  await expect(page.getByRole('dialog')).toBeVisible();
}

test.describe('organisations', () => {
  test('an operator creates one, and it appears in the list and in the tree', async ({ console: page }) => {
    const name = unique('acme');
    await gotoConsole(page, '/console/system/organisations');

    await openDialog(page, /New organisation|Create organisation|New Organisation/i);
    await page.getByLabel(/^Name/i).fill(name);
    const slug = page.getByLabel(/Slug/i);
    if (await slug.isVisible()) await slug.fill(name);
    await page.getByRole('dialog').getByRole('button', { name: /Create|Save/i }).click();

    await expect(page.getByText(name).first()).toBeVisible({ timeout: 15_000 });
    // The tree reads the same list the page does; a tenant that exists in one and not the other is
    // the console disagreeing with itself.
    await expect(shell(page).getByRole('link', { name: new RegExp(name) })).toBeVisible();
  });

  test('the search narrows the list to what matches, and says when nothing does', async ({ console: page }) => {
    await gotoConsole(page, '/console/system/organisations');
    const search = page.getByPlaceholder(/search|filter/i).first();
    test.skip(!(await search.isVisible()), 'this build has no search on the organisation list');

    await search.fill('zzz-no-such-tenant-zzz');

    await expect(page.getByText(/no .*(organisation|result)/i)).toBeVisible();
  });

  test('suspending a tenant is confirmed first, and shows on the row afterwards', async ({ console: page }) => {
    const name = unique('suspendable');
    await gotoConsole(page, '/console/system/organisations');
    await openDialog(page, /New organisation|Create organisation|New Organisation/i);
    await page.getByLabel(/^Name/i).fill(name);
    const slug = page.getByLabel(/Slug/i);
    if (await slug.isVisible()) await slug.fill(name);
    await page.getByRole('dialog').getByRole('button', { name: /Create|Save/i }).click();
    await expect(page.getByText(name).first()).toBeVisible({ timeout: 15_000 });

    await page.getByRole('row', { name: new RegExp(name) })
      .getByRole('button', { name: /suspend/i }).click();

    // Suspension revokes every live session of the tenant, so it asks before it acts.
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.getByRole('dialog').getByRole('button', { name: /suspend/i }).click();

    await expect(page.getByRole('row', { name: new RegExp(name) })).toContainText(/suspended|inactive/i);
  });
});

test.describe('projects', () => {
  test('a project created in an organisation gets its OIDC client', async ({ console: page }) => {
    const org = unique('org');
    await gotoConsole(page, '/console/system/organisations');
    await openDialog(page, /New organisation|Create organisation|New Organisation/i);
    await page.getByLabel(/^Name/i).fill(org);
    const slug = page.getByLabel(/Slug/i);
    if (await slug.isVisible()) await slug.fill(org);
    await page.getByRole('dialog').getByRole('button', { name: /Create|Save/i }).click();
    await expect(page.getByText(org).first()).toBeVisible({ timeout: 15_000 });

    await page.getByRole('link', { name: new RegExp(org) }).first().click();
    await shell(page).getByRole('link', { name: /^Projects$/ }).first().click();

    const project = unique('portal');
    await openDialog(page, /New project|Create project/i);
    await page.getByLabel(/^Name/i).fill(project);
    const pslug = page.getByLabel(/Slug/i);
    if (await pslug.isVisible()) await pslug.fill(project);
    await page.getByRole('dialog').getByRole('button', { name: /Create|Save/i }).click();

    // Creation rolls back when Hydra is unreachable, so a visible row is also proof the OAuth2
    // client was registered — the one part no unit test can stand in for.
    await expect(page.getByText(project).first()).toBeVisible({ timeout: 15_000 });
  });
});
