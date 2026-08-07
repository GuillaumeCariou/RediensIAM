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

    // Row actions live behind a per-row menu, named for the tenant it acts on.
    await page.getByRole('button', { name: `Actions for ${name}` }).click();
    await page.getByRole('button', { name: 'Suspend', exact: true }).click();

    // Suspension revokes every live session of the tenant — its own administrators are signed out
    // mid-task — so it asks before it acts, the way Delete does.
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.getByRole('dialog').getByRole('button', { name: 'Suspend', exact: true }).click();

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

    // Through the tree, and through the tenant's own Projects rather than the deployment's: both
    // rows carry the label, and the deployment-wide list offers no way to create one because a
    // project belongs to an organisation.
    const tree = shell(page).getByRole('tree', { name: 'Console navigation' });
    await tree.getByRole('button', { name: 'Collapse Deployment' }).click();
    await tree.getByRole('button', { name: `Expand ${org}` }).click();
    await tree.getByRole('link', { name: 'Projects', exact: true }).click();

    const project = unique('portal');
    await openDialog(page, /New project|Create project/i);
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel(/^Name/i).fill(project);
    await dialog.getByLabel(/^Slug/i).fill(project);
    // A redirect URI, because a project is an OIDC client and one with nowhere to send the browser
    // back to is refused. The form does not mark the field required, so leaving it empty fails on
    // the server and the dialog stays open carrying the reason.
    await dialog.getByLabel('Redirect URIs (one per line)', { exact: true }).fill('https://portal.example.test/callback');
    await dialog.getByRole('button', { name: /Create|Save/i }).click();

    // The dialog closing is the first proof: it stays open and shows the reason when the server
    // refuses, so asserting only on the row below would time out without saying why.
    await expect(dialog).toBeHidden({ timeout: 15_000 });

    // Creation rolls back when Hydra is unreachable, so a visible row is also proof the OAuth2
    // client was registered — the one part no unit test can stand in for.
    await expect(page.getByText(project).first()).toBeVisible({ timeout: 15_000 });
  });
});
