import { useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { requestPasswordReset, verifyPasswordResetOtp, confirmPasswordReset } from '../api';

type Step = 'email' | 'otp' | 'password' | 'done';

function LoginLogo() {
  return (
    <div className="login-logo">
      <div className="brand-mark">R</div>
      <span>RediensIAM</span>
    </div>
  );
}

function parseResetError(res: { error?: string; count?: number }): string | null {
  if (!res.error) return null;
  if (res.error === 'password_breached') {
    const count = res.count ? res.count.toLocaleString() : 'multiple';
    return `This password has appeared in ${count} data breaches. Choose a different password.`;
  }
  return 'Reset link expired. Please start over.';
}

function ErrorBanner({ message }: Readonly<{ message: string }>) {
  if (!message) return null;
  return (
    <div className="deny-banner deny-banner-error" style={{ marginTop: 16 }}>
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0 }}><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>
      {message}
    </div>
  );
}

export default function PasswordReset() {
  const [params] = useSearchParams();
  const projectId = params.get('project_id') ?? '';

  const [step, setStep]           = useState<Step>('email');
  const [sessionId, setSessionId] = useState('');
  const [resetToken, setResetToken] = useState('');

  const [email, setEmail]       = useState('');
  const [cells, setCells]       = useState<string[]>(new Array(6).fill(''));
  const [password, setPassword] = useState('');
  const [confirm, setConfirm]   = useState('');
  const cellRefs = useRef<(HTMLInputElement | null)[]>([]);

  const [error, setError]     = useState('');
  const [loading, setLoading] = useState(false);

  const code = cells.join('');

  const subtitleMap: Record<Step, string> = {
    email: 'Enter your email to receive a reset code.',
    otp: 'Enter the 6-digit code from your email.',
    password: 'Create your new password.',
    done: 'Your password has been reset.',
  };

  async function handleEmail(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    setLoading(true); setError('');
    try {
      const res = await requestPasswordReset(projectId, email);
      if (res.error) { setError('Password reset is not available for this project.'); return; }
      if (res.session_id) { setSessionId(res.session_id); setStep('otp'); }
      else setError('No account found or verification not configured.');
    } catch { setError('Something went wrong. Please try again.'); }
    finally { setLoading(false); }
  }

  async function handleOtp(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    setLoading(true); setError('');
    try {
      const res = await verifyPasswordResetOtp(sessionId, code);
      if (res.error) { setError('Invalid or expired code.'); setCells(new Array(6).fill('')); cellRefs.current[0]?.focus(); return; }
      setResetToken(res.reset_token);
      setStep('password');
    } catch { setError('Something went wrong. Please try again.'); }
    finally { setLoading(false); }
  }

  async function handlePassword(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setLoading(true); setError('');
    try {
      const res = await confirmPasswordReset(resetToken, password);
      const errMsg = parseResetError(res);
      if (errMsg) { setError(errMsg); return; }
      setStep('done');
    } catch { setError('Something went wrong. Please try again.'); }
    finally { setLoading(false); }
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
    cellRefs.current[Math.min(digits.length, 5)]?.focus();
  }


  return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <h1 className="login-title">Reset your password.</h1>
        <p className="login-subtitle">{subtitleMap[step]}</p>

        {step === 'done' ? (
          <>
            <div className="deny-banner" style={{ background: 'var(--success-soft)', color: 'var(--success)', borderColor: 'oklch(from var(--success) l c h / 0.4)', marginTop: 20 }}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" style={{ flexShrink: 0 }}><polyline points="20,6 9,17 4,12"/></svg>
              Password updated successfully. You can now sign in.
            </div>
            <a href="/login" className="btn btn-primary btn-lg" style={{ marginTop: 16, textDecoration: 'none' }}>Back to sign in</a>
          </>
        ) : step === 'email' ? (
          <>
            <ErrorBanner message={error} />
            <form className="login-form" onSubmit={handleEmail}>
              <div>
                <label className="label" htmlFor="pr-email">Email</label>
                <input id="pr-email" className="input" type="email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus placeholder="you@example.com" />
              </div>
              <button type="submit" className="btn btn-primary btn-lg" disabled={loading}>
                {loading ? 'Sending…' : <>Send code <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2z"/><polyline points="22,6 12,13 2,6"/></svg></>}
              </button>
            </form>
            <div style={{ marginTop: 16, textAlign: 'center' }}>
              <a href="/login" className="btn btn-ghost btn-sm">
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="15,18 9,12 15,6"/></svg>
                Back to sign in
              </a>
            </div>
          </>
        ) : step === 'otp' ? (
          <>
            <ErrorBanner message={error} />
            <form onSubmit={handleOtp}>
              <div className="label" style={{ textAlign: 'center', marginTop: 20 }}>Enter the code we sent to <strong>{email}</strong></div>
              <div className="otp-grid">
                {cells.map((c, i) => (
                  <input key={i} ref={el => { cellRefs.current[i] = el; }}
                    className="otp-cell" type="text" inputMode="numeric"
                    maxLength={1} value={c} autoFocus={i === 0}
                    aria-label={`Digit ${i + 1} of 6`}
                    onChange={e => handleCellChange(i, e.target.value)}
                    onKeyDown={e => handleCellKeyDown(e, i)}
                    onPaste={handleCellPaste}
                  />
                ))}
              </div>
              <button className="btn btn-primary btn-lg" type="submit" disabled={loading || code.length !== 6} style={{ marginTop: 16 }}>
                {loading ? 'Verifying…' : 'Verify'}
              </button>
            </form>
            <button className="btn btn-ghost btn-sm" style={{ marginTop: 10 }} onClick={() => setStep('email')}>
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="15,18 9,12 15,6"/></svg>
              Back
            </button>
          </>
        ) : (
          <>
            <ErrorBanner message={error} />
            <form className="login-form" onSubmit={handlePassword}>
              <div>
                <label className="label" htmlFor="pr-new-password">New password</label>
                <input id="pr-new-password" className="input" type="password" value={password} onChange={e => setPassword(e.target.value)} required minLength={8} autoFocus placeholder="••••••••" />
              </div>
              <div>
                <label className="label" htmlFor="pr-confirm-password">Confirm new password</label>
                <input id="pr-confirm-password" className="input" type="password" value={confirm} onChange={e => setConfirm(e.target.value)} required placeholder="••••••••" />
              </div>
              <button type="submit" className="btn btn-primary btn-lg" disabled={loading}>
                {loading ? 'Saving…' : 'Set password'}
              </button>
            </form>
          </>
        )}
      </div>
    </div>
  );
}
