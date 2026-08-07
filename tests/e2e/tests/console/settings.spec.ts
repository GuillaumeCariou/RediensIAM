import { test, expect } from '../../fixtures/console';
import { gotoConsole, orgIdFor, shell } from '../../fixtures/console';
import { SEED } from '../../seed-dev.mjs';

/**
 * Deployment settings and user lists.
 *
 * The settings page is the one place in the console where an end-to-end test can prove something a
 * component test structurally cannot: that a value typed into a form is still there after a
 * reload. The instance row is a configuration provider, and until 0.7.0 re-reading the environment
 * overwrote whatever the console had written — a control that did not hold. Only a real round trip
 * against a real row can tell the difference.
 *
 * The navigation tests this file used to carry now live in navigation.spec.ts, which covers PLAN
 * §11 properly — three assertions about the tree and the palette were a thin copy of twenty-one.
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
  test('a list created inside a tenant appears on the deployment-wide index', async ({ console: page }) => {
    // Created where creation lives. The deployment page offers no button on purpose — a user list
    // belongs to an organisation, and `/system/userlists` is an index across every tenant, not a
    // place to make one. What is worth an end-to-end test is that the two views agree: a list made
    // inside Acme has to turn up in the list of all lists.
    const acme = await orgIdFor(page, SEED.orgs.acme.name);
    await gotoConsole(page, `/console/system/organisations/${acme}/userlists`);

    const name = unique('list');
    await page.getByRole('button', { name: /New user list/i }).first().click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.getByLabel(/^Name/i).fill(name);
    await page.getByRole('dialog').getByRole('button', { name: /Create|Save/i }).click();
    await expect(page.getByText(name).first()).toBeVisible({ timeout: 15_000 });

    await gotoConsole(page, '/console/system/userlists');

    await expect(page.getByText(name).first()).toBeVisible({ timeout: 15_000 });
    await page.getByText(name).first().click();
    await expect(page).toHaveURL(/userlists\/[0-9a-f-]{36}/i);
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
