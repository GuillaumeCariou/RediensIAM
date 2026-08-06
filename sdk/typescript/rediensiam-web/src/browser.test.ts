import assert from 'node:assert/strict';
import test from 'node:test';

import { RediensIam, RediensIamError, createRediensIam } from './index.ts';

/**
 * The paths that need a browser: the constructor's origin check, the callback, the refresh, the
 * authenticated fetch and the sign-out. `index.test.ts` covers the pure helpers and two of these
 * end to end; this file drives the rest through the same real callback rather than by poking at
 * private fields, for the reason given there — assigning to `iam['#accessToken']` sets an
 * unrelated property and the test then passes with no token to protect.
 */

const validConfig = {
  issuer: 'https://auth.example.com',
  clientId: 'client_app',
  redirectUri: 'https://app.example.com/callback',
};

const DISCOVERY = {
  issuer: 'https://auth.example.com',
  authorization_endpoint: 'https://auth.example.com/oauth2/auth',
  token_endpoint: 'https://auth.example.com/oauth2/token',
  end_session_endpoint: 'https://auth.example.com/oauth2/sessions/logout',
};

/** A JWT whose payload is `claims`. Only the payload is ever read. */
function jwt(claims: Record<string, unknown>): string {
  const b64 = Buffer.from(JSON.stringify(claims)).toString('base64url');
  return `header.${b64}.signature`;
}

interface Browser {
  assigned: string[];
  store: Map<string, string>;
  restore: () => void;
  requests: { url: string; init?: RequestInit }[];
}

/**
 * Installs the browser globals the SDK reads, and a fetch that answers discovery and the token
 * endpoint. `routes` overrides either, keyed by a substring of the URL.
 */
function browser(opts: {
  href?: string;
  routes?: Record<string, () => Response | Promise<Response>>;
} = {}): Browser {
  const realFetch = globalThis.fetch;
  const assigned: string[] = [];
  const requests: { url: string; init?: RequestInit }[] = [];
  const store = new Map<string, string>();
  const stub = <T,>(name: string, value: T) =>
    Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });

  stub('sessionStorage', {
    setItem: (k: string, v: string) => store.set(k, v),
    getItem: (k: string) => store.get(k) ?? null,
    removeItem: (k: string) => store.delete(k),
  });
  stub('location', {
    origin: 'https://app.example.com',
    href: opts.href ?? 'https://app.example.com/callback?code=abc&state=the-state',
    assign: (url: string) => assigned.push(url),
  });
  stub('history', { replaceState() {} });

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'object' && 'url' in input ? input.url : String(input);
    requests.push({ url, init });
    for (const [match, handler] of Object.entries(opts.routes ?? {})) {
      if (url.includes(match)) return handler();
    }
    if (url.endsWith('/oauth2/token')) {
      return Response.json({ access_token: jwt({ sub: 'u1' }), expires_in: 3600 });
    }
    return Response.json(DISCOVERY);
  }) as typeof fetch;

  return {
    assigned, store, requests,
    restore() {
      globalThis.fetch = realFetch;
      for (const k of ['location', 'sessionStorage', 'history']) {
        delete (globalThis as Record<string, unknown>)[k];
      }
    },
  };
}

/** A client that has completed a callback, so it really holds a token. */
async function signedIn(b: Browser, config = validConfig): Promise<RediensIam> {
  b.store.set('rediensiam:pkce', JSON.stringify({ verifier: 'v'.repeat(43), state: 'the-state' }));
  const iam = new RediensIam(config);
  assert.equal(await iam.handleRedirect(), true, 'the callback should complete');
  return iam;
}

// ── Configuration ───────────────────────────────────────────────────────────

test('a redirectUri pointing at another origin is refused at construction', () => {
  // Never trust a server-supplied redirect_uri whose origin differs from the app's: a compromised
  // config endpoint could otherwise hand the authorization code to an origin the app does not own.
  const b = browser();
  try {
    assert.throws(
      () => new RediensIam({ ...validConfig, redirectUri: 'https://evil.example/callback' }),
      (e: RediensIamError) => e.code === 'config_invalid',
    );
    assert.doesNotThrow(() => new RediensIam({ ...validConfig, redirectUri: '/callback' }));
  } finally { b.restore(); }
});

