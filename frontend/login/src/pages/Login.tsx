import { useEffect, useState } from 'react';
import { useSearchParams, useNavigate } from 'react-router';
import { getLoginChallenge, submitLogin } from '../api';
import { setAllowedRedirectOrigins } from '../safeNavigate';
import { useTheme, type Theme as ColorTheme } from '../useTheme';
import { safeNavigate } from '../safeNavigate';
import { sanitizeCss, safeCssValue } from '../lib/sanitizeCss';

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
  /** Origins this project registered — where the flow is allowed to end. See safeNavigate.ts. */
  allowed_redirect_origins?: string[];
}

/**
 * Every icon is inlined as a data: URI on purpose. A remote icon is a third-party request from
 * the unauthenticated login page — it tells that third party who is signing in and where — and
 * gstatic.com (or equivalent) would have to be allowed in img-src for it. Add new providers the
 * same way; do not reintroduce a remote <img> src.
 */
const PROVIDER_ICONS: Record<string, string> = {
  google: 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48"%3E%3Cpath fill="%234285F4" d="M45.12 24.5c0-1.56-.14-3.06-.4-4.5H24v8.51h11.84c-.51 2.75-2.06 5.08-4.39 6.64v5.52h7.11c4.16-3.83 6.56-9.47 6.56-16.17z"/%3E%3Cpath fill="%2334A853" d="M24 46c5.94 0 10.92-1.97 14.56-5.33l-7.11-5.52c-1.97 1.32-4.49 2.1-7.45 2.1-5.73 0-10.58-3.87-12.31-9.07H4.34v5.7C7.96 41.07 15.4 46 24 46z"/%3E%3Cpath fill="%23FBBC05" d="M11.69 28.18C11.25 26.86 11 25.45 11 24s.25-2.86.69-4.18v-5.7H4.34C2.85 17.09 2 20.45 2 24s.85 6.91 2.34 9.88l7.35-5.7z"/%3E%3Cpath fill="%23EA4335" d="M24 10.75c3.23 0 6.13 1.11 8.41 3.29l6.31-6.31C34.91 4.18 29.93 2 24 2 15.4 2 7.96 6.93 4.34 14.12l7.35 5.7c1.73-5.2 6.58-9.07 12.31-9.07z"/%3E%3C/svg%3E',
  github: 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0 0 24 12c0-6.63-5.37-12-12-12z"/%3E%3C/svg%3E',
  gitlab: 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath fill="%23FC6D26" d="m23.955 13.587-1.342-4.135-2.664-8.189a.455.455 0 0 0-.867 0L16.418 9.45H7.582L4.918 1.263a.455.455 0 0 0-.867 0L1.386 9.45.044 13.587a.924.924 0 0 0 .331 1.023L12 23.054l11.625-8.443a.92.92 0 0 0 .33-1.024"/%3E%3C/svg%3E',
};

function LoginLogo() {
  return (
    <div className="login-logo">
      <div className="brand-mark">R</div>
      <span>RediensIAM</span>
    </div>
  );
}

function TokenVisual() {
  return (
    <div className="token-visual">
      <div style={{ fontSize: 10, letterSpacing: '0.1em', color: 'var(--fg-subtle)', textTransform: 'uppercase', marginBottom: 10, display: 'flex', alignItems: 'center', gap: 6 }}>
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
          <path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4"/>
        </svg>
        Access Token · decoded
      </div>
      <div>{'{'}</div>
      <div style={{ paddingLeft: 14 }}>
        <span className="tok-key">"sub"</span>: <span className="tok-str">"org_acme-corp:usr_7f3c"</span>,<br/>
        <span className="tok-key">"project_id"</span>: <span className="tok-str">"proj_01"</span>, <span className="tok-comment">{'// scope = project'}</span><br/>
        <span className="tok-key">"project_slug"</span>: <span className="tok-str">"customer-portal"</span>,<br/>
        <span className="tok-key">"org_id"</span>: <span className="tok-str">"org_acme-corp"</span>,<br/>
        <span className="tok-key">"user_list_id"</span>: <span className="tok-str">"ul_02"</span>,<br/>
        <span className="tok-key">"roles"</span>: [<span className="tok-str">"editor"</span>, <span className="tok-str">"billing"</span>],<br/>
        <span className="tok-key">"exp"</span>: <span className="tok-val">1743595338</span><br/>
      </div>
      <div>{'}'}</div>
      <div style={{ marginTop: 14, paddingTop: 14, borderTop: '1px solid var(--border)', fontSize: 11, color: 'var(--fg-subtle)', fontFamily: 'var(--font-sans)' }}>
        This token carries <strong style={{ color: 'var(--fg-muted)' }}>only</strong> Project context. Cross-project visibility is super-admin only.
      </div>
    </div>
  );
}

