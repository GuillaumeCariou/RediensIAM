/**
 * mock-api.ts — helpers to wire page.route() mocks for the admin API.
 *
 * Usage inside a spec:
 *   import { mockGet, mockPost, mockPatch } from '../../fixtures/mock-api';
 *
 *   test('...', async ({ adminPage }) => {
 *     await mockGet(adminPage, '/admin/organizations', { organizations: [...] });
 *     await adminPage.goto('/admin/system/organisations');
 *     ...
 *   });
 */
import type { Page } from '@playwright/test';

type RoutePattern = string | RegExp;

function toGlob(pattern: RoutePattern): string | RegExp {
  if (typeof pattern !== 'string') return pattern;
  // Turn plain path like '/admin/organizations' into a glob that ignores origin
  return `**${pattern}`;
}

export async function mockGet(page: Page, path: RoutePattern, body: unknown, status = 200) {
  await page.route(toGlob(path), async (route) => {
    if (route.request().method() !== 'GET') { await route.fallback(); return; }
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  });
}

export async function mockPost(page: Page, path: RoutePattern, body: unknown, status = 200) {
  await page.route(toGlob(path), async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  });
}

export async function mockPatch(page: Page, path: RoutePattern, body: unknown, status = 200) {
  await page.route(toGlob(path), async (route) => {
    if (route.request().method() !== 'PATCH') { await route.fallback(); return; }
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  });
}

export async function mockDelete(page: Page, path: RoutePattern, status = 204) {
  await page.route(toGlob(path), async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    await route.fulfill({ status });
  });
}

// ── Pre-wired mock sets ───────────────────────────────────────────────────────

/** Mock the /admin/config endpoint so oidc-client-ts initialises without
 *  needing the real backend to be up when running mocked admin tests. */
export async function mockAdminConfig(page: Page, overrides?: Record<string, string>) {
  await page.route('**/admin/config', async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        hydra_url: 'http://localhost',
        client_id: 'admin-spa',
        redirect_uri: 'http://localhost/admin/',
        ...overrides,
      }),
    });
  });
}

/** Intercept any call and return a generic server error, for testing error states. */
export async function mockError(page: Page, path: RoutePattern, status = 500, message = 'Internal server error') {
  await page.route(toGlob(path), async (route) => {
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify({ error: message }) });
  });
}
