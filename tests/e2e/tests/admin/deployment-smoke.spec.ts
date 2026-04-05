/**
 * deployment-smoke.spec.ts — TODO2 ingress smoke tests.
 *
 * Verifies that after the TODO2 ingress refactor:
 *   - /admin/* is NOT reachable on the public port (localhost:80) → 404
 *   - /admin/* IS reachable on the NodePort (localhost:30501) → 200
 *
 * These tests make real HTTP requests (no mocks) and require the dev stack
 * to be running. They run under the "login" Playwright project so no OIDC
 * session is needed.
 */
import { test, expect } from '@playwright/test';

const PUBLIC_BASE  = process.env['TEST_BASE_URL']  ?? 'http://localhost';
const ADMIN_BASE   = process.env['TEST_ADMIN_URL']  ?? 'http://localhost:30501';

test('public ingress returns 404 for /admin/ path', async ({ request }) => {
  const response = await request.get(`${PUBLIC_BASE}/admin/`, { failOnStatusCode: false });
  expect(response.status()).toBe(404);
});

test('admin NodePort returns 200 for /admin/ path', async ({ request }) => {
  const response = await request.get(`${ADMIN_BASE}/admin/`, { failOnStatusCode: false });
  expect(response.status()).toBe(200);
});
