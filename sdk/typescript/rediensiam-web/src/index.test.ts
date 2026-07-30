import assert from 'node:assert/strict';
import test from 'node:test';

import {
  RediensIam,
  RediensIamError,
  base64UrlEncode,
  decodeJwtPayload,
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
