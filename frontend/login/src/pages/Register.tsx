import { useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { registerUser, verifyRegistrationOtp } from '../api';
import { safeNavigate } from '../safeNavigate';

type Step = 'form' | 'otp' | 'done';

function LoginLogo() {
  return (
    <div className="login-logo">
      <div className="brand-mark">R</div>
      <span>RediensIAM</span>
    </div>
  );
}

/** Six OTP inputs, fixed order — stable keys so React never keys on the array index. */
const OTP_CELL_IDS = ['otp-1', 'otp-2', 'otp-3', 'otp-4', 'otp-5', 'otp-6'];

function scoreLabel(score: number): string {
  if (score < 35) return 'weak';
  if (score < 65) return 'fair';
  if (score < 85) return 'strong';
  return 'excellent';
}

function scoreColor(score: number): string {
  if (score < 35) return 'var(--danger)';
  if (score < 65) return 'var(--warn)';
  return 'var(--success)';
}

function strengthScore(pw: string): number {
  return Math.min(100,
    pw.length * 11 +
    (/\d/.test(pw) ? 12 : 0) +
    (/[A-Z]/.test(pw) ? 12 : 0) +
    (/[^\w]/.test(pw) ? 18 : 0)
  );
}

export default function Register() {
  const [params] = useSearchParams();
  const challenge = params.get('login_challenge') ?? '';

  const [step, setStep] = useState<Step>('form');
  const [sessionId, setSessionId] = useState('');

  const [email, setEmail]       = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm]   = useState('');
  const [cells, setCells]       = useState<string[]>(new Array(6).fill(''));
  const cellRefs = useRef<(HTMLInputElement | null)[]>([]);

  const [error, setError]   = useState('');
  const [loading, setLoading] = useState(false);

  const code = cells.join('');
  const strength = strengthScore(password);
  const strengthLabel = scoreLabel(strength);
  const strengthColor = scoreColor(strength);

  async function handleSubmit(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setLoading(true);
    setError('');
    try {
      const res = await registerUser({ login_challenge: challenge, email, password, username: username || undefined });
      if (res.error === 'password_breached') {
        setError(`This password has appeared in ${res.count ? res.count.toLocaleString() : 'multiple'} data breaches. Choose a different password.`);
        return;
      }
      if (res.error) { setError(res.error_description ?? 'Registration failed.'); return; }
      if (res.requires_verification) { setSessionId(res.session_id); setStep('otp'); return; }
      if (res.redirect_to && !safeNavigate(res.redirect_to)) {
        setError('Could not complete registration. Please try again.');
      }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  async function handleVerify(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      const res = await verifyRegistrationOtp(sessionId, code);
      if (res.error) { setError('Invalid or expired code.'); setCells(new Array(6).fill('')); cellRefs.current[0]?.focus(); return; }
      if (res.redirect_to && !safeNavigate(res.redirect_to)) {
        setError('Could not complete registration. Please try again.');
      }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  function handleCellChange(i: number, v: string) {
    if (!/^\d?$/.test(v)) return;
    const next = [...cells]; next[i] = v; setCells(next);
    if (v && i < 5) cellRefs.current[i + 1]?.focus();
  }

  function handleCellKeyDown(e: React.KeyboardEvent, i: number) {
    if (e.key === 'Backspace' && !cells[i] && i > 0) cellRefs.current[i - 1]?.focus();
  }

  function handleCellPaste(e: React.ClipboardEvent) {
    const digits = e.clipboardData.getData('text').replaceAll(/\D/g, '').slice(0, 6);
    if (digits.length === 0) return;
    e.preventDefault();
    const next = ['', '', '', '', '', ''];
    for (let i = 0; i < digits.length; i++) next[i] = digits[i];
    setCells(next);
    const focusIdx = Math.min(digits.length, 5);
    cellRefs.current[focusIdx]?.focus();
  }

  if (step === 'otp') return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <h1 className="login-title">Check your inbox.</h1>
        <p className="login-subtitle">Enter the 6-digit code we sent to <strong>{email}</strong></p>

        {error && (
          <div className="deny-banner deny-banner-error" style={{ marginTop: 16 }}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0 }}><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>
            {error}
          </div>
        )}

        <div className="deny-banner" style={{ marginTop: 16 }}>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0 }}>
            <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>
          </svg>
          No role means no access. Ask your admin to assign one.
        </div>

        <form onSubmit={handleVerify}>
          <div className="label" style={{ textAlign: 'center', marginTop: 20 }}>Verification code</div>
          <div className="otp-grid">
            {OTP_CELL_IDS.map((cellId, i) => (
              <input key={cellId} ref={el => { cellRefs.current[i] = el; }}
                className="otp-cell" type="text" inputMode="numeric"
                maxLength={1} value={cells[i]} autoFocus={i === 0}
                aria-label={`Digit ${i + 1} of 6`}
                onChange={e => handleCellChange(i, e.target.value)}
                onKeyDown={e => handleCellKeyDown(e, i)}
                onPaste={handleCellPaste}
              />
            ))}
          </div>
          <button className="btn btn-primary btn-lg" type="submit" disabled={loading || code.length !== 6} style={{ marginTop: 16 }}>
            {loading ? 'Verifying…' : 'Verify & sign in'}
          </button>
        </form>

        <button className="btn btn-ghost btn-sm" style={{ marginTop: 12 }} onClick={() => setStep('form')}>
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="15,18 9,12 15,6"/></svg>
          Back
        </button>
      </div>
    </div>
  );

  return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <h1 className="login-title">Create your account.</h1>
        <p className="login-subtitle">You'll be added with no roles — an admin will grant access.</p>

        {error && (
          <div className="deny-banner deny-banner-error" style={{ marginTop: 16 }}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0 }}><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>
            {error}
          </div>
        )}

        <form className="login-form" onSubmit={handleSubmit}>
          <div>
            <label className="label" htmlFor="reg-email">Email</label>
            <input id="reg-email" className="input" type="email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus placeholder="you@example.com" />
          </div>
          <div>
            <label className="label" htmlFor="reg-username">Username <span style={{ color: 'var(--fg-subtle)', fontWeight: 400 }}>(optional)</span></label>
            <input id="reg-username" className="input" type="text" value={username} onChange={e => setUsername(e.target.value)} placeholder="shortname" />
          </div>
          <div>
            <label className="label" htmlFor="reg-password">Password</label>
            <input id="reg-password" className="input" type="password" value={password} onChange={e => setPassword(e.target.value)} required minLength={8} placeholder="At least 8 characters" />
            {password && (
              <div className="strength-row">
                <div className="progress" style={{ flex: 1 }}>
                  <div className="progress-fill" style={{ width: `${strength}%`, background: strengthColor }} />
                </div>
                <span className="strength-label" style={{ color: strengthColor }}>{strengthLabel}</span>
              </div>
            )}
          </div>
          <div>
            <label className="label" htmlFor="reg-confirm">Confirm password</label>
            <input id="reg-confirm" className="input" type="password" value={confirm} onChange={e => setConfirm(e.target.value)} required placeholder="••••••••" />
          </div>
          <button type="submit" className="btn btn-primary btn-lg" disabled={loading} style={{ marginTop: 4 }}>
            {loading ? 'Creating account…' : <>Continue <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="5" y1="12" x2="19" y2="12"/><polyline points="12,5 19,12 12,19"/></svg></>}
          </button>
        </form>

        <div style={{ marginTop: 20, textAlign: 'center', fontSize: 12.5, color: 'var(--fg-muted)' }}>
          Already have an account?{' '}
          <a href={`/login?login_challenge=${challenge}`} style={{ color: 'var(--accent)', fontWeight: 500 }}>Sign in</a>
        </div>
      </div>
    </div>
  );
}
