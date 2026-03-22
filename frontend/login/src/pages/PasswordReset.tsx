import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { requestPasswordReset, confirmPasswordReset } from '../api';

export default function PasswordReset() {
  const [params] = useSearchParams();
  const token = params.get('token');
  const projectId = params.get('project_id') ?? '';
  const [value, setValue] = useState('');
  const [confirm, setConfirm] = useState('');
  const [status, setStatus] = useState<'idle' | 'success' | 'error'>('idle');
  const [loading, setLoading] = useState(false);

  async function handleRequest(e: React.FormEvent) {
    e.preventDefault();
    setLoading(true);
    try {
      await requestPasswordReset(projectId, value);
      setStatus('success');
    } catch { setStatus('error'); }
    finally { setLoading(false); }
  }

  async function handleConfirm(e: React.FormEvent) {
    e.preventDefault();
    if (value !== confirm) { alert('Passwords do not match'); return; }
    setLoading(true);
    try {
      await confirmPasswordReset(token!, value);
      setStatus('success');
    } catch { setStatus('error'); }
    finally { setLoading(false); }
  }

  if (status === 'success' && !token) return (
    <div className="card">
      <h1 className="card-title">Check your email</h1>
      <div className="alert alert-success">If an account exists for that email, a reset link has been sent.</div>
      <div className="links"><a href="/login" className="btn-ghost">Back to sign in</a></div>
    </div>
  );

  if (status === 'success' && token) return (
    <div className="card">
      <h1 className="card-title">Password updated</h1>
      <div className="alert alert-success">Your password has been reset. You can now sign in.</div>
      <div className="links"><a href="/login" className="btn-ghost">Sign in</a></div>
    </div>
  );

  if (token) return (
    <div className="card">
      <h1 className="card-title">Set new password</h1>
      {status === 'error' && <div className="alert alert-error">Invalid or expired reset link.</div>}
      <form onSubmit={handleConfirm}>
        <div className="form-group"><label>New password</label>
          <input type="password" value={value} onChange={e => setValue(e.target.value)} required minLength={8} /></div>
        <div className="form-group"><label>Confirm password</label>
          <input type="password" value={confirm} onChange={e => setConfirm(e.target.value)} required /></div>
        <button className="btn" type="submit" disabled={loading}>{loading ? 'Saving…' : 'Set password'}</button>
      </form>
    </div>
  );

  return (
    <div className="card">
      <h1 className="card-title">Forgot password?</h1>
      <p className="card-subtitle">Enter your email and we'll send you a reset link.</p>
      {status === 'error' && <div className="alert alert-error">Something went wrong. Try again.</div>}
      <form onSubmit={handleRequest}>
        <div className="form-group"><label>Email</label>
          <input type="email" value={value} onChange={e => setValue(e.target.value)} required autoFocus /></div>
        <button className="btn" type="submit" disabled={loading}>{loading ? 'Sending…' : 'Send reset link'}</button>
      </form>
      <div className="links"><a href="/login" className="btn-ghost">Back to sign in</a></div>
    </div>
  );
}
