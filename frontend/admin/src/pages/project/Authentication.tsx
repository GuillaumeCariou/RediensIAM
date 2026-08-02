import { useEffect, useMemo, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { IamChip, IamDialog } from '@/components/iam';
import { getProjectInfo, updateProject, listRoles, listSamlProviders, createSamlProvider, deleteSamlProvider } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

// ── Types ─────────────────────────────────────────────────────────────────────

interface Provider {
  id: string;
  type: 'google' | 'github' | 'gitlab' | 'facebook' | 'oidc';
  label: string;
  client_id: string;
  client_secret?: string;
  client_secret_saved?: boolean;
  issuer_url?: string;
  logo_url?: string;
  enabled: boolean;
}

interface SamlProvider {
  id: string; entity_id: string; metadata_url?: string;
  email_attribute_name: string; display_name_attribute_name?: string;
  jit_provisioning: boolean; active: boolean;
}

interface Theme {
  primary_color?: string; background_color?: string; surface_color?: string;
  text_color?: string; border_radius?: string; font_family?: string;
  logo_url?: string; custom_css?: string; providers?: Provider[];
  hydra_local_login?: boolean;
}

interface Role { id: string; name: string; rank: number; }

// ── Constants ─────────────────────────────────────────────────────────────────

const FONT_OPTIONS = ['Inter', 'Roboto', 'Open Sans', 'Montserrat', 'DM Sans', 'System UI', 'Custom'];

const BUILTIN_PROVIDERS: { type: Provider['type']; label: string; defaultLabel: string }[] = [
  { type: 'google',   label: 'Google',   defaultLabel: 'Continue with Google' },
  { type: 'github',   label: 'GitHub',   defaultLabel: 'Continue with GitHub' },
  { type: 'gitlab',   label: 'GitLab',   defaultLabel: 'Continue with GitLab' },
  { type: 'facebook', label: 'Facebook', defaultLabel: 'Continue with Facebook' },
];

const PROVIDER_ICONS: Record<string, string> = {
  google:   'https://www.gstatic.com/firebasejs/ui/2.0.0/images/auth/google.svg',
  github:   'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0 0 24 12c0-6.63-5.37-12-12-12z"/%3E%3C/svg%3E',
  gitlab:   'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath fill="%23FC6D26" d="m23.955 13.587-1.342-4.135-2.664-8.189a.455.455 0 0 0-.867 0L16.418 9.45H7.582L4.918 1.263a.455.455 0 0 0-.867 0L1.386 9.45.044 13.587a.924.924 0 0 0 .331 1.023L12 23.054l11.625-8.443a.92.92 0 0 0 .33-1.024"/%3E%3C/svg%3E',
  facebook: 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"%3E%3Cpath fill="%231877F2" d="M24 12.073C24 5.405 18.627 0 12 0S0 5.405 0 12.073C0 18.1 4.388 23.094 10.125 24v-8.437H7.078v-3.49h3.047V9.43c0-3.007 1.792-4.669 4.533-4.669 1.312 0 2.686.235 2.686.235v2.953H15.83c-1.491 0-1.956.925-1.956 1.874v2.25h3.328l-.532 3.49h-2.796V24C19.612 23.094 24 18.1 24 12.073z"/%3E%3C/svg%3E',
};

const DEFAULT_THEME: Theme = {
  primary_color: '#1a56db', background_color: '#f9fafb', surface_color: '#ffffff',
  text_color: '#111827', border_radius: '8', font_family: 'Inter',
  logo_url: '', custom_css: '', providers: [], hydra_local_login: true,
};

// ── Sub-components ────────────────────────────────────────────────────────────

function nanoid() { return crypto.randomUUID().replaceAll('-', '').slice(0, 8); }

function Toggle({ checked, onChange, label }: Readonly<{ checked: boolean; onChange: (v: boolean) => void; label?: string }>) {
  // A styled checkbox rather than a bare <button>: it carries the role, the checked state and the
  // accessible name for free. The button version announced itself as "button", fifteen times on
  // this page, with no name and no state — "Require MFA" was indistinguishable from "Reject
  // breached passwords".
  return (
    <input
      type="checkbox"
      className="iam-switch"
      checked={checked}
      aria-label={label}
      onChange={e => onChange(e.target.checked)}
    />
  );
}

function ColorRow({ label, value, onChange }: Readonly<{ label: string; value: string; onChange: (v: string) => void }>) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <label className="iam-label">{label}</label>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        <input type="color" value={value || '#000000'} onChange={e => onChange(e.target.value)}
          style={{ height: 36, width: 44, borderRadius: 6, cursor: 'pointer', border: '1px solid var(--border)', padding: 2, background: 'transparent', flexShrink: 0 }} />
        <input className="iam-input iam-mono" value={value} onChange={e => onChange(e.target.value)} placeholder="#000000" />
      </div>
    </div>
  );
}

const MAX_LOGO_BYTES = 256 * 1024;

