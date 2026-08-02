import { beforeEach, describe, expect, it, vi } from 'vitest';
import * as api from './api';

/**
 * Every request the login app makes goes through one wrapper, and two of its properties hold
 * everywhere or nowhere:
 *
 *  - `X-Requested-With` is set on all of them. It is CSRF defence-in-depth behind SameSite
 *    cookies, and it only works while no call bypasses the wrapper.
 *  - credentials are included by default, because most of these endpoints authenticate off the
 *    pending-MFA session cookie. The pre-authentication ones must NOT send it: the challenge
 *    lookup and the password-reset flow are reachable by anyone with a link, and attaching a
 *    half-finished session to them is how one visitor's reset lands on another's session.
 *
 * So this is a table of the wire contract, asserted call by call.
 */

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({ ok: true, status: 200, text: async () => '{"ok":true}' });
  vi.stubGlobal('fetch', fetchMock);
});

const call = () => fetchMock.mock.calls.at(-1) as [string, RequestInit];
const init = () => call()[1];
const headers = () => init().headers as Record<string, string>;

/** [what it is, the call, the path, the method (absent = GET), whether the cookie goes]. */
type Route = readonly [string, () => Promise<unknown>, string, string | undefined, 'include' | 'omit'];

const ROUTES: readonly Route[] = [
  ['getLoginChallenge', () => api.getLoginChallenge('c 1'), '/auth/login?login_challenge=c%201', undefined, 'omit'],
  ['submitLogin', () => api.submitLogin({ login_challenge: 'c1', email: 'a@b.test', password: 'p' }),
    '/auth/login', 'POST', 'include'],
  ['getLogoutChallenge', () => api.getLogoutChallenge('c1'), '/auth/logout?logout_challenge=c1', undefined, 'omit'],
  ['acceptLogout', () => api.acceptLogout('c1'), '/auth/logout', 'POST', 'include'],

  ['verifyTotp', () => api.verifyTotp('123456'), '/auth/mfa/totp/verify', 'POST', 'include'],
  ['sendSmsOtp', () => api.sendSmsOtp(), '/auth/mfa/phone/send', 'POST', 'include'],
  ['verifySmsOtp', () => api.verifySmsOtp('123456'), '/auth/mfa/phone/verify', 'POST', 'include'],
  ['getWebAuthnOptions', () => api.getWebAuthnOptions(), '/auth/mfa/webauthn/options', undefined, 'include'],
  ['verifyWebAuthn', () => api.verifyWebAuthn({ id: 'c1' }), '/auth/mfa/webauthn/verify', 'POST', 'include'],
  ['verifyBackupCode', () => api.verifyBackupCode('abcd'), '/auth/mfa/backup-codes/verify', 'POST', 'include'],

  ['registerUser', () => api.registerUser({ login_challenge: 'c1', email: 'a@b.test', password: 'p' }),
    '/auth/register', 'POST', 'include'],
  ['verifyRegistrationOtp', () => api.verifyRegistrationOtp('s1', '123456'), '/auth/register/verify', 'POST', 'include'],

  ['requestPasswordReset', () => api.requestPasswordReset('p1', 'a@b.test'),
    '/auth/password-reset/request', 'POST', 'omit'],
  ['verifyPasswordResetOtp', () => api.verifyPasswordResetOtp('s1', '123456'),
    '/auth/password-reset/verify', 'POST', 'omit'],
  ['confirmPasswordReset', () => api.confirmPasswordReset('t1', 'new-password'),
    '/auth/password-reset/confirm', 'POST', 'omit'],

  ['getThemeByProject', () => api.getThemeByProject('p 1'), '/auth/login/theme?project_id=p%201', undefined, 'omit'],
  ['completeInvite', () => api.completeInvite('t1', 'p'), '/auth/invite/complete', 'POST', 'include'],

  // Mid-login enrolment: the /account/* equivalents need a bearer the user does not have yet.
  ['setupTotp', () => api.setupTotp(), '/auth/mfa/setup/totp/start', 'POST', 'include'],
  ['confirmTotp', () => api.confirmTotp('123456'), '/auth/mfa/setup/totp/confirm', 'POST', 'include'],
];

