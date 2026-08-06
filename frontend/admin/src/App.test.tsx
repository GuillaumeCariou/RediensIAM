import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import App, { PAGES } from './App';
import { RETURN_TO_KEY } from './context/returnTo';
import { DESTINATIONS, ROUTE_BASES, type Level } from './scope';

/**
 * The router is where a role becomes a set of reachable pages. Three things here have gone wrong
 * before and each is a lockout rather than a cosmetic fault:
 *
 *  - a token with no roles used to bounce between the project guard and the home it redirects to,
 *    forever, with no way out and no way to sign out;
 *  - `/account` must stay reachable by anyone signed in, including someone whose only role was
 *    revoked a moment ago, or they cannot even reach the button that ends the session;
 *  - the basename comes from Vite. Written as a literal it drifted from the server's `base` and
 *    the router matched no URL at all — a blank page and one console warning.
 */

const auth = vi.hoisted(() => ({
  isAuthenticated: vi.fn(() => true),
  startLogin: vi.fn(async () => {}),
  handleCallback: vi.fn(async () => true),
  logout: vi.fn(async () => {}),
  getToken: vi.fn<() => string | null>(() => null),
  restoreSession: vi.fn(async () => {}),
  getServerVersion: vi.fn(() => null),
  // api.ts imports this, and the sidebar's tree pulls api.ts into the graph. A mock that omits it
  // fails the whole file at import time, before a single assertion runs.
  apiFetch: vi.fn(async () => new Response('[]', { headers: { 'content-type': 'application/json' } })),
}));
vi.mock('./auth', () => auth);
vi.mock('@/auth', () => auth);

/** Pages have their own tests; here each one only has to be identifiable. */
const stub = vi.hoisted(() => (name: string) => ({ default: () => <h2>{name}</h2> }));

vi.mock('./pages/account/AccountPage', () => stub('Account'));
vi.mock('./pages/system/Dashboard', () => stub('SystemDashboard'));
vi.mock('./pages/system/Organisations', () => stub('Organisations'));
vi.mock('./pages/system/Users', () => stub('SystemUsers'));
vi.mock('./pages/system/Metrics', () => stub('Metrics'));
vi.mock('./pages/system/SystemEmail', () => stub('SystemEmail'));
vi.mock('./pages/system/SystemAdmins', () => stub('SystemAdmins'));
vi.mock('./pages/system/OrgDetail', () => stub('OrgDetail'));
vi.mock('./pages/system/SystemProjectDetail', () => stub('SystemProjectDetail'));
vi.mock('./pages/system/SystemProjects', () => stub('SystemProjects'));
vi.mock('./pages/system/SystemHealth', () => stub('SystemHealth'));
vi.mock('./pages/shared/ServiceAccountDetail', () => stub('ServiceAccountDetail'));
vi.mock('./pages/shared/OrgAdmins', () => stub('OrgAdmins'));
vi.mock('./pages/shared/UserListDetail', () => stub('UserListDetail'));
vi.mock('./pages/org/OrgDashboard', () => stub('OrgDashboard'));
vi.mock('./pages/org/UserLists', () => stub('UserLists'));
vi.mock('./pages/org/Projects', () => stub('Projects'));
vi.mock('./pages/org/OrgEmail', () => stub('OrgEmail'));
vi.mock('./pages/org/OrgWebhooks', () => stub('OrgWebhooks'));
vi.mock('./pages/org/OrgSettings', () => stub('OrgSettings'));
vi.mock('./pages/project/ProjectDashboard', () => stub('ProjectDashboard'));
vi.mock('./pages/project/ProjectUsers', () => stub('ProjectUsers'));
vi.mock('./pages/project/ProjectRoles', () => stub('ProjectRoles'));
// One page for all three levels now, stubbed once.
vi.mock('./pages/shared/ServiceAccounts', () => stub('ServiceAccounts'));
vi.mock('./pages/shared/AuditLog', () => stub('AuditLog'));
vi.mock('./pages/system/Impersonation', () => stub('Impersonation'));
vi.mock('./pages/project/Authentication', () => stub('Authentication'));
vi.mock('./pages/project/ProjectSettings', () => stub('ProjectSettings'));
// The shell fetches the operator's MFA status; the reminder inside it has its own tests.
vi.mock('@/components/MfaReminder', () => ({ default: () => null }));

function token(roles: string[]) {
  const b64 = btoa(JSON.stringify({ ext: { roles } })).replaceAll('+', '-').replaceAll('/', '_');
  return `header.${b64}.signature`;
}

const BASE = import.meta.env.BASE_URL.replace(/\/$/, '');
const navigate = globalThis.history.replaceState.bind(globalThis.history);
const ORIGINAL_URL = globalThis.location.href;

/** Boots the application as `roles` at `path`, and waits for a page to render. */
async function boot(roles: string[], path = '/system') {
  auth.getToken.mockReturnValue(token(roles));
  navigate({}, '', BASE + path);
  const user = userEvent.setup();
  render(<App />);
  return user;
}

