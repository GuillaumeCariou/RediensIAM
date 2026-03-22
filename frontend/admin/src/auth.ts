import { UserManager, WebStorageStateStore } from 'oidc-client-ts';

interface AdminConfig {
  hydra_url: string;
  client_id: string;
  redirect_uri: string;
}

let mgr: UserManager | null = null;
let accessToken: string | null = null;

async function getManager(): Promise<UserManager> {
  if (mgr) return mgr;
  const res = await fetch('/admin/config');
  const cfg: AdminConfig = await res.json();
  mgr = new UserManager({
    authority: cfg.hydra_url,
    client_id: cfg.client_id,
    redirect_uri: cfg.redirect_uri,
    scope: 'openid offline',
    response_type: 'code',
    userStore: new WebStorageStateStore({ store: sessionStorage }),
  });
  // Restore access token from stored session (survives page reload)
  try {
    const stored = await mgr.getUser();
    if (stored && !stored.expired) {
      accessToken = stored.access_token ?? null;
    }
  } catch { /* ignore — fresh login will be triggered */ }
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
  sessionStorage.clear();
  const m = await getManager();
  m.signoutRedirect();
}

export async function apiFetch(path: string, opts: RequestInit = {}) {
  return fetch(path, {
    ...opts,
    headers: {
      'Content-Type': 'application/json',
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      ...opts.headers,
    },
  });
}