function LogoUpload({ value, onChange, label = 'Logo' }: Readonly<{ value?: string; onChange: (v: string) => void; label?: string }>) {
  const [dragOver, setDragOver] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputId = `logo-file-input-${label.replaceAll(/\s+/g, '-')}`;
  /**
   * Raster formats only, as an allowlist. `image/svg+xml` is absent on purpose and must stay
   * absent: an SVG can carry <script>/<onload> and executes when rendered via <img>/<object>/CSS
   * background-image on the downstream login page, which is where this logo ends up.
   */
  const isSafeImageMime = (mime: string) => {
    const allowed = new Set(['image/png', 'image/jpeg', 'image/jpg', 'image/gif', 'image/webp', 'image/avif']);
    return allowed.has(mime.toLowerCase());
  };
  const handle = (file: File) => {
    setError(null);
    if (!isSafeImageMime(file.type)) { setError('Logo must be a raster image (PNG, JPEG, GIF, WebP, AVIF). SVG is not allowed.'); return; }
    if (file.size > MAX_LOGO_BYTES) { setError(`Image must be under ${Math.floor(MAX_LOGO_BYTES / 1024)} KB.`); return; }
    const reader = new FileReader();
    reader.onload = e => onChange(e.target?.result as string);
    reader.readAsDataURL(file);
  };
  /**
   * The `data:` branch repeats both checks the file-picker path applies — MIME allowlist and size
   * cap. Without them the URL field is a straight bypass of the upload limits: paste an
   * `data:image/svg+xml,…` and the SVG reaches the login page, or paste a huge blob and the size
   * cap never runs. Neither check may be dropped from this branch.
   *
   * The size test uses raw URL length as an upper bound on the decoded bytes (base64 expands 4/3),
   * so it over-estimates and never under-estimates.
   *
   * Non-data URLs must be https: an http logo on the login page is mixed content and an
   * on-path rewrite point.
   */
  const handleUrlInput = (v: string) => {
    setError(null);
    if (v === '') { onChange(v); return; }
    if (v.startsWith('data:')) {
      const match = /^data:([^;,]+)[;,]/.exec(v);
      if (!match || !isSafeImageMime(match[1])) {
        setError('data: URL must reference a raster image (PNG, JPEG, GIF, WebP, AVIF).');
        return;
      }
      if (v.length > MAX_LOGO_BYTES * 1.5) {
        setError(`Embedded image must be under ${Math.floor(MAX_LOGO_BYTES / 1024)} KB.`);
        return;
      }
      onChange(v);
      return;
    }
    if (!/^https:\/\//i.test(v)) {
      setError('URL must use https://.');
      return;
    }
    onChange(v);
  };
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <label className="iam-label">{label}</label>
      <button type="button"
        onDragOver={e => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        onDrop={e => { e.preventDefault(); setDragOver(false); const f = e.dataTransfer.files[0]; if (f) handle(f); }}
        onClick={() => document.getElementById(inputId)?.click()}
        style={{
          width: '100%', border: `2px dashed ${dragOver ? 'var(--ia-accent)' : 'var(--border)'}`,
          borderRadius: 8, padding: 16, textAlign: 'center', cursor: 'pointer', transition: 'border-color 150ms',
          background: dragOver ? 'oklch(from var(--ia-accent) l c h / 5%)' : 'transparent',
        }}>
        {value ? (
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 12 }}>
            <img src={value} alt="Logo" style={{ maxHeight: 40, maxWidth: 160, objectFit: 'contain' }} onError={e => (e.currentTarget.style.display = 'none')} />
            <button type="button" className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" onClick={e => { e.stopPropagation(); onChange(''); }}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
            </button>
          </div>
        ) : (
          <div>
            <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ margin: '0 auto 8px', color: 'var(--fg-muted)', display: 'block' }}><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/></svg>
            <p style={{ fontSize: 12, color: 'var(--fg-muted)' }}>Drag & drop or{' '}
              <label style={{ color: 'var(--ia-accent)', cursor: 'pointer', textDecoration: 'underline' }}>
                {'browse'}<input id={inputId} type="file" accept="image/*" style={{ display: 'none' }} onChange={e => { if (e.target.files?.[0]) handle(e.target.files[0]); }} />
              </label>
            </p>
          </div>
        )}
      </button>
      <input className="iam-input" value={value?.startsWith('data:') ? '' : (value ?? '')} onChange={e => handleUrlInput(e.target.value)} placeholder="https://cdn.example.com/logo.png" />
      {error && <div role="alert" style={{ fontSize: 12, color: 'var(--danger)' }}>{error}</div>}
    </div>
  );
}

function CopyButton({ text }: Readonly<{ text: string }>) {
  const [copied, setCopied] = useState(false);
  return (
    <button type="button" className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
      onClick={() => { navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 2000); }}>
      {copied
        ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="var(--success)" strokeWidth="2"><polyline points="20 6 9 17 4 12"/></svg>
        : <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>}
    </button>
  );
}

function SecretInput({ value, saved: secretSaved, onChange }: Readonly<{ value: string; saved?: boolean; onChange: (v: string) => void }>) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <label className="iam-label" htmlFor="secret-input" style={{ fontSize: 11 }}>Client Secret</label>
      <input id="secret-input" className="iam-input" type="password" value={value} onChange={e => onChange(e.target.value)}
        placeholder={secretSaved && !value ? '••••••••• (saved — enter new to replace)' : 'OAuth2 client secret'}
        autoComplete="new-password" />
      {secretSaved && !value && (
        <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Secret is saved. Enter a new one to replace it.</p>
      )}
    </div>
  );
}

type TabId = 'appearance' | 'providers' | 'registration' | 'verification' | 'security' | 'css';
type PreviewMode = 'login' | 'register' | 'verify';

const TABS: { id: TabId; label: string }[] = [
  { id: 'appearance', label: 'Appearance' },
  { id: 'providers', label: 'Providers' },
  { id: 'registration', label: 'Registration' },
  { id: 'verification', label: 'Verification' },
  { id: 'security', label: 'Security' },
  { id: 'css', label: 'Custom CSS' },
];

// ── Main component ────────────────────────────────────────────────────────────

