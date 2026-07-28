/**
 * Browser SDK for RediensIAM.
 *
 * Runs the OpenID Connect authorization-code flow with PKCE against the RediensIAM-managed
 * Hydra, keeps the access token **in memory only**, and attaches it to your API calls.
 *
 * Zero dependencies: Web Crypto and `fetch` cover everything needed here.
 *
 * ## What this SDK does not do
 *
 * It does not introspect tokens. Introspection requires a service-account credential, which
 * cannot be shipped to a browser — anyone with devtools would have it. Token validation belongs
 * on your server; use `rediensiam-client` (C#/Rust) there.
 *
 * Claims exposed by {@link RediensIam.claims} are for **rendering decisions only** — showing a
 * menu, hiding a button. They are read from the token without verification. Never gate anything
 * that matters on them; the server re-checks every request.
 */

export interface RediensIamConfig {
  /** Issuer URL of the RediensIAM Hydra, e.g. `https://auth.example.com`. */
  issuer: string;
  /** OAuth2 client ID registered for this application. */
  clientId: string;
  /** Must exactly match a redirect URI registered for the client. */
  redirectUri: string;
  /** Defaults to `openid profile offline_access`. */
  scope?: string;
  /** Where to send the browser after logout. Defaults to the app origin. */
  postLogoutRedirectUri?: string;
  /**
   * Project this app belongs to. RediensIAM uses the client's registered project; passing it
   * here only makes the login page render the right theme sooner.
   */
  projectId?: string;
}

/**
 * Claims read out of the access token. **Unverified** — for rendering only.
 */
export interface Claims {
  userId?: string;
  orgId?: string;
  projectId?: string;
  roles: string[];
  expiresAt?: Date;
}

export type RediensIamErrorCode =
  | 'not_authenticated'
  | 'state_mismatch'
  | 'token_exchange_failed'
  | 'discovery_failed'
  | 'config_invalid';

export class RediensIamError extends Error {
  // Plain field rather than a constructor parameter property: Node's type-stripping runs
  // strip-only, and parameter properties would need a transform.
  readonly code: RediensIamErrorCode;

  constructor(message: string, code: RediensIamErrorCode) {
    super(message);
    this.name = 'RediensIamError';
    this.code = code;
  }
}

interface Discovery {
  authorization_endpoint: string;
  token_endpoint: string;
  end_session_endpoint?: string;
}

interface TokenResponse {
  access_token: string;
  refresh_token?: string;
  expires_in?: number;
  id_token?: string;
}

/** Transient PKCE state. Session-scoped: it must survive the redirect but nothing beyond it. */
const STATE_KEY = 'rediensiam:pkce';

export class RediensIam {
  /**
   * Access token, in memory only.
   *
   * Deliberately not in localStorage or sessionStorage: anything readable by JavaScript is
   * readable by injected JavaScript, and a stored token outlives the tab that earned it. The
   * cost is a silent re-authentication after a reload, which `handleRedirect()` covers.
   */
  #accessToken: string | null = null;
  #refreshToken: string | null = null;
  #expiresAt = 0;
  #discovery: Discovery | null = null;
  #refreshInFlight: Promise<string | null> | null = null;

  readonly #config: RediensIamConfig;

  constructor(config: RediensIamConfig) {
    this.#config = config;

    if (!config.issuer) throw new RediensIamError('issuer is required', 'config_invalid');
    if (!config.clientId) throw new RediensIamError('clientId is required', 'config_invalid');
    if (!config.redirectUri) throw new RediensIamError('redirectUri is required', 'config_invalid');

    // The redirect target must be this origin. A redirect_uri pointing elsewhere would hand the
    // authorization code to another origin — Hydra also enforces its registered list, this is
    // the second lock.
    if (globalThis.location) {
      const redirectOrigin = new URL(config.redirectUri, globalThis.location.origin).origin;
      if (redirectOrigin !== globalThis.location.origin) {
        throw new RediensIamError(
          `redirectUri origin (${redirectOrigin}) does not match the app origin (${globalThis.location.origin})`,
          'config_invalid',
        );
      }
    }
  }

  // ── Session ──────────────────────────────────────────────────────────────

  get isAuthenticated(): boolean {
    return this.#accessToken !== null && Date.now() < this.#expiresAt;
  }

