import { useEffect, useState } from 'react';
import { User, Shield, Key, Copy, Check, RefreshCw, Eye, EyeOff } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Alert } from '@/components/ui/alert';
import { Skeleton } from '@/components/ui/skeleton';
import { Separator } from '@/components/ui/separator';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { getMe, updateMe, changePassword, getMfaStatus, setupTotp, confirmTotp, regenerateBackupCodes } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface Me {
  id: string; username: string; discriminator: string; email: string;
  display_name: string | null; email_verified: boolean; totp_enabled: boolean;
  last_login_at: string | null; roles: string[]; org_id: string; project_id: string;
}
interface MfaStatus { totp_enabled: boolean; backup_codes_remaining: number; }

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <Button variant="outline" size="sm" onClick={() => { navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 2000); }}>
      {copied ? <><Check className="h-3 w-3" />Copied</> : <><Copy className="h-3 w-3" />Copy</>}
    </Button>
  );
}

// ── Profile tab ───────────────────────────────────────────────────
function ProfileTab({ me, onUpdated }: { me: Me; onUpdated: () => void }) {
  const [displayName, setDisplayName] = useState(me.display_name ?? '');
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      await updateMe({ display_name: displayName || undefined });
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
      onUpdated();
    } finally { setSaving(false); }
  };

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Identity</CardTitle>
          <CardDescription>Your account identifier — these cannot be changed.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-1">
              <Label className="text-xs text-muted-foreground">Username</Label>
              <p className="font-mono text-sm font-medium">{me.username}<span className="text-muted-foreground">#{me.discriminator}</span></p>
            </div>
            <div className="space-y-1">
              <Label className="text-xs text-muted-foreground">Email</Label>
              <div className="flex items-center gap-2">
                <p className="text-sm">{me.email}</p>
                {me.email_verified
                  ? <Badge variant="success" className="text-xs">Verified</Badge>
                  : <Badge variant="secondary" className="text-xs">Unverified</Badge>
                }
              </div>
            </div>
          </div>
          <Separator />
          <div className="space-y-1">
            <Label className="text-xs text-muted-foreground">Roles</Label>
            <div className="flex flex-wrap gap-1 mt-1">
              {me.roles.length === 0
                ? <span className="text-sm text-muted-foreground">No roles</span>
                : me.roles.map(r => <Badge key={r} variant="secondary" className="text-xs font-mono">{r}</Badge>)
              }
            </div>
          </div>
          {me.last_login_at && (
            <>
              <Separator />
              <div className="space-y-1">
                <Label className="text-xs text-muted-foreground">Last login</Label>
                <p className="text-sm">{new Date(me.last_login_at).toLocaleString()}</p>
              </div>
            </>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Display Name</CardTitle>
          <CardDescription>Shown instead of your username in some views.</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSave} className="flex gap-3 items-end">
            <div className="flex-1 space-y-2">
              <Label htmlFor="display-name">Display name</Label>
              <Input
                id="display-name"
                value={displayName}
                onChange={e => setDisplayName(e.target.value)}
                placeholder="e.g. John Doe"
              />
            </div>
            <Button type="submit" disabled={saving}>
              {saved ? <><Check className="h-4 w-4" />Saved</> : saving ? 'Saving…' : 'Save'}
            </Button>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}