export default function Authentication() {
  const { projectId } = useProjectContext();
  const [tab, setTab] = useState<TabId>('appearance');
  const [theme, setTheme] = useState<Theme>(DEFAULT_THEME);
  const [customFont, setCustomFont] = useState('');
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [previewDark, setPreviewDark] = useState(false);
  const [previewMode, setPreviewMode] = useState<PreviewMode>('login');

  const [allowSelfReg,           setAllowSelfReg]           = useState(false);
  const [requireMfa,             setRequireMfa]             = useState(false);
  const [checkBreachedPasswords, setCheckBreachedPasswords] = useState(false);
  const [emailVerif,             setEmailVerif]             = useState(false);
  const [smsVerif,               setSmsVerif]               = useState(false);
  const [allowedDomains,         setAllowedDomains]         = useState('');
  const [emailFromName,          setEmailFromName]          = useState('');
  const [defaultRoleId,          setDefaultRoleId]          = useState<string | null>(null);
  const [minPasswordLength,      setMinPasswordLength]      = useState(0);
  const [requireUppercase,       setRequireUppercase]       = useState(false);
  const [requireLowercase,       setRequireLowercase]       = useState(false);
  const [requireDigit,           setRequireDigit]           = useState(false);
  const [requireSpecial,         setRequireSpecial]         = useState(false);
  const [roles,                  setRoles]                  = useState<Role[]>([]);

  const [ipAllowlist,      setIpAllowlist]      = useState('');
  const [ipAllowlistError, setIpAllowlistError] = useState('');

  const [customScopes, setCustomScopes] = useState<string[]>([]);
  const [newScope,     setNewScope]     = useState('');
  const [scopeError,   setScopeError]   = useState('');

  const [samlProviders, setSamlProviders] = useState<SamlProvider[]>([]);
  const [addSamlOpen,   setAddSamlOpen]   = useState(false);
  const [samlForm,      setSamlForm]      = useState({
    entity_id: '', metadata_url: '', email_attribute_name: 'email',
    display_name_attribute_name: '', jit_provisioning: true, active: true,
  });
  const [samlSaving, setSamlSaving] = useState(false);
  const [samlError,  setSamlError]  = useState('');

  useEffect(() => {
    if (!projectId) { setLoading(false); return; }
    Promise.all([
      getProjectInfo(projectId).then(p => {
        if (p.login_theme) {
          const t = { ...DEFAULT_THEME, ...p.login_theme };
          if (t.providers) {
            t.providers = t.providers.map((pr: Provider) => ({
              ...pr, id: pr.id ?? nanoid(),
              client_secret_saved: pr.client_secret === null,
              client_secret: pr.client_secret ?? '',
            }));
          }
          setTheme(t);
          if (t.font_family && !FONT_OPTIONS.includes(t.font_family)) setCustomFont(t.font_family);
        }
        setAllowSelfReg(p.allow_self_registration ?? false);
        setRequireMfa(p.require_mfa ?? false);
        setCheckBreachedPasswords(p.check_breached_passwords ?? false);
        setEmailVerif(p.email_verification_enabled ?? false);
        setSmsVerif(p.sms_verification_enabled ?? false);
        setAllowedDomains((p.allowed_email_domains ?? []).join(', '));
        setEmailFromName(p.email_from_name ?? '');
        setDefaultRoleId(p.default_role_id ?? null);
        setMinPasswordLength(p.min_password_length ?? 0);
        setRequireUppercase(p.password_require_uppercase ?? false);
        setRequireLowercase(p.password_require_lowercase ?? false);
        setRequireDigit(p.password_require_digit ?? false);
        setRequireSpecial(p.password_require_special ?? false);
        setIpAllowlist((p.ip_allowlist ?? []).join('\n'));
        setCustomScopes((p.allowed_scopes ?? []).filter((s: string) => !['openid', 'offline'].includes(s)));
      }),
      listRoles(projectId).then(r => setRoles((r.roles ?? r ?? []).sort((a: Role, b: Role) => a.rank - b.rank))),
      listSamlProviders(projectId).then(r => setSamlProviders(r.providers ?? r ?? [])).catch(() => {}),
    ]).catch(err => { console.error(err); setLoadError(true); }).finally(() => setLoading(false));
  }, [projectId]);

  const set = <K extends keyof Theme>(k: K, v: Theme[K]) => setTheme(t => ({ ...t, [k]: v }));

  /**
   * `safeProviders` below strips the server-only `client_secret_saved` flag, and drops
   * `client_secret` entirely when the server already holds one and the admin did not retype it —
   * otherwise saving any unrelated setting would overwrite the stored secret with an empty string.
   * The `no-unused-vars` suppression is there because the discarded bindings exist only to be
   * omitted by rest-destructuring; it is not hiding a real unused variable.
   */
  const handleSave = async () => {
    const ipLines = ipAllowlist.split('\n').map(s => s.trim()).filter(Boolean);
    const badIp = ipLines.find(s => !/^(\d{1,3}\.){3}\d{1,3}(\/\d{1,2})?$|^[0-9a-fA-F:]+\/\d{1,3}$/.test(s));
    if (badIp) { setIpAllowlistError(`Invalid CIDR: ${badIp}`); return; }
    setIpAllowlistError('');
    setSaving(true);
    try {
      const domains = allowedDomains.split(',').map(d => d.trim()).filter(Boolean);
      const safeProviders = (theme.providers ?? []).map(p => {
        // eslint-disable-next-line @typescript-eslint/no-unused-vars
        const { client_secret_saved, ...rest } = p;
        if (p.client_secret_saved && !p.client_secret) {
          const noSecret = { ...rest };
          delete noSecret.client_secret;
          return noSecret;
        }
        return rest;
      });
      const body: Parameters<typeof updateProject>[1] = {
        login_theme: { ...theme, providers: safeProviders } as Record<string, unknown>,
        allow_self_registration: allowSelfReg,
        require_mfa: requireMfa,
        check_breached_passwords: checkBreachedPasswords,
        email_verification_enabled: emailVerif,
        sms_verification_enabled: smsVerif,
        allowed_email_domains: domains,
        ...(emailFromName ? { email_from_name: emailFromName } : { clear_email_from_name: true }),
        min_password_length: minPasswordLength,
        password_require_uppercase: requireUppercase,
        password_require_lowercase: requireLowercase,
        password_require_digit: requireDigit,
        password_require_special: requireSpecial,
        ip_allowlist: ipLines,
        allowed_scopes: ['openid', 'offline', ...customScopes],
      };
      if (defaultRoleId) body.default_role_id = defaultRoleId;
      else body.clear_default_role = true;
      await updateProject(projectId, body);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } finally { setSaving(false); }
  };

  /**
   * Per-provider secrets are stripped before the theme is base64'd into the preview URL. That URL
   * ends up in browser history, web-server logs and Referer headers, so an OAuth `client_secret`
   * must never travel in it. Adding a field back to `cfg` without filtering it here leaks it.
   *
   * The `no-unused-vars` suppression is there because the discarded bindings exist only to be
   * omitted by rest-destructuring; it is not hiding a real unused variable.
   */
  const previewUrl = useMemo(() => {
    const safeProviders = (theme.providers ?? []).map(p => {
      // eslint-disable-next-line @typescript-eslint/no-unused-vars
      const { client_secret, client_secret_saved, ...rest } = p as unknown as Record<string, unknown>;
      return rest;
    });
    const safeTheme = { ...theme, providers: safeProviders };
    const cfg = {
      mode: previewMode, dark: previewDark, theme: safeTheme,
      allow_self_registration: allowSelfReg, email_verification_enabled: emailVerif,
      sms_verification_enabled: smsVerif, min_password_length: minPasswordLength,
      password_require_uppercase: requireUppercase, password_require_lowercase: requireLowercase,
      password_require_digit: requireDigit, password_require_special: requireSpecial,
    };
    return `/preview?cfg=${btoa(JSON.stringify(cfg))}`;
  }, [previewMode, previewDark, theme, allowSelfReg, emailVerif, smsVerif,
      minPasswordLength, requireUppercase, requireLowercase, requireDigit, requireSpecial]);

  const getBuiltin = (type: Provider['type']) => (theme.providers ?? []).find(p => p.type === type && p.id === type);
  const toggleBuiltin = (type: Provider['type'], def: string) => {
    const existing = theme.providers ?? [];
    const idx = existing.findIndex(p => p.id === type);
    if (idx >= 0) {
      set('providers', existing.map((p, i) => i === idx ? { ...p, enabled: !p.enabled } : p));
    } else {
      set('providers', [...existing, { id: type, type, label: def, client_id: '', enabled: true }]);
    }
  };
  const updateBuiltin = (type: Provider['type'], patch: Partial<Provider>) =>
    set('providers', (theme.providers ?? []).map(p => p.id === type ? { ...p, ...patch } : p));

  const customOidcs = (theme.providers ?? []).filter(p => p.type === 'oidc' && p.id !== 'oidc');
  const addOidc = () => {
    const id = nanoid();
    set('providers', [...(theme.providers ?? []), { id, type: 'oidc', label: 'Continue with SSO', client_id: '', issuer_url: '', logo_url: '', enabled: true }]);
  };
  const updateOidc = (id: string, patch: Partial<Provider>) =>
    set('providers', (theme.providers ?? []).map(p => p.id === id ? { ...p, ...patch } : p));
  const removeOidc = (id: string) =>
    set('providers', (theme.providers ?? []).filter(p => p.id !== id));

  const addScope = () => {
    const s = newScope.trim();
    if (!s) return;
    if (!/^[a-z][a-z0-9:_-]*$/.test(s)) { setScopeError('Scope must be lowercase and may contain letters, numbers, colons, hyphens, underscores.'); return; }
    if (['openid', 'offline'].includes(s) || customScopes.includes(s)) { setScopeError('Scope already exists.'); return; }
    setCustomScopes(prev => [...prev, s]);
    setNewScope(''); setScopeError('');
  };
  const removeScope = (s: string) => setCustomScopes(prev => prev.filter(x => x !== s));

  const spMetadataUrl = `${globalThis.location.origin}/admin/projects/${projectId}/saml/metadata`;

  /**
   * `samlForm.active` is deliberately not sent below: the create endpoint does not accept it and
   * always creates providers enabled. Adding it here looks like it works and silently does
   * nothing — disable a provider with the PATCH endpoint afterwards instead.
   */
  const handleAddSaml = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSamlSaving(true); setSamlError('');
    try {
      const res = await createSamlProvider(projectId, {
        entity_id: samlForm.entity_id,
        metadata_url: samlForm.metadata_url || undefined,
        email_attribute_name: samlForm.email_attribute_name || 'email',
        display_name_attribute_name: samlForm.display_name_attribute_name || undefined,
        jit_provisioning: samlForm.jit_provisioning,
      });
      if (res.error) { setSamlError(res.error_description ?? 'Failed to add provider.'); return; }
      setSamlProviders(prev => [...prev, res]);
      setAddSamlOpen(false);
      setSamlForm({ entity_id: '', metadata_url: '', email_attribute_name: 'email', display_name_attribute_name: '', jit_provisioning: true, active: true });
    } catch { setSamlError('Something went wrong.'); }
    finally { setSamlSaving(false); }
  };

  const handleDeleteSaml = async (idpId: string) => {
    await deleteSamlProvider(projectId, idpId);
    setSamlProviders(prev => prev.filter(p => p.id !== idpId));
  };

  // A load that failed leaves every field at its useState default. Rendering the form anyway means
  // the next Save PATCHes those defaults over the tenant's real configuration — MFA off, allowlist
  // empty — and reports success. Refusing to render is the whole fix.
  if (loadError) return (
    <div>
      <PageHeader title="Authentication" />
      <div className="iam-page">
        <div className="iam-empty">
          <p>This configuration could not be loaded, so it is not safe to edit.</p>
          <button type="button" className="iam-btn" onClick={() => window.location.reload()}>Retry</button>
        </div>
      </div>
    </div>
  );

  if (loading) return (
    <div>
      <PageHeader title="Authentication" />
      <div className="iam-page" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {Array.from({ length: 4 }, (_, i) => <div key={i} style={{ height: 48, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8 }} />)}
      </div>
    </div>
  );

  let saveLabel: string;
  if (saving) saveLabel = 'Saving…';
  else if (saved) saveLabel = 'Saved!';
  else saveLabel = 'Save Changes';

  return (
    <div>
      <PageHeader
        title="Authentication"
        description="Configure login appearance, providers, registration, and verification"
        actions={[
          <button key="save" className="iam-btn iam-btn-primary iam-btn-sm" onClick={handleSave} disabled={saving}>
            {saveLabel}
          </button>
        ]}
      />

      <div className="iam-page" style={{ display: 'grid', gridTemplateColumns: '1fr', gap: 24 }}>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 460px', gap: 24, alignItems: 'start' }}>
          <div>
            <div style={{ display: 'flex', borderBottom: '1px solid var(--border)', marginBottom: 20, gap: 0, flexWrap: 'wrap' }}>
              {TABS.map(t => (
                <button key={t.id} onClick={() => setTab(t.id)} style={{
                  padding: '8px 16px', fontSize: 13, fontWeight: 500, border: 'none',
                  background: 'none', cursor: 'pointer', transition: 'color 150ms, border-color 150ms',
                  color: tab === t.id ? 'var(--ia-accent)' : 'var(--fg-muted)',
                  borderBottom: tab === t.id ? '2px solid var(--ia-accent)' : '2px solid transparent',
                  marginBottom: -1,
                }}>
                  {t.label}
                </button>
              ))}
            </div>

            {/* ── Appearance ── */}
            {tab === 'appearance' && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 14 }}>Logo</div>
                  <LogoUpload value={theme.logo_url} onChange={v => set('logo_url', v)} />
                </div>
                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 14 }}>Colors</div>
                  <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 14 }}>
                    <ColorRow label="Primary" value={theme.primary_color ?? '#1a56db'} onChange={v => set('primary_color', v)} />
                    <ColorRow label="Background" value={theme.background_color ?? '#f9fafb'} onChange={v => set('background_color', v)} />
                    <ColorRow label="Card surface" value={theme.surface_color ?? '#ffffff'} onChange={v => set('surface_color', v)} />
                    <ColorRow label="Text" value={theme.text_color ?? '#111827'} onChange={v => set('text_color', v)} />
                  </div>
                </div>
                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 14 }}>Typography & Layout</div>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                    <div>
                      <label className="iam-label" htmlFor="auth-font-family">Font Family</label>
                      <select id="auth-font-family" className="iam-input"
                        value={FONT_OPTIONS.includes(theme.font_family ?? 'Inter') ? (theme.font_family ?? 'Inter') : 'Custom'}
                        onChange={e => { set('font_family', e.target.value === 'Custom' ? customFont : e.target.value); }}>
                        {FONT_OPTIONS.map(f => <option key={f} value={f}>{f}</option>)}
                      </select>
                      {(theme.font_family === 'Custom' || !FONT_OPTIONS.includes(theme.font_family ?? 'Inter')) && (
                        <input className="iam-input" style={{ marginTop: 8 }} value={customFont}
                          onChange={e => { setCustomFont(e.target.value); set('font_family', e.target.value); }}
                          placeholder="e.g. 'Nunito', sans-serif" />
                      )}
                    </div>
                    <div>
                      <label className="iam-label">Border Radius — {theme.border_radius ?? 8}px</label>
                      <input type="range" min={0} max={24} value={theme.border_radius ?? 8}
                        onChange={e => set('border_radius', e.target.value)} style={{ width: '100%', accentColor: 'var(--ia-accent)', marginTop: 6 }} />
                      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, color: 'var(--fg-subtle)', marginTop: 2 }}>
                        <span>Square</span><span>Rounded</span><span>Pill</span>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            )}

            {/* ── Providers ── */}
            {tab === 'providers' && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                <div className="iam-card iam-card-pad">
                  <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                    <div>
                      <p style={{ fontSize: 13, fontWeight: 500 }}>Password login</p>
                      <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Email/username + password form</p>
                    </div>
                    <Toggle checked={theme.hydra_local_login ?? true} onChange={v => set('hydra_local_login', v)} />
                  </div>
                </div>

                {BUILTIN_PROVIDERS.map(({ type, label, defaultLabel }) => {
                  const p = getBuiltin(type);
                  const enabled = p?.enabled ?? false;
                  return (
                    <div key={type} className="iam-card iam-card-pad">
                      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                          {PROVIDER_ICONS[type] && <img src={PROVIDER_ICONS[type]} alt={type} style={{ height: 20, width: 20, objectFit: 'contain' }} />}
                          <div>
                            <p style={{ fontSize: 13, fontWeight: 500 }}>{label}</p>
                            <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{defaultLabel}</p>
                          </div>
                        </div>
                        <Toggle checked={enabled} onChange={() => toggleBuiltin(type, defaultLabel)} />
                      </div>
                      {enabled && (
                        <div style={{ marginTop: 14, paddingTop: 14, borderTop: '1px solid var(--border)', display: 'flex', flexDirection: 'column', gap: 10 }}>
                          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                            <div>
                              <label className="iam-label" htmlFor={`builtin-${type}-label`} style={{ fontSize: 11 }}>Button Label</label>
                              <input id={`builtin-${type}-label`} className="iam-input" value={p?.label ?? defaultLabel} onChange={e => updateBuiltin(type, { label: e.target.value })} />
                            </div>
                            <div>
                              <label className="iam-label" htmlFor={`builtin-${type}-client-id`} style={{ fontSize: 11 }}>Client ID</label>
                              <input id={`builtin-${type}-client-id`} className="iam-input" value={p?.client_id ?? ''} onChange={e => updateBuiltin(type, { client_id: e.target.value })} placeholder="OAuth2 client ID" />
                            </div>
                          </div>
                          <SecretInput value={p?.client_secret ?? ''} saved={p?.client_secret_saved}
                            onChange={v => updateBuiltin(type, { client_secret: v, client_secret_saved: false })} />
                          <LogoUpload value={p?.logo_url} onChange={v => updateBuiltin(type, { logo_url: v })} label="Custom logo (optional)" />
                        </div>
                      )}
                    </div>
                  );
                })}

                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', paddingTop: 4 }}>
                  <p style={{ fontSize: 13, fontWeight: 500 }}>Custom OIDC Providers</p>
                  <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={addOidc}>
                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
                    Add Provider
                  </button>
                </div>

                {customOidcs.length === 0 && (
                  <div style={{ fontSize: 13, color: 'var(--fg-muted)', textAlign: 'center', padding: '16px', border: '1px dashed var(--border)', borderRadius: 8 }}>
                    No custom OIDC providers configured
                  </div>
                )}

                {customOidcs.map(p => (
                  <div key={p.id} className="iam-card iam-card-pad">
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                        {p.logo_url && <img src={p.logo_url} alt={p.label} style={{ height: 20, width: 20, objectFit: 'contain' }} onError={e => (e.currentTarget.style.display = 'none')} />}
                        <div>
                          <p style={{ fontSize: 13, fontWeight: 500 }}>{p.label || 'New OIDC Provider'}</p>
                          <p className="iam-mono" style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{p.issuer_url || 'No issuer set'}</p>
                        </div>
                      </div>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                        <Toggle checked={p.enabled} onChange={v => updateOidc(p.id, { enabled: v })} />
                        <button type="button" className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" onClick={() => removeOidc(p.id)}>
                          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
                        </button>
                      </div>
                    </div>
                    <div style={{ marginTop: 14, paddingTop: 14, borderTop: '1px solid var(--border)', display: 'flex', flexDirection: 'column', gap: 10 }}>
                      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                        <div>
                          <label className="iam-label" htmlFor={`oidc-${p.id}-label`} style={{ fontSize: 11 }}>Button Label</label>
                          <input id={`oidc-${p.id}-label`} className="iam-input" value={p.label} onChange={e => updateOidc(p.id, { label: e.target.value })} placeholder="Continue with SSO" />
                        </div>
                        <div>
                          <label className="iam-label" htmlFor={`oidc-${p.id}-client-id`} style={{ fontSize: 11 }}>Client ID</label>
                          <input id={`oidc-${p.id}-client-id`} className="iam-input" value={p.client_id} onChange={e => updateOidc(p.id, { client_id: e.target.value })} placeholder="OAuth2 client ID" />
                        </div>
                      </div>
                      <div>
                        <label className="iam-label" htmlFor={`oidc-${p.id}-issuer`} style={{ fontSize: 11 }}>Issuer URL</label>
                        <input id={`oidc-${p.id}-issuer`} className="iam-input" value={p.issuer_url ?? ''} onChange={e => updateOidc(p.id, { issuer_url: e.target.value })} placeholder="https://accounts.example.com" />
                      </div>
                      <SecretInput value={p.client_secret ?? ''} saved={p.client_secret_saved}
                        onChange={v => updateOidc(p.id, { client_secret: v, client_secret_saved: false })} />
                      <LogoUpload value={p.logo_url} onChange={v => updateOidc(p.id, { logo_url: v })} label="Logo" />
                    </div>
                  </div>
                ))}

                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', paddingTop: 4 }}>
                  <p style={{ fontSize: 13, fontWeight: 500 }}>SAML 2.0 Identity Providers</p>
                  <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={() => setAddSamlOpen(true)}>
                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
                    Add IdP
                  </button>
                </div>
                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 11, color: 'var(--fg-muted)', marginBottom: 6 }}>SP Metadata URL — give this to your IdP</div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, background: 'var(--surface-2)', borderRadius: 6, padding: '8px 12px' }}>
                    <code className="iam-mono" style={{ fontSize: 11, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{spMetadataUrl}</code>
                    <CopyButton text={spMetadataUrl} />
                  </div>
                </div>
                {samlProviders.length === 0 ? (
                  <div style={{ fontSize: 13, color: 'var(--fg-muted)', textAlign: 'center', padding: 16, border: '1px dashed var(--border)', borderRadius: 8 }}>
                    No SAML providers configured
                  </div>
                ) : (
                  <div className="iam-card">
                    {samlProviders.map((idp, i) => (
                      <div key={idp.id} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '12px 16px', borderBottom: i < samlProviders.length - 1 ? '1px solid var(--border)' : 'none' }}>
                        <div>
                          <p style={{ fontSize: 13, fontWeight: 500 }}>{idp.entity_id}</p>
                          <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{idp.metadata_url ?? 'Manual config'}</p>
                        </div>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                          <IamChip tone={idp.active ? 'success' : 'default'}>{idp.active ? 'Active' : 'Inactive'}</IamChip>
                          <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" style={{ color: 'var(--danger)' }} onClick={() => handleDeleteSaml(idp.id)}>
                            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>
                          </button>
                        </div>
                      </div>
                    ))}
                  </div>
                )}

                <p style={{ fontSize: 13, fontWeight: 500, paddingTop: 4 }}>OAuth2 Scopes</p>
                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Custom Scopes</div>
                  <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 12 }}>
                    Additional scopes for this project's OAuth2 client. Built-in scopes{' '}
                    <code className="iam-mono">openid</code> and <code className="iam-mono">offline</code> are always included.
                  </div>
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, minHeight: 32, marginBottom: 10 }}>
                    <IamChip tone="default" mono>openid</IamChip>
                    <IamChip tone="default" mono>offline</IamChip>
                    {customScopes.map(s => (
                      <span key={s} className="iam-mono" style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: 11, padding: '2px 8px', border: '1px solid var(--border)', borderRadius: 4 }}>
                        {s}
                        <button type="button" onClick={() => removeScope(s)} style={{ background: 'none', border: 'none', cursor: 'pointer', color: 'var(--fg-muted)', fontSize: 14, lineHeight: 1 }}>×</button>
                      </span>
                    ))}
                  </div>
                  <div style={{ display: 'flex', gap: 8 }}>
                    <input className="iam-input iam-mono" value={newScope}
                      onChange={e => { setNewScope(e.target.value.toLowerCase().replaceAll(/[^a-z0-9:_-]/g, '')); setScopeError(''); }}
                      placeholder="read:orders"
                      onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addScope(); } }} />
                    <button type="button" className="iam-btn iam-btn-secondary" onClick={addScope}>Add</button>
                  </div>
                  {scopeError && <p style={{ fontSize: 12, color: 'var(--danger)', marginTop: 4 }}>{scopeError}</p>}
                </div>
              </div>
            )}

            {/* ── Registration ── */}
            {tab === 'registration' && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Self-Registration</div>
                  <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 14 }}>Allow users to create their own accounts on the login page.</div>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                      <span className="iam-label" style={{ margin: 0 }}>Allow self-registration</span>
                      <Toggle checked={allowSelfReg} onChange={setAllowSelfReg} />
                    </div>
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', borderTop: '1px solid var(--border)', paddingTop: 14 }}>
                      <div>
                        <p style={{ fontSize: 13, fontWeight: 500 }}>Require MFA</p>
                        <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Users without a second factor cannot complete login until they enroll one.</p>
                      </div>
                      <Toggle checked={requireMfa} onChange={setRequireMfa} />
                    </div>
                  </div>
                </div>

                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Allowed Email Domains</div>
                  <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 12 }}>Restrict registration to specific email domains. Leave blank to allow any domain.</div>
                  <input className="iam-input" value={allowedDomains} onChange={e => setAllowedDomains(e.target.value)} placeholder="example.com, company.io" />
                  <p style={{ fontSize: 11, color: 'var(--fg-muted)', marginTop: 4 }}>Comma-separated list of allowed domains.</p>
                  {allowedDomains.trim() && (
                    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 8 }}>
                      {allowedDomains.split(',').map(d => d.trim()).filter(Boolean).map(d => (
                        <IamChip key={d} tone="default" mono>{d}</IamChip>
                      ))}
                    </div>
                  )}
                </div>

                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Password Policy</div>
                  <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 14 }}>Requirements enforced when users register or are created by an admin.</div>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                      <label className="iam-label" htmlFor="auth-min-length" style={{ margin: 0, flexShrink: 0 }}>Minimum length</label>
                      <input id="auth-min-length" className="iam-input" type="number" min={0} max={128} value={minPasswordLength}
                        onChange={e => setMinPasswordLength(Math.max(0, Math.min(128, Number(e.target.value) || 0)))}
                        style={{ width: 80 }} />
                      <span style={{ fontSize: 11, color: 'var(--fg-muted)' }}>characters (0 = disabled)</span>
                    </div>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                      {([
                        { label: 'Require uppercase letter (A–Z)', checked: requireUppercase, setter: setRequireUppercase },
                        { label: 'Require lowercase letter (a–z)', checked: requireLowercase, setter: setRequireLowercase },
                        { label: 'Require number (0–9)',           checked: requireDigit,     setter: setRequireDigit },
                        { label: 'Require special character (!@#$…)', checked: requireSpecial, setter: setRequireSpecial },
                      ] as const).map(({ label, checked, setter }) => (
                        <div key={label} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                          <label className="iam-label" style={{ margin: 0, fontWeight: 400 }}>{label}</label>
                          <Toggle checked={checked} onChange={setter} />
                        </div>
                      ))}
                    </div>
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', borderTop: '1px solid var(--border)', paddingTop: 14 }}>
                      <div>
                        <p style={{ fontSize: 13, fontWeight: 500 }}>Reject breached passwords</p>
                        <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Passwords found in known data breaches are rejected. Uses HaveIBeenPwned k-anonymity API — no password is transmitted.</p>
                      </div>
                      <Toggle checked={checkBreachedPasswords} onChange={setCheckBreachedPasswords} />
                    </div>
                  </div>
                </div>

                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Default Role</div>
                  <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 12 }}>Role automatically assigned when a user registers or signs in via social login for the first time.</div>
                  <select className="iam-input" style={{ maxWidth: 256 }}
                    value={defaultRoleId ?? '__none__'} onChange={e => setDefaultRoleId(e.target.value === '__none__' ? null : e.target.value)}>
                    <option value="__none__">No default role</option>
                    {roles.map(r => (
                      <option key={r.id} value={r.id}>{r.name} (rank {r.rank})</option>
                    ))}
                  </select>
                </div>
              </div>
            )}

            {/* ── Verification ── */}
            {tab === 'verification' && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Account Verification</div>
                  <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 14 }}>Require new users to verify their identity with a one-time code before accessing the app.</div>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                      <div>
                        <p style={{ fontSize: 13, fontWeight: 500 }}>Email verification</p>
                        <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Send a 6-digit OTP to the user's email address</p>
                      </div>
                      <Toggle checked={emailVerif} onChange={setEmailVerif} />
                    </div>
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                      <div>
                        <p style={{ fontSize: 13, fontWeight: 500 }}>SMS verification</p>
                        <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Send a 6-digit OTP to the user's phone number</p>
                      </div>
                      <Toggle checked={smsVerif} onChange={setSmsVerif} />
                    </div>
                  </div>
                </div>
                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Email Branding</div>
                  <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 12 }}>Override the sender display name for emails sent from this project. Leave blank to use the organisation's setting.</div>
                  <div>
                    <label className="iam-label" htmlFor="auth-from-name">From name</label>
                    <input id="auth-from-name" className="iam-input" value={emailFromName} onChange={e => setEmailFromName(e.target.value)}
                      placeholder="e.g. Acme Dev Portal (inherits from org if blank)" />
                  </div>
                </div>
              </div>
            )}

            {/* ── Security ── */}
            {tab === 'security' && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                <div className="iam-card iam-card-pad">
                  <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>IP Allowlist</div>
                  <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 12 }}>
                    Restrict logins to specific IP ranges. Leave empty to allow all IPs.
                    Enter one CIDR range per line (e.g. <code className="iam-mono">10.0.0.0/8</code>).
                  </div>
                  <textarea className="iam-input iam-mono"
                    value={ipAllowlist}
                    onChange={e => { setIpAllowlist(e.target.value); setIpAllowlistError(''); }}
                    placeholder={'10.0.0.0/8\n192.168.1.0/24'}
                    rows={5}
                    style={{ fontSize: 12, resize: 'vertical' }}
                  />
                  {ipAllowlistError && (
                    <div style={{ padding: '8px 12px', background: 'var(--danger-soft)', color: 'var(--danger)', borderRadius: 6, fontSize: 13, marginTop: 8 }}>{ipAllowlistError}</div>
                  )}
                  <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8, marginTop: 10, color: 'var(--warn)' }}>
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={{ flexShrink: 0, marginTop: 1 }}><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/></svg>
                    <p style={{ fontSize: 12 }}>If you misconfigure this, you may lock yourself out. Verify your current IP before saving.</p>
                  </div>
                </div>
              </div>
            )}

            {/* ── Custom CSS ── */}
            {tab === 'css' && (
              <div className="iam-card iam-card-pad">
                <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Custom CSS</div>
                <div role="alert" style={{
                  fontSize: 12.5, padding: 10, marginBottom: 12, borderRadius: 6,
                  background: 'oklch(from var(--warn) l c h / 8%)',
                  border: '1px dashed var(--warn)', color: 'var(--fg)',
                }}>
                  <strong>Security:</strong> Custom CSS runs in your users' browsers on the login page.
                  Malicious CSS can exfiltrate typed values via attribute selectors and background-image
                  requests. Only paste CSS you wrote or fully trust. <code>@import</code>, external <code>url()</code>,
                  and selectors targeting password inputs are stripped server-side.
                </div>
                <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 12 }}>
                  Injected into the login page &lt;head&gt;. Available CSS variables:{' '}
                  <code className="iam-mono" style={{ fontSize: 11 }}>--primary --background --surface --text --text-muted --border --radius --font-family</code>
                </div>
                <textarea className="iam-input iam-mono"
                  value={theme.custom_css ?? ''}
                  onChange={e => set('custom_css', e.target.value)}
                  style={{ fontSize: 12, minHeight: 300, resize: 'vertical' }}
                  placeholder={`.card {\n  box-shadow: 0 20px 60px rgba(0,0,0,0.2);\n}\n\n.btn {\n  text-transform: uppercase;\n  letter-spacing: 0.05em;\n}`}
                />
              </div>
            )}
          </div>

          <div style={{ position: 'sticky', top: 24, display: 'flex', flexDirection: 'column', gap: 10 }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div style={{ display: 'flex', border: '1px solid var(--border)', borderRadius: 6, overflow: 'hidden', fontSize: 12, fontWeight: 500 }}>
                {(['login', 'register', 'verify'] as PreviewMode[]).map(m => (
                  <button key={m} onClick={() => setPreviewMode(m)} style={{
                    padding: '6px 12px', textTransform: 'capitalize', border: 'none', cursor: 'pointer', transition: 'background 150ms, color 150ms',
                    background: previewMode === m ? 'var(--ia-accent)' : 'var(--surface)',
                    color: previewMode === m ? '#fff' : 'var(--fg-muted)',
                  }}>
                    {m}
                  </button>
                ))}
              </div>
              <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={() => setPreviewDark(d => !d)} title="Toggle dark/light preview">
                {previewDark
                  ? <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="12" cy="12" r="5"/><line x1="12" y1="1" x2="12" y2="3"/><line x1="12" y1="21" x2="12" y2="23"/><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"/><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"/><line x1="1" y1="12" x2="3" y2="12"/><line x1="21" y1="12" x2="23" y2="12"/><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"/><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"/></svg>
                  : <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/></svg>}
              </button>
            </div>
            <div style={{ border: '1px solid var(--border)', borderRadius: 8, overflow: 'hidden' }}>
              <iframe key={previewUrl} src={previewUrl} sandbox="allow-scripts allow-same-origin" style={{ width: '100%', height: 620, border: 'none', pointerEvents: 'none', display: 'block' }} title="Login page preview" />
            </div>
          </div>
        </div>
      </div>

      <IamDialog
        open={addSamlOpen}
        onClose={() => { setAddSamlOpen(false); setSamlError(''); }}
        title="Add SAML 2.0 Identity Provider"
        desc="Connect a corporate IdP (Okta, Azure AD, ADFS)."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setAddSamlOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="add-saml-form" type="submit" disabled={samlSaving}>
              {samlSaving ? 'Adding…' : 'Add IdP'}
            </button>
          </>
        }
      >
        <form id="add-saml-form" onSubmit={handleAddSaml} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          {samlError && <div style={{ padding: '8px 12px', background: 'var(--danger-soft)', color: 'var(--danger)', borderRadius: 6, fontSize: 13 }}>{samlError}</div>}
          <div>
            <label className="iam-label" htmlFor="saml-entity-id">Entity ID <span style={{ color: 'var(--danger)' }}>*</span></label>
            <input id="saml-entity-id" className="iam-input" value={samlForm.entity_id} onChange={e => setSamlForm(f => ({ ...f, entity_id: e.target.value }))} required placeholder="https://your-idp.example.com" />
          </div>
          <div>
            <label className="iam-label" htmlFor="saml-metadata-url">Metadata URL <span style={{ fontSize: 11, color: 'var(--fg-muted)' }}>(recommended)</span></label>
            <input id="saml-metadata-url" className="iam-input" value={samlForm.metadata_url} onChange={e => setSamlForm(f => ({ ...f, metadata_url: e.target.value }))} placeholder="https://your-idp.example.com/metadata" />
            <p style={{ fontSize: 11, color: 'var(--fg-muted)', marginTop: 4 }}>Must use HTTPS. If provided, certificates are fetched automatically.</p>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
            <div>
              <label className="iam-label" htmlFor="saml-email-attr">Email attribute</label>
              <input id="saml-email-attr" className="iam-input" value={samlForm.email_attribute_name} onChange={e => setSamlForm(f => ({ ...f, email_attribute_name: e.target.value }))} placeholder="email" />
            </div>
            <div>
              <label className="iam-label" htmlFor="saml-name-attr">Name attribute <span style={{ fontSize: 11, color: 'var(--fg-muted)' }}>(optional)</span></label>
              <input id="saml-name-attr" className="iam-input" value={samlForm.display_name_attribute_name} onChange={e => setSamlForm(f => ({ ...f, display_name_attribute_name: e.target.value }))} placeholder="displayName" />
            </div>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <div>
              <p style={{ fontSize: 13, fontWeight: 500 }}>JIT provisioning</p>
              <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Automatically create users on first login</p>
            </div>
            <Toggle checked={samlForm.jit_provisioning} onChange={v => setSamlForm(f => ({ ...f, jit_provisioning: v }))} />
          </div>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <span className="iam-label" style={{ margin: 0 }}>Active</span>
            <Toggle checked={samlForm.active} onChange={v => setSamlForm(f => ({ ...f, active: v }))} />
          </div>
        </form>
      </IamDialog>
    </div>
  );
}
