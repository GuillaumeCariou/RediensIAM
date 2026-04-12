import { useEffect, useMemo, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { Save, Upload, X, Plus, Sun, Moon, Copy, Trash2, Shield } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Textarea } from '@/components/ui/textarea';
import { Skeleton } from '@/components/ui/skeleton';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Badge } from '@/components/ui/badge';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { getProjectInfo, updateProject, listRoles, listSamlProviders, createSamlProvider, deleteSamlProvider } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

// ── Types ─────────────────────────────────────────────────────────────────────

interface Provider {
  id: string;
  type: 'google' | 'github' | 'gitlab' | 'facebook' | 'oidc';
  label: string;
  client_id: string;
  client_secret?: string;
  client_secret_saved?: boolean; // server returned null → secret is stored, don't overwrite unless changed
  issuer_url?: string;
  logo_url?: string;
  enabled: boolean;
}

interface SamlProvider {
  id: string;
  entity_id: string;
  metadata_url?: string;
  email_attribute_name: string;
  name_attribute_name?: string;
  jit_provisioning: boolean;
  active: boolean;
}

interface Theme {
  primary_color?: string;
  background_color?: string;
  surface_color?: string;
  text_color?: string;
  border_radius?: string;
  font_family?: string;
  logo_url?: string;
  custom_css?: string;
  providers?: Provider[];
  hydra_local_login?: boolean;
}

interface Role { id: string; name: string; rank: number; }

// ── Constants ────────────────────────────────────────────────────────────────

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

function nanoid() { return Math.random().toString(36).slice(2, 10); }

function ColorRow({ label, value, onChange }: Readonly<{ label: string; value: string; onChange: (v: string) => void }>) {
  return (
    <div className="space-y-2">
      <Label>{label}</Label>
      <div className="flex gap-2 items-center">
        <input type="color" value={value || '#000000'} onChange={e => onChange(e.target.value)}
          className="h-9 w-12 rounded cursor-pointer border border-input p-0.5 bg-transparent" />
        <Input value={value} onChange={e => onChange(e.target.value)} className="font-mono" placeholder="#000000" />
      </div>
    </div>
  );
}

function LogoUpload({ value, onChange, label = 'Logo' }: Readonly<{ value?: string; onChange: (v: string) => void; label?: string }>) {
  const [dragOver, setDragOver] = useState(false);
  const handle = (file: File) => {
    if (!file.type.startsWith('image/')) return;
    const reader = new FileReader();
    reader.onload = e => onChange(e.target?.result as string);
    reader.readAsDataURL(file);
  };
  return (
    <div className="space-y-2">
      <Label>{label}</Label>
      <button
        type="button"
        onDragOver={e => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        onDrop={e => { e.preventDefault(); setDragOver(false); const f = e.dataTransfer.files[0]; if (f) handle(f); }}
        onClick={() => document.getElementById('logo-file-input')?.click()}
        className={`relative w-full border-2 border-dashed rounded-lg p-4 text-center transition-colors ${dragOver ? 'border-primary bg-primary/5' : 'border-border'}`}
      >
        {value ? (
          <div className="flex items-center justify-center gap-3">
            <img src={value} alt="Logo" className="max-h-10 max-w-[160px] object-contain" onError={e => (e.currentTarget.style.display = 'none')} />
            <Button type="button" variant="ghost" size="icon" onClick={() => onChange('')}><X className="h-4 w-4" /></Button>
          </div>
        ) : (
          <div className="space-y-1">
            <Upload className="h-6 w-6 mx-auto text-muted-foreground" />
            <p className="text-xs text-muted-foreground">Drag & drop or{' '}
              <label className="cursor-pointer text-primary underline">
                browse{' '}
                <input id="logo-file-input" type="file" accept="image/*" className="hidden" onChange={e => { if (e.target.files?.[0]) handle(e.target.files[0]); }} />
              </label>
            </p>
          </div>
        )}
      </button>
      <Input value={value?.startsWith('data:') ? '' : (value ?? '')} onChange={e => onChange(e.target.value)}
        placeholder="https://cdn.example.com/logo.png" />
    </div>
  );
}

function CopyButton({ text }: Readonly<{ text: string }>) {
  const [copied, setCopied] = useState(false);
  return (
    <Button type="button" variant="ghost" size="icon" onClick={() => { navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 2000); }}>
      {copied ? <span className="text-xs text-green-600">✓</span> : <Copy className="h-3.5 w-3.5" />}
    </Button>
  );
}

type PreviewMode = 'login' | 'register' | 'verify';

// ── Main component ────────────────────────────────────────────────────────────

