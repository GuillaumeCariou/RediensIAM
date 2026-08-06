import { test, expect } from '../../fixtures/console';
import { gotoConsole, shell } from '../../fixtures/console';

/**
 * Service accounts, through the page that serves all three levels.
 *
 * The property worth an end-to-end test is the one the component suite can only assert against a
 * mock: that the narrowing is real. `/service-accounts` answers for the **caller** — a super-admin
 * gets every account in the deployment — so a page that forgets to narrow shows another tenant's
 * automation identities with a delete button beside each. That is what shipped until 0.6.1, and it
 * is the assertion below.
 *
 * The token dialog is the other one. A personal access token is shown once and never again; a test
 * that only checks the dialog opened would pass against a page that shows nothing.
 */

const unique = (prefix: string) => `${prefix}-${Date.now().toString(36)}`;

test('the deployment page lists only deployment accounts', async ({ console: page }) => {
  await gotoConsole(page, '/console/system/service-accounts');

  await expect(shell(page)).toBeVisible();
  // Every row here belongs to the __system__ list. The page is allowed to be empty; it is not
  // allowed to show an account that belongs to a tenant.
  const rows = page.getByRole('row');
  const count = await rows.count();
  for (let i = 1; i < count; i++) {
    await expect(rows.nth(i)).not.toContainText(/@org|tenant-/i);
  }
});

test('creating one, issuing a token, and revoking it', async ({ console: page }) => {
  await gotoConsole(page, '/console/system/service-accounts');

  const name = unique('ci-bot');
  await page.getByRole('button', { name: /New service account/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByLabel(/^Name/i).fill(name);
  await page.getByRole('dialog').getByRole('button', { name: /^Create$/i }).click();
  await expect(page.getByText(name)).toBeVisible({ timeout: 15_000 });

  await page.getByRole('row', { name: new RegExp(name) }).getByRole('button', { name: /^Tokens$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button', { name: /Generate token/i }).click();
  await page.getByLabel(/^Name/i).fill('e2e');
  await page.getByRole('button', { name: /^Generate$/i }).click();

  // Shown once: the value has to be on screen now, because nothing can fetch it again.
  await expect(page.getByText(/^rediens_pat_/)).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText(/not shown again/i)).toBeVisible();
  await page.getByRole('button', { name: /^Done$/i }).click();

  await page.getByRole('row', { name: new RegExp(name) }).getByRole('button', { name: /^Tokens$/i }).click();
  await page.getByRole('button', { name: /^Revoke$/i }).click();
  await expect(page.getByRole('button', { name: /^Revoke$/i })).toHaveCount(0);
});

test('deleting one asks first and says what else goes', async ({ console: page }) => {
  await gotoConsole(page, '/console/system/service-accounts');

  const name = unique('doomed');
  await page.getByRole('button', { name: /New service account/i }).click();
  await page.getByLabel(/^Name/i).fill(name);
  await page.getByRole('dialog').getByRole('button', { name: /^Create$/i }).click();
  await expect(page.getByText(name)).toBeVisible({ timeout: 15_000 });

  await page.getByRole('row', { name: new RegExp(name) }).getByRole('button', { name: /^Delete$/i }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText(/tokens .*will also be revoked/i);
  await dialog.getByRole('button', { name: /^Delete$/i }).click();

  await expect(page.getByText(name)).toHaveCount(0, { timeout: 15_000 });
});
