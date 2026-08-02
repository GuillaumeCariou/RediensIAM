import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApiError as ApiErrorInstance } from './auth';

/**
 * `apiFetch` has to tell two 401s apart:
 *   - the session is gone            → drop the token and start a fresh login;
 *   - `reauthentication_required`    → the session is fine, the caller just has to prove a factor.
 * Getting that wrong throws away a working session in the middle of an MFA change and dumps the
 * user back on the login page, losing whatever they were doing.
 */

const { login, handleRedirect, sdkFetch, getToken } = vi.hoisted(() => ({
  login: vi.fn(async () => {}),
  handleRedirect: vi.fn(async () => true),
  // Delegates to the stubbed global fetch: these tests are about apiFetch's 401 handling, so the
  // SDK's fetch stands in only for "attaches the bearer and calls the network".
  sdkFetch: vi.fn((path: string, init?: RequestInit) => globalThis.fetch(path, init)),
  getToken: vi.fn(async () => 'token'),
}));

// The console runs on this repo's own browser SDK; the double stands in for the network, not for
// the OIDC logic, which has its own tests in sdk/typescript/rediensiam-web.
vi.mock('rediensiam-web', () => ({
  createRediensIam: () => ({
    login, handleRedirect, getToken, logout: vi.fn(async () => {}),
    fetch: sdkFetch,
  }),
}));

/**
 * This file runs in the `node` project — no DOM, so no `location`, which `auth.ts` reads to check
 * the config's redirect_uri origin against the SPA's own. It is stubbed rather than inherited from
 * a jsdom window so the origin the assertions below turn on is written down here.
 */
const ORIGIN = 'https://console.example.test';

const CONFIG = {
  hydra_url: 'https://hydra.example.test',
  client_id: 'admin-spa',
  redirect_uri: `${ORIGIN}/admin/callback`,
};

function respond(status: number, body: unknown) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

let fetchMock: ReturnType<typeof vi.fn>;

/** Fresh module instance per test — auth.ts keeps the token and the UserManager in module state. */
async function freshAuth() {
  vi.resetModules();
  return import('./auth');
}

