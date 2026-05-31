import { UserManager, WebStorageStateStore, InMemoryWebStorage } from 'oidc-client-ts';

interface AdminConfig {
  hydra_url: string;
  client_id: string;
  redirect_uri: string;
}

let mgr: UserManager | null = null;
let accessToken: string | null = null;
// One-shot guard so concurrent 401 responses do not each fire signinRedirect — the
// last redirect wins the stored state, racing the others and breaking PKCE callback.
let signinRedirectInFlight = false;

async function getManager(): Promise<UserManager> {
  if (mgr) return mgr;
  const res = await fetch('/admin/config');
  const cfg: AdminConfig = await res.json();
  // Defence-in-depth: even though Hydra also validates the registered redirect_uri,
  // never trust a server-provided redirect_uri whose origin differs from this SPA's origin.
  // A compromised config endpoint could otherwise hand the auth code to another origin.
  try {
    const cfgOrigin = new URL(cfg.redirect_uri).origin;
    if (cfgOrigin !== globalThis.location.origin) {
      throw new Error(`redirect_uri origin (${cfgOrigin}) does not match SPA origin (${globalThis.location.origin})`);
    }
  } catch (e) {
    throw new Error(`Invalid OIDC redirect_uri from /admin/config: ${(e as Error).message}`);
  }
  mgr = new UserManager({
    authority: cfg.hydra_url,
    client_id: cfg.client_id,
    redirect_uri: cfg.redirect_uri,
    scope: 'openid offline',
    response_type: 'code',
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
  });
  return mgr;
}

export async function restoreSession(): Promise<void> {
  await getManager();
}

export async function startLogin() {
  const m = await getManager();
  await m.signinRedirect();
}

export async function handleCallback(_code: string, _state: string): Promise<boolean> {
  try {
    const m = await getManager();
    const user = await m.signinRedirectCallback();
    accessToken = user.access_token ?? null;
    return !!accessToken;
  } catch {
    return false;
  }
}

export function getToken() { return accessToken; }
export function isAuthenticated() { return !!accessToken; }
export async function logout() {
  accessToken = null;
  const m = await getManager();
  m.signoutRedirect();
}

export class ApiError extends Error {
  readonly status: number;
  readonly body: unknown;
  constructor(status: number, body: unknown) {
    super(`API error ${status}`);
    this.status = status;
    this.body = body;
  }
}

export async function apiFetch(path: string, opts: RequestInit = {}) {
  const res = await fetch(path, {
    ...opts,
    headers: {
      'Content-Type': 'application/json',
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      ...opts.headers,
    },
  });
  if (res.status === 401) {
    accessToken = null;
    if (!signinRedirectInFlight) {
      signinRedirectInFlight = true;
      const m = await getManager();
      await m.signinRedirect();
    }
    throw new ApiError(401, null);
  }
  if (!res.ok) {
    let body: unknown;
    try { body = await res.json(); } catch { body = null; }
    throw new ApiError(res.status, body);
  }
  return res;
}