function SecretInput({ value, saved: secretSaved, onChange }: Readonly<{ value: string; saved?: boolean; onChange: (v: string) => void }>) {
  return (
    <div className="space-y-1">
      <Label className="text-xs">Client Secret</Label>
      <Input
        type="password"
        value={value}
        onChange={e => onChange(e.target.value)}
        placeholder={secretSaved && !value ? '••••••••• (saved — enter new to replace)' : 'OAuth2 client secret'}
        autoComplete="new-password"
      />
      {secretSaved && !value && (
        <p className="text-xs text-muted-foreground">Secret is saved. Enter a new one to replace it.</p>
      )}
    </div>
  );
}

export default function Authentication() {
  const { projectId } = useProjectContext();
  const [theme, setTheme] = useState<Theme>(DEFAULT_THEME);
  const [customFont, setCustomFont] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [previewDark, setPreviewDark] = useState(false);
  const [previewMode, setPreviewMode] = useState<PreviewMode>('login');

  // ── Registration / policy settings ───────────────────────────────
  const [allowSelfReg,          setAllowSelfReg]          = useState(false);
  const [requireMfa,            setRequireMfa]            = useState(false);
  const [checkBreachedPasswords,setCheckBreachedPasswords]= useState(false);
  const [emailVerif,            setEmailVerif]            = useState(false);
  const [smsVerif,              setSmsVerif]              = useState(false);
  const [allowedDomains,        setAllowedDomains]        = useState('');
  const [emailFromName,         setEmailFromName]         = useState('');
  const [defaultRoleId,         setDefaultRoleId]         = useState<string | null>(null);
  const [minPasswordLength,     setMinPasswordLength]     = useState(0);
  const [requireUppercase,      setRequireUppercase]      = useState(false);
  const [requireLowercase,      setRequireLowercase]      = useState(false);
  const [requireDigit,          setRequireDigit]          = useState(false);
  const [requireSpecial,        setRequireSpecial]        = useState(false);
  const [roles,                 setRoles]                 = useState<Role[]>([]);

  // ── Security settings ─────────────────────────────────────────────
  const [ipAllowlist,     setIpAllowlist]     = useState('');
  const [ipAllowlistError,setIpAllowlistError]= useState('');

  // ── Custom scopes ─────────────────────────────────────────────────
  const [customScopes, setCustomScopes] = useState<string[]>([]);
  const [newScope,     setNewScope]     = useState('');
  const [scopeError,   setScopeError]   = useState('');

  // ── SAML ──────────────────────────────────────────────────────────
  const [samlProviders, setSamlProviders] = useState<SamlProvider[]>([]);
  const [addSamlOpen,   setAddSamlOpen]   = useState(false);
  const [samlForm,      setSamlForm]      = useState({
    entity_id: '', metadata_url: '', email_attribute_name: 'email',
    name_attribute_name: '', jit_provisioning: true, active: true,
  });
  const [samlSaving,    setSamlSaving]    = useState(false);
  const [samlError,     setSamlError]     = useState('');

  // ── Load ──────────────────────────────────────────────────────────
  useEffect(() => {
    if (!projectId) { setLoading(false); return; }
    Promise.all([
      getProjectInfo(projectId).then(p => {
        if (p.login_theme) {
          const t = { ...DEFAULT_THEME, ...p.login_theme };
          if (t.providers) {
            t.providers = t.providers.map((pr: Provider) => ({
              ...pr,
              id: pr.id ?? nanoid(),
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
    ]).catch(console.error).finally(() => setLoading(false));
  }, [projectId]);

  const set = <K extends keyof Theme>(k: K, v: Theme[K]) => setTheme(t => ({ ...t, [k]: v }));

  // ── Save ──────────────────────────────────────────────────────────
  const handleSave = async () => {
    // Validate IP allowlist
    const ipLines = ipAllowlist.split('\n').map(s => s.trim()).filter(Boolean);
    const badIp = ipLines.find(s => !/^(\d{1,3}\.){3}\d{1,3}(\/\d{1,2})?$|^[0-9a-fA-F:]+\/\d{1,3}$/.test(s));
    if (badIp) { setIpAllowlistError(`Invalid CIDR: ${badIp}`); return; }
    setIpAllowlistError('');

    setSaving(true);
    try {
      const domains = allowedDomains.split(',').map(d => d.trim()).filter(Boolean);

      // Strip client_secret_saved flag and omit secret if unchanged
      const safeProviders = (theme.providers ?? []).map(p => {
        // eslint-disable-next-line @typescript-eslint/no-unused-vars
        const { client_secret_saved, ...rest } = p;
        if (p.client_secret_saved && !p.client_secret) {
          // Secret saved server-side, user didn't change it — omit from payload
          const { client_secret: _cs, ...noSecret } = rest;
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

  // ── Preview URL ───────────────────────────────────────────────────
  const previewUrl = useMemo(() => {
    const cfg = {
      mode: previewMode, dark: previewDark, theme,
      allow_self_registration: allowSelfReg,
      email_verification_enabled: emailVerif,
      sms_verification_enabled: smsVerif,
      min_password_length: minPasswordLength,
      password_require_uppercase: requireUppercase,
      password_require_lowercase: requireLowercase,
      password_require_digit: requireDigit,
      password_require_special: requireSpecial,
    };
    return `/preview?cfg=${btoa(JSON.stringify(cfg))}`;
  }, [previewMode, previewDark, theme, allowSelfReg, emailVerif, smsVerif,
      minPasswordLength, requireUppercase, requireLowercase, requireDigit, requireSpecial]);

  // ── Builtin provider helpers ──────────────────────────────────────
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

  // ── Custom OIDC helpers ───────────────────────────────────────────
  const customOidcs = (theme.providers ?? []).filter(p => p.type === 'oidc' && p.id !== 'oidc');
  const addOidc = () => {
    const id = nanoid();
    set('providers', [...(theme.providers ?? []), {
      id, type: 'oidc', label: 'Continue with SSO',
      client_id: '', issuer_url: '', logo_url: '', enabled: true,
    }]);
  };
  const updateOidc = (id: string, patch: Partial<Provider>) =>
    set('providers', (theme.providers ?? []).map(p => p.id === id ? { ...p, ...patch } : p));
  const removeOidc = (id: string) =>
    set('providers', (theme.providers ?? []).filter(p => p.id !== id));

  // ── Custom scopes helpers ─────────────────────────────────────────
  const addScope = () => {
    const s = newScope.trim();
    if (!s) return;
    if (!/^[a-z][a-z0-9:_-]*$/.test(s)) { setScopeError('Scope must be lowercase and may contain letters, numbers, colons, hyphens, underscores.'); return; }
    if (['openid', 'offline'].includes(s) || customScopes.includes(s)) { setScopeError('Scope already exists.'); return; }
    setCustomScopes(prev => [...prev, s]);
    setNewScope('');
    setScopeError('');
  };
  const removeScope = (s: string) => setCustomScopes(prev => prev.filter(x => x !== s));

  // ── SAML helpers ──────────────────────────────────────────────────
  const spMetadataUrl = `${globalThis.location.origin}/admin/projects/${projectId}/saml/metadata`;

  const handleAddSaml = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSamlSaving(true); setSamlError('');
    try {
      const res = await createSamlProvider(projectId, {
        entity_id: samlForm.entity_id,
        metadata_url: samlForm.metadata_url || undefined,
        email_attribute_name: samlForm.email_attribute_name || 'email',
        name_attribute_name: samlForm.name_attribute_name || undefined,
        jit_provisioning: samlForm.jit_provisioning,
        active: samlForm.active,
      });
      if (res.error) { setSamlError(res.error_description ?? 'Failed to add provider.'); return; }
      setSamlProviders(prev => [...prev, res]);
      setAddSamlOpen(false);
      setSamlForm({ entity_id: '', metadata_url: '', email_attribute_name: 'email', name_attribute_name: '', jit_provisioning: true, active: true });
    } catch { setSamlError('Something went wrong.'); }
    finally { setSamlSaving(false); }
  };

  const handleDeleteSaml = async (idpId: string) => {
    await deleteSamlProvider(projectId, idpId);
    setSamlProviders(prev => prev.filter(p => p.id !== idpId));
  };

  // ── Loading skeleton ──────────────────────────────────────────────
  if (loading) return (
    <div>
      <PageHeader title="Authentication" />
      <div className="p-6 space-y-4">{Array.from({ length: 4 }, (_, i) => `sk-${i}`).map(id => <Skeleton key={id} className="h-12 rounded-lg" />)}</div>
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
        action={
          <Button onClick={handleSave} disabled={saving}>
            <Save className="h-4 w-4" />{saveLabel}
          </Button>
        }
      />

      <div className="p-6 grid grid-cols-1 xl:grid-cols-[1fr_460px] gap-6 items-start">
        {/* ── Left: config tabs ── */}
        <div>
          <Tabs defaultValue="visual">
            <TabsList className="flex-wrap">
              <TabsTrigger value="visual">Appearance</TabsTrigger>
              <TabsTrigger value="providers">Providers</TabsTrigger>
              <TabsTrigger value="registration">Registration</TabsTrigger>
              <TabsTrigger value="verification">Verification</TabsTrigger>
              <TabsTrigger value="security">Security</TabsTrigger>
              <TabsTrigger value="css">Custom CSS</TabsTrigger>
            </TabsList>

            {/* ════════════════════════════════════════════════════════
                APPEARANCE
            ════════════════════════════════════════════════════════ */}
            <TabsContent value="visual" className="mt-6 space-y-6">
              <Card>
                <CardHeader><CardTitle className="text-base">Logo</CardTitle></CardHeader>
                <CardContent>
                  <LogoUpload value={theme.logo_url} onChange={v => set('logo_url', v)} />
                </CardContent>
              </Card>

              <Card>
                <CardHeader><CardTitle className="text-base">Colors</CardTitle></CardHeader>
                <CardContent className="grid grid-cols-2 gap-4">
                  <ColorRow label="Primary" value={theme.primary_color ?? '#1a56db'} onChange={v => set('primary_color', v)} />
                  <ColorRow label="Background" value={theme.background_color ?? '#f9fafb'} onChange={v => set('background_color', v)} />
                  <ColorRow label="Card surface" value={theme.surface_color ?? '#ffffff'} onChange={v => set('surface_color', v)} />
                  <ColorRow label="Text" value={theme.text_color ?? '#111827'} onChange={v => set('text_color', v)} />
                </CardContent>
              </Card>

              <Card>
                <CardHeader><CardTitle className="text-base">Typography & Layout</CardTitle></CardHeader>
                <CardContent className="space-y-4">
                  <div className="space-y-2">
                    <Label>Font Family</Label>
                    <Select value={FONT_OPTIONS.includes(theme.font_family ?? 'Inter') ? (theme.font_family ?? 'Inter') : 'Custom'}
                      onValueChange={v => { set('font_family', v === 'Custom' ? customFont : v); }}>
                      <SelectTrigger className="bg-background"><SelectValue /></SelectTrigger>
                      <SelectContent>
                        {FONT_OPTIONS.map(f => <SelectItem key={f} value={f}>{f}</SelectItem>)}
                      </SelectContent>
                    </Select>
                    {(theme.font_family === 'Custom' || !FONT_OPTIONS.includes(theme.font_family ?? 'Inter')) && (
                      <Input value={customFont} onChange={e => { setCustomFont(e.target.value); set('font_family', e.target.value); }}
                        placeholder="e.g. 'Nunito', sans-serif" />
                    )}
                  </div>
                  <div className="space-y-2">
                    <Label>Border Radius — {theme.border_radius ?? 8}px</Label>
                    <input type="range" min={0} max={24} value={theme.border_radius ?? 8}
                      onChange={e => set('border_radius', e.target.value)} className="w-full accent-primary" />
                    <div className="flex justify-between text-xs text-muted-foreground"><span>Square</span><span>Rounded</span><span>Pill</span></div>
                  </div>
                </CardContent>
              </Card>
            </TabsContent>

            {/* ════════════════════════════════════════════════════════
                PROVIDERS
            ════════════════════════════════════════════════════════ */}
            <TabsContent value="providers" className="mt-6 space-y-4">

              {/* Password login */}
              <Card>
                <CardContent className="pt-4">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="font-medium text-sm">Password login</p>
                      <p className="text-xs text-muted-foreground">Email/username + password form</p>
                    </div>
                    <Switch checked={theme.hydra_local_login ?? true} onCheckedChange={v => set('hydra_local_login', v)} />
                  </div>
                </CardContent>
              </Card>

              {/* Built-in social providers */}
              {BUILTIN_PROVIDERS.map(({ type, label, defaultLabel }) => {
                const p = getBuiltin(type);
                const enabled = p?.enabled ?? false;
                return (
                  <Card key={type}>
                    <CardContent className="pt-4 space-y-3">
                      <div className="flex items-center justify-between">
                        <div className="flex items-center gap-3">
                          {PROVIDER_ICONS[type] && <img src={PROVIDER_ICONS[type]} alt={type} className="h-5 w-5 object-contain" />}
                          <div>
                            <p className="font-medium text-sm">{label}</p>
                            <p className="text-xs text-muted-foreground">{defaultLabel}</p>
                          </div>
                        </div>
                        <Switch checked={enabled} onCheckedChange={() => toggleBuiltin(type, defaultLabel)} />
                      </div>
                      {enabled && (
                        <div className="space-y-3 pt-2 border-t">
                          <div className="grid grid-cols-2 gap-3">
                            <div className="space-y-1">
                              <Label className="text-xs">Button Label</Label>
                              <Input value={p?.label ?? defaultLabel} onChange={e => updateBuiltin(type, { label: e.target.value })} />
                            </div>
                            <div className="space-y-1">
                              <Label className="text-xs">Client ID</Label>
                              <Input value={p?.client_id ?? ''} onChange={e => updateBuiltin(type, { client_id: e.target.value })} placeholder="OAuth2 client ID" />
                            </div>
                          </div>
                          <SecretInput
                            value={p?.client_secret ?? ''}
                            saved={p?.client_secret_saved}
                            onChange={v => updateBuiltin(type, { client_secret: v, client_secret_saved: false })}
                          />
                          <LogoUpload value={p?.logo_url} onChange={v => updateBuiltin(type, { logo_url: v })} label="Custom logo (optional)" />
                        </div>
                      )}
                    </CardContent>
                  </Card>
                );
              })}

              {/* Custom OIDC */}
              <div className="flex items-center justify-between pt-2">
                <p className="text-sm font-medium">Custom OIDC Providers</p>
                <Button size="sm" variant="outline" onClick={addOidc}>
                  <Plus className="h-3.5 w-3.5" />Add Provider
                </Button>
              </div>

              {customOidcs.length === 0 && (
                <p className="text-sm text-muted-foreground text-center py-4 border border-dashed rounded-lg">
                  No custom OIDC providers configured
                </p>
              )}

              {customOidcs.map(p => (
                <Card key={p.id}>
                  <CardContent className="pt-4 space-y-3">
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-3">
                        {p.logo_url && <img src={p.logo_url} alt={p.label} className="h-5 w-5 object-contain" onError={e => (e.currentTarget.style.display = 'none')} />}
                        <div>
                          <p className="font-medium text-sm">{p.label || 'New OIDC Provider'}</p>
                          <p className="text-xs text-muted-foreground font-mono">{p.issuer_url || 'No issuer set'}</p>
                        </div>
                      </div>
                      <div className="flex items-center gap-2">
                        <Switch checked={p.enabled} onCheckedChange={v => updateOidc(p.id, { enabled: v })} />
                        <Button variant="ghost" size="icon" onClick={() => removeOidc(p.id)}><X className="h-4 w-4" /></Button>
                      </div>
                    </div>
                    <div className="space-y-3 pt-2 border-t">
                      <div className="grid grid-cols-2 gap-3">
                        <div className="space-y-1">
                          <Label className="text-xs">Button Label</Label>
                          <Input value={p.label} onChange={e => updateOidc(p.id, { label: e.target.value })} placeholder="Continue with SSO" />
                        </div>
                        <div className="space-y-1">
                          <Label className="text-xs">Client ID</Label>
                          <Input value={p.client_id} onChange={e => updateOidc(p.id, { client_id: e.target.value })} placeholder="OAuth2 client ID" />
                        </div>
                      </div>
                      <div className="space-y-1">
                        <Label className="text-xs">Issuer URL</Label>
                        <Input value={p.issuer_url ?? ''} onChange={e => updateOidc(p.id, { issuer_url: e.target.value })} placeholder="https://accounts.example.com" />
                      </div>
                      <SecretInput
                        value={p.client_secret ?? ''}
                        saved={p.client_secret_saved}
                        onChange={v => updateOidc(p.id, { client_secret: v, client_secret_saved: false })}
                      />
                      <LogoUpload value={p.logo_url} onChange={v => updateOidc(p.id, { logo_url: v })} label="Logo" />
                    </div>
                  </CardContent>
                </Card>
              ))}

              {/* ── SAML 2.0 ── */}
              <div className="flex items-center justify-between pt-2">
                <p className="text-sm font-medium">SAML 2.0 Identity Providers</p>
                <Button size="sm" variant="outline" onClick={() => setAddSamlOpen(true)}>
                  <Plus className="h-3.5 w-3.5" />Add IdP
                </Button>
              </div>

              {/* SP Metadata URL */}
              <Card>
                <CardContent className="pt-4">
                  <div className="space-y-1">
                    <Label className="text-xs text-muted-foreground">SP Metadata URL — give this to your IdP</Label>
                    <div className="flex items-center gap-2 bg-muted rounded px-3 py-2">
                      <code className="text-xs font-mono flex-1 truncate">{spMetadataUrl}</code>
                      <CopyButton text={spMetadataUrl} />
                    </div>
                  </div>
                </CardContent>
              </Card>

              {samlProviders.length === 0 ? (
                <p className="text-sm text-muted-foreground text-center py-4 border border-dashed rounded-lg">
                  No SAML providers configured
                </p>
              ) : (
                <Card>
                  <CardContent className="p-0 divide-y">
                    {samlProviders.map(idp => (
                      <div key={idp.id} className="flex items-center justify-between px-4 py-3">
                        <div>
                          <p className="font-medium text-sm">{idp.entity_id}</p>
                          <p className="text-xs text-muted-foreground">{idp.metadata_url ?? 'Manual config'}</p>
                        </div>
                        <div className="flex items-center gap-2">
                          <Badge variant={idp.active ? 'default' : 'secondary'}>{idp.active ? 'Active' : 'Inactive'}</Badge>
                          <Button variant="ghost" size="icon" onClick={() => handleDeleteSaml(idp.id)}>
                            <Trash2 className="h-4 w-4 text-destructive" />
                          </Button>
                        </div>
                      </div>
                    ))}
                  </CardContent>
                </Card>
              )}

              {/* ── Custom OAuth2 scopes ── */}
              <div className="pt-2">
                <p className="text-sm font-medium mb-3">OAuth2 Scopes</p>
              </div>
              <Card>
                <CardHeader>
                  <CardTitle className="text-base">Custom Scopes</CardTitle>
                  <CardDescription>
                    Define additional scopes for this project's OAuth2 client. The built-in scopes{' '}
                    <code className="font-mono text-xs">openid</code> and <code className="font-mono text-xs">offline</code> are always included.
                  </CardDescription>
                </CardHeader>
                <CardContent className="space-y-3">
                  <div className="flex flex-wrap gap-2 min-h-8">
                    <Badge variant="secondary" className="font-mono">openid</Badge>
                    <Badge variant="secondary" className="font-mono">offline</Badge>
                    {customScopes.map(s => (
                      <Badge key={s} variant="outline" className="font-mono gap-1">
                        {s}
                        <button type="button" onClick={() => removeScope(s)} className="ml-0.5 hover:text-destructive">×</button>
                      </Badge>
                    ))}
                  </div>
                  <div className="flex gap-2">
                    <Input
                      value={newScope}
                      onChange={e => { setNewScope(e.target.value.toLowerCase().replaceAll(/[^a-z0-9:_-]/g, '')); setScopeError(''); }}
                      placeholder="read:orders"
                      className="font-mono"
                      onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addScope(); } }}
                    />
                    <Button type="button" variant="outline" onClick={addScope}>Add</Button>
                  </div>
                  {scopeError && <p className="text-xs text-destructive">{scopeError}</p>}
                </CardContent>
              </Card>
            </TabsContent>

            {/* ════════════════════════════════════════════════════════
                REGISTRATION
            ════════════════════════════════════════════════════════ */}
            <TabsContent value="registration" className="mt-6 space-y-4">
              <Card>
                <CardHeader>
                  <CardTitle className="text-base">Self-Registration</CardTitle>
                  <CardDescription>Allow users to create their own accounts on the login page.</CardDescription>
                </CardHeader>
                <CardContent className="space-y-4">
                  <div className="flex items-center justify-between">
                    <Label>Allow self-registration</Label>
                    <Switch checked={allowSelfReg} onCheckedChange={setAllowSelfReg} />
                  </div>
                  <div className="flex items-center justify-between border-t pt-4">
                    <div>
                      <p className="text-sm font-medium">Require MFA</p>
                      <p className="text-xs text-muted-foreground">Users without a second factor cannot complete login until they enroll one.</p>
                    </div>
                    <Switch checked={requireMfa} onCheckedChange={setRequireMfa} />
                  </div>
                </CardContent>
              </Card>

              <Card>
                <CardHeader>
                  <CardTitle className="text-base">Allowed Email Domains</CardTitle>
                  <CardDescription>Restrict registration to specific email domains. Leave blank to allow any domain.</CardDescription>
                </CardHeader>
                <CardContent className="space-y-2">
                  <Input value={allowedDomains} onChange={e => setAllowedDomains(e.target.value)} placeholder="example.com, company.io" />
                  <p className="text-xs text-muted-foreground">Comma-separated list of allowed domains.</p>
                  {allowedDomains.trim() && (
                    <div className="flex flex-wrap gap-1 pt-1">
                      {allowedDomains.split(',').map(d => d.trim()).filter(Boolean).map(d => (
                        <Badge key={d} variant="secondary" className="font-mono text-xs">{d}</Badge>
                      ))}
                    </div>
                  )}
                </CardContent>
              </Card>

              <Card>
                <CardHeader>
                  <CardTitle className="text-base">Password Policy</CardTitle>
                  <CardDescription>Requirements enforced when users register or are created by an admin.</CardDescription>
                </CardHeader>
                <CardContent className="space-y-4">
                  <div className="flex items-center gap-3">
                    <Label className="shrink-0">Minimum length</Label>
                    <Input type="number" min={0} max={128} value={minPasswordLength}
                      onChange={e => setMinPasswordLength(Math.max(0, Math.min(128, Number(e.target.value) || 0)))}
                      className="w-24" />
                    <span className="text-xs text-muted-foreground">characters (0 = disabled)</span>
                  </div>
                  <div className="space-y-3">
                    {([
                      { label: 'Require uppercase letter (A–Z)', checked: requireUppercase, set: setRequireUppercase },
                      { label: 'Require lowercase letter (a–z)', checked: requireLowercase, set: setRequireLowercase },
                      { label: 'Require number (0–9)',           checked: requireDigit,     set: setRequireDigit },
                      { label: 'Require special character (!@#$…)', checked: requireSpecial, set: setRequireSpecial },
                    ] as const).map(({ label, checked, set: setter }) => (
                      <div key={label} className="flex items-center justify-between">
                        <Label className="font-normal">{label}</Label>
                        <Switch checked={checked} onCheckedChange={setter} />
                      </div>
                    ))}
                  </div>
                  <div className="flex items-center justify-between border-t pt-4">
                    <div>
                      <p className="text-sm font-medium">Reject breached passwords</p>
                      <p className="text-xs text-muted-foreground">
                        Passwords found in known data breaches are rejected. Uses HaveIBeenPwned k-anonymity API — no password is transmitted.
                      </p>
                    </div>
                    <Switch checked={checkBreachedPasswords} onCheckedChange={setCheckBreachedPasswords} />
                  </div>
                </CardContent>
              </Card>

              <Card>
                <CardHeader>
                  <CardTitle className="text-base">Default Role</CardTitle>
                  <CardDescription>Role automatically assigned when a user registers or signs in via social login for the first time.</CardDescription>
                </CardHeader>
                <CardContent>
                  <Select value={defaultRoleId ?? '__none__'} onValueChange={v => setDefaultRoleId(v === '__none__' ? null : v)}>
                    <SelectTrigger className="w-64 bg-background"><SelectValue placeholder="No default role" /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="__none__">No default role</SelectItem>
                      {roles.map(r => (
                        <SelectItem key={r.id} value={r.id}>{r.name} <span className="text-muted-foreground ml-1 text-xs">(rank {r.rank})</span></SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </CardContent>
              </Card>
            </TabsContent>

            {/* ════════════════════════════════════════════════════════
                VERIFICATION
            ════════════════════════════════════════════════════════ */}
            <TabsContent value="verification" className="mt-6 space-y-4">
              <Card>
                <CardHeader>
                  <CardTitle className="text-base">Account Verification</CardTitle>
                  <CardDescription>Require new users to verify their identity with a one-time code before accessing the app.</CardDescription>
                </CardHeader>
                <CardContent className="space-y-4">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm font-medium">Email verification</p>
                      <p className="text-xs text-muted-foreground">Send a 6-digit OTP to the user's email address</p>
                    </div>
                    <Switch checked={emailVerif} onCheckedChange={setEmailVerif} />
                  </div>
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm font-medium">SMS verification</p>
                      <p className="text-xs text-muted-foreground">Send a 6-digit OTP to the user's phone number</p>
                    </div>
                    <Switch checked={smsVerif} onCheckedChange={setSmsVerif} />
                  </div>
                </CardContent>
              </Card>

              <Card>
                <CardHeader>
                  <CardTitle className="text-base">Email Branding</CardTitle>
                  <CardDescription>Override the sender display name for emails sent from this project. Leave blank to use the organisation's setting.</CardDescription>
                </CardHeader>
                <CardContent>
                  <div className="space-y-1.5">
                    <Label>From name</Label>
                    <Input value={emailFromName} onChange={e => setEmailFromName(e.target.value)}
                      placeholder="e.g. Acme Dev Portal (inherits from org if blank)" />
                  </div>
                </CardContent>
              </Card>
            </TabsContent>

            {/* ════════════════════════════════════════════════════════
                SECURITY
            ════════════════════════════════════════════════════════ */}
            <TabsContent value="security" className="mt-6 space-y-4">
              <Card>
                <CardHeader>
                  <CardTitle className="text-base">IP Allowlist</CardTitle>
                  <CardDescription>
                    Restrict logins to specific IP ranges. Leave empty to allow all IPs.
                    Enter one CIDR range per line (e.g. <code className="font-mono text-xs">10.0.0.0/8</code>).
                  </CardDescription>
                </CardHeader>
                <CardContent className="space-y-3">
                  <Textarea
                    value={ipAllowlist}
                    onChange={e => { setIpAllowlist(e.target.value); setIpAllowlistError(''); }}
                    placeholder={"10.0.0.0/8\n192.168.1.0/24"}
                    rows={5}
                    className="font-mono text-sm"
                  />
                  {ipAllowlistError && (
                    <Alert variant="destructive"><AlertDescription>{ipAllowlistError}</AlertDescription></Alert>
                  )}
                  <div className="flex items-start gap-2 text-amber-600">
                    <Shield className="h-4 w-4 mt-0.5 shrink-0" />
                    <p className="text-xs">If you misconfigure this, you may lock yourself out. Verify your current IP before saving.</p>
                  </div>
                </CardContent>
              </Card>
            </TabsContent>

            {/* ════════════════════════════════════════════════════════
                CUSTOM CSS
            ════════════════════════════════════════════════════════ */}
            <TabsContent value="css" className="mt-6">
              <Card>
                <CardHeader>
                  <CardTitle className="text-base">Custom CSS</CardTitle>
                  <CardDescription>Injected into the login page &lt;head&gt;. Available CSS variables: <code className="font-mono text-xs">--primary --background --surface --text --text-muted --border --radius --font-family</code></CardDescription>
                </CardHeader>
                <CardContent>
                  <Textarea
                    value={theme.custom_css ?? ''}
                    onChange={e => set('custom_css', e.target.value)}
                    className="font-mono text-sm min-h-[300px]"
                    placeholder={`.card {\n  box-shadow: 0 20px 60px rgba(0,0,0,0.2);\n}\n\n.btn {\n  text-transform: uppercase;\n  letter-spacing: 0.05em;\n}`}
                  />
                </CardContent>
              </Card>
            </TabsContent>
          </Tabs>
        </div>

        {/* ── Right: always-visible preview ── */}
        <div className="xl:sticky xl:top-6 space-y-3">
          <div className="flex items-center justify-between">
            <div className="flex rounded-md border overflow-hidden text-xs font-medium">
              {(['login', 'register', 'verify'] as PreviewMode[]).map(m => (
                <button key={m} onClick={() => setPreviewMode(m)}
                  className={`px-3 py-1.5 capitalize transition-colors ${previewMode === m ? 'bg-primary text-primary-foreground' : 'bg-background text-muted-foreground hover:text-foreground'}`}>
                  {m}
                </button>
              ))}
            </div>
            <Button variant="outline" size="sm" onClick={() => setPreviewDark(d => !d)} title="Toggle dark/light preview">
              {previewDark ? <Sun className="h-4 w-4" /> : <Moon className="h-4 w-4" />}
            </Button>
          </div>
          <div className="rounded-xl border overflow-hidden">
            <iframe key={previewUrl} src={previewUrl} className="w-full border-0"
              style={{ height: '620px', pointerEvents: 'none' }} title="Login page preview" />
          </div>
        </div>
      </div>

      {/* ── Add SAML dialog ── */}
      <Dialog open={addSamlOpen} onOpenChange={v => { setAddSamlOpen(v); setSamlError(''); }}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Add SAML 2.0 Identity Provider</DialogTitle>
            <DialogDescription>Connect a corporate IdP (Okta, Azure AD, ADFS).</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAddSaml} className="space-y-4">
            {samlError && <Alert variant="destructive"><AlertDescription>{samlError}</AlertDescription></Alert>}
            <div className="space-y-2">
              <Label>Entity ID <span className="text-destructive">*</span></Label>
              <Input value={samlForm.entity_id} onChange={e => setSamlForm(f => ({ ...f, entity_id: e.target.value }))} required placeholder="https://your-idp.example.com" />
            </div>
            <div className="space-y-2">
              <Label>Metadata URL <span className="text-xs text-muted-foreground">(recommended)</span></Label>
              <Input value={samlForm.metadata_url} onChange={e => setSamlForm(f => ({ ...f, metadata_url: e.target.value }))} placeholder="https://your-idp.example.com/metadata" />
              <p className="text-xs text-muted-foreground">Must use HTTPS. If provided, certificates are fetched automatically.</p>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div className="space-y-2">
                <Label>Email attribute</Label>
                <Input value={samlForm.email_attribute_name} onChange={e => setSamlForm(f => ({ ...f, email_attribute_name: e.target.value }))} placeholder="email" />
              </div>
              <div className="space-y-2">
                <Label>Name attribute <span className="text-xs text-muted-foreground">(optional)</span></Label>
                <Input value={samlForm.name_attribute_name} onChange={e => setSamlForm(f => ({ ...f, name_attribute_name: e.target.value }))} placeholder="displayName" />
              </div>
            </div>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium">JIT provisioning</p>
                <p className="text-xs text-muted-foreground">Automatically create users on first login</p>
              </div>
              <Switch checked={samlForm.jit_provisioning} onCheckedChange={v => setSamlForm(f => ({ ...f, jit_provisioning: v }))} />
            </div>
            <div className="flex items-center justify-between">
              <Label>Active</Label>
              <Switch checked={samlForm.active} onCheckedChange={v => setSamlForm(f => ({ ...f, active: v }))} />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAddSamlOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={samlSaving}>{samlSaving ? 'Adding…' : 'Add IdP'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  );
}
