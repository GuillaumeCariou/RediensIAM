import { useEffect, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { Save, Upload, X, Plus, Sun, Moon } from 'lucide-react';
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
import { getProjectInfo, updateProject, listRoles } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface Provider {
  id: string;
  type: 'google' | 'github' | 'gitlab' | 'facebook' | 'oidc';
  label: string;
  client_id: string;
  client_secret?: string;
  issuer_url?: string;
  logo_url?: string;
  enabled: boolean;
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
  primary_color: '#1a56db',
  background_color: '#f9fafb',
  surface_color: '#ffffff',
  text_color: '#111827',
  border_radius: '8',
  font_family: 'Inter',
  logo_url: '',
  custom_css: '',
  providers: [],
  hydra_local_login: true,
};

function nanoid() {
  return Math.random().toString(36).slice(2, 10);
}

function ColorRow({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
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

function LogoUpload({ value, onChange, label = 'Logo' }: { value?: string; onChange: (v: string) => void; label?: string }) {
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
      <div
        onDragOver={e => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        onDrop={e => { e.preventDefault(); setDragOver(false); const f = e.dataTransfer.files[0]; if (f) handle(f); }}
        className={`relative border-2 border-dashed rounded-lg p-4 text-center transition-colors ${dragOver ? 'border-primary bg-primary/5' : 'border-border'}`}
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
                browse
                <input type="file" accept="image/*" className="hidden" onChange={e => { if (e.target.files?.[0]) handle(e.target.files[0]); }} />
              </label>
            </p>
          </div>
        )}
      </div>
      <Input value={value?.startsWith('data:') ? '' : (value ?? '')} onChange={e => onChange(e.target.value)}
        placeholder="https://cdn.example.com/logo.png" />
    </div>
  );
}

// ── Preview ──────────────────────────────────────────────────────────────────

type PreviewMode = 'login' | 'register' | 'verify';

interface PreviewProps {
  theme: Theme;
  dark: boolean;
  mode: PreviewMode;
  allowSelfReg: boolean;
  emailVerif: boolean;
  smsVerif: boolean;
}

function AuthPreview({ theme, dark, mode, allowSelfReg, emailVerif, smsVerif }: PreviewProps) {
  const radius = `${theme.border_radius ?? 8}px`;
  const bg = dark ? '#0f172a' : (theme.background_color ?? '#f9fafb');
  const surface = dark ? '#1e293b' : (theme.surface_color ?? '#ffffff');
  const text = dark ? '#f1f5f9' : (theme.text_color ?? '#111827');
  const muted = dark ? 'rgba(241,245,249,0.45)' : `color-mix(in srgb, ${theme.text_color ?? '#111827'} 50%, transparent)`;
  const border = dark ? '#334155' : '#d1d5db';
  const inputBg = dark ? '#0f172a' : `color-mix(in srgb, ${theme.surface_color ?? '#fff'} 80%, #f3f4f6)`;
  const primary = theme.primary_color ?? '#1a56db';
  const effectiveFont = theme.font_family;

  const enabledProviders = (theme.providers ?? []).filter(p => p.enabled);
  const Field = () => <div className="h-8 border" style={{ borderRadius: radius, borderColor: border, background: inputBg }} />;
  const Btn = ({ label }: { label: string }) => (
    <div className="h-8 flex items-center justify-center text-white text-xs font-semibold"
      style={{ background: primary, borderRadius: radius }}>{label}</div>
  );

  const Logo = () => theme.logo_url ? (
    <div className="text-center">
      <img src={theme.logo_url} alt="Logo" className="h-10 mx-auto object-contain"
        onError={e => (e.currentTarget.style.display = 'none')} />
    </div>
  ) : null;

  const Providers = () => enabledProviders.length > 0 ? (
    <div className="space-y-2">
      {enabledProviders.map(p => {
        const icon = p.logo_url || PROVIDER_ICONS[p.type];
        return (
          <div key={p.id} className="flex items-center justify-center gap-2 w-full px-3 py-2 text-xs font-medium border cursor-default select-none"
            style={{ borderRadius: radius, borderColor: border, background: surface, color: text }}>
            {icon && <img src={icon} alt={p.type} className="h-4 w-4 object-contain" />}
            {p.label}
          </div>
        );
      })}
      {(theme.hydra_local_login ?? true) && (
        <div className="flex items-center gap-2 my-1">
          <div className="flex-1 h-px" style={{ background: border }} />
          <span className="text-xs" style={{ color: muted }}>or</span>
          <div className="flex-1 h-px" style={{ background: border }} />
        </div>
      )}
    </div>
  ) : null;

  let content: React.ReactNode;

  if (mode === 'login') {
    content = (
      <>
        <Logo />
        <h1 className="text-lg font-bold text-center">Sign in</h1>
        {(theme.hydra_local_login ?? true) && (
          <p className="text-xs text-center" style={{ color: muted }}>Enter your credentials to continue</p>
        )}
        <Providers />
        {(theme.hydra_local_login ?? true) && (
          <div className="space-y-2">
            <Field /><Field />
            <Btn label="Sign in" />
          </div>
        )}
        {allowSelfReg && (
          <p className="text-xs text-center" style={{ color: muted }}>
            Don't have an account?{' '}
            <span style={{ color: primary, textDecoration: 'underline', cursor: 'pointer' }}>Sign up</span>
          </p>
        )}
      </>
    );
  } else if (mode === 'register') {
    content = (
      <>
        <Logo />
        <h1 className="text-lg font-bold text-center">Create account</h1>
        <p className="text-xs text-center" style={{ color: muted }}>Fill in your details to get started</p>
        <Providers />
        {(theme.hydra_local_login ?? true) && (
          <div className="space-y-2">
            <Field /><Field /><Field />
            {smsVerif && <Field />}
            <Btn label="Create account" />
          </div>
        )}
        <p className="text-xs text-center" style={{ color: muted }}>
          Already have an account?{' '}
          <span style={{ color: primary, textDecoration: 'underline', cursor: 'pointer' }}>Sign in</span>
        </p>
      </>
    );
  } else {
    const channel = emailVerif ? 'email' : smsVerif ? 'phone number' : 'contact';
    content = (
      <>
        <Logo />
        <h1 className="text-lg font-bold text-center">Verify your {emailVerif ? 'email' : 'phone'}</h1>
        <p className="text-xs text-center" style={{ color: muted }}>Enter the 6-digit code sent to your {channel}</p>
        <div className="space-y-2">
          <div className="h-10 border flex items-center justify-center text-sm font-mono tracking-[0.4em]"
            style={{ borderRadius: radius, borderColor: border, background: inputBg, color: muted }}>
            • • • • • •
          </div>
          <Btn label="Verify" />
        </div>
        <p className="text-xs text-center" style={{ color: muted }}>
          Didn't receive a code?{' '}
          <span style={{ color: primary, textDecoration: 'underline', cursor: 'pointer' }}>Resend</span>
        </p>
      </>
    );
  }

  return (
    <div className="rounded-xl flex items-center justify-center min-h-[520px] p-6 transition-colors"
      style={{ background: bg }}>
      <div className="w-72 rounded-xl p-7 shadow-xl space-y-4 transition-colors"
        style={{ background: surface, fontFamily: effectiveFont, borderRadius: radius, color: text }}>
        {content}
      </div>
    </div>
  );
}

// ── Main component ────────────────────────────────────────────────────────────

interface Role { id: string; name: string; rank: number; }

export default function Authentication() {
  const { projectId } = useProjectContext();
  const [theme, setTheme] = useState<Theme>(DEFAULT_THEME);
  const [customFont, setCustomFont] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [previewDark, setPreviewDark] = useState(false);
  const [previewMode, setPreviewMode] = useState<PreviewMode>('login');

  // Project-level settings
  const [allowSelfReg, setAllowSelfReg] = useState(false);
  const [emailVerif, setEmailVerif] = useState(false);
  const [smsVerif, setSmsVerif] = useState(false);
  const [allowedDomains, setAllowedDomains] = useState('');
  const [defaultRoleId, setDefaultRoleId] = useState<string | null>(null);
  const [roles, setRoles] = useState<Role[]>([]);

  useEffect(() => {
    if (!projectId) { setLoading(false); return; }
    Promise.all([
      getProjectInfo(projectId).then(p => {
        if (p.login_theme) {
          const t = { ...DEFAULT_THEME, ...p.login_theme };
          if (t.providers) {
            t.providers = t.providers.map((pr: Provider) => ({ ...pr, id: pr.id ?? nanoid() }));
          }
          setTheme(t);
          if (t.font_family && !FONT_OPTIONS.includes(t.font_family)) setCustomFont(t.font_family);
        }
        setAllowSelfReg(p.allow_self_registration ?? false);
        setEmailVerif(p.email_verification_enabled ?? false);
        setSmsVerif(p.sms_verification_enabled ?? false);
        setAllowedDomains((p.allowed_email_domains ?? []).join(', '));
        setDefaultRoleId(p.default_role_id ?? null);
      }),
      listRoles(projectId).then(r => setRoles((r.roles ?? r ?? []).sort((a: Role, b: Role) => a.rank - b.rank))),
    ]).catch(console.error).finally(() => setLoading(false));
  }, [projectId]);

  const set = <K extends keyof Theme>(k: K, v: Theme[K]) => setTheme(t => ({ ...t, [k]: v }));

  const handleSave = async () => {
    setSaving(true);
    try {
      const domains = allowedDomains.split(',').map(d => d.trim()).filter(Boolean);
      const body: Parameters<typeof updateProject>[1] = {
        login_theme: theme as Record<string, unknown>,
        allow_self_registration: allowSelfReg,
        email_verification_enabled: emailVerif,
        sms_verification_enabled: smsVerif,
        allowed_email_domains: domains,
      };
      if (defaultRoleId) body.default_role_id = defaultRoleId;
      else body.clear_default_role = true;
      await updateProject(projectId, body);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } finally { setSaving(false); }
  };

  // ── Builtin provider helpers ──
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
  const updateBuiltin = (type: Provider['type'], patch: Partial<Provider>) => {
    set('providers', (theme.providers ?? []).map(p => p.id === type ? { ...p, ...patch } : p));
  };

  // ── Custom OIDC helpers ──
  const customOidcs = (theme.providers ?? []).filter(p => p.type === 'oidc' && p.id !== 'oidc');
  const addOidc = () => {
    const id = nanoid();
    set('providers', [...(theme.providers ?? []), {
      id, type: 'oidc', label: 'Continue with SSO',
      client_id: '', issuer_url: '', logo_url: '', enabled: true,
    }]);
  };
  const updateOidc = (id: string, patch: Partial<Provider>) => {
    set('providers', (theme.providers ?? []).map(p => p.id === id ? { ...p, ...patch } : p));
  };
  const removeOidc = (id: string) => {
    set('providers', (theme.providers ?? []).filter(p => p.id !== id));
  };

  if (loading) return (
    <div>
      <PageHeader title="Authentication" />
      <div className="p-6 space-y-4">{Array.from({ length: 4 }).map((_, i) => <Skeleton key={i} className="h-12 rounded-lg" />)}</div>
    </div>
  );

  return (
    <div>
      <PageHeader
        title="Authentication"
        description="Configure login appearance, providers, registration, and verification"
        action={
          <Button onClick={handleSave} disabled={saving}>
            <Save className="h-4 w-4" />{saving ? 'Saving…' : saved ? 'Saved!' : 'Save Changes'}
          </Button>
        }
      />

      <div className="p-6 grid grid-cols-1 xl:grid-cols-[1fr_380px] gap-6 items-start">
        {/* ── Left: config tabs ── */}
        <div>
          <Tabs defaultValue="visual">
            <TabsList>
              <TabsTrigger value="visual">Appearance</TabsTrigger>
              <TabsTrigger value="providers">Providers</TabsTrigger>
              <TabsTrigger value="registration">Registration</TabsTrigger>
              <TabsTrigger value="verification">Verification</TabsTrigger>
              <TabsTrigger value="css">Custom CSS</TabsTrigger>
            </TabsList>

            {/* ── Visual ── */}
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

            {/* ── Providers ── */}
            <TabsContent value="providers" className="mt-6 space-y-4">
              {/* Hydra local login */}
              <Card>
                <CardContent className="pt-4">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="font-medium text-sm">Password login (Hydra)</p>
                      <p className="text-xs text-muted-foreground">Email/username + password form</p>
                    </div>
                    <Switch
                      checked={theme.hydra_local_login ?? true}
                      onCheckedChange={v => set('hydra_local_login', v)}
                    />
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
                          {PROVIDER_ICONS[type] && (
                            <img src={PROVIDER_ICONS[type]} alt={type} className="h-5 w-5 object-contain" />
                          )}
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
                          <div className="space-y-1">
                            <Label className="text-xs">Client Secret</Label>
                            <Input type="password" value={p?.client_secret ?? ''} onChange={e => updateBuiltin(type, { client_secret: e.target.value })} placeholder="OAuth2 client secret" autoComplete="new-password" />
                          </div>
                          <LogoUpload value={p?.logo_url} onChange={v => updateBuiltin(type, { logo_url: v })} label="Custom logo (optional)" />
                        </div>
                      )}
                    </CardContent>
                  </Card>
                );
              })}

              {/* Custom OIDC providers */}
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
                      <div className="space-y-1">
                        <Label className="text-xs">Client Secret</Label>
                        <Input type="password" value={p.client_secret ?? ''} onChange={e => updateOidc(p.id, { client_secret: e.target.value })} placeholder="OAuth2 client secret" autoComplete="new-password" />
                      </div>
                      <LogoUpload value={p.logo_url} onChange={v => updateOidc(p.id, { logo_url: v })} label="Logo" />
                    </div>
                  </CardContent>
                </Card>
              ))}
            </TabsContent>

            {/* ── Registration ── */}
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
                </CardContent>
              </Card>

              <Card>
                <CardHeader>
                  <CardTitle className="text-base">Allowed Email Domains</CardTitle>
                  <CardDescription>Restrict registration to specific email domains. Leave blank to allow any domain.</CardDescription>
                </CardHeader>
                <CardContent className="space-y-2">
                  <Input
                    value={allowedDomains}
                    onChange={e => setAllowedDomains(e.target.value)}
                    placeholder="example.com, company.io"
                  />
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
                  <CardTitle className="text-base">Default Role</CardTitle>
                  <CardDescription>Role automatically assigned when a user registers or signs in via social login for the first time.</CardDescription>
                </CardHeader>
                <CardContent>
                  <Select value={defaultRoleId ?? '__none__'} onValueChange={v => setDefaultRoleId(v === '__none__' ? null : v)}>
                    <SelectTrigger className="w-64 bg-background">
                      <SelectValue placeholder="No default role" />
                    </SelectTrigger>
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

            {/* ── Verification ── */}
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
            </TabsContent>

            {/* ── Custom CSS ── */}
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
                <button
                  key={m}
                  onClick={() => setPreviewMode(m)}
                  className={`px-3 py-1.5 capitalize transition-colors ${
                    previewMode === m
                      ? 'bg-primary text-primary-foreground'
                      : 'bg-background text-muted-foreground hover:text-foreground'
                  }`}
                >
                  {m}
                </button>
              ))}
            </div>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setPreviewDark(d => !d)}
              title="Toggle dark/light preview"
            >
              {previewDark ? <Sun className="h-4 w-4" /> : <Moon className="h-4 w-4" />}
            </Button>
          </div>
          <div className="rounded-xl border overflow-hidden">
            <AuthPreview
              theme={theme}
              dark={previewDark}
              mode={previewMode}
              allowSelfReg={allowSelfReg}
              emailVerif={emailVerif}
              smsVerif={smsVerif}
            />
          </div>
          <p className="text-xs text-muted-foreground text-center">Approximate preview — actual rendering may differ</p>
        </div>
      </div>

    </div>
  );
}
