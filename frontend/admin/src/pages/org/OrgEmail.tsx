import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { IamChip, IamDot } from '@/components/iam';
import { useAuth } from '@/context/AuthContext';
import {
  getOrgSmtp, upsertOrgSmtp, deleteOrgSmtp, testOrgSmtp,
  adminGetOrgSmtp, adminUpsertOrgSmtp, adminDeleteOrgSmtp, adminTestOrgSmtp,
} from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { ApiError } from '@/auth';

/**
 * Every SMTP failure has to be explained from its error code alone. The write endpoints validate
 * the relay before storing it (SmtpEndpointValidator), and /org/smtp/test deliberately no longer
 * echoes the SMTP server's own message — that message distinguished "host unreachable" from
 * "connection refused", which turns the endpoint into a port scanner for anyone with an org admin
 * account. Do not surface the server text here even if the API starts returning it again.
 */
const SMTP_ERRORS: Record<string, string> = {
  smtp_host_required:   'Host is required.',
  smtp_host_too_long:   'Host is too long (255 characters maximum).',
  smtp_port_not_allowed:'Port must be one of 25, 465, 587, 1025 or 2525.',
  smtp_tls_required:    'TLS is required. Enable StartTLS, or use port 465 for implicit TLS.',
  smtp_host_not_allowed:'That host resolves to a private or reserved address and cannot be used.',
  smtp_test_failed:     'Could not send through this relay. Check the host, port, and credentials.',
};

function smtpErrorMessage(e: unknown, fallback: string): string {
  const code = e instanceof ApiError ? (e.body as { error?: string } | null)?.error : undefined;
  return (code && SMTP_ERRORS[code]) ?? fallback;
}

interface SmtpConfig {
  configured: boolean; host?: string; port?: number; start_tls?: boolean;
  username?: string; from_address?: string; from_name?: string; updated_at?: string;
}

interface FormState {
  host: string; port: string; start_tls: boolean; username: string;
  password: string; from_address: string; from_name: string;
}

const EMPTY_FORM: FormState = { host: '', port: '587', start_tls: true, username: '', password: '', from_address: '', from_name: '' };

