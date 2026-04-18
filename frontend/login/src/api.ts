const BASE = import.meta.env.VITE_API_BASE_URL ?? '';

async function parseJson(r: Response): Promise<unknown> {
  const text = await r.text();
  try { return JSON.parse(text); }
  catch { throw new Error(`Server error ${r.status}`); }
}

export async function getLoginTheme(challenge: string) {
  const r = await fetch(`${BASE}/auth/login/theme?login_challenge=${challenge}`);
  if (!r.ok) throw new Error('Failed to load theme');
  return parseJson(r);
}

export async function getLoginChallenge(challenge: string) {
  const r = await fetch(`${BASE}/auth/login?login_challenge=${challenge}`);
  if (!r.ok) throw new Error('Failed to load challenge');
  return parseJson(r);
}

export async function submitLogin(body: {
  login_challenge: string;
  email?: string;
  username?: string;
  password: string;
}) {
  const r = await fetch(`${BASE}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
    credentials: 'include',
  });
  return parseJson(r);
}

export async function verifyTotp(code: string) {
  const r = await fetch(`${BASE}/auth/mfa/totp/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code }),
    credentials: 'include',
  });
  return parseJson(r);
}

export async function sendSmsOtp() {
  const r = await fetch(`${BASE}/auth/mfa/phone/send`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
  });
  return parseJson(r);
}

export async function verifySmsOtp(code: string) {
  const r = await fetch(`${BASE}/auth/mfa/phone/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code }),
    credentials: 'include',
  });
  return parseJson(r);
}

export async function getWebAuthnOptions() {
  const r = await fetch(`${BASE}/auth/mfa/webauthn/options`, { credentials: 'include' });
  return parseJson(r);
}

export async function verifyWebAuthn(assertionResponse: object) {
  const r = await fetch(`${BASE}/auth/mfa/webauthn/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(assertionResponse),
    credentials: 'include',
  });
  return parseJson(r);
}

export async function verifyBackupCode(code: string) {
  const r = await fetch(`${BASE}/auth/mfa/backup-codes/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code }),
    credentials: 'include',
  });
  return parseJson(r);
}

export async function registerUser(body: {
  login_challenge: string;
  email: string;
  password: string;
  username?: string;
  phone?: string;
}) {
  const r = await fetch(`${BASE}/auth/register`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
    credentials: 'include',
  });
  return parseJson(r);
}

export async function verifyRegistrationOtp(sessionId: string, code: string) {
  const r = await fetch(`${BASE}/auth/register/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ session_id: sessionId, code }),
    credentials: 'include',
  });
  return parseJson(r);
}

export async function requestPasswordReset(projectId: string, email: string) {
  const r = await fetch(`${BASE}/auth/password-reset/request`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ project_id: projectId, email }),
  });
  return parseJson(r);
}

export async function verifyPasswordResetOtp(sessionId: string, code: string) {
  const r = await fetch(`${BASE}/auth/password-reset/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ session_id: sessionId, code }),
  });
  return parseJson(r);
}

export async function confirmPasswordReset(token: string, newPassword: string) {
  const r = await fetch(`${BASE}/auth/password-reset/confirm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, new_password: newPassword }),
  });
  return parseJson(r);
}

export async function getThemeByProject(projectId: string) {
  const r = await fetch(`${BASE}/auth/login/theme?project_id=${encodeURIComponent(projectId)}`);
  if (!r.ok) throw new Error('Failed to load theme');
  return parseJson(r);
}

export async function completeInvite(token: string, password: string) {
  const r = await fetch(`${BASE}/auth/invite/complete`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ token, password }),
  });
  return parseJson(r);
}

export async function setupTotp() {
  const r = await fetch(`${BASE}/account/mfa/totp/setup`, {
    method: 'POST',
    credentials: 'include',
  });
  return parseJson(r);
}

export async function confirmTotp(code: string) {
  const r = await fetch(`${BASE}/account/mfa/totp/confirm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ code }),
  });
  return parseJson(r);
}