test('the factory builds the same client the constructor does', () => {
  assert.ok(createRediensIam(validConfig) instanceof RediensIam);
});

// ── The callback ────────────────────────────────────────────────────────────

test('a callback whose state is not the one we issued is refused', async () => {
  // The state is what ties the callback to this browser's own login attempt; accepting another's
  // is the login-CSRF the parameter exists to prevent.
  const b = browser();
  try {
    b.store.set('rediensiam:pkce', JSON.stringify({ verifier: 'v'.repeat(43), state: 'a-different-state' }));
    const iam = new RediensIam(validConfig);

    await assert.rejects(() => iam.handleRedirect(), (e: RediensIamError) => e.code === 'state_mismatch');
    assert.equal(b.store.has('rediensiam:pkce'), false, 'the state should be spent either way');
  } finally { b.restore(); }
});

test('a token exchange the server refuses is reported, not swallowed', async () => {
  const b = browser({ routes: { '/oauth2/token': () => new Response('nope', { status: 400 }) } });
  try {
    b.store.set('rediensiam:pkce', JSON.stringify({ verifier: 'v'.repeat(43), state: 'the-state' }));
    const iam = new RediensIam(validConfig);

    await assert.rejects(
      () => iam.handleRedirect(),
      (e: RediensIamError) => e.code === 'token_exchange_failed',
    );
  } finally { b.restore(); }
});

test('discovery that fails is reported with its status', async () => {
  const b = browser({ routes: { 'openid-configuration': () => new Response('down', { status: 503 }) } });
  try {
    const iam = new RediensIam(validConfig);
    await assert.rejects(() => iam.login(), (e: RediensIamError) => e.code === 'discovery_failed');
  } finally { b.restore(); }
});

test('a discovery document naming an endpoint outside the issuer is refused', async () => {
  // Discovery is fetched over TLS from the issuer, but a compromised or mis-configured one could
  // name someone else's token endpoint — which is where the authorization code and the PKCE
  // verifier would then go.
  for (const token_endpoint of ['https://evil.example/oauth2/token', 'not a url', 42]) {
    const b = browser({
      routes: { 'openid-configuration': () => Response.json({ ...DISCOVERY, token_endpoint }) },
    });
    try {
      const iam = new RediensIam(validConfig);
      await assert.rejects(
        () => iam.login(),
        (e: RediensIamError) => e.code === 'discovery_failed',
        `token_endpoint: ${String(token_endpoint)}`,
      );
    } finally { b.restore(); }
  }
});

// ── Claims ──────────────────────────────────────────────────────────────────

test('claims come out of the token the callback returned', async () => {
  const b = browser({
    routes: {
      '/oauth2/token': () => Response.json({
        access_token: jwt({ sub: 'u1', ext: { roles: ['org_admin'], org_id: 'o1', project_id: 'p1' } }),
        expires_in: 3600,
      }),
    },
  });
  try {
    const iam = await signedIn(b);

    assert.deepEqual(iam.claims.roles, ['org_admin']);
    assert.equal(iam.claims.orgId, 'o1');
    assert.equal(iam.hasRole('org_admin'), true);
    assert.equal(iam.hasRole('super_admin'), false);
  } finally { b.restore(); }
});

test('a project role only matches when it names that project', async () => {
  const b = browser({
    routes: {
      '/oauth2/token': () => Response.json({
        access_token: jwt({ ext: { roles: ['p1/editor'], project_id: 'p1' } }),
        expires_in: 3600,
      }),
    },
  });
  try {
    const iam = await signedIn(b);

    assert.equal(iam.hasProjectRole('editor'), true, 'the token\'s own project');
    assert.equal(iam.hasProjectRole('editor', 'p1'), true);
    assert.equal(iam.hasProjectRole('editor', 'p2'), false, 'another tenant\'s project');
  } finally { b.restore(); }
});

