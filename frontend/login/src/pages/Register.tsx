import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { registerUser, verifyRegistrationOtp } from '../api';

type Step = 'form' | 'otp' | 'done';

export default function Register() {
  const [params] = useSearchParams();
  const challenge = params.get('login_challenge') ?? '';

  const [step, setStep] = useState<Step>('form');
  const [sessionId, setSessionId] = useState('');

  const [email, setEmail] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [code, setCode] = useState('');

  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setLoading(true);
    setError('');
    try {
      const res = await registerUser({ login_challenge: challenge, email, password, username: username || undefined });
      if (res.error === 'password_breached') {
        setError(`This password has appeared in ${res.count ? res.count.toLocaleString() : 'multiple'} data breaches. Please choose a different password.`);
        return;
      }
      if (res.error) { setError(res.error_description ?? 'Registration failed.'); return; }
      if (res.requires_verification) { setSessionId(res.session_id); setStep('otp'); return; }
      if (res.redirect_to) globalThis.location.href = res.redirect_to;
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
      if (res.error) { setError('Invalid or expired code.'); return; }
      if (res.redirect_to) globalThis.location.href = res.redirect_to;
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  if (step === 'otp') return (
    <div className="card">
      <h1 className="card-title">Check your inbox</h1>
      <p className="card-subtitle">Enter the 6-digit code we sent to <strong>{email}</strong></p>
      {error && <div className="alert alert-error">{error}</div>}
      <form onSubmit={handleVerify}>
        <div className="form-group">
          <label htmlFor="code">Verification code</label>
          <input id="code" type="text" inputMode="numeric" pattern="\d{6}" maxLength={6}
            value={code} onChange={e => setCode(e.target.value)} required autoFocus placeholder="123456" />
        </div>
        <button className="btn" type="submit" disabled={loading}>
          {loading ? 'Verifying…' : 'Verify'}
        </button>
      </form>
    </div>
  );

  return (
    <div className="card">
      <h1 className="card-title">Create account</h1>
      <p className="card-subtitle">Fill in your details to get started</p>
      {error && <div className="alert alert-error">{error}</div>}
      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <label htmlFor="email">Email</label>
          <input id="email" type="email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus placeholder="you@example.com" />
        </div>
        <div className="form-group">
          <label htmlFor="username">Username</label>
          <input id="username" type="text" value={username} onChange={e => setUsername(e.target.value)} placeholder="alice (optional)" />
        </div>
        <div className="form-group">
          <label htmlFor="password">Password</label>
          <input id="password" type="password" value={password} onChange={e => setPassword(e.target.value)} required minLength={8} placeholder="••••••••" />
        </div>
        <div className="form-group">
          <label htmlFor="confirm">Confirm password</label>
          <input id="confirm" type="password" value={confirm} onChange={e => setConfirm(e.target.value)} required placeholder="••••••••" />
        </div>
        <button className="btn" type="submit" disabled={loading}>
          {loading ? 'Creating account…' : 'Create account'}
        </button>
      </form>
      <div className="links">
        <a href={`/login?login_challenge=${challenge}`} className="btn-ghost">Already have an account? Sign in</a>
      </div>
    </div>
  );
}
