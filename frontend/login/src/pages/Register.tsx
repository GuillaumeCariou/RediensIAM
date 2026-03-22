import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';

export default function Register() {
  const [params] = useSearchParams();
  const challenge = params.get('login_challenge') ?? '';
  const [email, setEmail] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setLoading(true);
    setError('');
    try {
      const r = await fetch('/auth/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ login_challenge: challenge, email, username, password }),
        credentials: 'include',
      });
      const res = await r.json();
      if (res.error) { setError(res.error_description ?? 'Registration failed.'); return; }
      if (res.redirect_to) window.location.href = res.redirect_to;
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

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
          <input id="username" type="text" value={username} onChange={e => setUsername(e.target.value)} required placeholder="alice" />
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
