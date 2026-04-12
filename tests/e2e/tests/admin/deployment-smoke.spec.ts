/**
 * deployment-smoke.spec.ts — ingress smoke tests.
 *
 * Verifies that the admin SPA is reachable on both the public ingress (port 80)
 * and directly via the NodePort (port 30501). API endpoints are protected by
 * bearer-token auth regardless of which port is used.
 *
 * These tests make real HTTP requests (no mocks) and require the dev stack
 * to be running.
 */
import { test, expect } from '@playwright/test';

const PUBLIC_BASE  = process.env['TEST_BASE_URL']  ?? 'http://localhost';
const ADMIN_BASE   = process.env['TEST_ADMIN_URL']  ?? 'http://localhost:30501';

test('public ingress serves /admin/ path', async ({ request }) => {
  const response = await request.get(`${PUBLIC_BASE}/admin/`, { failOnStatusCode: false });
  expect(response.status()).toBe(200);
});

test('admin NodePort serves /admin/ path', async ({ request }) => {
  const response = await request.get(`${ADMIN_BASE}/admin/`, { failOnStatusCode: false });
  expect(response.status()).toBe(200);
});