test('a token that is not a token yields no claims rather than throwing', async () => {
  const b = browser({
    routes: { '/oauth2/token': () => Response.json({ access_token: 'not.a.jwt', expires_in: 3600 }) },
  });
  try {
    const iam = await signedIn(b);

    assert.deepEqual(iam.claims, { roles: [] });
    assert.equal(iam.hasProjectRole('editor'), false, 'no project means no project role');
  } finally { b.restore(); }
});

test('claims are empty before anything has been signed in', () => {
  const b = browser();
  try {
    assert.deepEqual(new RediensIam(validConfig).claims, { roles: [] });
  } finally { b.restore(); }
});

// ── Refresh ─────────────────────────────────────────────────────────────────

const expiring = (refresh?: string) => ({
  '/oauth2/token': () => Response.json({
    access_token: jwt({ sub: 'u1' }), expires_in: -1,
    ...(refresh === undefined ? {} : { refresh_token: refresh }),
  }),
});

test('an expired token with no refresh token leaves the client signed out', async () => {
  const b = browser({ routes: expiring() });
  try {
    const iam = await signedIn(b);

    assert.equal(iam.isAuthenticated, false);
    assert.equal(await iam.getToken(), null);
  } finally { b.restore(); }
});

test('an expired token is refreshed once, however many callers ask at the same time', async () => {
  // Two refreshes race, and the loser's response overwrites the winner's with a token Hydra has
  // already rotated away.
  let exchanges = 0;
  const b = browser({
    routes: {
      '/oauth2/token': () => {
        exchanges++;
        return Response.json(
          exchanges === 1
            ? { access_token: jwt({ sub: 'u1' }), refresh_token: 'r1', expires_in: -1 }
            : { access_token: jwt({ sub: 'u1', v: 2 }), refresh_token: 'r2', expires_in: 3600 },
        );
      },
    },
  });
  try {
    const iam = await signedIn(b);

    const [a, c] = await Promise.all([iam.getToken(), iam.getToken()]);

    assert.equal(a, c);
    assert.equal(exchanges, 2, 'the code exchange, and exactly one refresh');
    assert.equal(iam.isAuthenticated, true);
  } finally { b.restore(); }
});

test('a refresh the server refuses signs the client out rather than leaving a dead token', async () => {
  let exchanges = 0;
  const b = browser({
    routes: {
      '/oauth2/token': () => {
        exchanges++;
        return exchanges === 1
          ? Response.json({ access_token: jwt({ sub: 'u1' }), refresh_token: 'r1', expires_in: -1 })
          : new Response('revoked', { status: 400 });
      },
    },
  });
  try {
    const iam = await signedIn(b);

    assert.equal(await iam.getToken(), null);
    assert.equal(iam.isAuthenticated, false);
  } finally { b.restore(); }
});

test('a refresh that cannot reach the network reports no token rather than throwing', async () => {
  let exchanges = 0;
  const b = browser({
    routes: {
      '/oauth2/token': () => {
        exchanges++;
        if (exchanges === 1) {
          return Response.json({ access_token: jwt({ sub: 'u1' }), refresh_token: 'r1', expires_in: -1 });
        }
        throw new TypeError('Failed to fetch');
      },
    },
  });
  try {
    const iam = await signedIn(b);
    assert.equal(await iam.getToken(), null);
  } finally { b.restore(); }
});

// ── Authenticated fetch ─────────────────────────────────────────────────────

test('the bearer is never attached to an origin the app did not declare', async () => {
  const b = browser();
  try {
    const iam = await signedIn(b);

    await assert.rejects(
      () => iam.fetch('https://evil.example/steal'),
      (e: RediensIamError) => e.code === 'untrusted_target',
    );
  } finally { b.restore(); }
});