  /** Starts the login redirect. Never returns — the browser navigates away. */
  async login(): Promise<never> {
    const { authorization_endpoint } = await this.#discover();

    const verifier = randomUrlSafe(64);
    const state = randomUrlSafe(24);
    const challenge = await s256(verifier);

    sessionStorage.setItem(STATE_KEY, JSON.stringify({ verifier, state }));

    const params = new URLSearchParams({
      response_type: 'code',
      client_id: this.#config.clientId,
      redirect_uri: this.#config.redirectUri,
      scope: this.#config.scope ?? 'openid profile offline_access',
      state,
      code_challenge: challenge,
      code_challenge_method: 'S256',
    });
    if (this.#config.projectId) params.set('project_id', this.#config.projectId);

    globalThis.location.assign(`${authorization_endpoint}?${params}`);
    return new Promise<never>(() => {}); // navigation in progress
  }

  /**
   * Call this on page load. If the URL carries `?code=…&state=…` it completes the login,
   * strips those parameters from the address bar, and returns true.
   */
  async handleRedirect(): Promise<boolean> {
    const url = new URL(globalThis.location.href);
    const code = url.searchParams.get('code');
    const state = url.searchParams.get('state');
    if (!code || !state) return false;

    const stored = sessionStorage.getItem(STATE_KEY);
    sessionStorage.removeItem(STATE_KEY);
    if (!stored) throw new RediensIamError('No PKCE state for this callback', 'state_mismatch');

    const { verifier, state: expected } = JSON.parse(stored) as { verifier: string; state: string };
    // Rejecting a mismatched state is what stops an attacker feeding you their authorization
    // code and logging your user into the attacker's account.
    if (state !== expected) {
      throw new RediensIamError('state does not match the value we issued', 'state_mismatch');
    }

    const { token_endpoint } = await this.#discover();
    const response = await fetch(token_endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        grant_type: 'authorization_code',
        code,
        redirect_uri: this.#config.redirectUri,
        client_id: this.#config.clientId,
        code_verifier: verifier,
      }),
    });

    if (!response.ok) {
      throw new RediensIamError(
        `Token exchange failed (${response.status})`,
        'token_exchange_failed',
      );
    }

    this.#store((await response.json()) as TokenResponse);

    url.searchParams.delete('code');
    url.searchParams.delete('state');
    globalThis.history.replaceState({}, '', url.toString());
    return true;
  }

  /** Current access token, refreshing it first if it is expired and a refresh token exists. */
  async getToken(): Promise<string | null> {
    if (this.isAuthenticated) return this.#accessToken;
    return this.#refresh();
  }

  /** Clears local state and redirects to the IdP so the SSO session ends too. */
  async logout(): Promise<void> {
    const idToken = this.#accessToken;
    this.#accessToken = null;
    this.#refreshToken = null;
    this.#expiresAt = 0;

    const discovery = await this.#discover().catch(() => null);
    if (!discovery?.end_session_endpoint) {
      // Local sign-out only. Say so rather than pretending the SSO session is gone.
      globalThis.location.assign(this.#config.postLogoutRedirectUri ?? globalThis.location.origin);
      return;
    }

    const params = new URLSearchParams({
      post_logout_redirect_uri: this.#config.postLogoutRedirectUri ?? globalThis.location.origin,
    });
    if (idToken) params.set('id_token_hint', idToken);
    globalThis.location.assign(`${discovery.end_session_endpoint}?${params}`);
  }

  // ── Calling your API ─────────────────────────────────────────────────────

  /**
   * `fetch` with the bearer token attached. On 401 it refreshes once and retries; if that fails
   * the session is cleared, so callers can treat a thrown `not_authenticated` as "send them to
   * login".
   */
  async fetch(input: string | URL | Request, init: RequestInit = {}): Promise<Response> {
    const token = await this.getToken();
    if (!token) throw new RediensIamError('No valid session', 'not_authenticated');

    const call = (bearer: string) =>
      fetch(input, {
        ...init,
        headers: { ...init.headers, Authorization: `Bearer ${bearer}` },
      });

    const response = await call(token);
    if (response.status !== 401) return response;

    const refreshed = await this.#refresh();
    if (!refreshed) throw new RediensIamError('Session expired', 'not_authenticated');
    return call(refreshed);
  }

  /**
   * Claims decoded from the access token. **Unverified — rendering only.**
   * The signature is not checked here and roles may have been revoked since issuance; every
   * privileged decision is re-made server-side.
   */
  get claims(): Claims {
    if (!this.#accessToken) return { roles: [] };

    const payload = decodeJwtPayload(this.#accessToken);
    if (!payload) return { roles: [] };

    const ext = (payload.ext ?? payload) as Record<string, unknown>;
    return {
      userId: asString(ext.user_id) ?? asString(payload.sub),
      orgId: asString(ext.org_id),
      projectId: asString(ext.project_id),
      roles: asRoles(ext.roles),
      expiresAt: typeof payload.exp === 'number' ? new Date(payload.exp * 1000) : undefined,
    };
  }

  /** Convenience for menu/route rendering. Not a security check. */
  hasRole(role: string): boolean {
    return this.claims.roles.includes(role);
  }

  // ── Internals ────────────────────────────────────────────────────────────

  async #discover(): Promise<Discovery> {
    if (this.#discovery) return this.#discovery;

    const url = `${this.#config.issuer.replace(/\/$/, '')}/.well-known/openid-configuration`;
    const response = await fetch(url);
    if (!response.ok) {
      throw new RediensIamError(`OIDC discovery failed (${response.status})`, 'discovery_failed');
    }

    this.#discovery = (await response.json()) as Discovery;
    return this.#discovery;
  }

  /** Single-flight: concurrent 401s must not each start their own refresh. */
  #refresh(): Promise<string | null> {
    if (!this.#refreshToken) return Promise.resolve(null);
    this.#refreshInFlight ??= this.#doRefresh().finally(() => {
      this.#refreshInFlight = null;
    });
    return this.#refreshInFlight;
  }

  async #doRefresh(): Promise<string | null> {
    try {
      const { token_endpoint } = await this.#discover();
      const response = await fetch(token_endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          grant_type: 'refresh_token',
          refresh_token: this.#refreshToken!,
          client_id: this.#config.clientId,
        }),
      });
      if (!response.ok) {
        this.#accessToken = null;
        this.#refreshToken = null;
        this.#expiresAt = 0;
        return null;
      }
      this.#store((await response.json()) as TokenResponse);
      return this.#accessToken;
    } catch {
      return null;
    }
  }

  #store(tokens: TokenResponse): void {
    this.#accessToken = tokens.access_token;
    // Hydra rotates refresh tokens; keep the new one when present.
    if (tokens.refresh_token) this.#refreshToken = tokens.refresh_token;
    // Renew 30s early so a request in flight cannot land on an expired token.
    const lifetime = (tokens.expires_in ?? 3600) - 30;
    this.#expiresAt = Date.now() + Math.max(lifetime, 0) * 1000;
  }
}

