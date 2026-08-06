import { test, expect } from '../../fixtures/console';
import { gotoConsole, shell } from '../../fixtures/console';

/**
 * Deployment settings, user lists, and the two navigation surfaces.
 *
 * The settings page is the one place in the console where an end-to-end test can prove something a
 * component test structurally cannot: that a value typed into a form is still there after a
 * reload. The instance row is a configuration provider, and until 0.7.0 re-reading the environment
 * overwrote whatever the console had written — a control that did not hold. Only a real round trip
 * against a real row can tell the difference.
 */

const unique = (prefix: string) => `${prefix}-${Date.now().toString(36)}`;

test.describe('deployment settings', () => {
  test('a saved setting is still there after a reload', async ({ console: page }) => {
    await gotoConsole(page, '/console/system/settings');

    const field = page.getByLabel(/Invitation expiry/i);
    await expect(field).toBeVisible({ timeout: 15_000 });
    const wanted = String(24 + (Date.now() % 40));   // a value the row does not already hold
    await field.fill(wanted);
    await page.getByRole('button', { name: /Save changes/i }).click();
    await expect(page.getByText(/Saved \d+ setting/i)).toBeVisible({ timeout: 15_000 });

    await gotoConsole(page, '/console/system/settings');

    await expect(page.getByLabel(/Invitation expiry/i)).toHaveValue(wanted);
  });

  test('an out-of-range value is stored clamped, not refused', async ({ console: page }) => {
    await gotoConsole(page, '/console/system/settings');

    await page.getByLabel(/Invitation expiry/i).fill('100000');
    await page.getByRole('button', { name: /Save changes/i }).click();

    await expect(page.getByText(/Saved \d+ setting/i)).toBeVisible({ timeout: 15_000 });
    // The page re-reads rather than trusting what was typed: showing 100000 where the row holds
    // 720 would be the console lying on the server's behalf.
    await expect(page.getByLabel(/Invitation expiry/i)).toHaveValue('720');
  });

  test('what only the deployment decides is shown, and has no field', async ({ console: page }) => {
    await gotoConsole(page, '/console/system/settings');

    await expect(page.getByText('argon_memory_cost')).toBeVisible({ timeout: 15_000 });
    // Visible as a fact, never as an input: raising the Argon cost from a browser kills the pod
    // that served the request.
    await expect(page.getByLabel(/argon_memory_cost/i)).toHaveCount(0);
  });

  test('nothing can be saved before something changes', async ({ console: page }) => {
    await gotoConsole(page, '/console/system/settings');

    await expect(page.getByRole('button', { name: /Save changes/i })).toBeDisabled();
  });
});

test.describe('user lists', () => {
  test('a list created at deployment level appears and can be opened', async ({ console: page }) => {
    await gotoConsole(page, '/console/system/userlists');

    const name = unique('list');
    await page.getByRole('button', { name: /New user list|Create list/i }).first().click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.getByLabel(/^Name/i).fill(name);
    await page.getByRole('dialog').getByRole('button', { name: /Create|Save/i }).click();

    await expect(page.getByText(name)).toBeVisible({ timeout: 15_000 });
    await page.getByText(name).click();
    await expect(page).toHaveURL(/userlists\/[0-9a-f-]{36}/i);
  });
});

test.describe('the navigation', () => {
  test('the tree filter narrows to what matches', async ({ console: page }) => {
    await gotoConsole(page, '/console/system');

    const filter = shell(page).getByRole('textbox', { name: /Filter the tree/i });
    await expect(filter).toBeVisible();
    await filter.fill('impersonation');

    await expect(shell(page).getByRole('link', { name: 'Impersonation' })).toBeVisible();
    await expect(shell(page).getByRole('link', { name: 'Metrics' })).toHaveCount(0);
  });

  test('the tree opens a tenant onto its own destinations', async ({ console: page }) => {
    await gotoConsole(page, '/console/system');

    const expander = shell(page).getByRole('button', { name: /^Expand / }).nth(1);
    test.skip(!(await expander.isVisible()), 'this deployment has no tenant to expand yet');
    await expander.click();

    await expect(shell(page).getByRole('link', { name: 'Webhooks' })).toBeVisible();
  });

  /** The palette is the shortcut over the tree; both are built from the same destination list. */
  test('the command palette navigates', async ({ console: page }) => {
    await gotoConsole(page, '/console/system');

    await page.keyboard.press('Control+k');
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.keyboard.type('audit');
    await page.keyboard.press('Enter');

    await expect(page).toHaveURL(/audit-log/);
  });
});

test.describe('impersonation', () => {
  test('the page lists live sessions, and offers no way to open one', async ({ console: page }) => {
    await gotoConsole(page, '/console/system/impersonation');

    await expect(shell(page)).toBeVisible();
    // Opening mints a credential and is refused to a browser session by design. A control that
    // always answers 403 reads as a feature the operator is using wrong.
    await expect(page.getByRole('button', { name: /new session|open session|start/i })).toHaveCount(0);
  });
});
