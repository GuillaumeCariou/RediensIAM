import { useEffect } from 'react';
import { useSearchParams } from 'react-router-dom';

const PROVIDER_ICONS: Record<string, string> = {
  google:   'https://www.gstatic.com/firebasejs/ui/2.0.0/images/auth/google.svg',
  github:   'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0 0 24 12c0-6.63-5.37-12-12-12z"/%3E%3C/svg%3E',
  gitlab:   'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath fill="%23FC6D26" d="m23.955 13.587-1.342-4.135-2.664-8.189a.455.455 0 0 0-.867 0L16.418 9.45H7.582L4.918 1.263a.455.455 0 0 0-.867 0L1.386 9.45.044 13.587a.924.924 0 0 0 .331 1.023L12 23.054l11.625-8.443a.92.92 0 0 0 .33-1.024"/%3E%3C/svg%3E',
  facebook: 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath fill="%231877F2" d="M24 12.073C24 5.405 18.627 0 12 0S0 5.405 0 12.073C0 18.1 4.388 23.094 10.125 24v-8.437H7.078v-3.49h3.047V9.43c0-3.007 1.792-4.669 4.533-4.669 1.312 0 2.686.235 2.686.235v2.953H15.83c-1.491 0-1.956.925-1.956 1.874v2.25h3.328l-.532 3.49h-2.796V24C19.612 23.094 24 18.1 24 12.073z"/%3E%3C/svg%3E',
};

interface PreviewProvider {
  id: string;
  type: string;
  label: string;
  enabled: boolean;
  logo_url?: string;
}

interface PreviewCfg {
  mode?: 'login' | 'register' | 'verify';
  dark?: boolean;
  theme?: {
    primary_color?: string;
    background_color?: string;
    surface_color?: string;
    text_color?: string;
    border_radius?: number;
    font_family?: string;
    logo_url?: string;
    providers?: PreviewProvider[];
    hydra_local_login?: boolean;
  };
  allow_self_registration?: boolean;
  email_verification_enabled?: boolean;
  sms_verification_enabled?: boolean;
  min_password_length?: number;
  password_require_uppercase?: boolean;
  password_require_lowercase?: boolean;
  password_require_digit?: boolean;
  password_require_special?: boolean;
}

interface ProvidersProps {
  enabledProviders: PreviewProvider[];
  showLocal: boolean;
}

function Providers({ enabledProviders, showLocal }: Readonly<ProvidersProps>) {
  if (!enabledProviders.length) return null;
  return (
    <div style={{ marginBottom: '1rem' }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
        {enabledProviders.map(p => {
          const icon = p.logo_url || PROVIDER_ICONS[p.type];
          return (
            <div key={p.id} style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '0.5rem', width: '100%', padding: '0.625rem', border: '1px solid var(--border)', borderRadius: 'var(--radius)', background: 'var(--surface)', color: 'var(--text)', fontSize: '0.875rem', fontWeight: 500 }}>
              {icon && <img src={icon} alt={p.type} style={{ height: '1rem', width: '1rem' }} />}
              {p.label}
            </div>
          );
        })}
      </div>
      {showLocal && (
        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', margin: '1rem 0' }}>
          <div style={{ flex: 1, height: '1px', background: 'var(--border)' }} />
          <span style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>or</span>
          <div style={{ flex: 1, height: '1px', background: 'var(--border)' }} />
        </div>
      )}
    </div>
  );
}

