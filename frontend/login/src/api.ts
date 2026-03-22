const BASE = import.meta.env.VITE_API_BASE_URL ?? '';

export async function getLoginTheme(challenge: string) {
  const r = await fetch(`${BASE}/auth/login/theme?login_challenge=${challenge}`);
  if (!r.ok) throw new Error('Failed to load theme');
  return r.json();
}

export async function getLoginChallenge(challenge: string) {
  const r = await fetch(`${BASE}/auth/login?login_challenge=${challenge}`);
  if (!r.ok) throw new Error('Failed to load challenge');
  return r.json();
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
  return r.json();
}

export async function verifyTotp(code: string) {
  const r = await fetch(`${BASE}/auth/mfa/totp/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code }),
    credentials: 'include',
  });
  return r.json();
}

export async function sendSmsOtp() {
  const r = await fetch(`${BASE}/auth/mfa/phone/send`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
  });
  return r.json();
}

export async function verifySmsOtp(code: string) {
  const r = await fetch(`${BASE}/auth/mfa/phone/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code }),
    credentials: 'include',
  });
  return r.json();
}

export async function verifyBackupCode(code: string) {
  const r = await fetch(`${BASE}/auth/mfa/backup-codes/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code }),
    credentials: 'include',
  });
  return r.json();
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
  return r.json();
}

export async function verifyRegistrationOtp(sessionId: string, code: string) {
  const r = await fetch(`${BASE}/auth/register/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ session_id: sessionId, code }),
    credentials: 'include',
  });
  return r.json();
}

export async function requestPasswordReset(projectId: string, email: string) {
  const r = await fetch(`${BASE}/auth/password-reset/request`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ project_id: projectId, email }),
  });
  return r.json();
}

export async function verifyPasswordResetOtp(sessionId: string, code: string) {
  const r = await fetch(`${BASE}/auth/password-reset/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ session_id: sessionId, code }),
  });
  return r.json();
}

export async function confirmPasswordReset(token: string, newPassword: string) {
  const r = await fetch(`${BASE}/auth/password-reset/confirm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, new_password: newPassword }),
  });
  return r.json();
}
