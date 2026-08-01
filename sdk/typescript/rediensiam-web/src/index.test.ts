import assert from 'node:assert/strict';
import test from 'node:test';

import {
  RediensIam,
  RediensIamError,
  base64UrlEncode,
  decodeJwtPayload,
  isTrustedTarget,
  matchProjectRole,
  randomUrlSafe,
  s256,
} from './index.ts';

const validConfig = {
  issuer: 'https://auth.example.com',
  clientId: 'client_app',
  redirectUri: 'https://app.example.com/callback',
};

test('config is validated up front', () => {
  assert.throws(() => new RediensIam({ ...validConfig, issuer: '' }), RediensIamError);
  assert.throws(() => new RediensIam({ ...validConfig, clientId: '' }), RediensIamError);
  assert.throws(() => new RediensIam({ ...validConfig, redirectUri: '' }), RediensIamError);
  assert.doesNotThrow(() => new RediensIam(validConfig));
});

/**
 * R-30: the PKCE verifier, the refresh token and the bearer all ride on the issuer origin, so
 * cleartext there hands an on-path attacker the whole session. Loopback is the one exemption —
 * a check that has to be disabled for local development gets disabled in production too.
 */
test('issuer must be https, except on loopback', () => {
  assert.throws(
    () => new RediensIam({ ...validConfig, issuer: 'http://auth.example.com' }),
    (e: RediensIamError) => e.code === 'config_invalid',
  );
  assert.throws(
    () => new RediensIam({ ...validConfig, issuer: 'auth.example.com' }),
    (e: RediensIamError) => e.code === 'config_invalid',
  );
  assert.doesNotThrow(() => new RediensIam({ ...validConfig, issuer: 'http://localhost:4444' }));
  assert.doesNotThrow(() => new RediensIam({ ...validConfig, issuer: 'http://127.0.0.1:4444' }));
  // RFC 6761 reserves the whole .localhost TLD for loopback, and a dev deployment that names its
  // services (iam.localhost) is the normal shape. Refusing it sent people to plain http elsewhere.
  assert.doesNotThrow(() => new RediensIam({ ...validConfig, issuer: 'http://iam.localhost' }));
  // The suffix must be a label boundary: a host that merely ends in the word is not loopback.
  assert.throws(
    () => new RediensIam({ ...validConfig, issuer: 'http://notlocalhost' }),
    (e: RediensIamError) => e.code === 'config_invalid',
  );
});

test('apiOrigins are held to the same scheme rule', () => {
  assert.throws(
    () => new RediensIam({ ...validConfig, apiOrigins: ['http://api.example.com'] }),
    (e: RediensIamError) => e.code === 'config_invalid',
  );
  assert.doesNotThrow(
    () => new RediensIam({ ...validConfig, apiOrigins: ['https://api.example.com'] }),
  );
});

/**
 * R-31: without this, one user-influenced URL through iam.fetch() ships the access token
 * off-origin.
 */
test('the bearer only goes to the app origin or a declared api origin', () => {
  const app = 'https://app.example.com';
  const allowed = new Set(['https://api.example.com']);

  assert.equal(isTrustedTarget('/api/me', app, allowed), true);
  assert.equal(isTrustedTarget('https://app.example.com/api/me', app, allowed), true);
  assert.equal(isTrustedTarget('https://api.example.com/v1/me', app, allowed), true);

  assert.equal(isTrustedTarget('https://evil.example/collect', app, allowed), false);
  assert.equal(isTrustedTarget('//evil.example/collect', app, allowed), false);
  // A subdomain of the app origin is still another origin.
  assert.equal(isTrustedTarget('https://cdn.app.example.com/x', app, allowed), false);
  // A Request carries its own resolved URL.
  assert.equal(isTrustedTarget(new Request('https://evil.example/'), app, allowed), false);
  // Fails closed when there is no app origin to compare against.
  assert.equal(isTrustedTarget('/api/me', undefined, allowed), false);
});

/**
 * R-30, second half: an unvalidated discovery document redirects the whole flow. Whoever answers
 * for the issuer names the token endpoint, and the PKCE verifier goes there.
 */