export default function Preview() {
  const [params] = useSearchParams();

  let cfg: PreviewCfg = {};
  try {
    const raw = params.get('cfg');
    if (raw) cfg = JSON.parse(atob(raw));
  } catch { /* invalid cfg — render defaults */ }

  const {
    mode = 'login',
    dark,
    theme = {},
    allow_self_registration,
    email_verification_enabled,
    sms_verification_enabled,
    min_password_length = 0,
    password_require_uppercase,
    password_require_lowercase,
    password_require_digit,
    password_require_special,
  } = cfg;

  useEffect(() => {
    const el = document.documentElement;
    el.dataset['theme'] = dark ? 'dark' : 'light';
    if (theme.primary_color)    el.style.setProperty('--primary', theme.primary_color);
    if (theme.background_color) el.style.setProperty('--background', theme.background_color);
    if (theme.surface_color)    el.style.setProperty('--surface', theme.surface_color);
    if (theme.text_color)       el.style.setProperty('--text', theme.text_color);
    if (theme.font_family)      el.style.setProperty('--font-family', theme.font_family);
    if (theme.border_radius != null) el.style.setProperty('--radius', `${theme.border_radius}px`);
  });

  const showLocal = theme.hydra_local_login ?? true;
  const enabledProviders = (theme.providers ?? []).filter(p => p.enabled);

  const policyRules: string[] = [];
  if (min_password_length > 0)    policyRules.push(`At least ${min_password_length} characters`);
  if (password_require_uppercase)  policyRules.push('One uppercase letter (A–Z)');
  if (password_require_lowercase)  policyRules.push('One lowercase letter (a–z)');
  if (password_require_digit)      policyRules.push('One number (0–9)');
  if (password_require_special)    policyRules.push('One special character (!@#$…)');

  if (mode === 'login') return (
    <div className="card">
      {theme.logo_url && <div className="card-logo"><img src={theme.logo_url} alt="Logo" /></div>}
      <h1 className="card-title">Sign in</h1>
      <p className="card-subtitle">Enter your credentials to continue</p>
      <Providers enabledProviders={enabledProviders} showLocal={showLocal} />
      {showLocal && (
        <form>
          <div className="form-group">
            <label htmlFor="preview-email">Email or username</label>
            <input id="preview-email" type="text" placeholder="you@example.com or username#1234" disabled />
          </div>
          <div className="form-group">
            <label htmlFor="preview-password">Password</label>
            <input id="preview-password" type="password" placeholder="••••••••" disabled />
          </div>
          <button className="btn" disabled>Sign in</button>
        </form>
      )}
      <div className="links">
        {(email_verification_enabled || sms_verification_enabled) && (
          <span className="btn-ghost" style={{ cursor: 'default' }}>Forgot password?</span>
        )}
        {allow_self_registration && (
          <span className="btn-ghost" style={{ cursor: 'default' }}>Create account</span>
        )}
      </div>
    </div>
  );

  if (mode === 'register') return (
    <div className="card">
      {theme.logo_url && <div className="card-logo"><img src={theme.logo_url} alt="Logo" /></div>}
      <h1 className="card-title">Create account</h1>
      <p className="card-subtitle">Fill in your details to get started</p>
      <Providers enabledProviders={enabledProviders} showLocal={showLocal} />
      {showLocal && (
        <form>
          <div className="form-group">
            <label htmlFor="preview-reg-email">Email</label>
            <input id="preview-reg-email" type="email" placeholder="you@example.com" disabled />
          </div>
          <div className="form-group">
            <label htmlFor="preview-reg-username">Username</label>
            <input id="preview-reg-username" type="text" placeholder="alice (optional)" disabled />
          </div>
          <div className="form-group">
            <label htmlFor="preview-reg-password">Password</label>
            <input id="preview-reg-password" type="password" placeholder="••••••••" disabled />
            {policyRules.length > 0 && (
              <ul style={{ marginTop: '0.5rem', paddingLeft: 0, listStyle: 'none', display: 'flex', flexDirection: 'column', gap: '0.3rem' }}>
                {policyRules.map(rule => (
                  <li key={rule} style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', fontSize: '0.75rem', color: 'var(--text-muted)' }}>
                    <span style={{ width: '14px', height: '14px', borderRadius: '50%', border: '1.5px solid var(--border)', display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }} />
                    {rule}
                  </li>
                ))}
              </ul>
            )}
          </div>
          <div className="form-group">
            <label htmlFor="preview-reg-confirm">Confirm password</label>
            <input id="preview-reg-confirm" type="password" placeholder="••••••••" disabled />
          </div>
          <button className="btn" disabled>Create account</button>
        </form>
      )}
      <div className="links">
        <span className="btn-ghost" style={{ cursor: 'default' }}>Already have an account? Sign in</span>
      </div>
    </div>
  );

  // verify
  let channel = 'contact';
  if (email_verification_enabled) channel = 'email';
  else if (sms_verification_enabled) channel = 'phone';

  return (
    <div className="card">
      {theme.logo_url && <div className="card-logo"><img src={theme.logo_url} alt="Logo" /></div>}
      <h1 className="card-title">Check your {channel}</h1>
      <p className="card-subtitle">Enter the 6-digit code we sent to you</p>
      <form>
        <div className="form-group">
          <label htmlFor="preview-otp">Verification code</label>
          <input id="preview-otp" type="text" className="otp-input" placeholder="123456" disabled />
        </div>
        <button className="btn" disabled>Verify</button>
      </form>
    </div>
  );
}