const page = () => screen.queryByRole('heading', { level: 2 })?.textContent ?? null;
const at = () => globalThis.location.pathname.slice(BASE.length) || '/';

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
  auth.isAuthenticated.mockReturnValue(true);
  auth.handleCallback.mockResolvedValue(true);
});

afterEach(() => navigate({}, '', ORIGINAL_URL));

describe('before the session is known', () => {
  it('shows a spinner rather than a flash of the wrong scope', async () => {
    // startLogin never resolves here: the redirect is what ends this state in a real browser.
    auth.isAuthenticated.mockReturnValue(false);
    auth.startLogin.mockImplementation(() => new Promise(() => {}));
    await boot([]);

    await vi.waitFor(() => expect(auth.startLogin).toHaveBeenCalled());
    expect(page()).toBeNull();
    expect(document.querySelector('.animate-spin')).not.toBeNull();
  });
});

describe('an account with no usable role', () => {
  it.each([
    ['no roles at all', []],
    ['only roles this console does not know', ['billing_reader']],
  ])('is told so and offered a way out (%s)', async (_n, roles) => {
    // Not a redirect: defaultPath sends a roleless token to /project, whose guard sends it home
    // again — the two used to take turns forever.
    await boot(roles);

    expect(await screen.findByRole('heading', { name: 'No access' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Log out' })).toBeInTheDocument();
  });

  it('can sign out from it', async () => {
    const user = await boot([]);
    await screen.findByRole('heading', { name: 'No access' });

    await user.click(screen.getByRole('button', { name: 'Log out' }));

    expect(auth.logout).toHaveBeenCalledOnce();
  });
});

describe('the home each role lands on', () => {
  it.each([
    ['super_admin', 'SystemDashboard'],
    ['org_admin', 'OrgDashboard'],
    ['project_admin', 'ProjectDashboard'],
  ])('sends %s to %s', async (role, expected) => {
    await boot([role], '/');
    expect(await screen.findByRole('heading', { name: expected })).toBeInTheDocument();
  });

  it('sends an unroutable URL there too, rather than showing nothing', async () => {
    await boot(['org_admin'], '/org/does-not-exist');
    expect(await screen.findByRole('heading', { name: 'OrgDashboard' })).toBeInTheDocument();
  });
});

describe('the role guards', () => {
  it('let a super admin into the system pages', async () => {
    await boot(['super_admin'], '/system/organisations/o1/projects/p1/authentication');
    expect(await screen.findByRole('heading', { name: 'Authentication' })).toBeInTheDocument();
  });

  it('turn an org admin away from them, back to their own scope', async () => {
    await boot(['org_admin'], '/system/users');

    expect(await screen.findByRole('heading', { name: 'OrgDashboard' })).toBeInTheDocument();
    expect(at()).toBe('/org');
  });

  it('turn a project manager away from the organisation pages', async () => {
    await boot(['project_admin'], '/org/webhooks');

    expect(await screen.findByRole('heading', { name: 'ProjectDashboard' })).toBeInTheDocument();
    expect(at()).toBe('/project');
  });

  it('let an org admin into the project pages, which they administer too', async () => {
    await boot(['org_admin'], '/project/roles');
    expect(await screen.findByRole('heading', { name: 'ProjectRoles' })).toBeInTheDocument();
  });
});

describe('the account page', () => {
  it.each(['super_admin', 'org_admin', 'project_admin'])('is reachable by %s', async role => {
    // It sits outside every guard on purpose — MFA, sessions and profile belong to the person,
    // not to the scope.
    await boot([role], '/account');
    expect(await screen.findByRole('heading', { name: 'Account' })).toBeInTheDocument();
  });
});

describe('coming back from the sign-in redirect', () => {
  it('returns the operator to the page they asked for, once', async () => {
    sessionStorage.setItem(RETURN_TO_KEY, `${BASE}/org/webhooks`);
    await boot(['org_admin'], '/');

    expect(await screen.findByRole('heading', { name: 'OrgWebhooks' })).toBeInTheDocument();
    expect(sessionStorage.getItem(RETURN_TO_KEY)).toBeNull();
  });

  it('falls through to the scope home when the stored page no longer exists', async () => {
    // The destination is consumed before it is used, so an unroutable one cannot loop the
    // catch-all against itself.
    sessionStorage.setItem(RETURN_TO_KEY, `${BASE}/org/removed-page`);
    await boot(['org_admin'], '/');

    expect(await screen.findByRole('heading', { name: 'OrgDashboard' })).toBeInTheDocument();
  });

  it('goes to the scope home when nothing was stored', async () => {
    await boot(['super_admin'], '/');
    expect(await screen.findByRole('heading', { name: 'SystemDashboard' })).toBeInTheDocument();
  });
});

/**
 * The route table is generated from `scope.ts`, so the two can disagree in exactly one way: a
 * destination with no page behind it. That renders as a blank route rather than a 404 — the most
 * confusing failure a router has — so it is checked here rather than discovered.
 */
describe('every destination has a page', () => {
  it.each(Object.keys(ROUTE_BASES) as Level[])('%s', level => {
    for (const destination of DESTINATIONS[level]) {
      expect(PAGES[level][destination.key], `${level}/${destination.key} has no page`).toBeDefined();
    }
  });
});