function Toggle({ checked, onChange }: Readonly<{ checked: boolean; onChange: (v: boolean) => void }>) {
  return (
    <input type="checkbox" className="iam-switch" checked={checked} onChange={e => onChange(e.target.checked)} />
  );
}

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

  // Keyed on the org being viewed. React Router reuses this component across :id changes, and with
  // an empty dependency list the effect never re-ran: navigating from one organisation's Email page
  // to another showed the first org's relay under the second org's URL, and Save wrote it there.
  const fetchConfig = useCallback(async () => {
    try {
      const data: SmtpConfig = isAdmin ? await adminGetOrgSmtp(id) : await getOrgSmtp();
      setConfig(data);
      if (data.configured) {
        setForm({ host: data.host ?? '', port: String(data.port ?? 587), start_tls: data.start_tls ?? true, username: data.username ?? '', password: '', from_address: data.from_address ?? '', from_name: data.from_name ?? '' });
      } else { setForm(EMPTY_FORM); }
    } catch { setError('Failed to load SMTP configuration.'); }
    finally { setLoading(false); }
  }, [id, isAdmin]);

  useEffect(() => { void fetchConfig(); }, [fetchConfig]);

  const handleSave = async () => {
    setSaving(true); setError('');
    try {
      const body = { host: form.host, port: Number(form.port), start_tls: form.start_tls, username: form.username || undefined, password: form.password || undefined, from_address: form.from_address, from_name: form.from_name };
      if (isAdmin) await adminUpsertOrgSmtp(id, body);
      else await upsertOrgSmtp(body);
      await fetchConfig(); setEditing(false);
    } catch (e: unknown) { setError(smtpErrorMessage(e, 'Failed to save SMTP configuration.')); }
    finally { setSaving(false); }
  };

  const handleDelete = async () => {
    if (!confirm('Remove SMTP configuration and revert to global SMTP?')) return;
    try {
      if (isAdmin) await adminDeleteOrgSmtp(id);
      else await deleteOrgSmtp();
      await fetchConfig(); setEditing(false);
    } catch { setError('Failed to remove SMTP configuration.'); }
  };

  const handleTest = async () => {
    setTesting(true); setTestResult(null);
    try {
      const res = isAdmin ? await adminTestOrgSmtp(id) : await testOrgSmtp();
      setTestResult({ ok: true, msg: `Test email sent to ${res.to}` });
    } catch (e: unknown) { setTestResult({ ok: false, msg: smtpErrorMessage(e, 'Test failed.') }); }
    finally { setTesting(false); }
  };

  const set = (k: keyof FormState, v: string | boolean) => setForm(f => ({ ...f, [k]: v }));

  if (loading) return (
    <div>
      <PageHeader title="Email Settings" />
      <div className="iam-page"><div style={{ height: 160, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8 }} /></div>
    </div>
  );

  return (
    <div>
      <PageHeader title="Email Settings" description="Configure the SMTP relay used to send verification emails for this organisation" />
      <div className="iam-page" style={{ maxWidth: 600 }}>
        {!config?.configured && !editing && (
          <div className="iam-card iam-card-pad" style={{ borderStyle: 'dashed' }}>
            <div style={{ display: 'flex', gap: 12, alignItems: 'flex-start' }}>
              <IamDot tone="warn" />
              <div style={{ flex: 1 }}>
                <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 4 }}>Using global SMTP</div>
                <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', lineHeight: 1.5 }}>
                  No organisation-level SMTP is configured. Emails will be sent using the global relay set by the system administrator.
                </div>
                <button className="iam-btn iam-btn-secondary iam-btn-sm" style={{ marginTop: 12 }} onClick={() => setEditing(true)}>
                  Configure custom SMTP
                </button>
              </div>
            </div>
          </div>
        )}

        {config?.configured && !editing && (
          <div className="iam-card iam-card-pad">
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 14 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <div style={{ fontSize: 14, fontWeight: 600 }}>Custom SMTP</div>
                <IamChip tone="success">Active</IamChip>
              </div>
              <div style={{ display: 'flex', gap: 8 }}>
                <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={handleTest} disabled={testing}>
                  {testing ? 'Sending…' : 'Test'}
                </button>
                <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={() => setEditing(true)}>Edit</button>
              </div>
            </div>
            <div style={{ fontSize: 12, color: 'var(--fg-muted)', marginBottom: 14 }}>
              {config.host}:{config.port} · {config.start_tls ? 'StartTLS' : 'No TLS'}
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '120px 1fr', gap: '6px 12px', fontSize: 13 }}>
              <span style={{ color: 'var(--fg-muted)' }}>From address</span><span className="iam-mono">{config.from_address}</span>
              <span style={{ color: 'var(--fg-muted)' }}>From name</span><span>{config.from_name}</span>
              {config.username && (<><span style={{ color: 'var(--fg-muted)' }}>Username</span><span className="iam-mono">{config.username}</span></>)}
            </div>
            {testResult && (
              <div style={{ marginTop: 12, fontSize: 12.5, color: testResult.ok ? 'var(--success)' : 'var(--danger)' }}>
                {testResult.msg}
              </div>
            )}
          </div>
        )}

        {editing && (
          <div className="iam-card iam-card-pad">
            <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>
              {config?.configured ? 'Edit SMTP Configuration' : 'Configure SMTP'}
            </div>
            <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 20 }}>
              Emails will be sent using this relay. Leave password blank to keep the existing one.
            </div>

            <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--fg-subtle)', marginBottom: 12 }}>Connection</div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 100px', gap: 12, marginBottom: 14 }}>
              <div><label className="iam-label" htmlFor="email-host">Host</label><input id="email-host" className="iam-input" value={form.host} onChange={e => set('host', e.target.value)} placeholder="smtp.example.com" /></div>
              <div><label className="iam-label" htmlFor="email-port">Port</label><input id="email-port" className="iam-input" type="number" value={form.port} onChange={e => set('port', e.target.value)} placeholder="587" /></div>
            </div>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 20, padding: '10px 0', borderBottom: '1px solid var(--border)' }}>
              <div>
                <div style={{ fontSize: 13, fontWeight: 500 }}>STARTTLS</div>
                <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Use STARTTLS negotiation (port 587). Disable for SSL on port 465.</div>
              </div>
              <Toggle checked={form.start_tls} onChange={v => set('start_tls', v)} />
            </div>

            <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--fg-subtle)', marginBottom: 12 }}>Authentication</div>
            <div style={{ marginBottom: 12 }}><label className="iam-label" htmlFor="email-username">Username</label><input id="email-username" className="iam-input" value={form.username} onChange={e => set('username', e.target.value)} placeholder="noreply@example.com" autoComplete="off" /></div>
            <div style={{ marginBottom: 20 }}>
              <label className="iam-label" htmlFor="email-password">Password</label>
              <div style={{ position: 'relative' }}>
                <input id="email-password" className="iam-input" type={showPassword ? 'text' : 'password'} value={form.password} onChange={e => set('password', e.target.value)} placeholder={config?.configured ? '(unchanged)' : 'SMTP password'} autoComplete="new-password" style={{ paddingRight: 36 }} />
                <button type="button" onClick={() => setShowPassword(v => !v)}
                  style={{ position: 'absolute', right: 10, top: '50%', transform: 'translateY(-50%)', background: 'none', border: 'none', color: 'var(--fg-muted)', cursor: 'pointer' }}>
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                    {showPassword
                      ? <><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"/><line x1="1" y1="1" x2="23" y2="23"/></>
                      : <><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></>}
                  </svg>
                </button>
              </div>
            </div>

            <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--fg-subtle)', marginBottom: 12 }}>From</div>
            <div style={{ marginBottom: 12 }}><label className="iam-label" htmlFor="email-from-address">From address</label><input id="email-from-address" className="iam-input" value={form.from_address} onChange={e => set('from_address', e.target.value)} placeholder="noreply@yourorg.com" /></div>
            <div style={{ marginBottom: 20 }}>
              <label className="iam-label" htmlFor="email-from-name">From name</label>
              <input id="email-from-name" className="iam-input" value={form.from_name} onChange={e => set('from_name', e.target.value)} placeholder="Acme Platform" />
              <div style={{ fontSize: 11, color: 'var(--fg-subtle)', marginTop: 4 }}>Can be overridden per project in project Authentication settings.</div>
            </div>

            {error && <div style={{ padding: '8px 12px', background: 'var(--danger-soft)', color: 'var(--danger)', borderRadius: 6, fontSize: 13, marginBottom: 14 }}>{error}</div>}

            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', paddingTop: 16, borderTop: '1px solid var(--border)' }}>
              <div>
                {config?.configured && (
                  <button className="iam-btn iam-btn-danger iam-btn-sm" onClick={handleDelete}>Reset to global</button>
                )}
              </div>
              <div style={{ display: 'flex', gap: 8 }}>
                <button className="iam-btn iam-btn-ghost iam-btn-sm" onClick={() => { setEditing(false); setError(''); }}>Cancel</button>
                <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={handleSave} disabled={saving}>
                  {saving ? 'Saving…' : 'Save'}
                </button>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