// ── Security tab ──────────────────────────────────────────────────
function SecurityTab() {
  const [form, setForm] = useState({ current: '', next: '', confirm: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState(false);
  const [showCurrent, setShowCurrent] = useState(false);
  const [showNext, setShowNext] = useState(false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    if (form.next !== form.confirm) { setError('New passwords do not match.'); return; }
    if (form.next.length < 8) { setError('Password must be at least 8 characters.'); return; }
    setSaving(true);
    try {
      const res = await changePassword({ current_password: form.current, new_password: form.next });
      if (res.error === 'invalid_current_password') { setError('Current password is incorrect.'); return; }
      setSuccess(true);
      setForm({ current: '', next: '', confirm: '' });
      setTimeout(() => setSuccess(false), 4000);
    } catch { setError('Failed to change password. Please try again.'); }
    finally { setSaving(false); }
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Change Password</CardTitle>
        <CardDescription>Your password must be at least 8 characters.</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit} className="space-y-4 max-w-sm">
          {error && <Alert variant="destructive" className="text-sm py-2 px-3">{error}</Alert>}
          {success && <Alert className="text-sm py-2 px-3 border-green-500 text-green-700">Password changed successfully.</Alert>}
          <div className="space-y-2">
            <Label>Current password</Label>
            <div className="relative">
              <Input type={showCurrent ? 'text' : 'password'} value={form.current} onChange={e => setForm(f => ({ ...f, current: e.target.value }))} required />
              <Button type="button" variant="ghost" size="icon" className="absolute right-0 top-0 h-full px-3" onClick={() => setShowCurrent(v => !v)}>
                {showCurrent ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </Button>
            </div>
          </div>
          <div className="space-y-2">
            <Label>New password</Label>
            <div className="relative">
              <Input type={showNext ? 'text' : 'password'} value={form.next} onChange={e => setForm(f => ({ ...f, next: e.target.value }))} required />
              <Button type="button" variant="ghost" size="icon" className="absolute right-0 top-0 h-full px-3" onClick={() => setShowNext(v => !v)}>
                {showNext ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </Button>
            </div>
          </div>
          <div className="space-y-2">
            <Label>Confirm new password</Label>
            <Input type="password" value={form.confirm} onChange={e => setForm(f => ({ ...f, confirm: e.target.value }))} required />
          </div>
          <Button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Change Password'}</Button>
        </form>
      </CardContent>
    </Card>
  );
}

// ── MFA tab ───────────────────────────────────────────────────────
function MfaTab() {
  const [status, setStatus] = useState<MfaStatus | null>(null);
  const [loading, setLoading] = useState(true);

  // TOTP setup flow
  const [setupData, setSetupData] = useState<{ otpauth_url: string; secret: string } | null>(null);
  const [setupCode, setSetupCode] = useState('');
  const [setupSaving, setSetupSaving] = useState(false);
  const [setupError, setSetupError] = useState('');
  const [backupCodes, setBackupCodes] = useState<string[]>([]);

  // Backup code regen
  const [regenOpen, setRegenOpen] = useState(false);
  const [regenCodes, setRegenCodes] = useState<string[]>([]);

  const load = () => {
    setLoading(true);
    getMfaStatus().then(setStatus).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const handleStartSetup = async () => {
    setSetupError('');
    const data = await setupTotp();
    setSetupData(data);
  };

  const handleConfirmSetup = async (e: React.FormEvent) => {
    e.preventDefault();
    setSetupError('');
    setSetupSaving(true);
    try {
      const res = await confirmTotp({ code: setupCode });
      if (res.error) { setSetupError('Invalid code. Please try again.'); return; }
      setBackupCodes(res.backup_codes ?? []);
      setSetupData(null);
      setSetupCode('');
      load();
    } finally { setSetupSaving(false); }
  };

  const handleRegen = async () => {
    const res = await regenerateBackupCodes();
    setRegenCodes(res.backup_codes ?? []);
    setRegenOpen(false);
    load();
  };

  if (loading) return <Skeleton className="h-40 rounded-xl" />;

  return (
    <div className="space-y-4">
      {/* TOTP card */}
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <div>
              <CardTitle className="text-base">Authenticator App (TOTP)</CardTitle>
              <CardDescription className="mt-1">Use an app like Google Authenticator or Authy.</CardDescription>
            </div>
            {status?.totp_enabled
              ? <Badge variant="success">Enabled</Badge>
              : <Badge variant="secondary">Disabled</Badge>
            }
          </div>
        </CardHeader>
        {!status?.totp_enabled && (
          <CardContent>
            {!setupData ? (
              <Button onClick={handleStartSetup}><Shield className="h-4 w-4" />Set up TOTP</Button>
            ) : (
              <div className="space-y-4 max-w-sm">
                <div className="rounded-lg bg-muted p-4 space-y-3">
                  <p className="text-sm font-medium">1. Open your authenticator app and add a new account manually.</p>
                  <div className="space-y-1">
                    <Label className="text-xs text-muted-foreground">Secret key</Label>
                    <div className="flex items-center gap-2">
                      <code className="text-xs font-mono bg-background rounded px-2 py-1 break-all flex-1">{setupData.secret}</code>
                      <CopyButton text={setupData.secret} />
                    </div>
                  </div>
                  <div className="space-y-1">
                    <Label className="text-xs text-muted-foreground">Or open in authenticator app</Label>
                    <a href={setupData.otpauth_url} className="text-xs text-primary underline break-all">Open authenticator link</a>
                  </div>
                </div>
                <p className="text-sm font-medium">2. Enter the 6-digit code from your app to confirm.</p>
                <form onSubmit={handleConfirmSetup} className="flex gap-2">
                  <Input
                    value={setupCode}
                    onChange={e => setSetupCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
                    placeholder="000000"
                    maxLength={6}
                    className="font-mono w-32 text-center text-lg tracking-widest"
                    required
                  />
                  <Button type="submit" disabled={setupSaving || setupCode.length !== 6}>
                    {setupSaving ? 'Verifying…' : 'Confirm'}
                  </Button>
                  <Button type="button" variant="outline" onClick={() => { setSetupData(null); setSetupCode(''); }}>Cancel</Button>
                </form>
                {setupError && <p className="text-sm text-destructive">{setupError}</p>}
              </div>
            )}
          </CardContent>
        )}
      </Card>

      {/* Backup codes shown after setup */}
      {backupCodes.length > 0 && (
        <Card className="border-amber-500/50 bg-amber-50 dark:bg-amber-950/20">
          <CardHeader>
            <CardTitle className="text-base text-amber-700 dark:text-amber-400">Save your backup codes</CardTitle>
            <CardDescription>Each code can be used once if you lose access to your authenticator. Store them somewhere safe.</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="grid grid-cols-4 gap-2 mb-3">
              {backupCodes.map(c => <code key={c} className="text-xs font-mono bg-background rounded px-2 py-1 text-center">{c}</code>)}
            </div>
            <CopyButton text={backupCodes.join('\n')} />
          </CardContent>
        </Card>
      )}

      {/* Backup codes status (when TOTP enabled) */}
      {status?.totp_enabled && (
        <Card>
          <CardHeader>
            <div className="flex items-center justify-between">
              <div>
                <CardTitle className="text-base">Backup Codes</CardTitle>
                <CardDescription>{status.backup_codes_remaining} code{status.backup_codes_remaining !== 1 ? 's' : ''} remaining.</CardDescription>
              </div>
              <Button variant="outline" size="sm" onClick={() => setRegenOpen(true)}>
                <RefreshCw className="h-4 w-4" />Regenerate
              </Button>
            </div>
          </CardHeader>
          {regenCodes.length > 0 && (
            <CardContent>
              <div className="grid grid-cols-4 gap-2 mb-3">
                {regenCodes.map(c => <code key={c} className="text-xs font-mono bg-muted rounded px-2 py-1 text-center">{c}</code>)}
              </div>
              <CopyButton text={regenCodes.join('\n')} />
            </CardContent>
          )}
        </Card>
      )}

      <AlertDialog open={regenOpen} onOpenChange={setRegenOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Regenerate backup codes?</AlertDialogTitle>
            <AlertDialogDescription>All existing backup codes will be invalidated. Make sure you save the new ones.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleRegen}>Regenerate</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────
export default function AccountPage() {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);

  const load = () => {
    getMe().then(setMe).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  return (
    <div>
      <PageHeader
        title="My Account"
        description={me ? `${me.username}#${me.discriminator} · ${me.email}` : undefined}
        action={loading ? undefined : (
          <div className="flex items-center gap-2">
            <User className="h-4 w-4 text-muted-foreground" />
            {me && <span className="text-sm text-muted-foreground font-mono">{me.id.slice(0, 8)}…</span>}
          </div>
        )}
      />
      <div className="p-6">
        {loading ? (
          <div className="space-y-4">
            <Skeleton className="h-40 rounded-xl" />
            <Skeleton className="h-32 rounded-xl" />
          </div>
        ) : !me ? (
          <p className="text-muted-foreground">Failed to load account.</p>
        ) : (
          <Tabs defaultValue="profile" className="space-y-4">
            <TabsList>
              <TabsTrigger value="profile"><User className="h-4 w-4" />Profile</TabsTrigger>
              <TabsTrigger value="security"><Key className="h-4 w-4" />Security</TabsTrigger>
              <TabsTrigger value="mfa"><Shield className="h-4 w-4" />MFA</TabsTrigger>
            </TabsList>
            <TabsContent value="profile"><ProfileTab me={me} onUpdated={load} /></TabsContent>
            <TabsContent value="security"><SecurityTab /></TabsContent>
            <TabsContent value="mfa"><MfaTab /></TabsContent>
          </Tabs>
        )}
      </div>
    </div>
  );
}
