import { useEffect, useState } from 'react';
import { User, Shield, Key, Copy, Check, RefreshCw, Eye, EyeOff, MonitorSmartphone, LogOut, Fingerprint, Trash2 } from 'lucide-react';
import { getMe, updateMe, changePassword, getMfaStatus, setupTotp, confirmTotp, regenerateBackupCodes, getSessions, revokeSession, revokeAllSessions, setupPhone, verifyPhone, removePhone, beginWebAuthnRegistration, completeWebAuthnRegistration, listWebAuthnCredentials, deleteWebAuthnCredential, getSocialAccounts, unlinkSocialAccount } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { useReauth } from '@/components/ReauthDialog';
import { fmtDate } from '@/lib/utils';
import { IamChip, IamDialog } from '@/components/iam';

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
    <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={() => { navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 2000); }}>
      {copied ? <><Check className="h-3 w-3" />Copied</> : <><Copy className="h-3 w-3" />Copy</>}
    </button>
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

  let saveLabel;
  if (saved) saveLabel = <><Check className="h-4 w-4" />Saved</>;
  else if (saving) saveLabel = 'Saving…';
  else saveLabel = 'Save';

  return (
    <div className="space-y-6">
      <div className="iam-card">
        <div className="iam-card-pad pb-0">
          <h3 className="text-sm font-semibold text-base">Identity</h3>
          <p className="text-xs text-[var(--fg-muted)]">Your account identifier — these cannot be changed.</p>
        </div>
        <div className="iam-card-pad space-y-4">
          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-1">
              <label className="iam-label text-xs text-muted-foreground">Username</label>
              <p className="font-mono text-sm font-medium">{me.username}<span className="text-muted-foreground">#{me.discriminator}</span></p>
            </div>
            <div className="space-y-1">
              <label className="iam-label text-xs text-muted-foreground">Email</label>
              <div className="flex items-center gap-2">
                <p className="text-sm">{me.email}</p>
                {me.email_verified
                  ? <IamChip className="text-xs" tone="success">Verified</IamChip>
                  : <IamChip className="text-xs" tone="default">Unverified</IamChip>
                }
              </div>
            </div>
          </div>
          <hr className="iam-sep" />
          <div className="space-y-1">
            <label className="iam-label text-xs text-muted-foreground">Roles</label>
            <div className="flex flex-wrap gap-1 mt-1">
              {me.roles.length === 0
                ? <span className="text-sm text-muted-foreground">No roles</span>
                : me.roles.map(r => <IamChip className="text-xs font-mono" tone="default" key={r}>{r}</IamChip>)
              }
            </div>
          </div>
          {me.last_login_at && (
            <>
              <hr className="iam-sep" />
              <div className="space-y-1">
                <label className="iam-label text-xs text-muted-foreground">Last login</label>
                <p className="text-sm">{new Date(me.last_login_at).toLocaleString()}</p>
              </div>
            </>
          )}
        </div>
      </div>

      <div className="iam-card">
        <div className="iam-card-pad pb-0">
          <h3 className="text-sm font-semibold text-base">Display Name</h3>
          <p className="text-xs text-[var(--fg-muted)]">Shown instead of your username in some views.</p>
        </div>
        <div className="iam-card-pad space-y-4">
          <form onSubmit={handleSave} className="flex gap-3 items-end">
            <div className="flex-1 space-y-2">
              <label className="iam-label" htmlFor="display-name">Display name</label>
              <input className="iam-input" id="display-name" value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder="e.g. John Doe" />
            </div>
            <button className="iam-btn iam-btn-primary" type="submit" disabled={saving}>
              {saveLabel}
            </button>
          </form>
          <div className="flex items-center justify-between pt-4 border-t">
            <div>
              <p className="font-medium text-sm">New device login alerts</p>
              <p className="text-xs text-muted-foreground">
                Receive an email when you log in from a device or location not seen in the last 90 days.
              </p>
            </div>
            <input type="checkbox" className="iam-switch" checked={newDeviceAlerts} onChange={e => handleToggleNewDeviceAlerts(e.target.checked)} />
          </div>
        </div>
      </div>
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
      <div className="iam-card">
        <div className="iam-card-pad pb-0">
          <h3 className="text-sm font-semibold text-base">Change Password</h3>
          <p className="text-xs text-[var(--fg-muted)]">Your password must be at least 8 characters.</p>
        </div>
        <div className="iam-card-pad">
          <form onSubmit={handleSubmit} className="space-y-4 max-w-sm">
            {error && <div className="iam-alert iam-alert-danger text-sm py-2 px-3">{error}</div>}
            {success && <div className="iam-alert text-sm py-2 px-3 border-green-500 text-green-700">Password changed successfully.</div>}
            <div className="space-y-2">
              <label className="iam-label">Current password</label>
              <div className="relative">
                <input className="iam-input" type={showCurrent ? 'text' : 'password'} value={form.current} onChange={e => setForm(f => ({ ...f, current: e.target.value }))} required />
                <button className="iam-btn iam-btn-ghost iam-btn-icon absolute right-0 top-0 h-full px-3" type="button" onClick={() => setShowCurrent(v => !v)}>
                  {showCurrent ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                </button>
              </div>
            </div>
            <div className="space-y-2">
              <label className="iam-label">New password</label>
              <div className="relative">
                <input className="iam-input" type={showNext ? 'text' : 'password'} value={form.next} onChange={e => setForm(f => ({ ...f, next: e.target.value }))} required />
                <button className="iam-btn iam-btn-ghost iam-btn-icon absolute right-0 top-0 h-full px-3" type="button" onClick={() => setShowNext(v => !v)}>
                  {showNext ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                </button>
              </div>
            </div>
            <div className="space-y-2">
              <label className="iam-label">Confirm new password</label>
              <input className="iam-input" type="password" value={form.confirm} onChange={e => setForm(f => ({ ...f, confirm: e.target.value }))} required />
            </div>
            <button className="iam-btn iam-btn-primary" type="submit" disabled={saving}>{saving ? 'Saving…' : 'Change Password'}</button>
          </form>
        </div>
      </div>

      <div className="iam-card">
        <div className="iam-card-pad pb-0">
          <h3 className="text-sm font-semibold text-base">Linked Accounts</h3>
          <p className="text-xs text-[var(--fg-muted)]">Social accounts connected to your profile for sign-in.</p>
        </div>
        <div className="iam-card-pad space-y-4">
          {unlinkError && <div className="iam-alert iam-alert-danger text-sm py-2 px-3">{unlinkError}</div>}
          {(() => {
            if (linkedLoading) return (
            <div className="iam-skeleton h-12 w-full" />
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
                  <button className="iam-btn iam-btn-ghost iam-btn-sm text-destructive hover:text-destructive" disabled={unlinking === acc.id} onClick={() => handleUnlink(acc.id)}>
                    <Trash2 className="h-4 w-4" />
                    {unlinking === acc.id ? 'Unlinking…' : 'Unlink'}
                  </button>
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
                  <button className="iam-btn iam-btn-secondary iam-btn-sm" key={provider} onClick={() => { globalThis.location.href = `/auth/oauth2/link/start?provider=${provider}`; }}>
                    {PROVIDER_LABELS[provider]}
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
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
  const { guard, dialog } = useReauth();

  const load = () => {
    setLoading(true);
    listWebAuthnCredentials().then(setCreds).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  /**
   * The completion call goes through `guard`, not straight to the API: adding a passkey to an
   * account that already has a factor needs a re-authentication proof, while the first one does
   * not. `guard` only prompts if the backend asks. The retry is safe because the backend checks
   * the proof before it consumes the pending registration, so it replays the same attestation.
   */
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

      await guard(async proof => {
        const res = await completeWebAuthnRegistration(body, proof);
        if (res.error) { setError('Registration failed: ' + res.error); return; }
        setDeviceName('');
        load();
      });
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
    setError('');
    try {
      await guard(proof => deleteWebAuthnCredential(id, proof).then(load));
    } catch { setError('Failed to remove the passkey.'); }
  };

  return (
    <div className="iam-card">
      <div className="iam-card-pad pb-0">
        <div className="flex items-center justify-between">
          <div>
            <h3 className="text-sm font-semibold text-base">Passkeys</h3>
            <p className="text-xs text-[var(--fg-muted)]">Sign in with your device fingerprint, face, or security key.</p>
          </div>
          <IamChip tone={creds.length > 0 ? 'success' : 'default'}>
            {creds.length > 0 ? `${creds.length} registered` : 'None'}
          </IamChip>
        </div>
      </div>
      <div className="iam-card-pad space-y-4">
        {(() => {
          if (loading) return (
          <div className="iam-skeleton h-16 w-full" />
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
                <button className="iam-btn iam-btn-ghost iam-btn-sm text-destructive hover:text-destructive" onClick={() => handleDelete(c.id)}>
                  <Trash2 className="h-4 w-4" />
                </button>
              </div>
            ))}
          </div>
          );
          return null;
        })()}

        <div className="flex gap-2 items-center">
          <input className="iam-input max-w-xs" placeholder="Passkey name (optional)" value={deviceName} onChange={e => setDeviceName(e.target.value)} />
          <button className="iam-btn iam-btn-primary" onClick={handleRegister} disabled={registering}>
            <Fingerprint className="h-4 w-4" />
            {registering ? 'Waiting…' : 'Add passkey'}
          </button>
        </div>

        {error && <p className="text-sm text-destructive">{error}</p>}
        {dialog}
      </div>
    </div>
  );
}

function base64urlToBuffer(b64: string): ArrayBuffer {
  const bin = atob(b64.replaceAll('-', '+').replaceAll('_', '/'));
  const buf = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) buf[i] = bin.codePointAt(i)!;
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

  const [setupData, setSetupData] = useState<{ otpauth_url: string; secret: string } | null>(null);
  const [setupCode, setSetupCode] = useState('');
  const [setupSaving, setSetupSaving] = useState(false);
  const [setupError, setSetupError] = useState('');
  const [backupCodes, setBackupCodes] = useState<string[]>([]);

  const [regenOpen, setRegenOpen] = useState(false);
  const [regenCodes, setRegenCodes] = useState<string[]>([]);

  const [phoneInput, setPhoneInput] = useState('');
  const [phoneOtp, setPhoneOtp]     = useState('');
  const [phoneSending, setPhoneSending] = useState(false);
  const [phoneCodeSent, setPhoneCodeSent] = useState(false);
  const [phoneError, setPhoneError] = useState('');
  const [phoneSuccess, setPhoneSuccess] = useState(false);

  const [regenError, setRegenError] = useState('');
  const { guard, dialog } = useReauth();

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

  /**
   * Goes through `guard`: replacing an existing TOTP factor needs a re-authentication proof, a
   * first enrolment does not. `guard` only prompts if the backend actually asks, so both cases
   * go through this one call — do not add a separate unguarded path for first enrolment.
   */
  const handleConfirmSetup = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSetupError('');
    setSetupSaving(true);
    try {
      await guard(async proof => {
        const res = await confirmTotp({ code: setupCode }, proof);
        if (res.error) { setSetupError('Invalid code. Please try again.'); return; }
        setBackupCodes(res.backup_codes ?? []);
        setSetupData(null);
        setSetupCode('');
        load();
      });
    } catch { setSetupError('Could not confirm the code. Please try again.'); }
    finally { setSetupSaving(false); }
  };

  const handleRegen = async () => {
    setRegenError('');
    setRegenOpen(false);
    try {
      await guard(async proof => {
        const res = await regenerateBackupCodes(proof);
        setRegenCodes(res.backup_codes ?? []);
        load();
      });
    } catch { setRegenError('Failed to regenerate backup codes.'); }
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

  /**
   * Same rule as the passkey and TOTP paths, hence the `guard`: adding a factor to an account
   * that already has one needs a re-authentication proof, a first enrolment does not.
   */
  const handlePhoneVerify = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setPhoneError('');
    setPhoneSending(true);
    try {
      await guard(async proof => {
        const res = await verifyPhone(phoneOtp, proof);
        if (res.error) { setPhoneError('Invalid code. Try again.'); return; }
        setPhoneSuccess(true);
        setPhoneCodeSent(false);
        setPhoneInput('');
        setPhoneOtp('');
        load();
      });
    } catch { setPhoneError('Failed to verify code.'); }
    finally { setPhoneSending(false); }
  };

  const handleRemovePhone = async () => {
    setPhoneError('');
    try {
      await guard(proof => removePhone(proof).then(load));
    } catch { setPhoneError('Failed to remove the phone number.'); }
  };

  if (loading) return <div className="iam-skeleton h-40 rounded-xl" />;

  const renderPhoneForm = () => {
    if (status?.phone_verified) {
      return (
        <div className="flex items-center gap-3">
          <p className="text-sm text-muted-foreground">Phone number verified and active.</p>
          <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={handleRemovePhone}>Remove</button>
        </div>
      );
    }
    if (phoneCodeSent) {
      return (
        <form onSubmit={handlePhoneVerify} className="space-y-3 max-w-sm">
          <p className="text-sm text-muted-foreground">Enter the 6-digit code sent to {phoneInput}.</p>
          <div className="flex gap-2 items-center">
            <input className="iam-input font-mono w-32 text-center text-lg tracking-widest" value={phoneOtp} onChange={e => setPhoneOtp(e.target.value.replaceAll(/\D/g, '').slice(0, 6))} placeholder="000000" maxLength={6} required />
            <button className="iam-btn iam-btn-primary" type="submit" disabled={phoneSending || phoneOtp.length !== 6}>
              {phoneSending ? 'Verifying…' : 'Verify'}
            </button>
            <button className="iam-btn iam-btn-secondary" type="button" onClick={() => { setPhoneCodeSent(false); setPhoneOtp(''); }}>
              Cancel
            </button>
          </div>
        </form>
      );
    }
    return (
      <form onSubmit={handlePhoneSend} className="flex gap-2 items-end max-w-sm">
        <div className="flex-1 space-y-2">
          <label className="iam-label">Phone number</label>
          <input className="iam-input" type="tel" placeholder="+1234567890" value={phoneInput} onChange={e => setPhoneInput(e.target.value)} required />
        </div>
        <button className="iam-btn iam-btn-primary" type="submit" disabled={phoneSending}>{phoneSending ? 'Sending…' : 'Send code'}</button>
      </form>
    );
  };

  return (
    <div className="space-y-4">
      <div className="iam-card">
        <div className="iam-card-pad pb-0">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-sm font-semibold text-base">Authenticator App (TOTP)</h3>
              <p className="text-xs text-[var(--fg-muted)] mt-1">Use an app like Google Authenticator or Authy.</p>
            </div>
            {status?.totp_enabled
              ? <IamChip tone="success">Enabled</IamChip>
              : <IamChip tone="default">Disabled</IamChip>
            }
          </div>
        </div>
        {!status?.totp_enabled && (
          <div className="iam-card-pad">
            {setupData ? (
              <div className="space-y-4 max-w-sm">
                <div className="rounded-lg bg-muted p-4 space-y-3">
                  <p className="text-sm font-medium">1. Open your authenticator app and add a new account manually.</p>
                  <div className="space-y-1">
                    <label className="iam-label text-xs text-muted-foreground">Secret key</label>
                    <div className="flex items-center gap-2">
                      <code className="text-xs font-mono bg-background rounded px-2 py-1 break-all flex-1">{setupData.secret}</code>
                      <CopyButton text={setupData.secret} />
                    </div>
                  </div>
                  <div className="space-y-1">
                    <label className="iam-label text-xs text-muted-foreground">Or open in authenticator app</label>
                    <a href={setupData.otpauth_url} className="text-xs text-primary underline break-all">Open authenticator link</a>
                  </div>
                </div>
                <p className="text-sm font-medium">2. Enter the 6-digit code from your app to confirm.</p>
                <form onSubmit={handleConfirmSetup} className="flex gap-2">
                  <input className="iam-input font-mono w-32 text-center text-lg tracking-widest" value={setupCode} onChange={e => setSetupCode(e.target.value.replaceAll(/\D/g, '').slice(0, 6))} placeholder="000000" maxLength={6} required />
                  <button className="iam-btn iam-btn-primary" type="submit" disabled={setupSaving || setupCode.length !== 6}>
                    {setupSaving ? 'Verifying…' : 'Confirm'}
                  </button>
                  <button className="iam-btn iam-btn-secondary" type="button" onClick={() => { setSetupData(null); setSetupCode(''); }}>Cancel</button>
                </form>
                {setupError && <p className="text-sm text-destructive">{setupError}</p>}
              </div>
            ) : (
              <button className="iam-btn iam-btn-primary" onClick={handleStartSetup}><Shield className="h-4 w-4" />Set up TOTP</button>
            )}
          </div>
        )}
      </div>

      {backupCodes.length > 0 && (
        <div className="iam-card border-amber-500/50 bg-amber-50 dark:bg-amber-950/20">
          <div className="iam-card-pad pb-0">
            <h3 className="text-sm font-semibold text-base text-amber-700 dark:text-amber-400">Save your backup codes</h3>
            <p className="text-xs text-[var(--fg-muted)]">Each code can be used once if you lose access to your authenticator. Store them somewhere safe.</p>
          </div>
          <div className="iam-card-pad">
            <div className="grid grid-cols-4 gap-2 mb-3">
              {backupCodes.map(c => <code key={c} className="text-xs font-mono bg-background rounded px-2 py-1 text-center">{c}</code>)}
            </div>
            <CopyButton text={backupCodes.join('\n')} />
          </div>
        </div>
      )}

      {status?.totp_enabled && (
        <div className="iam-card">
          <div className="iam-card-pad pb-0">
            <div className="flex items-center justify-between">
              <div>
                <h3 className="text-sm font-semibold text-base">Backup Codes</h3>
                <p className="text-xs text-[var(--fg-muted)]">{status.backup_codes_remaining} code{status.backup_codes_remaining === 1 ? '' : 's'} remaining.</p>
              </div>
              <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={() => setRegenOpen(true)}>
                <RefreshCw className="h-4 w-4" />Regenerate
              </button>
            </div>
          </div>
          {(regenCodes.length > 0 || regenError) && (
            <div className="iam-card-pad">
              {regenError && <p className="text-sm text-destructive mb-3">{regenError}</p>}
              {regenCodes.length > 0 && (
                <>
                  <div className="grid grid-cols-4 gap-2 mb-3">
                    {regenCodes.map(c => <code key={c} className="text-xs font-mono bg-muted rounded px-2 py-1 text-center">{c}</code>)}
                  </div>
                  <CopyButton text={regenCodes.join('\n')} />
                </>
              )}
            </div>
          )}
        </div>
      )}

      <PasskeysCard />

      <div className="iam-card">
        <div className="iam-card-pad pb-0">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-sm font-semibold text-base">SMS Authentication</h3>
              <p className="text-xs text-[var(--fg-muted)]">Use your phone number as a second factor at login.</p>
            </div>
            {status?.phone_verified
              ? <IamChip tone="success">Verified</IamChip>
              : <IamChip tone="default">Not set</IamChip>
            }
          </div>
        </div>
        <div className="iam-card-pad">
          {renderPhoneForm()}
          {phoneError && <p className="text-sm text-destructive mt-2">{phoneError}</p>}
          {phoneSuccess && <p className="text-sm text-green-600 mt-2">Phone number verified successfully.</p>}
        </div>
      </div>

      <IamDialog open={regenOpen} onClose={() => setRegenOpen(false)}
      title="Regenerate backup codes?"
      desc="All existing backup codes will be invalidated. Make sure you save the new ones."
      footer={<><button className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-primary" onClick={handleRegen}>Regenerate</button></>}
    >

    </IamDialog>

      {dialog}
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

  const renderSessionsList = () => {
    if (loading) return <div className="space-y-2">{Array.from({ length: 3 }, (_, i) => `sk-${i}`).map(id => <div className="iam-skeleton h-12 rounded-lg" key={id} />)}</div>;
    if (sessions.length === 0) return <p className="text-sm text-muted-foreground py-4 text-center">No active sessions.</p>;
    return (
      <div className="space-y-2">
        {sessions.map((s, i) => (
          <div key={s.client_id ?? `session-${i}`} className="flex items-center justify-between rounded-lg border px-4 py-3">
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
              <button className="iam-btn iam-btn-ghost iam-btn-sm text-destructive hover:text-destructive" disabled={revoking === s.client_id} onClick={() => handleRevoke(s.client_id!)}>
                <LogOut className="h-4 w-4" />
                {revoking === s.client_id ? 'Revoking…' : 'Revoke'}
              </button>
            )}
          </div>
        ))}
      </div>
    );
  };

  return (
    <div className="space-y-4">
      <div className="iam-card">
        <div className="iam-card-pad pb-0">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-sm font-semibold text-base">Active Sessions</h3>
              <p className="text-xs text-[var(--fg-muted)]">OAuth2 applications you have granted access to.</p>
            </div>
            {sessions.length > 0 && (
              <button className="iam-btn iam-btn-danger iam-btn-sm" onClick={() => setRevokeAllOpen(true)}>
                <LogOut className="h-4 w-4" />Revoke All
              </button>
            )}
          </div>
        </div>
        <div className="iam-card-pad">
          {renderSessionsList()}
        </div>
      </div>

      <IamDialog open={revokeAllOpen} onClose={() => setRevokeAllOpen(false)}
      title="Revoke all sessions?"
      desc="All applications will be signed out. You may be asked to log in again."
      footer={<><button className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-danger" onClick={handleRevokeAll}>Revoke All</button></>}
    >

    </IamDialog>
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────
const TABS = [
  { id: 'profile',  label: 'Profile',  icon: User },
  { id: 'security', label: 'Security', icon: Key },
  { id: 'mfa',      label: 'MFA',      icon: Shield },
  { id: 'sessions', label: 'Sessions', icon: MonitorSmartphone },
] as const;

export default function AccountPage() {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);
  const [tab, setTab] = useState<(typeof TABS)[number]['id']>('profile');

  const load = () => {
    getMe().then(setMe).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const renderContent = () => {
    if (loading) {
      return (
        <div className="space-y-4">
          <div className="iam-skeleton h-40 rounded-xl" />
          <div className="iam-skeleton h-32 rounded-xl" />
        </div>
      );
    }
    if (!me) return <p className="text-muted-foreground">Failed to load account.</p>;
    return (
      <div className="space-y-4">
        <div className="iam-tabs" role="tablist">
          {TABS.map(t => (
            <button key={t.id} type="button" role="tab" className="iam-tab"
              aria-selected={tab === t.id} onClick={() => setTab(t.id)}>
              <t.icon className="h-4 w-4" />{t.label}
            </button>
          ))}
        </div>
        {tab === 'profile'  && <ProfileTab me={me} onUpdated={load} />}
        {tab === 'security' && <SecurityTab />}
        {tab === 'mfa'      && <MfaTab />}
        {tab === 'sessions' && <SessionsTab />}
      </div>
    );
  };

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
        {renderContent()}
      </div>
    </div>
  );
}