test('a 401 is retried once with a fresh token', async () => {
  let calls = 0;
  const b = browser({
    routes: {
      '/oauth2/token': () => Response.json({
        access_token: jwt({ sub: 'u1' }), refresh_token: 'r1', expires_in: 3600,
      }),
      '/api/thing': () => {
        calls++;
        return new Response(null, { status: calls === 1 ? 401 : 200 });
      },
    },
  });
  try {
    const iam = await signedIn(b, { ...validConfig, apiOrigins: ['https://api.example.com'] });

    const res = await iam.fetch('https://api.example.com/api/thing');

    assert.equal(res.status, 200);
    assert.equal(calls, 2, 'the first attempt, then the retry');
  } finally { b.restore(); }
});

test('a 401 that survives the refresh is reported as an expired session', async () => {
  let exchanges = 0;
  const b = browser({
    routes: {
      '/oauth2/token': () => {
        exchanges++;
        return exchanges === 1
          ? Response.json({ access_token: jwt({ sub: 'u1' }), refresh_token: 'r1', expires_in: 3600 })
          : new Response('revoked', { status: 400 });
      },
      '/api/thing': () => new Response(null, { status: 401 }),
    },
  });
  try {
    const iam = await signedIn(b, { ...validConfig, apiOrigins: ['https://api.example.com'] });

    await assert.rejects(
      () => iam.fetch('https://api.example.com/api/thing'),
      (e: RediensIamError) => e.code === 'not_authenticated',
    );
  } finally { b.restore(); }
});

// ── Sign-out ────────────────────────────────────────────────────────────────

test('signing out without an id token still ends the session at the authorization server', async () => {
  // The token endpoint answered without one, so there is no `id_token_hint` to send — but the
  // sign-out still has to reach the end-session endpoint, or the Hydra cookie outlives it.
  const b = browser();
  try {
    const iam = await signedIn(b);

    await iam.logout();

    assert.equal(b.assigned.length, 1);
    assert.ok(b.assigned[0].startsWith('https://auth.example.com/oauth2/sessions/logout'));
    assert.ok(!b.assigned[0].includes('id_token_hint'), b.assigned[0]);
    assert.equal(iam.isAuthenticated, false);
  } finally { b.restore(); }
});

test('it asks to be returned where the app said', async () => {
  const b = browser();
  try {
    const iam = await signedIn(b, {
      ...validConfig, postLogoutRedirectUri: 'https://app.example.com/goodbye',
    });

    await iam.logout();

    assert.ok(
      b.assigned[0].includes('post_logout_redirect_uri=https%3A%2F%2Fapp.example.com%2Fgoodbye'),
      b.assigned[0],
    );
  } finally { b.restore(); }
});

test('an authorization server with no end-session endpoint is left locally, not hung on', async () => {
  const { end_session_endpoint: _unused, ...noEndSession } = DISCOVERY;
  const b = browser({ routes: { 'openid-configuration': () => Response.json(noEndSession) } });
  try {
    const iam = await signedIn(b, {
      ...validConfig, postLogoutRedirectUri: 'https://app.example.com/goodbye',
    });

    await iam.logout();

    assert.deepEqual(b.assigned, ['https://app.example.com/goodbye']);
    assert.equal(iam.isAuthenticated, false);
  } finally { b.restore(); }
});

test('and falls back to the app origin when it named nowhere', async () => {
  const { end_session_endpoint: _unused, ...noEndSession } = DISCOVERY;
  const b = browser({ routes: { 'openid-configuration': () => Response.json(noEndSession) } });
  try {
    const iam = await signedIn(b);

    await iam.logout();

    assert.deepEqual(b.assigned, ['https://app.example.com']);
  } finally { b.restore(); }
});

// ── Roles, however Hydra chose to serialise them ────────────────────────────

test('roles are read whether they arrive as a list, a JSON string or a comma-separated one', async () => {
  const cases: [unknown, string[]][] = [
    [['a', 'b'], ['a', 'b']],
    // Hydra has sent all three shapes; a reader that handles one strips the operator's access.
    ['["a","b"]', ['a', 'b']],
    ['a,b', ['a', 'b']],
    ['', []],
    [42, []],
    // A JSON string that is not a list, and one that does not parse, both fall back rather than throw.
    ['{"a":1}', ['{"a":1}']],
    ['[not json', ['[not json']],
    [['a', 7], ['a']],
  ];

  for (const [roles, expected] of cases) {
    const b = browser({
      routes: {
        '/oauth2/token': () => Response.json({
          access_token: jwt({ ext: { roles } }), expires_in: 3600,
        }),
      },
    });
    try {
      const iam = await signedIn(b);
      assert.deepEqual(iam.claims.roles, expected, `roles: ${JSON.stringify(roles)}`);
    } finally { b.restore(); }
  }
});