describe('the wire contract', () => {
  it.each(ROUTES.map(r => [r] as const))('%s', async ([_n, invoke, path, method, credentials]) => {
    await invoke();

    expect(call()[0]).toBe(path);
    expect(init().method).toBe(method);
    expect(init().credentials).toBe(credentials);
    expect(headers()['X-Requested-With']).toBe('XMLHttpRequest');
  });

  it('covers every exported function', () => {
    const exported = Object.keys(api).filter(k => typeof (api as Record<string, unknown>)[k] === 'function');
    const called = new Set(ROUTES.map(([name]) => name));
    expect(exported.filter(name => !called.has(name))).toEqual([]);
  });
});

describe('the bodies', () => {
  it.each([
    ['submitLogin', () => api.submitLogin({ login_challenge: 'c1', username: 'ada', password: 'p' }),
      { login_challenge: 'c1', username: 'ada', password: 'p' }],
    ['acceptLogout', () => api.acceptLogout('c1'), { logout_challenge: 'c1' }],
    ['verifyTotp', () => api.verifyTotp('123456'), { code: '123456' }],
    ['verifyWebAuthn', () => api.verifyWebAuthn({ id: 'c1' }), { id: 'c1' }],
    ['verifyRegistrationOtp', () => api.verifyRegistrationOtp('s1', '1'), { session_id: 's1', code: '1' }],
    ['requestPasswordReset', () => api.requestPasswordReset('p1', 'a@b.test'),
      { project_id: 'p1', email: 'a@b.test' }],
    // new_password, not newPassword: the backend binds snake_case.
    ['confirmPasswordReset', () => api.confirmPasswordReset('t1', 'pw'), { token: 't1', new_password: 'pw' }],
    ['completeInvite', () => api.completeInvite('t1', 'pw'), { token: 't1', password: 'pw' }],
  ])('%s sends what the endpoint binds', async (_n, invoke, body) => {
    await invoke();
    expect(JSON.parse(init().body as string)).toEqual(body);
  });

  it('sets the content type only where there is a body', async () => {
    await api.sendSmsOtp();
    expect(headers()['Content-Type']).toBeUndefined();

    await api.verifyTotp('123456');
    expect(headers()['Content-Type']).toBe('application/json');
  });
});

describe('reading the response', () => {
  it('parses the body', async () => {
    fetchMock.mockResolvedValue({ ok: true, status: 200, text: async () => '{"mfa_required":true}' });
    await expect(api.submitLogin({ login_challenge: 'c1', password: 'p' }))
      .resolves.toEqual({ mfa_required: true });
  });

  it('turns a non-JSON body into the status, not a parser stack trace', async () => {
    // A proxy timeout or an HTML error page must not surface as a SyntaxError to a visitor who
    // has not even signed in.
    fetchMock.mockResolvedValue({ ok: false, status: 502, text: async () => '<html>Bad Gateway</html>' });

    await expect(api.submitLogin({ login_challenge: 'c1', password: 'p' }))
      .rejects.toThrow('Server error 502');
  });

  it('parses an error body the server did send, so the page can explain it', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 400, text: async () => '{"error":"invalid_credentials"}' });

    await expect(api.submitLogin({ login_challenge: 'c1', password: 'p' }))
      .resolves.toEqual({ error: 'invalid_credentials' });
  });

  it.each([
    ['getLoginChallenge', () => api.getLoginChallenge('c1'), 'Failed to load challenge'],
    ['getLogoutChallenge', () => api.getLogoutChallenge('c1'), 'Failed to load logout challenge'],
    ['acceptLogout', () => api.acceptLogout('c1'), 'Failed to complete sign-out'],
    ['getThemeByProject', () => api.getThemeByProject('p1'), 'Failed to load theme'],
  ])('%s refuses a failed response before parsing it', async (_n, invoke, message) => {
    fetchMock.mockResolvedValue({ ok: false, status: 404, text: async () => '{"error":"not_found"}' });
    await expect(invoke()).rejects.toThrow(message);
  });
});
