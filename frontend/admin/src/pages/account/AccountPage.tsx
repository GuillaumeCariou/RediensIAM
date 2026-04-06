import { useEffect, useState } from 'react';
import { User, Shield, Key, Copy, Check, RefreshCw, Eye, EyeOff, MonitorSmartphone, LogOut, Fingerprint, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Alert } from '@/components/ui/alert';
import { Skeleton } from '@/components/ui/skeleton';
import { Separator } from '@/components/ui/separator';
import { Switch } from '@/components/ui/switch';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { getMe, updateMe, changePassword, getMfaStatus, setupTotp, confirmTotp, regenerateBackupCodes, getSessions, revokeSession, revokeAllSessions, setupPhone, verifyPhone, removePhone, beginWebAuthnRegistration, completeWebAuthnRegistration, listWebAuthnCredentials, deleteWebAuthnCredential, getSocialAccounts, unlinkSocialAccount } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface Me {
  id: string; username: string; discriminator: string; email: string;
  display_name: string | null; email_verified: boolean; totp_enabled: boolean;
  last_login_at: string | null; roles: string[]; org_id: string; project_id: string;
  new_device_alerts_enabled: boolean;
}
interface MfaStatus { totp_enabled: boolean; backup_codes_remaining: number; phone_verified: boolean; }

function CopyButton({ text }: Readonly<{ text: string }>) {
  const [copied, setCopied] = useState(false);
  return (
    <Button variant="outline" size="sm" onClick={() => { navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 2000); }}>
      {copied ? <><Check className="h-3 w-3" />Copied</> : <><Copy className="h-3 w-3" />Copy</>}
    </Button>
  );
}

