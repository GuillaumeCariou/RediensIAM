import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { getThemeByProject, completeInvite } from '../api';

function sanitizeCss(css: string): string {
  let out = css;
  out = out.replaceAll(/@(import|charset|namespace)[^;]*;?/gi, '');
  out = out.replaceAll(/url\([^)]*\)/gi, 'url(about:blank)');
  out = out.replaceAll(/input\s*\[\s*type\s*[~|^$*]?=\s*['"]?password['"]?\s*\][^{]*\{[^}]*\}/gi, '');
  return out;
}

// Returns a cleanup function that removes the applied CSS vars and <style> node.
function applyTheme(data: Record<string, unknown>): () => void {
  const t = (data?.theme ?? {}) as Record<string, string>;
  const el = document.documentElement;
  const touched: string[] = [];
  const set = (v: string, val?: string) => {
    if (val) { el.style.setProperty(v, val); touched.push(v); }
  };
  set('--primary', t.primary_color);
  set('--accent', t.primary_color);
  set('--background', t.background_color);
  set('--bg', t.background_color);
  set('--surface', t.surface_color);
  set('--text', t.text_color);
  set('--fg', t.text_color);
  set('--font-sans', t.font_family);
  if (t.border_radius) {
    const r = Number.parseInt(t.border_radius);
    el.style.setProperty('--radius', `${r}px`);
    el.style.setProperty('--radius-sm', `${Math.max(4, r - 2)}px`);
    touched.push('--radius', '--radius-sm');
  }
  let styleNode: HTMLStyleElement | null = null;
  if (t.custom_css) {
    styleNode = document.createElement('style');
    styleNode.dataset['iamTheme'] = 'set-password';
    styleNode.textContent = sanitizeCss(t.custom_css);
    document.head.appendChild(styleNode);
  }
  return () => {
    for (const p of touched) el.style.removeProperty(p);
    styleNode?.remove();
  };
}

function LoginLogo() {
  return (
    <div className="login-logo">
      <div className="brand-mark">R</div>
      <span>RediensIAM</span>
    </div>
  );
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

  // Scrub the invite token from the address bar and browser history so it doesn't
  // leak via Referer headers (e.g. to Google Fonts) or stay in shared screenshots.
  useEffect(() => {
    if (!token) return;
    const cleaned = new URL(globalThis.location.href);
    cleaned.searchParams.delete('token');
    globalThis.history.replaceState({}, '', cleaned.toString());
  }, [token]);

  useEffect(() => {
    if (!projectId) return;
    let cleanup: (() => void) | null = null;
    let cancelled = false;
    getThemeByProject(projectId).then(data => {
      if (cancelled) return;
      cleanup = applyTheme(data);
    }).catch(() => {});
    return () => { cancelled = true; cleanup?.(); };
  }, [projectId]);

  if (!token) return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <h1 className="login-title">Invalid link.</h1>
        <p className="login-subtitle">This invite link is invalid or has already been used. Ask your administrator to send a new one.</p>
      </div>
    </div>
  );

  if (done) return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <h1 className="login-title">Password set!</h1>
        <div className="deny-banner" style={{ background: 'var(--success-soft)', color: 'var(--success)', borderColor: 'oklch(from var(--success) l c h / 0.4)', marginTop: 20 }}>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" style={{ flexShrink: 0 }}><polyline points="20,6 9,17 4,12"/></svg>
          Your account is ready. You can now sign in.
        </div>
        <a href="/login" className="btn btn-primary btn-lg" style={{ marginTop: 16, textDecoration: 'none' }}>Go to sign in</a>
      </div>
    </div>
  );

  async function handleSubmit(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setLoading(true);
    setError('');
    try {
      const res = await completeInvite(token, password);
      if (res.error === 'password_breached') {
        setError(`This password has appeared in ${res.count ? res.count.toLocaleString() : 'multiple'} data breaches. Choose a different password.`);
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
      if (res.error) { setError('Something went wrong. Please try again.'); return; }
      setDone(true);
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="login-center">
      <div className="login-card fade-in">
        <LoginLogo />
        <h1 className="login-title">You've been invited.</h1>
        <p className="login-subtitle">Set a password to activate your account.</p>

        {error && (
          <div className="deny-banner deny-banner-error" style={{ marginTop: 16 }}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0 }}><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>
            {error}
          </div>
        )}

        <form className="login-form" onSubmit={handleSubmit}>
          <div>
            <label className="label" htmlFor="sp-new-password">New password</label>
            <input id="sp-new-password" className="input" type="password" value={password} onChange={e => setPassword(e.target.value)}
              required minLength={8} autoFocus autoComplete="new-password" placeholder="At least 8 characters" />
          </div>
          <div>
            <label className="label" htmlFor="sp-confirm-password">Confirm password</label>
            <input id="sp-confirm-password" className="input" type="password" value={confirm} onChange={e => setConfirm(e.target.value)}
              required autoComplete="new-password" placeholder="••••••••" />
          </div>
          <button className="btn btn-primary btn-lg" type="submit" disabled={loading} style={{ marginTop: 4 }}>
            {loading ? 'Setting password…' : <>Accept invite & sign in <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="5" y1="12" x2="19" y2="12"/><polyline points="12,5 19,12 12,19"/></svg></>}
          </button>
        </form>
      </div>
    </div>
  );
}
