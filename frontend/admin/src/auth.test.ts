import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApiError as ApiErrorInstance } from './auth';

/**
 * `apiFetch` has to tell two 401s apart:
 *   - the session is gone            → drop the token and start a fresh login;
 *   - `reauthentication_required`    → the session is fine, the caller just has to prove a factor.
 * Getting that wrong throws away a working session in the middle of an MFA change and dumps the
 * user back on the login page, losing whatever they were doing.
 */

const { signinRedirect } = vi.hoisted(() => ({ signinRedirect: vi.fn(async () => {}) }));

vi.mock('oidc-client-ts', () => ({
  UserManager: class {
    signinRedirect = signinRedirect;
    signinRedirectCallback = vi.fn();
    signoutRedirect = vi.fn();
  },
  WebStorageStateStore: class {},
  InMemoryWebStorage: class {},
}));

const CONFIG = {
  hydra_url: 'https://hydra.example.test',
  client_id: 'admin-spa',
  redirect_uri: `${globalThis.location.origin}/admin/callback`,
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
  signinRedirect.mockClear();
  fetchMock = vi.fn(async (path: string) =>
    path === '/admin/config' ? respond(200, CONFIG) : respond(200, {}));
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
    fetchMock.mockImplementation(async () =>
      respond(401, { error: 'reauthentication_required', methods: ['current_password'] }));
    const { apiFetch, ApiError } = await freshAuth();

    const err = await apiFetch('/account/mfa/totp/confirm', { method: 'POST' }).catch((e: unknown) => e);

    expect(err).toBeInstanceOf(ApiError);
    expect((err as InstanceType<typeof ApiError>).status).toBe(401);
    expect(signinRedirect).not.toHaveBeenCalled();
    // No second call: fetching /admin/config would be the first step of throwing the session away.
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('starts a fresh login when a 401 means the session really is gone', async () => {
    const auth = await freshAuth();
    fetchMock.mockImplementation(async (path: string) =>
      path === '/admin/config' ? respond(200, CONFIG) : respond(401, { error: 'invalid_token' }));

    await expect(auth.apiFetch('/admin/organizations')).rejects.toBeInstanceOf(auth.ApiError);

    expect(signinRedirect).toHaveBeenCalledTimes(1);
  });

  it('redirects once even when several requests fail at the same time', async () => {
    // Each redirect rewrites the stored PKCE state; the last one wins and breaks the callback
    // for all the others.
    const auth = await freshAuth();
    fetchMock.mockImplementation(async (path: string) =>
      path === '/admin/config' ? respond(200, CONFIG) : respond(401, { error: 'invalid_token' }));

    await Promise.allSettled([
      auth.apiFetch('/admin/organizations'),
      auth.apiFetch('/admin/users'),
      auth.apiFetch('/org/info'),
    ]);

    expect(signinRedirect).toHaveBeenCalledTimes(1);
  });

  it('propagates other statuses as an ApiError carrying the body', async () => {
    fetchMock.mockImplementation(async () => respond(400, { error: 'smtp_port_not_allowed' }));
    const { apiFetch, ApiError } = await freshAuth();

    const err = await apiFetch('/org/smtp', { method: 'PUT' }).catch((e: unknown) => e) as ApiErrorInstance;

    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(400);
    expect(err.body).toEqual({ error: 'smtp_port_not_allowed' });
  });

  it('survives an error response that is not JSON', async () => {
    fetchMock.mockImplementation(async () => ({
      ok: false, status: 502, json: async () => { throw new SyntaxError('not json'); },
    } as unknown as Response));
    const { apiFetch, ApiError } = await freshAuth();

    const err = await apiFetch('/org/info').catch((e: unknown) => e) as ApiErrorInstance;

    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(502);
    expect(err.body).toBeNull();
  });
});

describe('the OIDC redirect_uri from /admin/config', () => {
  it('is refused when its origin is not this SPA', async () => {
    // A compromised config endpoint could otherwise hand the authorization code to another origin.
    fetchMock.mockImplementation(async (path: string) =>
      path === '/admin/config'
        ? respond(200, { ...CONFIG, redirect_uri: 'https://evil.example/admin/callback' })
        : respond(200, {}));
    const { restoreSession } = await freshAuth();

    await expect(restoreSession()).rejects.toThrow(/redirect_uri/);
  });

  it('is refused when it is not a URL at all', async () => {
    fetchMock.mockImplementation(async (path: string) =>
      path === '/admin/config' ? respond(200, { ...CONFIG, redirect_uri: '/admin/callback' }) : respond(200, {}));
    const { restoreSession } = await freshAuth();

    await expect(restoreSession()).rejects.toThrow(/redirect_uri/);
  });

  it('is accepted when it matches this SPA', async () => {
    const { restoreSession } = await freshAuth();
    await expect(restoreSession()).resolves.toBeUndefined();
  });
});