// ── Profile tab ───────────────────────────────────────────────────
function ProfileTab({ me, onUpdated }: Readonly<{ me: Me; onUpdated: () => void }>) {
  const [displayName, setDisplayName] = useState(me.display_name ?? '');
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [newDeviceAlerts, setNewDeviceAlerts] = useState(me.new_device_alerts_enabled);

  const handleSave = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true);
    try {
      await updateMe({ display_name: displayName || undefined });
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
      onUpdated();
    } finally { setSaving(false); }
  };

  const handleToggleNewDeviceAlerts = async (value: boolean) => {
    setNewDeviceAlerts(value);
    await updateMe({ new_device_alerts_enabled: value });
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
        <CardContent className="space-y-4">
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
          <div className="flex items-center justify-between pt-4 border-t">
            <div>
              <p className="font-medium text-sm">New device login alerts</p>
              <p className="text-xs text-muted-foreground">
                Receive an email when you log in from a device or location not seen in the last 90 days.
              </p>
            </div>
            <Switch checked={newDeviceAlerts} onCheckedChange={handleToggleNewDeviceAlerts} />
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

// ── Security tab ──────────────────────────────────────────────────
interface SocialAccount { id: string; provider: string; email: string | null; linked_at: string; }

const PROVIDER_LABELS: Record<string, string> = {
  google: 'Google', github: 'GitHub', gitlab: 'GitLab', facebook: 'Facebook',
};

function SecurityTab() {
  const [form, setForm] = useState({ current: '', next: '', confirm: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState(false);
  const [showCurrent, setShowCurrent] = useState(false);
  const [showNext, setShowNext] = useState(false);

  // Linked accounts
  const [linked, setLinked] = useState<SocialAccount[]>([]);
  const [linkedLoading, setLinkedLoading] = useState(true);
  const [unlinkError, setUnlinkError] = useState('');
  const [unlinking, setUnlinking] = useState<string | null>(null);

  const loadLinked = () => {
    setLinkedLoading(true);
    getSocialAccounts().then((d: SocialAccount[]) => setLinked(Array.isArray(d) ? d : [])).catch(console.error).finally(() => setLinkedLoading(false));
  };
  useEffect(loadLinked, []);

  const handleSubmit = async (e: React.SyntheticEvent<HTMLFormElement>) => {
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

  const handleUnlink = async (id: string) => {
    setUnlinkError('');
    setUnlinking(id);
    try {
      const res = await unlinkSocialAccount(id);
      if (res.error === 'cannot_remove_last_auth_method') {
        setUnlinkError('Cannot unlink — this is your only login method. Set a password first.');
        return;
      }
      loadLinked();
    } finally { setUnlinking(null); }
  };

  const linkedProviders = new Set(linked.map(l => l.provider));
  const availableToConnect = Object.keys(PROVIDER_LABELS).filter(p => !linkedProviders.has(p));

  return (
    <div className="space-y-6">
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

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Linked Accounts</CardTitle>
          <CardDescription>Social accounts connected to your profile for sign-in.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {unlinkError && <Alert variant="destructive" className="text-sm py-2 px-3">{unlinkError}</Alert>}
          {(() => {
            if (linkedLoading) return (
            <Skeleton className="h-12 w-full" />
            );
            if (linked.length === 0) return (
            <p className="text-sm text-muted-foreground">No linked accounts.</p>
            );
            return (
            <div className="space-y-2">
              {linked.map(acc => (
                <div key={acc.id} className="flex items-center justify-between rounded-lg border px-4 py-3">
                  <div className="space-y-0.5">
                    <p className="text-sm font-medium">{PROVIDER_LABELS[acc.provider] ?? acc.provider}</p>
                    <p className="text-xs text-muted-foreground">
                      {acc.email ? `${acc.email} · ` : ''}Linked {fmtDate(acc.linked_at)}
                    </p>
                  </div>
                  <Button
                    variant="ghost" size="sm"
                    className="text-destructive hover:text-destructive"
                    disabled={unlinking === acc.id}
                    onClick={() => handleUnlink(acc.id)}
                  >
                    <Trash2 className="h-4 w-4" />
                    {unlinking === acc.id ? 'Unlinking…' : 'Unlink'}
                  </Button>
                </div>
              ))}
            </div>
            );
          })()}

          {availableToConnect.length > 0 && (
            <div className="space-y-2 pt-2 border-t">
              <p className="text-sm text-muted-foreground">Connect a provider</p>
              <div className="flex flex-wrap gap-2">
                {availableToConnect.map(provider => (
                  <Button
                    key={provider}
                    variant="outline"
                    size="sm"
                    onClick={() => { globalThis.location.href = `/auth/oauth2/link/start?provider=${provider}`; }}
                  >
                    {PROVIDER_LABELS[provider]}
                  </Button>
                ))}
              </div>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

// ── Passkeys card ─────────────────────────────────────────────────
interface Passkey { id: string; device_name: string | null; created_at: string; last_used_at: string | null; }

function PasskeysCard() {
  const [creds, setCreds] = useState<Passkey[]>([]);
  const [loading, setLoading] = useState(true);
  const [registering, setRegistering] = useState(false);
  const [deviceName, setDeviceName] = useState('');
  const [error, setError] = useState('');

  const load = () => {
    setLoading(true);
    listWebAuthnCredentials().then(setCreds).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const handleRegister = async () => {
    setError('');
    setRegistering(true);
    try {
      const options = await beginWebAuthnRegistration();
      options.challenge = base64urlToBuffer(options.challenge);
      options.user.id   = base64urlToBuffer(options.user.id);
      if (options.excludeCredentials) {
        options.excludeCredentials = options.excludeCredentials.map((c: { id: string }) => ({
          ...c, id: base64urlToBuffer(c.id)
        }));
      }

      const cred = await navigator.credentials.create({ publicKey: options }) as PublicKeyCredential;
      if (!cred) { setError('No credential created.'); return; }

      const resp = cred.response as AuthenticatorAttestationResponse;
      const body = {
        response: {
          id:       cred.id,
          rawId:    bufferToBase64url(cred.rawId),
          type:     cred.type,
          response: {
            attestationObject: bufferToBase64url(resp.attestationObject),
            clientDataJSON:    bufferToBase64url(resp.clientDataJSON),
          }
        },
        device_name: deviceName || null,
      };

      const res = await completeWebAuthnRegistration(body);
      if (res.error) { setError('Registration failed: ' + res.error); return; }
      setDeviceName('');
      load();
    } catch (e: unknown) {
      if (e instanceof Error && e.name === 'NotAllowedError') {
        setError('Passkey prompt was cancelled.');
      } else {
        setError('Passkey registration failed.');
      }
    } finally {
      setRegistering(false);
    }
  };

  const handleDelete = async (id: string) => {
    await deleteWebAuthnCredential(id);
    load();
  };

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center justify-between">
          <div>
            <CardTitle className="text-base">Passkeys</CardTitle>
            <CardDescription>Sign in with your device fingerprint, face, or security key.</CardDescription>
          </div>
          <Badge variant={creds.length > 0 ? 'success' : 'secondary'}>
            {creds.length > 0 ? `${creds.length} registered` : 'None'}
          </Badge>
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        {(() => {
          if (loading) return (
          <Skeleton className="h-16 w-full" />
          );
          if (creds.length > 0) return (
          <div className="space-y-2">
            {creds.map(c => (
              <div key={c.id} className="flex items-center justify-between rounded-lg border px-4 py-3">
                <div className="flex items-center gap-3">
                  <Fingerprint className="h-4 w-4 text-muted-foreground" />
                  <div>
                    <p className="text-sm font-medium">{c.device_name ?? 'Unnamed passkey'}</p>
                    <p className="text-xs text-muted-foreground">
                      Added {fmtDate(c.created_at)}
                      {c.last_used_at && ` · Last used ${fmtDate(c.last_used_at)}`}
                    </p>
                  </div>
                </div>
                <Button variant="ghost" size="sm" className="text-destructive hover:text-destructive" onClick={() => handleDelete(c.id)}>
                  <Trash2 className="h-4 w-4" />
                </Button>
              </div>
            ))}
          </div>
        ) : null}

        <div className="flex gap-2 items-center">
          <Input
            placeholder="Passkey name (optional)"
            value={deviceName}
            onChange={e => setDeviceName(e.target.value)}
            className="max-w-xs"
          />
          <Button onClick={handleRegister} disabled={registering}>
            <Fingerprint className="h-4 w-4" />
            {registering ? 'Waiting…' : 'Add passkey'}
          </Button>
        </div>

        {error && <p className="text-sm text-destructive">{error}</p>}
      </CardContent>
    </Card>
  );
}

function base64urlToBuffer(b64: string): ArrayBuffer {
  const bin = atob(b64.replaceAll('-', '+').replaceAll('_', '/'));
  const buf = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) buf[i] = bin.codePointAt(i);
  return buf.buffer;
}
function bufferToBase64url(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let str = '';
  for (const b of bytes) str += String.fromCodePoint(b);
  return btoa(str).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
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

  // Phone / SMS MFA setup
  const [phoneInput, setPhoneInput] = useState('');
  const [phoneOtp, setPhoneOtp]     = useState('');
  const [phoneSending, setPhoneSending] = useState(false);
  const [phoneCodeSent, setPhoneCodeSent] = useState(false);
  const [phoneError, setPhoneError] = useState('');
  const [phoneSuccess, setPhoneSuccess] = useState(false);

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

  const handleConfirmSetup = async (e: React.SyntheticEvent<HTMLFormElement>) => {
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

  const handlePhoneSend = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setPhoneError('');
    setPhoneSending(true);
    try {
      await setupPhone(phoneInput);
      setPhoneCodeSent(true);
    } catch { setPhoneError('Failed to send code.'); }
    finally { setPhoneSending(false); }
  };

  const handlePhoneVerify = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setPhoneError('');
    setPhoneSending(true);
    try {
      const res = await verifyPhone(phoneOtp);
      if (res.error) { setPhoneError('Invalid code. Try again.'); return; }
      setPhoneSuccess(true);
      setPhoneCodeSent(false);
      setPhoneInput('');
      setPhoneOtp('');
      load();
    } catch { setPhoneError('Failed to verify code.'); }
    finally { setPhoneSending(false); }
  };

  const handleRemovePhone = async () => {
    await removePhone();
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
                    onChange={e => setSetupCode(e.target.value.replaceAll(/\D/g, '').slice(0, 6))}
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

      {/* Passkeys */}
      <PasskeysCard />

      {/* Phone / SMS MFA */}
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <div>
              <CardTitle className="text-base">SMS Authentication</CardTitle>
              <CardDescription>Use your phone number as a second factor at login.</CardDescription>
            </div>
            {status?.phone_verified
              ? <Badge variant="success">Verified</Badge>
              : <Badge variant="secondary">Not set</Badge>
            }
          </div>
        </CardHeader>
        <CardContent>
          {status?.phone_verified ? (
            <div className="flex items-center gap-3">
              <p className="text-sm text-muted-foreground">Phone number verified and active.</p>
              <Button variant="outline" size="sm" onClick={handleRemovePhone}>Remove</Button>
            </div>
          ) : !phoneCodeSent ? (
            <form onSubmit={handlePhoneSend} className="flex gap-2 items-end max-w-sm">
              <div className="flex-1 space-y-2">
                <Label>Phone number</Label>
                <Input
                  type="tel" placeholder="+1234567890"
                  value={phoneInput} onChange={e => setPhoneInput(e.target.value)} required
                />
              </div>
              <Button type="submit" disabled={phoneSending}>{phoneSending ? 'Sending…' : 'Send code'}</Button>
            </form>
          ) : (
            <form onSubmit={handlePhoneVerify} className="space-y-3 max-w-sm">
              <p className="text-sm text-muted-foreground">Enter the 6-digit code sent to {phoneInput}.</p>
              <div className="flex gap-2 items-center">
                <Input
                  value={phoneOtp} onChange={e => setPhoneOtp(e.target.value.replaceAll(/\D/g, '').slice(0, 6))}
                  placeholder="000000" maxLength={6} className="font-mono w-32 text-center text-lg tracking-widest"
                  required
                />
                <Button type="submit" disabled={phoneSending || phoneOtp.length !== 6}>
                  {phoneSending ? 'Verifying…' : 'Verify'}
                </Button>
                <Button type="button" variant="outline" onClick={() => { setPhoneCodeSent(false); setPhoneOtp(''); }}>
                  Cancel
                </Button>
              </div>
            </form>
          )}
          {phoneError && <p className="text-sm text-destructive mt-2">{phoneError}</p>}
          {phoneSuccess && <p className="text-sm text-green-600 mt-2">Phone number verified successfully.</p>}
        </CardContent>
      </Card>

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

// ── Sessions tab ──────────────────────────────────────────────────
interface Session {
  client_id: string | null;
  client_name: string | null;
  granted_at: string | null;
  expires_at: string | null;
}

function SessionsTab() {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [revoking, setRevoking] = useState<string | null>(null);
  const [revokeAllOpen, setRevokeAllOpen] = useState(false);

  const load = () => {
    setLoading(true);
    getSessions().then(setSessions).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const handleRevoke = async (clientId: string) => {
    setRevoking(clientId);
    try {
      await revokeSession(clientId);
      setSessions(s => s.filter(x => x.client_id !== clientId));
    } finally { setRevoking(null); }
  };

  const handleRevokeAll = async () => {
    await revokeAllSessions();
    setRevokeAllOpen(false);
    setSessions([]);
  };

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <div>
              <CardTitle className="text-base">Active Sessions</CardTitle>
              <CardDescription>OAuth2 applications you have granted access to.</CardDescription>
            </div>
            {sessions.length > 0 && (
              <Button variant="destructive" size="sm" onClick={() => setRevokeAllOpen(true)}>
                <LogOut className="h-4 w-4" />Revoke All
              </Button>
            )}
          </div>
        </CardHeader>
        <CardContent>
          {loading ? (
            <div className="space-y-2">{Array.from({ length: 3 }, (_, i) => `sk-${i}`).map(id => <Skeleton key={id} className="h-12 rounded-lg" />)}</div>
          ) : sessions.length === 0 ? (
            <p className="text-sm text-muted-foreground py-4 text-center">No active sessions.</p>
          ) : (
            <div className="space-y-2">
              {sessions.map(s => (
                <div key={s.client_id ?? Math.random()} className="flex items-center justify-between rounded-lg border px-4 py-3">
                  <div className="space-y-0.5">
                    <div className="flex items-center gap-2">
                      <MonitorSmartphone className="h-4 w-4 text-muted-foreground" />
                      <span className="text-sm font-medium">{s.client_name ?? s.client_id ?? 'Unknown client'}</span>
                    </div>
                    <p className="text-xs text-muted-foreground">
                      Granted {fmtDate(s.granted_at)}
                      {s.expires_at && ` · Expires ${fmtDate(s.expires_at)}`}
                    </p>
                  </div>
                  {s.client_id && (
                    <Button
                      variant="ghost"
                      size="sm"
                      className="text-destructive hover:text-destructive"
                      disabled={revoking === s.client_id}
                      onClick={() => handleRevoke(s.client_id!)}
                    >
                      <LogOut className="h-4 w-4" />
                      {revoking === s.client_id ? 'Revoking…' : 'Revoke'}
                    </Button>
                  )}
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      <AlertDialog open={revokeAllOpen} onOpenChange={setRevokeAllOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Revoke all sessions?</AlertDialogTitle>
            <AlertDialogDescription>All applications will be signed out. You may be asked to log in again.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleRevokeAll} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Revoke All</AlertDialogAction>
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
          );
          return (
          <Tabs defaultValue="profile" className="space-y-4">
            <TabsList>
              <TabsTrigger value="profile"><User className="h-4 w-4" />Profile</TabsTrigger>
              <TabsTrigger value="security"><Key className="h-4 w-4" />Security</TabsTrigger>
              <TabsTrigger value="mfa"><Shield className="h-4 w-4" />MFA</TabsTrigger>
              <TabsTrigger value="sessions"><MonitorSmartphone className="h-4 w-4" />Sessions</TabsTrigger>
            </TabsList>
            <TabsContent value="profile"><ProfileTab me={me} onUpdated={load} /></TabsContent>
            <TabsContent value="security"><SecurityTab /></TabsContent>
            <TabsContent value="mfa"><MfaTab /></TabsContent>
            <TabsContent value="sessions"><SessionsTab /></TabsContent>
          </Tabs>
          );
        })()}
      </div>
    </div>
  );
}
