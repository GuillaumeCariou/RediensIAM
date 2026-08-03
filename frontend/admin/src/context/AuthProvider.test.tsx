import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StrictMode } from 'react';
import { render, screen } from '@testing-library/react';
import { AuthProvider } from './AuthProvider';
import { RETURN_TO_KEY } from './returnTo';
import { useAuth } from './AuthContext';

/**
 * Two things live here and both have locked operators out before.
 *
 * The boot sequence decides whether a page load completes the OIDC redirect, resumes a session or
 * starts a fresh sign-in — and, on that last branch, whether the operator comes back to the page
 * they asked for or to the scope home.
 *
 * The role mapping decides what the console lets them see. It once tested for `project_manager`,
 * a name the API never emits, so an account whose only role was `project_admin` reached the "no
 * access" screen and could go no further.
 */

const auth = vi.hoisted(() => ({
  isAuthenticated: vi.fn(() => true),
  startLogin: vi.fn(async () => {}),
  handleCallback: vi.fn(async () => true),
  logout: vi.fn(async () => {}),
  getToken: vi.fn<() => string | null>(() => null),
  restoreSession: vi.fn(async () => {}),
}));
vi.mock('../auth', () => auth);

/** A JWT with `payload` in the middle. Only the payload is ever read. */
function token(payload: Record<string, unknown>) {
  const b64 = btoa(JSON.stringify(payload)).replaceAll('+', '-').replaceAll('/', '_');
  return `header.${b64}.signature`;
}

function Probe() {
  const a = useAuth();
  return (
    <dl>
      <dd data-testid="ready">{String(a.ready)}</dd>
      <dd data-testid="authenticated">{String(a.authenticated)}</dd>
      <dd data-testid="roles">{a.roles.join(',')}</dd>
      <dd data-testid="sys">{String(a.isSuperAdmin)}</dd>
      <dd data-testid="org">{String(a.isOrgAdmin)}</dd>
      <dd data-testid="proj">{String(a.isProjectManager)}</dd>
      <dd data-testid="orgId">{a.orgId}</dd>
      <dd data-testid="projectId">{a.projectId}</dd>
      <button type="button" onClick={a.logout}>sign out</button>
    </dl>
  );
}

/**
 * `location` cannot be replaced in a real browser, so these tests move the real URL instead and
 * spy on `history.replaceState` — which is also the call under test, hence the unspied reference
 * kept here to drive the page with.
 */
const navigate = globalThis.history.replaceState.bind(globalThis.history);
const ORIGINAL_URL = globalThis.location.href;
const url = (path: string) => globalThis.location.origin + path;

let replaceState: ReturnType<typeof vi.spyOn>;

function at(path: string) {
  navigate({}, '', path);
}

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
  auth.isAuthenticated.mockReturnValue(true);
  auth.handleCallback.mockResolvedValue(true);
  auth.getToken.mockReturnValue(token({ ext: { roles: ['super_admin'], org_id: 'o1', project_id: 'p1' } }));
  at('/console/system');
  replaceState = vi.spyOn(globalThis.history, 'replaceState').mockImplementation(() => {});
});

afterEach(() => {
  replaceState.mockRestore();
  navigate({}, '', ORIGINAL_URL);
});

const show = () => render(<AuthProvider><Probe /></AuthProvider>);
const value = (id: string) => screen.getByTestId(id).textContent;
const ready = () => screen.findByText('true', { selector: '[data-testid="ready"]' });

describe('a page load carrying an authorization code', () => {
  it('completes the redirect and scrubs the code out of the address bar', async () => {
    at('/console/callback?code=abc&state=xyz');
    show();

    await ready();
    expect(auth.handleCallback).toHaveBeenCalledOnce();
    expect(auth.restoreSession).not.toHaveBeenCalled();
    // The URL only: where to go next is the router's, and a history entry written here is
    // overwritten a tick later by the catch-all.
    expect(replaceState).toHaveBeenCalledWith({}, '', url('/console/callback'));
  });

  it('starts over when the code turns out to be spent', async () => {
    at('/console/callback?code=abc&state=xyz');
    auth.handleCallback.mockResolvedValue(false);
    show();

    await vi.waitFor(() => expect(auth.startLogin).toHaveBeenCalledOnce());
    expect(value('ready')).toBe('false');
  });

  it('does not consume the code twice under StrictMode', async () => {
    // The second run would hand Hydra a used code/state pair and loop back into startLogin.
    at('/console/callback?code=abc&state=xyz');
    render(
      <StrictMode><AuthProvider><Probe /></AuthProvider></StrictMode>,
    );

    await ready();
    expect(auth.handleCallback).toHaveBeenCalledOnce();
  });
});

