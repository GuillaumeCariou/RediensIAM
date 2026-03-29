import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { Mail, Save, Trash2, SendHorizonal, Eye, EyeOff, TriangleAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Badge } from '@/components/ui/badge';
import { useAuth } from '@/context/AuthContext';
import {
  getOrgSmtp, upsertOrgSmtp, deleteOrgSmtp, testOrgSmtp,
  adminGetOrgSmtp, adminUpsertOrgSmtp, adminDeleteOrgSmtp, adminTestOrgSmtp,
} from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface SmtpConfig {
  configured: boolean;
  host?: string;
  port?: number;
  start_tls?: boolean;
  username?: string;
  from_address?: string;
  from_name?: string;
  updated_at?: string;
}

interface FormState {
  host: string;
  port: string;
  start_tls: boolean;
  username: string;
  password: string;
  from_address: string;
  from_name: string;
}

const EMPTY_FORM: FormState = {
  host: '',
  port: '587',
  start_tls: true,
  username: '',
  password: '',
  from_address: '',
  from_name: '',
};

export default function OrgEmail() {
  const { id } = useParams<{ id?: string }>();
  const { isSuperAdmin } = useAuth();
  const isAdmin = isSuperAdmin && !!id;

  const [config, setConfig] = useState<SmtpConfig | null>(null);
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const [editing, setEditing] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<{ ok: boolean; msg: string } | null>(null);
  const [error, setError] = useState('');

  const fetchConfig = async () => {
    try {
      const data: SmtpConfig = isAdmin ? await adminGetOrgSmtp(id) : await getOrgSmtp();
      setConfig(data);
      if (data.configured) {
        setForm({
          host:         data.host ?? '',
          port:         String(data.port ?? 587),
          start_tls:    data.start_tls ?? true,
          username:     data.username ?? '',
          password:     '',
          from_address: data.from_address ?? '',
          from_name:    data.from_name ?? '',
        });
      } else {
        setForm(EMPTY_FORM);
      }
    } catch {
      setError('Failed to load SMTP configuration.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { fetchConfig(); }, []);

  const handleSave = async () => {
    setSaving(true);
    setError('');
    try {
      const body = {
        host:         form.host,
        port:         Number(form.port),
        start_tls:    form.start_tls,
        username:     form.username || undefined,
        password:     form.password || undefined,
        from_address: form.from_address,
        from_name:    form.from_name,
      };
      if (isAdmin) await adminUpsertOrgSmtp(id, body);
      else await upsertOrgSmtp(body);
      await fetchConfig();
      setEditing(false);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Failed to save SMTP configuration.');
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async () => {
    if (!confirm('Remove SMTP configuration and revert to global SMTP?')) return;
    try {
      if (isAdmin) await adminDeleteOrgSmtp(id);
      else await deleteOrgSmtp();
      await fetchConfig();
      setEditing(false);
    } catch {
      setError('Failed to remove SMTP configuration.');
    }
  };

  const handleTest = async () => {
    setTesting(true);
    setTestResult(null);
    try {
      const res = isAdmin ? await adminTestOrgSmtp(id) : await testOrgSmtp();
      setTestResult({ ok: true, msg: `Test email sent to ${res.to}` });
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Test failed';
      setTestResult({ ok: false, msg });
    } finally {
      setTesting(false);
    }
  };

  const set = (k: keyof FormState, v: string | boolean) =>
    setForm(f => ({ ...f, [k]: v }));

  if (loading) return (
    <div>
      <PageHeader title="Email Settings" />
      <div className="p-6 space-y-4">
        {Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-16 rounded-lg" />)}
      </div>
    </div>
  );

  return (
    <div>
      <PageHeader
        title="Email Settings"
        description="Configure the SMTP relay used to send verification emails for this organisation"
      />

      <div className="p-6 max-w-2xl space-y-6">

        {/* ── Status banner ── */}
        {!config?.configured && !editing && (
          <Card className="border-dashed">
            <CardContent className="pt-6">
              <div className="flex items-start gap-3">
                <TriangleAlert className="h-5 w-5 text-muted-foreground mt-0.5 shrink-0" />
                <div>
                  <p className="font-medium text-sm">Using global SMTP</p>
                  <p className="text-sm text-muted-foreground mt-0.5">
                    No organisation-level SMTP is configured. Emails will be sent using the global relay
                    set by the system administrator.
                  </p>
                  <Button size="sm" className="mt-3" onClick={() => setEditing(true)}>
                    Configure custom SMTP
                  </Button>
                </div>
              </div>
            </CardContent>
          </Card>
        )}

        {config?.configured && !editing && (
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <CardTitle className="text-base">Custom SMTP</CardTitle>
                  <Badge variant="secondary" className="text-xs">Active</Badge>
                </div>
                <div className="flex gap-2">
                  <Button size="sm" variant="outline" onClick={handleTest} disabled={testing}>
                    <SendHorizonal className="h-3.5 w-3.5" />
                    {testing ? 'Sending…' : 'Test'}
                  </Button>
                  <Button size="sm" onClick={() => setEditing(true)}>Edit</Button>
                </div>
              </div>
              <CardDescription>
                {config.host}:{config.port} · {config.start_tls ? 'StartTLS' : 'No TLS'}
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-2 text-sm">
              <div className="grid grid-cols-2 gap-x-4 gap-y-1.5">
                <span className="text-muted-foreground">From address</span>
                <span className="font-mono">{config.from_address}</span>
                <span className="text-muted-foreground">From name</span>
                <span>{config.from_name}</span>
                {config.username && (
                  <>
                    <span className="text-muted-foreground">Username</span>
                    <span className="font-mono">{config.username}</span>
                  </>
                )}
              </div>
              {testResult && (
                <p className={`text-sm mt-2 ${testResult.ok ? 'text-green-600 dark:text-green-400' : 'text-destructive'}`}>
                  {testResult.msg}
                </p>
              )}
            </CardContent>
          </Card>
        )}

        {/* ── Edit form ── */}
        {editing && (
          <Card>
            <CardHeader>
              <CardTitle className="text-base flex items-center gap-2">
                <Mail className="h-4 w-4" />
                {config?.configured ? 'Edit SMTP Configuration' : 'Configure SMTP'}
              </CardTitle>
              <CardDescription>
                Emails will be sent using this relay. Leave password blank to keep the existing one.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">

              {/* Connection */}
              <div className="space-y-3">
                <p className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">Connection</p>
                <div className="grid grid-cols-[1fr_100px] gap-3">
                  <div className="space-y-1.5">
                    <Label>Host</Label>
                    <Input value={form.host} onChange={e => set('host', e.target.value)} placeholder="smtp.example.com" />
                  </div>
                  <div className="space-y-1.5">
                    <Label>Port</Label>
                    <Input type="number" value={form.port} onChange={e => set('port', e.target.value)} placeholder="587" />
                  </div>
                </div>
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-sm font-medium">STARTTLS</p>
                    <p className="text-xs text-muted-foreground">Use STARTTLS negotiation (port 587). Disable for SSL on port 465.</p>
                  </div>
                  <Switch checked={form.start_tls} onCheckedChange={v => set('start_tls', v)} />
                </div>
              </div>

              {/* Auth */}
              <div className="space-y-3">
                <p className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">Authentication</p>
                <div className="space-y-1.5">
                  <Label>Username</Label>
                  <Input value={form.username} onChange={e => set('username', e.target.value)} placeholder="noreply@example.com" autoComplete="off" />
                </div>
                <div className="space-y-1.5">
                  <Label>Password</Label>
                  <div className="relative">
                    <Input
                      type={showPassword ? 'text' : 'password'}
                      value={form.password}
                      onChange={e => set('password', e.target.value)}
                      placeholder={config?.configured ? '(unchanged)' : 'SMTP password'}
                      autoComplete="new-password"
                      className="pr-10"
                    />
                    <button
                      type="button"
                      onClick={() => setShowPassword(v => !v)}
                      className="absolute right-2.5 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
                    >
                      {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                    </button>
                  </div>
                </div>
              </div>

              {/* From */}
              <div className="space-y-3">
                <p className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">From</p>
                <div className="space-y-1.5">
                  <Label>From address</Label>
                  <Input value={form.from_address} onChange={e => set('from_address', e.target.value)} placeholder="noreply@yourorg.com" />
                </div>
                <div className="space-y-1.5">
                  <Label>From name</Label>
                  <Input value={form.from_name} onChange={e => set('from_name', e.target.value)} placeholder="Acme Platform" />
                  <p className="text-xs text-muted-foreground">Can be overridden per project in project Authentication settings.</p>
                </div>
              </div>

              {error && <p className="text-sm text-destructive">{error}</p>}

              <div className="flex items-center justify-between pt-2 border-t">
                <div className="flex gap-2">
                  {config?.configured && (
                    <Button variant="destructive" size="sm" onClick={handleDelete}>
                      <Trash2 className="h-3.5 w-3.5" />
                      Reset to global
                    </Button>
                  )}
                </div>
                <div className="flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => { setEditing(false); setError(''); }}>
                    Cancel
                  </Button>
                  <Button size="sm" onClick={handleSave} disabled={saving}>
                    <Save className="h-3.5 w-3.5" />
                    {saving ? 'Saving…' : 'Save'}
                  </Button>
                </div>
              </div>
            </CardContent>
          </Card>
        )}

      </div>
    </div>
  );
}