test('discovery endpoints must sit on the issuer origin', async () => {
  const realFetch = globalThis.fetch;
  const assigned: string[] = [];
  const stub = <T,>(name: string, value: T) =>
    Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });

  stub('location', {
    origin: 'https://app.example.com',
    href: 'https://app.example.com/',
    assign: (url: string) => assigned.push(url),
  });
  stub('sessionStorage', { setItem() {}, getItem: () => null, removeItem() {} });

  const respondWith = (document: unknown) => {
    globalThis.fetch = (() =>
      Promise.resolve(new Response(JSON.stringify(document)))) as typeof fetch;
  };

  try {
    respondWith({
      authorization_endpoint: 'https://auth.example.com/oauth2/auth',
      token_endpoint: 'https://evil.example/oauth2/token',
    });
    await assert.rejects(
      () => new RediensIam(validConfig).login(),
      (e: RediensIamError) => e.code === 'discovery_failed',
    );

    // A document that stays on the issuer is still accepted — the check must not break the
    // ordinary deployment.
    respondWith({
      authorization_endpoint: 'https://auth.example.com/oauth2/auth',
      token_endpoint: 'https://auth.example.com/oauth2/token',
    });
    void new RediensIam(validConfig).login(); // never resolves: it navigates
    // Discovery, then the PKCE digest — both genuinely async, so wait for the navigation.
    for (let i = 0; i < 100 && assigned.length === 0; i++) {
      await new Promise((resolve) => setTimeout(resolve, 1));
    }
    assert.equal(assigned.length, 1);
    assert.ok(assigned[0].startsWith('https://auth.example.com/oauth2/auth?'));
  } finally {
    globalThis.fetch = realFetch;
    delete (globalThis as { location?: unknown }).location;
    delete (globalThis as { sessionStorage?: unknown }).sessionStorage;
  }
});

test('a fresh client is not authenticated', () => {
  assert.equal(new RediensIam(validConfig).isAuthenticated, false);
  assert.deepEqual(new RediensIam(validConfig).claims.roles, []);
  assert.equal(new RediensIam(validConfig).hasRole('super_admin'), false);
});

test('base64url output has no padding or non-url-safe characters', () => {
  const encoded = base64UrlEncode(new Uint8Array([251, 255, 190, 0, 1, 2]));
  assert.doesNotMatch(encoded, /[+/=]/);
});

test('PKCE challenge matches the RFC 7636 test vector', async () => {
  // RFC 7636 Appendix B.
  const verifier = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk';
  assert.equal(await s256(verifier), 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');
});

test('generated verifiers are unique and long enough', () => {
  const a = randomUrlSafe(64);
  const b = randomUrlSafe(64);
  assert.notEqual(a, b);
  // RFC 7636 requires 43..128 characters.
  assert.ok(a.length >= 43 && a.length <= 128, `unexpected length ${a.length}`);
});

test('claims are read from the ext object Hydra produces', () => {
  const payload = {
    sub: 'org-1:user-1',
    exp: 1_800_000_000,
    ext: { user_id: 'user-1', org_id: 'org-1', project_id: 'proj-1', roles: ['org_admin'] },
  };
  const token = `x.${base64UrlEncode(new TextEncoder().encode(JSON.stringify(payload)))}.y`;

  const decoded = decodeJwtPayload(token);
  assert.ok(decoded);
  assert.deepEqual((decoded.ext as Record<string, unknown>).roles, ['org_admin']);
});

test('tenant roles only match when qualified by their own project', () => {
  const roles = ['proj-1/admin', 'org_admin'];

  assert.equal(matchProjectRole(roles, 'proj-1', 'admin'), true);
  // The whole point of the namespacing: another tenant's "admin" is a different role.
  assert.equal(matchProjectRole(roles, 'proj-2', 'admin'), false);
  // A bare management name is not a project role, and vice versa.
  assert.equal(matchProjectRole(roles, 'proj-1', 'org_admin'), false);
  // No project in the token — nothing to qualify against, so nothing matches.
  assert.equal(matchProjectRole(roles, undefined, 'admin'), false);
});

test('malformed tokens decode to null rather than throwing', () => {
  assert.equal(decodeJwtPayload('not-a-jwt'), null);
  assert.equal(decodeJwtPayload('a.b'), null);
  assert.equal(decodeJwtPayload('a.!!!not-base64!!!.c'), null);
});

/**
 * `id_token_hint` must be the ID token, and the SDK dropped the one it received.
 *
 * It sent the ACCESS token instead — into a query string, which lands in browser history, the
 * IdP's access logs and every intermediary. That is precisely what the in-memory-only token design
 * exists to prevent. RP-initiated logout also wants an ID token, so Hydra would not have honoured
 * the post-logout redirect with an access token in that field.
 *
 * Driven through a real callback rather than by poking at state: `#accessToken` is a private
 * field, and a test that assigns to `iam['#accessToken']` silently sets an unrelated property and
 * then passes because there was no token to leak.
 */
test('logout sends the id token, never the access token', async () => {
  const assigned: string[] = [];
  const realFetch = globalThis.fetch;
  const stub = <T,>(name: string, value: T) =>
    Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });

  const store = new Map<string, string>();
  stub('sessionStorage', {
    setItem: (k: string, v: string) => store.set(k, v),
    getItem: (k: string) => store.get(k) ?? null,
    removeItem: (k: string) => store.delete(k),
  });
  stub('location', {
    origin: 'https://app.example.com',
    href: 'https://app.example.com/callback?code=abc&state=the-state',
    assign: (url: string) => assigned.push(url),
  });
  stub('history', { replaceState() {} });
  store.set('rediensiam:pkce', JSON.stringify({ verifier: 'v'.repeat(43), state: 'the-state' }));

  const discovery = {
    issuer: 'https://auth.example.com',
    authorization_endpoint: 'https://auth.example.com/oauth2/auth',
    token_endpoint: 'https://auth.example.com/oauth2/token',
    end_session_endpoint: 'https://auth.example.com/oauth2/sessions/logout',
  };
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.endsWith('/oauth2/token')) {
      return new Response(
        JSON.stringify({
          access_token: 'ACCESS-TOKEN-SECRET',
          id_token: 'THE-ID-TOKEN',
          expires_in: 3600,
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      );
    }
    return new Response(JSON.stringify(discovery), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  }) as typeof fetch;

  try {
    const iam = new RediensIam(validConfig);
    assert.equal(await iam.handleRedirect(), true, 'the callback should complete');

    await iam.logout();

    assert.equal(assigned.length, 1);
    assert.ok(
      !assigned[0].includes('ACCESS-TOKEN-SECRET'),
      `the access token reached the logout URL: ${assigned[0]}`,
    );
    assert.ok(
      assigned[0].includes('id_token_hint=THE-ID-TOKEN'),
      `the id token was not sent: ${assigned[0]}`,
    );
  } finally {
    globalThis.fetch = realFetch;
    delete (globalThis as { location?: unknown }).location;
    delete (globalThis as { sessionStorage?: unknown }).sessionStorage;
    delete (globalThis as { history?: unknown }).history;
  }
});

