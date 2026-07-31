const BASE = import.meta.env.VITE_API_BASE_URL ?? '';

// eslint-disable-next-line @typescript-eslint/no-explicit-any
async function parseJson(r: Response): Promise<any> {
  const text = await r.text();
  try { return JSON.parse(text); }
  catch { throw new Error(`Server error ${r.status}`); }
}

// Centralised fetch wrapper:
//  - Always sets X-Requested-With (CSRF defence-in-depth; SameSite cookies are the primary defence).
//  - Sets Content-Type when a JSON body is supplied.
//  - Includes credentials by default (most endpoints need the MFA session cookie).
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

// MFA enrolment mid-login. /account/* requires a bearer token, which the user does not have
// yet at this point in the flow — these endpoints authenticate off the pending-MFA session.
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