/** Convenience factory. */
export function createRediensIam(config: RediensIamConfig): RediensIam {
  return new RediensIam(config);
}

// ── Helpers (exported for testing) ──────────────────────────────────────────

export function base64UrlEncode(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function randomUrlSafe(byteLength: number): string {
  const bytes = new Uint8Array(byteLength);
  crypto.getRandomValues(bytes);
  return base64UrlEncode(bytes);
}

export async function s256(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  return base64UrlEncode(new Uint8Array(digest));
}

export function decodeJwtPayload(token: string): Record<string, unknown> | null {
  const parts = token.split('.');
  if (parts.length !== 3) return null;
  try {
    const padded = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(atob(padded)) as Record<string, unknown>;
  } catch {
    return null;
  }
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

/** Hydra may serialise roles as an array, a JSON string, or a comma-separated string. */
function asRoles(value: unknown): string[] {
  if (Array.isArray(value)) return value.filter((r): r is string => typeof r === 'string');
  if (typeof value !== 'string' || value.length === 0) return [];
  if (value.startsWith('[')) {
    try {
      const parsed = JSON.parse(value) as unknown;
      return Array.isArray(parsed) ? parsed.filter((r): r is string => typeof r === 'string') : [];
    } catch {
      /* fall through to comma-splitting */
    }
  }
  return value.split(',').filter(Boolean);
}