beforeEach(() => {
  login.mockClear();
  sdkFetch.mockClear();
  vi.stubGlobal('location', { origin: ORIGIN });
  fetchMock = vi.fn(async (path: string) =>
    path === '/console/config' ? respond(200, CONFIG) : respond(200, {}));
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => vi.unstubAllGlobals());

describe('reauthMethods', () => {
  it('reads the methods the account can supply', async () => {
    const { ApiError, reauthMethods } = await freshAuth();
    const e = new ApiError(401, { error: 'reauthentication_required', methods: ['totp_code'] });
    expect(reauthMethods(e)).toEqual(['totp_code']);
  });

  it('returns an empty list, not null, when the server names no methods', async () => {
    // null means "not a re-authentication demand" and would let the mutation error through as-is.
    const { ApiError, reauthMethods } = await freshAuth();
    expect(reauthMethods(new ApiError(401, { error: 'reauthentication_required' }))).toEqual([]);
  });

  it('ignores anything that is not a 401 re-authentication demand', async () => {
    const { ApiError, reauthMethods } = await freshAuth();
    expect(reauthMethods(new ApiError(401, { error: 'invalid_token' }))).toBeNull();
    expect(reauthMethods(new ApiError(403, { error: 'reauthentication_required' }))).toBeNull();
    expect(reauthMethods(new ApiError(500, null))).toBeNull();
    expect(reauthMethods(new Error('network down'))).toBeNull();
    expect(reauthMethods(null)).toBeNull();
    expect(reauthMethods({ status: 401, body: { error: 'reauthentication_required' } })).toBeNull();
  });
});

describe('apiFetch', () => {
  it('returns the response when the request succeeds', async () => {
    const { apiFetch } = await freshAuth();
    await expect(apiFetch('/account/mfa')).resolves.toMatchObject({ ok: true });
  });

  it('keeps the session when a 401 only asks for re-authentication', async () => {
    fetchMock.mockImplementation(async (path: string) =>
      path === '/console/config'
        ? respond(200, CONFIG)
        : respond(401, { error: 'reauthentication_required', methods: ['current_password'] }));
    const { apiFetch, ApiError } = await freshAuth();

    const err = await apiFetch('/account/mfa/totp/confirm', { method: 'POST' }).catch((e: unknown) => e);

    expect(err).toBeInstanceOf(ApiError);
    expect((err as InstanceType<typeof ApiError>).status).toBe(401);
    expect(login).not.toHaveBeenCalled();
    // No second call: fetching /console/config would be the first step of throwing the session away.
    // Counted on the SDK's fetch, not the global one: the global also serves /console/config.
    expect(sdkFetch).toHaveBeenCalledTimes(1);
  });

  it('starts a fresh login when a 401 means the session really is gone', async () => {
    const auth = await freshAuth();
    fetchMock.mockImplementation(async (path: string) =>
      path === '/console/config' ? respond(200, CONFIG) : respond(401, { error: 'invalid_token' }));

    await expect(auth.apiFetch('/admin/organizations')).rejects.toBeInstanceOf(auth.ApiError);

    expect(login).toHaveBeenCalledTimes(1);
  });

  it('redirects once even when several requests fail at the same time', async () => {
    // Each redirect rewrites the stored PKCE state; the last one wins and breaks the callback
    // for all the others.
    const auth = await freshAuth();
    fetchMock.mockImplementation(async (path: string) =>
      path === '/console/config' ? respond(200, CONFIG) : respond(401, { error: 'invalid_token' }));

    await Promise.allSettled([
      auth.apiFetch('/admin/organizations'),
      auth.apiFetch('/admin/users'),
      auth.apiFetch('/org/info'),
    ]);

    expect(login).toHaveBeenCalledTimes(1);
  });

  it('propagates other statuses as an ApiError carrying the body', async () => {
    fetchMock.mockImplementation(async (path: string) =>
      path === '/console/config' ? respond(200, CONFIG) : respond(400, { error: 'smtp_port_not_allowed' }));
    const { apiFetch, ApiError } = await freshAuth();

    const err = await apiFetch('/org/smtp', { method: 'PUT' }).catch((e: unknown) => e) as ApiErrorInstance;

    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(400);
    expect(err.body).toEqual({ error: 'smtp_port_not_allowed' });
  });

  it('survives an error response that is not JSON', async () => {
    fetchMock.mockImplementation(async (path: string) => path === '/console/config' ? respond(200, CONFIG) : ({
      ok: false, status: 502, json: async () => { throw new SyntaxError('not json'); },
    } as unknown as Response));
    const { apiFetch, ApiError } = await freshAuth();

    const err = await apiFetch('/org/info').catch((e: unknown) => e) as ApiErrorInstance;

    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(502);
    expect(err.body).toBeNull();
  });
});

describe('the session lifecycle', () => {
  it('has no token before anything has been signed in', async () => {
    const { getToken, isAuthenticated } = await freshAuth();
    expect(getToken()).toBeNull();
    expect(isAuthenticated()).toBe(false);
  });

  it('holds the token once the redirect has been completed', async () => {
    const { handleCallback, getToken, isAuthenticated } = await freshAuth();

    await expect(handleCallback()).resolves.toBe(true);

    expect(getToken()).toBe('token');
    expect(isAuthenticated()).toBe(true);
  });

  it('reports failure, and stays signed out, when this is not a redirect', async () => {
    handleRedirect.mockResolvedValueOnce(false);
    const { handleCallback, isAuthenticated } = await freshAuth();

    await expect(handleCallback()).resolves.toBe(false);

    expect(isAuthenticated()).toBe(false);
  });

  it('reports failure rather than throwing when the exchange goes wrong', async () => {
    // The caller's next step is startLogin either way; an exception here would take the boot
    // sequence down instead and leave a blank page.
    handleRedirect.mockRejectedValueOnce(new Error('invalid_grant'));
    const { handleCallback } = await freshAuth();

    await expect(handleCallback()).resolves.toBe(false);
  });

  it('reports failure when the exchange succeeds but yields no token', async () => {
    getToken.mockResolvedValueOnce(null as unknown as string);
    const { handleCallback, isAuthenticated } = await freshAuth();

    await expect(handleCallback()).resolves.toBe(false);
    expect(isAuthenticated()).toBe(false);
  });

  it('starts a login through the SDK', async () => {
    const { startLogin } = await freshAuth();

    await startLogin();

    expect(login).toHaveBeenCalledOnce();
  });

  it('drops the token on the way out, before the redirect', async () => {
    // Whatever the sign-out navigation does, this tab must not still be holding a bearer.
    const auth = await freshAuth();
    await auth.handleCallback();

    await auth.logout();

    expect(auth.getToken()).toBeNull();
    expect(auth.isAuthenticated()).toBe(false);
  });
});

describe('the server version', () => {
  it('is unknown until /console/config has been read', async () => {
    // The console must not invent a number, and must not report its own build: a SPA built
    // against one release and served by another would show the wrong one.
    const { getServerVersion } = await freshAuth();
    expect(getServerVersion()).toBeNull();
  });

  it('is whatever the running server said', async () => {
    fetchMock.mockImplementation(async (path: string) =>
      path === '/console/config' ? respond(200, { ...CONFIG, version: '0.5.0' }) : respond(200, {}));
    const { restoreSession, getServerVersion } = await freshAuth();

    await restoreSession();

    expect(getServerVersion()).toBe('0.5.0');
  });

  it('stays null when the server does not say', async () => {
    const { restoreSession, getServerVersion } = await freshAuth();
    await restoreSession();
    expect(getServerVersion()).toBeNull();
  });
});

describe('the OIDC redirect_uri from /console/config', () => {
  it('is refused when its origin is not this SPA', async () => {
    // A compromised config endpoint could otherwise hand the authorization code to another origin.
    fetchMock.mockImplementation(async (path: string) =>
      path === '/console/config'
        ? respond(200, { ...CONFIG, redirect_uri: 'https://evil.example/admin/callback' })
        : respond(200, {}));
    const { restoreSession } = await freshAuth();

    await expect(restoreSession()).rejects.toThrow(/redirect_uri/);
  });

  it('is refused when it is not a URL at all', async () => {
    fetchMock.mockImplementation(async (path: string) =>
      path === '/console/config' ? respond(200, { ...CONFIG, redirect_uri: '/console/callback' }) : respond(200, {}));
    const { restoreSession } = await freshAuth();

    await expect(restoreSession()).rejects.toThrow(/redirect_uri/);
  });

  it('is accepted when it matches this SPA', async () => {
    const { restoreSession } = await freshAuth();
    await expect(restoreSession()).resolves.toBeUndefined();
  });
});