test('an org or project id that is not a non-empty string is treated as absent', async () => {
  const b = browser({
    routes: {
      '/oauth2/token': () => Response.json({
        access_token: jwt({ ext: { org_id: '', project_id: 42 } }), expires_in: 3600,
      }),
    },
  });
  try {
    const iam = await signedIn(b);

    assert.equal(iam.claims.orgId, undefined);
    assert.equal(iam.claims.projectId, undefined);
  } finally { b.restore(); }
});

/**
 * Impersonation handover. An operator console opens this app with the delegated token in the URL
 * **fragment** — a fragment is never sent to a server, which is the reason it is the handover.
 *
 * Two properties matter and both are asserted: the token is adopted, and the URL no longer carries
 * it. The URL is the one place a credential survives into history, a screenshot or a pasted link.
 */
function impersonationBrowser(hash: string) {
  const replaced: string[] = [];
  const stub = <T,>(name: string, value: T) =>
    Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });

  stub('location', {
    origin: 'https://app.example.com',
    href: `https://app.example.com/dashboard${hash}`,
    pathname: '/dashboard',
    search: '',
    hash,
    assign() {},
  });
  stub('history', { replaceState: (_s: unknown, _t: string, url: string) => replaced.push(url) });

  return {
    replaced,
    restore() {
      for (const k of ['location', 'history']) delete (globalThis as Record<string, unknown>)[k];
    },
  };
}

test('adoptImpersonation takes the delegated session out of the fragment and scrubs the URL', () => {
  const env = impersonationBrowser(
    '#imp_token=rediens_imp_abc&imp_session=7f3&imp_org=acme&imp_mode=read&imp_actor=usr_operator',
  );
  try {
    const iam = createRediensIam(validConfig);

    const context = iam.adoptImpersonation();

    assert.equal(context?.token, 'rediens_imp_abc');
    assert.equal(context?.sessionId, '7f3');
    assert.equal(context?.orgId, 'acme');
    assert.equal(context?.mode, 'read');
    assert.equal(iam.isReadOnlyImpersonation, true);
    assert.equal(iam.impersonation?.operator, 'usr_operator');

    assert.equal(env.replaced.length, 1);
    assert.ok(!env.replaced[0].includes('rediens_imp_abc'), 'the token must not survive in the URL');
  } finally {
    env.restore();
  }
});

test('an ordinary page load adopts nothing', () => {
  const env = impersonationBrowser('');
  try {
    const iam = createRediensIam(validConfig);

    assert.equal(iam.adoptImpersonation(), null);
    assert.equal(iam.impersonation, null);
    assert.equal(iam.isReadOnlyImpersonation, false);
    assert.equal(env.replaced.length, 0, 'a no-op must not rewrite the URL');
  } finally {
    env.restore();
  }
});

test('an unknown mode in the fragment is read as the weaker one', () => {
  const env = impersonationBrowser('#imp_token=rediens_imp_abc&imp_mode=administrator');
  try {
    const iam = createRediensIam(validConfig);

    assert.equal(iam.adoptImpersonation()?.mode, 'read',
      'anything that is not literally "write" must not widen the session');
  } finally {
    env.restore();
  }
});

test('exitImpersonation clears the tab but says nothing about the server', () => {
  const env = impersonationBrowser('#imp_token=rediens_imp_abc&imp_session=7f3');
  try {
    const iam = createRediensIam(validConfig);
    iam.adoptImpersonation();

    iam.exitImpersonation();

    assert.equal(iam.impersonation, null);
    assert.equal(iam.isReadOnlyImpersonation, false);
  } finally {
    env.restore();
  }
});
