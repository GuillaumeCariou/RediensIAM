const BASE = import.meta.env.VITE_API_BASE_URL ?? '';

/**
 * Reads the body once and parses it, turning a non-JSON response (an HTML error page, a proxy
 * timeout) into `Server error <status>` rather than a parser stack trace shown to an
 * unauthenticated visitor.
 *
 * The return type is `any` — deliberately, not for want of a better one. Every endpoint in this
 * file has a different response shape, so a single named type here would be a lie; `unknown` would
 * be honest and would force a cast at all thirty call sites, which is a cast either way and one
 * that reads as a check without being one. What keeps this safe is that nothing branches on the
 * result without testing the field it wants first.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
async function parseJson(r: Response): Promise<any> {
  const text = await r.text();
  try { return JSON.parse(text); }
  catch { throw new Error(`Server error ${r.status}`); }
}

/**
 * The single place every login-app request goes through, so two things hold everywhere:
 *
 * - `X-Requested-With` is always set. It is CSRF defence-in-depth — SameSite cookies are the
 *   primary defence — and it only works as long as no call bypasses this wrapper.
 * - Credentials are included by default, because most of these endpoints authenticate off the
 *   pending-MFA session cookie. The few that must not send it pass `credentials: 'omit'`
 *   explicitly; note the spread of `init` after it, which is what lets them override.
 */
async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const headers: Record<string, string> = {
    'X-Requested-With': 'XMLHttpRequest',
    ...(init.headers as Record<string, string> | undefined),
  };
  if (init.body && !headers['Content-Type']) headers['Content-Type'] = 'application/json';
  return fetch(`${BASE}${path}`, { credentials: 'include', ...init, headers });
}

const enc = encodeURIComponent;

export async function getLoginChallenge(challenge: string) {
  const r = await apiFetch(`/auth/login?login_challenge=${enc(challenge)}`, { credentials: 'omit' });
  if (!r.ok) throw new Error('Failed to load challenge');
  return parseJson(r);
}

export async function submitLogin(body: {
  login_challenge: string;
  email?: string;
  username?: string;
  password: string;
}) {
  const r = await apiFetch('/auth/login', { method: 'POST', body: JSON.stringify(body) });
  return parseJson(r);
}

export async function getLogoutChallenge(challenge: string) {
  const r = await apiFetch(`/auth/logout?logout_challenge=${enc(challenge)}`, { credentials: 'omit' });
  if (!r.ok) throw new Error('Failed to load logout challenge');
  return parseJson(r);
}

export async function acceptLogout(challenge: string) {
  const r = await apiFetch('/auth/logout', {
    method: 'POST',
    body: JSON.stringify({ logout_challenge: challenge }),
  });
  if (!r.ok) throw new Error('Failed to complete sign-out');
  return parseJson(r);
}

export async function verifyTotp(code: string) {
  const r = await apiFetch('/auth/mfa/totp/verify', { method: 'POST', body: JSON.stringify({ code }) });
  return parseJson(r);
}

export async function sendSmsOtp() {
  const r = await apiFetch('/auth/mfa/phone/send', { method: 'POST' });
  return parseJson(r);
}

export async function verifySmsOtp(code: string) {
  const r = await apiFetch('/auth/mfa/phone/verify', { method: 'POST', body: JSON.stringify({ code }) });
  return parseJson(r);
}

export async function getWebAuthnOptions() {
  const r = await apiFetch('/auth/mfa/webauthn/options');
  return parseJson(r);
}

export async function verifyWebAuthn(assertionResponse: object) {
  const r = await apiFetch('/auth/mfa/webauthn/verify', { method: 'POST', body: JSON.stringify(assertionResponse) });
  return parseJson(r);
}

export async function verifyBackupCode(code: string) {
  const r = await apiFetch('/auth/mfa/backup-codes/verify', { method: 'POST', body: JSON.stringify({ code }) });
  return parseJson(r);
}

export async function registerUser(body: {
  login_challenge: string;
  email: string;
  password: string;
  username?: string;
  phone?: string;
}) {
  const r = await apiFetch('/auth/register', { method: 'POST', body: JSON.stringify(body) });
  return parseJson(r);
}

export async function verifyRegistrationOtp(sessionId: string, code: string) {
  const r = await apiFetch('/auth/register/verify', {
    method: 'POST',
    body: JSON.stringify({ session_id: sessionId, code }),
  });
  return parseJson(r);
}

export async function requestPasswordReset(projectId: string, email: string) {
  const r = await apiFetch('/auth/password-reset/request', {
    method: 'POST',
    body: JSON.stringify({ project_id: projectId, email }),
    credentials: 'omit',
  });
  return parseJson(r);
}

export async function verifyPasswordResetOtp(sessionId: string, code: string) {
  const r = await apiFetch('/auth/password-reset/verify', {
    method: 'POST',
    body: JSON.stringify({ session_id: sessionId, code }),
    credentials: 'omit',
  });
  return parseJson(r);
}

export async function confirmPasswordReset(token: string, newPassword: string) {
  const r = await apiFetch('/auth/password-reset/confirm', {
    method: 'POST',
    body: JSON.stringify({ token, new_password: newPassword }),
    credentials: 'omit',
  });
  return parseJson(r);
}

export async function getThemeByProject(projectId: string) {
  const r = await apiFetch(`/auth/login/theme?project_id=${enc(projectId)}`, { credentials: 'omit' });
  if (!r.ok) throw new Error('Failed to load theme');
  return parseJson(r);
}

export async function completeInvite(token: string, password: string) {
  const r = await apiFetch('/auth/invite/complete', {
    method: 'POST',
    body: JSON.stringify({ token, password }),
  });
  return parseJson(r);
}

/**
 * MFA enrolment mid-login. This and the other `/auth/mfa/setup/*` calls below deliberately do not
 * use the `/account/*` endpoints: those require a bearer token, which the user does not have yet
 * at this point in the flow. These authenticate off the pending-MFA session cookie instead.
 */
export async function setupTotp() {
  const r = await apiFetch('/auth/mfa/setup/totp/start', { method: 'POST' });
  return parseJson(r);
}

export async function confirmTotp(code: string) {
  const r = await apiFetch('/auth/mfa/setup/totp/confirm', {
    method: 'POST',
    body: JSON.stringify({ code }),
  });
  return parseJson(r);
}
