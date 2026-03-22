import { useEffect, useState } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { getLoginChallenge, submitLogin } from '../api';
import { useTheme, type Theme as ColorTheme } from '../useTheme';

const themeIcons: Record<ColorTheme, string> = { light: '☀', dark: '☾', system: '⊙' };
const themeOrder: ColorTheme[] = ['system', 'light', 'dark'];

interface Theme {
  project_id?: string;
  LoginTheme?: Record<string, string>;
  project_name?: string;
  has_custom_template?: boolean;
  require_role?: boolean;
  allow_self_registration?: boolean;
  email_verification_enabled?: boolean;
  sms_verification_enabled?: boolean;
  is_admin_login?: boolean;
}

export default function Login() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const challenge = params.get('login_challenge') ?? '';
  const [loginTheme, setLoginTheme] = useState<Theme | null>(null);
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const { theme: colorTheme, setTheme: setColorTheme } = useTheme();
  const nextTheme = () => setColorTheme(themeOrder[(themeOrder.indexOf(colorTheme) + 1) % themeOrder.length]);

  useEffect(() => {
    if (!challenge) return;
    getLoginChallenge(challenge).then(setLoginTheme).catch(() => setError('Invalid login link'));
  }, [challenge]);

  useEffect(() => {
    const t = loginTheme?.LoginTheme ?? {};
    if (t.primary_color) document.documentElement.style.setProperty('--primary', t.primary_color);
    if (t.background_color) document.documentElement.style.setProperty('--background', t.background_color);
    if (t.font_family) document.documentElement.style.setProperty('--font-family', t.font_family);
    if (t.custom_css) {
      const style = document.createElement('style');
      style.textContent = t.custom_css;
      document.head.appendChild(style);
    }
  }, [loginTheme]);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      const res = await submitLogin({ login_challenge: challenge, email, password });
      if (res.error) {
        if (res.error === 'no_role') { setError('You do not have permission to access this application.'); return; }
        if (res.error === 'account_locked') { setError(`Account locked until ${new Date(res.locked_until).toLocaleTimeString()}`); return; }
        setError('Invalid email or password.');
        return;
      }
      if (res.requires_mfa) { navigate(`/mfa?login_challenge=${challenge}`); return; }
      if (res.redirect_to) { window.location.href = res.redirect_to; }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  return (
    <>
    <button
      onClick={nextTheme}
      title={`Theme: ${colorTheme} (click to change)`}
      style={{ position: 'fixed', top: '1rem', right: '1rem', background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: '0.5rem', padding: '0.4rem 0.6rem', cursor: 'pointer', fontSize: '1rem', lineHeight: 1 }}
    >
      {themeIcons[colorTheme]}
    </button>
    <div className="card">
      {loginTheme?.LoginTheme?.logo_url && (
        <div className="card-logo"><img src={loginTheme.LoginTheme.logo_url} alt="Logo" /></div>
      )}
      <h1 className="card-title">{loginTheme?.project_name ?? 'Sign in'}</h1>
      <p className="card-subtitle">Enter your credentials to continue</p>

      {error && <div className="alert alert-error">{error}</div>}

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <label htmlFor="email">Email</label>
          <input id="email" type="email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus placeholder="you@example.com" />
        </div>
        <div className="form-group">
          <label htmlFor="password">Password</label>
          <input id="password" type="password" value={password} onChange={e => setPassword(e.target.value)} required placeholder="••••••••" />
        </div>
        <button className="btn" type="submit" disabled={loading}>
          {loading ? 'Signing in…' : 'Sign in'}
        </button>
      </form>

      <div className="links">
        {!loginTheme?.is_admin_login && (loginTheme?.email_verification_enabled || loginTheme?.sms_verification_enabled) && (
          <a href={`/password-reset?project_id=${loginTheme?.project_id ?? ''}`} className="btn-ghost">Forgot password?</a>
        )}
        {!loginTheme?.is_admin_login && loginTheme?.allow_self_registration && (
          <a href={`/register?login_challenge=${challenge}`} className="btn-ghost">Create account</a>
        )}
      </div>
    </div>
    </>
  );
}