export default function Login() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const challenge = params.get('login_challenge') ?? '';
  const [loginTheme, setLoginTheme] = useState<Theme | null>(null);
  const [identifier, setIdentifier] = useState('');
  const [password, setPassword] = useState('');
  const [showPw, setShowPw] = useState(false);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const { theme: colorTheme, setTheme: setColorTheme } = useTheme();
  const nextTheme = () => setColorTheme(themeOrder[(themeOrder.indexOf(colorTheme) + 1) % themeOrder.length]);

  useEffect(() => {
    if (!challenge) return;
    // Recorded before any redirect can happen: this page is where the server states which origins
    // the challenge's project registered, and every later page — MFA, enrolment — navigates on the
    // strength of it.
    getLoginChallenge(challenge)
      .then((t: Theme) => { setAllowedRedirectOrigins(t.allowed_redirect_origins); setLoginTheme(t); })
      .catch(() => setError('Invalid login link'));
  }, [challenge]);

  useEffect(() => {
    const t = loginTheme?.theme ?? {};
    const el = document.documentElement;
    const touchedProps: string[] = [];
    const set = (v: string, val?: string) => {
      const safe = safeCssValue(val);
      if (safe) { el.style.setProperty(v, safe); touchedProps.push(v); }
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
      touchedProps.push('--radius', '--radius-sm');
    }
    let styleNode: HTMLStyleElement | null = null;
    if (t.custom_css) {
      styleNode = document.createElement('style');
      styleNode.dataset['iamTheme'] = 'login';
      styleNode.textContent = sanitizeCss(t.custom_css);
      document.head.appendChild(styleNode);
    }
    return () => {
      // Cleanup so theme doesn't leak across navigations or accumulate <style> nodes on re-render.
      for (const p of touchedProps) el.style.removeProperty(p);
      styleNode?.remove();
    };
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
      if (res.redirect_to && !safeNavigate(res.redirect_to)) {
        setError('Sign-in could not complete. Please try again.');
      }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  const providers = (loginTheme?.theme?.providers ?? []).filter(p => p.enabled);
  const forgotUrl = `/password-reset?project_id=${loginTheme?.project_id ?? ''}`;
  const registerUrl = `/register?login_challenge=${challenge}`;
  const projectInitial = loginTheme?.project_name?.[0]?.toUpperCase() ?? 'P';

  return (
    <>
      <button onClick={nextTheme} className="theme-toggle" title={`Theme: ${colorTheme} (click to change)`}>
        {themeIcons[colorTheme]}
      </button>
      <div className="login-root">
        <div className="login-left">
          <div className="login-card fade-in">
            <LoginLogo />

            {loginTheme?.project_name && !loginTheme.is_admin_login && (
              <div className="login-project-chip">
                <div className="logo-dot">{projectInitial}</div>
                <span style={{ color: 'var(--fg-muted)' }}>Sign in to</span>
                <strong style={{ color: 'var(--fg)' }}>{loginTheme.project_name}</strong>
              </div>
            )}

            <h1 className="login-title">
              {loginTheme?.is_admin_login ? 'Admin sign in.' : 'Welcome back.'}
            </h1>
            <p className="login-subtitle">
              {loginTheme?.project_name && !loginTheme.is_admin_login
                ? 'Your session will be scoped to this project.'
                : 'Enter your credentials to continue.'}
            </p>

            {error && (
              <div className="deny-banner deny-banner-error" style={{ marginTop: 16 }}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0, marginTop: 1 }}>
                  <circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/>
                </svg>
                {error}
              </div>
            )}

            {providers.length > 0 && (
              <div style={{ marginTop: 24, display: 'flex', flexDirection: 'column', gap: 8 }}>
                {providers.map(p => (
                  <button key={p.type + p.client_id} type="button" className="btn btn-secondary"
                    onClick={() => { globalThis.location.href = `/auth/oauth2/start?login_challenge=${encodeURIComponent(challenge)}&provider_id=${encodeURIComponent(p.id)}`; }}
                    style={{ justifyContent: 'center' }}>
                    {(p.logo_url || PROVIDER_ICONS[p.type]) && (
                      <img src={p.logo_url || PROVIDER_ICONS[p.type]} alt={p.type} style={{ height: 16, width: 16 }} />
                    )}
                    Continue with {p.label}
                  </button>
                ))}
              </div>
            )}

            {providers.length > 0 && <div className="login-divider">or continue with email</div>}

            <form className="login-form" style={{ marginTop: providers.length > 0 ? 0 : undefined }} onSubmit={handleSubmit}>
              <div>
                <label className="label" htmlFor="login-identifier">{loginTheme?.is_admin_login ? 'Email' : 'Email or username'}</label>
                <input
                  id="login-identifier"
                  className="input"
                  type={loginTheme?.is_admin_login ? 'email' : 'text'}
                  value={identifier}
                  onChange={e => setIdentifier(e.target.value)}
                  required autoFocus autoComplete="username"
                  placeholder="you@example.com"
                />
              </div>
              <div>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 6 }}>
                  <label className="label" htmlFor="login-password" style={{ marginBottom: 0 }}>Password</label>
                  {!loginTheme?.is_admin_login && (loginTheme?.email_verification_enabled || loginTheme?.sms_verification_enabled) && (
                    <a href={forgotUrl} className="btn btn-ghost btn-sm">Forgot?</a>
                  )}
                </div>
                <div style={{ position: 'relative' }}>
                  <input
                    id="login-password"
                    className="input"
                    type={showPw ? 'text' : 'password'}
                    value={password}
                    onChange={e => setPassword(e.target.value)}
                    required autoComplete="current-password" placeholder="••••••••"
                    style={{ paddingRight: 38 }}
                  />
                  <button type="button" onClick={() => setShowPw(s => !s)}
                    style={{ position: 'absolute', right: 6, top: '50%', transform: 'translateY(-50%)', color: 'var(--fg-subtle)', padding: 4, lineHeight: 0 }}
                    aria-label="Toggle password visibility">
                    {showPw
                      ? <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"/><line x1="1" y1="1" x2="23" y2="23"/></svg>
                      : <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>
                    }
                  </button>
                </div>
              </div>
              <button type="submit" className="btn btn-primary btn-lg" disabled={loading} style={{ marginTop: 4 }}>
                {loading
                  ? 'Signing in…'
                  : <>Continue <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="5" y1="12" x2="19" y2="12"/><polyline points="12,5 19,12 12,19"/></svg></>
                }
              </button>
            </form>

            {!loginTheme?.is_admin_login && loginTheme?.allow_self_registration && (
              <div style={{ marginTop: 24, textAlign: 'center', fontSize: 12.5, color: 'var(--fg-muted)' }}>
                New to {loginTheme.project_name ?? 'this app'}?{' '}
                <a href={registerUrl} style={{ color: 'var(--accent)', fontWeight: 500 }}>Create account</a>
              </div>
            )}
          </div>
        </div>

        <div className="login-right">
          <div className="grid-bg" />
          <div className="login-right-inner">
            <TokenVisual />
          </div>
        </div>
      </div>
    </>
  );
}
