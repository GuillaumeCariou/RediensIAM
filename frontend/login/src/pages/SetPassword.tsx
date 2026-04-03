import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { getThemeByProject, completeInvite } from '../api';

function applyTheme(data: Record<string, unknown>) {
  const t = (data?.theme ?? {}) as Record<string, string>;
  const set = (v: string, val?: string) => { if (val) document.documentElement.style.setProperty(v, val); };
  set('--primary', t.primary_color);
  set('--background', t.background_color);
  set('--surface', t.surface_color);
  set('--text', t.text_color);
  set('--font-family', t.font_family);
  if (t.border_radius) document.documentElement.style.setProperty('--radius', `${t.border_radius}px`);
  if (t.custom_css) {
    const style = document.createElement('style');
    style.textContent = t.custom_css;
    document.head.appendChild(style);
  }
}

export default function SetPassword() {
  const [params] = useSearchParams();
  const token     = params.get('token')      ?? '';
  const projectId = params.get('project_id') ?? '';

  const [password, setPassword] = useState('');
  const [confirm,  setConfirm]  = useState('');
  const [error,    setError]    = useState('');
  const [loading,  setLoading]  = useState(false);
  const [done,     setDone]     = useState(false);

  useEffect(() => {
    if (!projectId) return;
    getThemeByProject(projectId).then(applyTheme).catch(() => {});
  }, [projectId]);

  if (!token) return (
    <div className="card">
      <h1 className="card-title">Invalid link</h1>
      <p className="card-subtitle">This invite link is invalid or has already been used. Ask your administrator to send a new one.</p>
    </div>
  );

  if (done) return (
    <div className="card">
      <h1 className="card-title">Password set!</h1>
      <div className="alert alert-success">Your account is ready. You can now sign in.</div>
      <div className="links">
        <a href="/login" className="btn-ghost">Go to sign in</a>
      </div>
    </div>
  );

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setLoading(true);
    setError('');
    try {
      const res = await completeInvite(token, password);
      if (res.error === 'password_breached') {
        setError(`This password has appeared in ${res.count ? res.count.toLocaleString() : 'multiple'} data breaches. Please choose a different password.`);
        return;
      }
      if (res.error === 'token_expired' || res.error === 'token_not_found') {
        setError('This invite link has expired. Ask your administrator to resend the invite.');
        return;
      }
      if (res.error === 'password_policy') {
        setError(res.detail ?? 'Password does not meet the requirements. Please try a stronger password.');
        return;
      }
      if (res.error) {
        setError('Something went wrong. Please try again.');
        return;
      }
      setDone(true);
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="card">
      <h1 className="card-title">Set your password</h1>
      <p className="card-subtitle">Create a password to activate your account</p>

      {error && <div className="alert alert-error">{error}</div>}

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <label htmlFor="password">New password</label>
          <input
            id="password"
            type="password"
            value={password}
            onChange={e => setPassword(e.target.value)}
            required
            minLength={8}
            autoFocus
            autoComplete="new-password"
            placeholder="••••••••"
          />
        </div>
        <div className="form-group">
          <label htmlFor="confirm">Confirm password</label>
          <input
            id="confirm"
            type="password"
            value={confirm}
            onChange={e => setConfirm(e.target.value)}
            required
            autoComplete="new-password"
            placeholder="••••••••"
          />
        </div>
        <button className="btn" type="submit" disabled={loading}>
          {loading ? 'Setting password…' : 'Set password'}
        </button>
      </form>
    </div>
  );
}
