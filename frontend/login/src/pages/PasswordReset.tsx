import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { requestPasswordReset, verifyPasswordResetOtp, confirmPasswordReset } from '../api';

type Step = 'email' | 'otp' | 'password' | 'done';

export default function PasswordReset() {
  const [params] = useSearchParams();
  const projectId = params.get('project_id') ?? '';

  const [step, setStep] = useState<Step>('email');
  const [sessionId, setSessionId] = useState('');
  const [resetToken, setResetToken] = useState('');

  const [email, setEmail] = useState('');
  const [code, setCode] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');

  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  async function handleEmail(e: React.FormEvent) {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      const res = await requestPasswordReset(projectId, email);
      if (res.error) { setError('Password reset is not available for this project.'); return; }
      if (res.session_id) { setSessionId(res.session_id); setStep('otp'); }
      else setError('No account found or verification not configured.');
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  async function handleOtp(e: React.FormEvent) {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      const res = await verifyPasswordResetOtp(sessionId, code);
      if (res.error) { setError('Invalid or expired code.'); return; }
      setResetToken(res.reset_token);
      setStep('password');
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  async function handlePassword(e: React.FormEvent) {
    e.preventDefault();
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setLoading(true);
    setError('');
    try {
      const res = await confirmPasswordReset(resetToken, password);
      if (res.error) { setError('Reset link expired. Please start over.'); return; }
      setStep('done');
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  if (step === 'done') return (
    <div className="card">
      <h1 className="card-title">Password updated</h1>
      <div className="alert alert-success">Your password has been reset. You can now sign in.</div>
      <div className="links"><a href="/login" className="btn-ghost">Sign in</a></div>
    </div>
  );

  if (step === 'password') return (
    <div className="card">
      <h1 className="card-title">Set new password</h1>
      {error && <div className="alert alert-error">{error}</div>}
      <form onSubmit={handlePassword}>
        <div className="form-group">
          <label>New password</label>
          <input type="password" value={password} onChange={e => setPassword(e.target.value)} required minLength={8} autoFocus placeholder="••••••••" />
        </div>
        <div className="form-group">
          <label>Confirm password</label>
          <input type="password" value={confirm} onChange={e => setConfirm(e.target.value)} required placeholder="••••••••" />
        </div>
        <button className="btn" type="submit" disabled={loading}>{loading ? 'Saving…' : 'Set password'}</button>
      </form>
    </div>
  );

  if (step === 'otp') return (
    <div className="card">
      <h1 className="card-title">Check your inbox</h1>
      <p className="card-subtitle">Enter the 6-digit code we sent to <strong>{email}</strong></p>
      {error && <div className="alert alert-error">{error}</div>}
      <form onSubmit={handleOtp}>
        <div className="form-group">
          <label>Verification code</label>
          <input type="text" inputMode="numeric" pattern="\d{6}" maxLength={6}
            value={code} onChange={e => setCode(e.target.value)} required autoFocus placeholder="123456" />
        </div>
        <button className="btn" type="submit" disabled={loading}>{loading ? 'Verifying…' : 'Verify'}</button>
      </form>
      <div className="links"><a href="/login" className="btn-ghost">Back to sign in</a></div>
    </div>
  );

  return (
    <div className="card">
      <h1 className="card-title">Forgot password?</h1>
      <p className="card-subtitle">Enter your email and we'll send you a reset code.</p>
      {error && <div className="alert alert-error">{error}</div>}
      <form onSubmit={handleEmail}>
        <div className="form-group">
          <label>Email</label>
          <input type="email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus placeholder="you@example.com" />
        </div>
        <button className="btn" type="submit" disabled={loading}>{loading ? 'Sending…' : 'Send reset code'}</button>
      </form>
      <div className="links"><a href="/login" className="btn-ghost">Back to sign in</a></div>
    </div>
  );
}
