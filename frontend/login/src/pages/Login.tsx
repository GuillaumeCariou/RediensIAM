import { useEffect, useState } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { getLoginChallenge, submitLogin } from '../api';
import { useTheme, type Theme as ColorTheme } from '../useTheme';

const themeIcons: Record<ColorTheme, string> = { light: '☀', dark: '☾', system: '⊙' };
const themeOrder: ColorTheme[] = ['system', 'light', 'dark'];

interface Provider {
  id: string;
  type: 'google' | 'github' | 'gitlab' | 'facebook' | 'oidc';
  label: string;
  client_id: string;
  issuer_url?: string;
  logo_url?: string;
  enabled: boolean;
}

interface LoginThemeConfig {
  primary_color?: string;
  background_color?: string;
  surface_color?: string;
  text_color?: string;
  border_radius?: string;
  font_family?: string;
  logo_url?: string;
  custom_css?: string;
  providers?: Provider[];
}

interface Theme {
  project_id?: string;
  theme?: LoginThemeConfig;
  project_name?: string;
  has_custom_template?: boolean;
  require_role?: boolean;
  allow_self_registration?: boolean;
  email_verification_enabled?: boolean;
  sms_verification_enabled?: boolean;
  is_admin_login?: boolean;
}

const PROVIDER_ICONS: Record<string, string> = {
  google: 'https://www.gstatic.com/firebasejs/ui/2.0.0/images/auth/google.svg',
  github: 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0 0 24 12c0-6.63-5.37-12-12-12z"/%3E%3C/svg%3E',
  gitlab: 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath fill="%23FC6D26" d="m23.955 13.587-1.342-4.135-2.664-8.189a.455.455 0 0 0-.867 0L16.418 9.45H7.582L4.918 1.263a.455.455 0 0 0-.867 0L1.386 9.45.044 13.587a.924.924 0 0 0 .331 1.023L12 23.054l11.625-8.443a.92.92 0 0 0 .33-1.024"/%3E%3C/svg%3E',
};

export default function Login() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const challenge = params.get('login_challenge') ?? '';
  const [loginTheme, setLoginTheme] = useState<Theme | null>(null);
  const [identifier, setIdentifier] = useState('');
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
    const t = loginTheme?.theme ?? {};
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
  }, [loginTheme]);

  async function handleSubmit(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      const isEmail = loginTheme?.is_admin_login || identifier.includes('@');
      const res = await submitLogin({
        login_challenge: challenge,
        ...(isEmail ? { email: identifier } : { username: identifier }),
        password,
      });
      if (res.error) {
        if (res.error === 'no_role') { setError('You do not have permission to access this application.'); return; }
        if (res.error === 'account_locked') { setError(`Account locked until ${new Date(res.locked_until).toLocaleTimeString()}`); return; }
        setError('Invalid email or password.');
        return;
      }
      if (res.requires_mfa) {
        sessionStorage.setItem('mfa_type', res.mfa_type ?? 'totp');
        if (res.phone_hint) sessionStorage.setItem('mfa_phone_hint', res.phone_hint);
        navigate(`/mfa?login_challenge=${challenge}`);
        return;
      }
      if (res.requires_mfa_setup) {
        sessionStorage.setItem('mfa_setup_challenge', challenge);
        if (res.user_id) sessionStorage.setItem('mfa_setup_user', res.user_id);
        navigate('/mfa-setup');
        return;
      }
      if (res.redirect_to) { globalThis.location.href = res.redirect_to; }
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
      {loginTheme?.theme?.logo_url && (
        <div className="card-logo"><img src={loginTheme.theme?.logo_url} alt="Logo" /></div>
      )}
      <h1 className="card-title">{loginTheme?.project_name ?? 'Sign in'}</h1>
      <p className="card-subtitle">Enter your credentials to continue</p>

      {error && <div className="alert alert-error">{error}</div>}

      {(() => {
        const providers = (loginTheme?.theme?.providers ?? []).filter(p => p.enabled);
        if (!providers.length) return null;
        return (
          <div style={{ marginBottom: '1rem' }}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
              {providers.map(p => (
                <button key={p.type + p.client_id} type="button"
                  onClick={() => {
                    globalThis.location.href = `/auth/oauth2/start?login_challenge=${encodeURIComponent(challenge)}&provider_id=${encodeURIComponent(p.id)}`;
                  }}
                  style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '0.5rem', width: '100%', padding: '0.625rem', border: '1px solid var(--border)', borderRadius: 'var(--radius)', background: 'var(--surface)', color: 'var(--text)', fontSize: '0.875rem', fontWeight: 500, cursor: 'pointer' }}>
                  {(p.logo_url || PROVIDER_ICONS[p.type]) && <img src={p.logo_url || PROVIDER_ICONS[p.type]} alt={p.type} style={{ height: '1rem', width: '1rem' }} />}
                  {p.label}
                </button>
              ))}
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', margin: '1rem 0' }}>
              <div style={{ flex: 1, height: '1px', background: 'var(--border)' }} />
              <span style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>or</span>
              <div style={{ flex: 1, height: '1px', background: 'var(--border)' }} />
            </div>
          </div>
        );
      })()}

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <label htmlFor="identifier">{loginTheme?.is_admin_login ? 'Email' : 'Email or username'}</label>
          <input
            id="identifier"
            type={loginTheme?.is_admin_login ? 'email' : 'text'}
            value={identifier}
            onChange={e => setIdentifier(e.target.value)}
            required
            autoFocus
            autoComplete="username"
            placeholder={loginTheme?.is_admin_login ? 'you@example.com' : 'you@example.com or username#1234'}
          />
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