/**
 * `fetch` merged headers by spreading `init.headers`. Spreading a `Headers` instance yields `{}` —
 * its entries are not own enumerable properties — and spreading the `[[k, v]]` array form yields
 * `{"0": [...]}`. Either way the caller's Content-Type and friends vanished without error and the
 * API answered 415. `Headers` is the canonical form and what `Request.headers` gives you.
 *
 * Driven through a real callback for the same reason as the logout test: the token lives in a
 * private field, so a client that was not really signed in throws `not_authenticated` and a test
 * that swallows the error passes without ever reaching the header merge.
 */
test('fetch preserves caller headers given as a Headers instance', async () => {
  let seen: Headers | undefined;
  const realFetch = globalThis.fetch;
  const stub = <T,>(name: string, value: T) =>
    Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });

  const store = new Map<string, string>();
  stub('sessionStorage', {
    setItem: (k: string, v: string) => store.set(k, v),
    getItem: (k: string) => store.get(k) ?? null,
    removeItem: (k: string) => store.delete(k),
  });
  stub('location', {
    origin: 'https://app.example.com',
    href: 'https://app.example.com/callback?code=abc&state=the-state',
    assign() {},
  });
  stub('history', { replaceState() {} });
  store.set('rediensiam:pkce', JSON.stringify({ verifier: 'v'.repeat(43), state: 'the-state' }));

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.endsWith('/oauth2/token')) {
      return new Response(JSON.stringify({ access_token: 'token', expires_in: 3600 }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      });
    }
    if (url.includes('/api/thing')) {
      seen = new Headers(init?.headers);
      return new Response('{}', { status: 200 });
    }
    return new Response(
      JSON.stringify({
        issuer: 'https://auth.example.com',
        authorization_endpoint: 'https://auth.example.com/oauth2/auth',
        token_endpoint: 'https://auth.example.com/oauth2/token',
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    );
  }) as typeof fetch;

  try {
    const iam = new RediensIam(validConfig);
    assert.equal(await iam.handleRedirect(), true);

    await iam.fetch('https://app.example.com/api/thing', {
      method: 'POST',
      headers: new Headers({ 'Content-Type': 'application/json', 'Idempotency-Key': 'abc' }),
    });

    assert.equal(seen?.get('content-type'), 'application/json');
    assert.equal(seen?.get('idempotency-key'), 'abc');
    assert.equal(seen?.get('authorization'), 'Bearer token');
  } finally {
    globalThis.fetch = realFetch;
    delete (globalThis as { location?: unknown }).location;
    delete (globalThis as { sessionStorage?: unknown }).sessionStorage;
    delete (globalThis as { history?: unknown }).history;
  }
});