describe('a page load with a session already in place', () => {
  it('resumes it without touching the callback path', async () => {
    show();

    await ready();
    expect(auth.restoreSession).toHaveBeenCalledOnce();
    expect(auth.handleCallback).not.toHaveBeenCalled();
    expect(value('authenticated')).toBe('true');
  });
});

describe('a page load with no session', () => {
  beforeEach(() => auth.isAuthenticated.mockReturnValue(false));

  it('remembers where the operator was going, then signs them in', async () => {
    at('/console/org/projects?tab=live');
    show();

    await vi.waitFor(() => expect(auth.startLogin).toHaveBeenCalledOnce());
    // Path and query only — a stored value that could name another origin is an open redirect
    // handed to whoever can write this tab's storage.
    expect(sessionStorage.getItem(RETURN_TO_KEY)).toBe('/console/org/projects?tab=live');
  });

  it('leaves the console unready while the redirect is in flight', async () => {
    show();
    await vi.waitFor(() => expect(auth.startLogin).toHaveBeenCalled());
    expect(value('ready')).toBe('false');
  });
});

describe('the roles the token carries', () => {
  const roleCase = async (payload: Record<string, unknown>) => {
    auth.getToken.mockReturnValue(token(payload));
    show();
    await ready();
  };

  it('reads them from ext.roles, where Hydra puts them', async () => {
    await roleCase({ ext: { roles: ['org_admin'], org_id: 'o9', project_id: 'p9' } });

    expect(value('roles')).toBe('org_admin');
    expect(value('orgId')).toBe('o9');
    expect(value('projectId')).toBe('p9');
  });

  it('reads them from a top-level claim too', async () => {
    await roleCase({ roles: ['org_admin'], org_id: 'o9', project_id: 'p9' });
    expect(value('roles')).toBe('org_admin');
    expect(value('orgId')).toBe('o9');
  });

  it('splits a comma-separated claim', async () => {
    await roleCase({ roles: 'org_admin,project_admin' });
    expect(value('roles')).toBe('org_admin,project_admin');
  });

  it('survives a claim that is neither a list nor a string', async () => {
    await roleCase({ roles: { admin: true } });
    expect(value('roles')).toBe('');
  });

  it('survives a token that is not a token', async () => {
    auth.getToken.mockReturnValue('not.a.jwt');
    show();
    await ready();
    expect(value('roles')).toBe('');
  });

  it('survives no token at all', async () => {
    auth.getToken.mockReturnValue(null);
    show();
    await ready();
    expect(value('roles')).toBe('');
    expect(value('orgId')).toBe('');
  });

  it.each([
    ['super_admin', ['super_admin'], ['true', 'true', 'true']],
    // A super admin is every scope's admin; an org admin is not the system's.
    ['org_admin', ['org_admin'], ['false', 'true', 'true']],
    // The API emits project_admin. Testing for `project_manager` locked these accounts out.
    ['project_admin', ['project_admin'], ['false', 'false', 'true']],
    // …and it emits a scoped form for a manager of one named project.
    ['project_admin:p1', ['project_admin:p1'], ['false', 'false', 'true']],
    ['nothing useful', ['reader'], ['false', 'false', 'false']],
  ])('grants the right scopes to %s', async (_n, roles, [sys, org, proj]) => {
    await roleCase({ ext: { roles } });

    expect(value('sys')).toBe(sys);
    expect(value('org')).toBe(org);
    expect(value('proj')).toBe(proj);
  });
});

describe('signing out', () => {
  it('logs out and does not chain a fresh login behind it', async () => {
    // signoutRedirect navigates to Hydra's logout endpoint; racing it with startLogin can leave
    // the Hydra session cookie alive, which is a silent re-login.
    show();
    await ready();

    screen.getByRole('button', { name: 'sign out' }).click();

    expect(auth.logout).toHaveBeenCalledOnce();
    expect(auth.startLogin).not.toHaveBeenCalled();
  });
});
