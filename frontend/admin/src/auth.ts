import { createRediensIam, type RediensIam } from 'rediensiam-web';

interface AdminConfig {
  hydra_url: string;
  client_id: string;
  redirect_uri: string;
  /** The running server's own version — see the /admin/config endpoint in src/Program.cs. */
  version?: string;
}

let client: RediensIam | null = null;
let serverVersion: string | null = null;
let accessToken: string | null = null;
/**
 * One-shot guard so concurrent 401 responses do not each fire a login redirect — the
 * last redirect wins the stored state, racing the others and breaking the PKCE callback.
 */
let signinRedirectInFlight = false;

/**
 * Builds (once) the SDK client from `/admin/config`.
 *
 * This console runs on `rediensiam-web`, the browser SDK this repo ships, rather than a second
 * OIDC implementation: the login the SDK gives integrators is the login the console itself uses,
 * so a defect in it fails here first.
 *
 * The origin check below is defence-in-depth: even though Hydra also validates the registered
 * redirect_uri, never trust a server-provided redirect_uri whose origin differs from this SPA's
 * origin. A compromised config endpoint could otherwise hand the authorization code to another
 * origin. The SDK constructor enforces the same rule; this one runs first and names the config
 * endpoint as the source. Do not relax either to a hostname or suffix comparison.
 *
 * Tokens live in the SDK's private field and in `accessToken` here, never in localStorage or
 * sessionStorage: anything persisted there survives the tab and is readable by any script that
 * lands on this origin.
 */
async function getClient(): Promise<RediensIam> {
  if (client) return client;
  const res = await fetch('/admin/config');
  const cfg: AdminConfig = await res.json();
  serverVersion = cfg.version ?? null;
  try {
    const cfgOrigin = new URL(cfg.redirect_uri).origin;
    if (cfgOrigin !== globalThis.location.origin) {
      throw new Error(`redirect_uri origin (${cfgOrigin}) does not match SPA origin (${globalThis.location.origin})`);
    }
  } catch (e) {
    throw new Error(`Invalid OIDC redirect_uri from /admin/config: ${(e as Error).message}`);
  }
  client = createRediensIam({
    issuer: cfg.hydra_url,
    clientId: cfg.client_id,
    redirectUri: cfg.redirect_uri,
    // The SDK would otherwise default this to location.origin, which is the API host rather than
    // the console — and Hydra refuses any value the client has not whitelisted, so a sign-out ended
    // on its error page with the session still open. This string must stay equal to the one
    // HydraService.EnsureAdminSpaClientAsync registers.
    postLogoutRedirectUri: `${globalThis.location.origin}/admin/`,
    scope: 'openid offline',
  });
  return client;
}

export async function restoreSession(): Promise<void> {
  await getClient();
}

/**
 * The version of the server that served this console, once /admin/config has been read. Null
 * before that — the console must not invent a number, and it must not report its own build:
 * a SPA built against one release and served by another would show the wrong one.
 */
export function getServerVersion(): string | null { return serverVersion; }

export async function startLogin() {
  const c = await getClient();
  await c.login();
}

/**
 * Completes the redirect. Takes no arguments on purpose: the SDK reads `code` and `state` off the
 * current URL, and passing them in only invited a caller to believe they were checked.
 */
export async function handleCallback(): Promise<boolean> {
  try {
    const c = await getClient();
    if (!await c.handleRedirect()) return false;
    accessToken = await c.getToken();
    return !!accessToken;
  } catch {
    return false;
  }
}

/**
 * The token as of the last call that touched it. Synchronous because the auth context reads it
 * during render; {@link apiFetch} is what keeps it current.
 */
export function getToken() { return accessToken; }
export function isAuthenticated() { return !!accessToken; }
export async function logout() {
  accessToken = null;
  const c = await getClient();
  await c.logout();
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

/**
 * Proof that the caller still controls an existing authentication factor.
 * Sent as the JSON body (or as `reauth` inside it) on the MFA mutation endpoints.
 * Exactly one field is filled — the backend tries password first, then TOTP.
 */
export interface MfaReauth {
  current_password?: string;
  totp_code?: string;
}

/** Shape of `401 {"error":"reauthentication_required","methods":[…]}` from AccountController. */
export interface ReauthRequired {
  error: 'reauthentication_required';
  methods: string[];
}

function isReauthRequired(body: unknown): body is ReauthRequired {
  return (body as ReauthRequired | null)?.error === 'reauthentication_required';
}

/**
 * The proofs the account can supply, or null when `e` is not a re-authentication demand.
 * `methods` is authoritative: a passwordless account cannot be asked for a password.
 */
export function reauthMethods(e: unknown): string[] | null {
  if (!(e instanceof ApiError) || e.status !== 401) return null;
  return isReauthRequired(e.body) ? (e.body.methods ?? []) : null;
}

/**
 * Authenticated fetch. Throws {@link ApiError} on any non-2xx.
 *
 * Goes through the SDK's `fetch`, which attaches the bearer and refuses any target outside this
 * origin and the configured API origins — so a caller-supplied path can never carry the token
 * off-origin.
 *
 * The `!isReauthRequired(body)` half of the 401 branch below is load-bearing. A 401 normally
 * means the token is gone and the only way forward is a fresh login. The MFA mutation endpoints
 * reuse 401 for something else entirely: the session is fine, the caller just has to prove
 * possession of a factor first. Redirecting on those would throw away a working session and drop
 * the user back on the login page mid-action.
 */
export async function apiFetch(path: string, opts: RequestInit = {}) {
  const c = await getClient();
  const res = await c.fetch(path, {
    ...opts,
    headers: {
      'Content-Type': 'application/json',
      ...opts.headers,
    },
  });
  accessToken = await c.getToken();
  if (!res.ok) {
    let body: unknown;
    try { body = await res.json(); } catch { body = null; }
    if (res.status === 401 && !isReauthRequired(body)) {
      accessToken = null;
      if (!signinRedirectInFlight) {
        signinRedirectInFlight = true;
        await c.login();
      }
    }
    throw new ApiError(res.status, body);
  }
  return res;
}
