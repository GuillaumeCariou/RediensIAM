import { useCallback, useEffect, useState } from 'react';
import PageHeader from '@/components/layout/PageHeader';
import { getInstanceConfig, updateInstanceConfig } from '@/api';

/**
 * The deployment's runtime settings — what used to require editing a manifest and rolling the pods.
 *
 * <p>Two columns, deliberately. <b>In force</b> is what this pod is actually using; <b>stored</b>
 * is what the instance row holds. They are normally equal, and when they are not it is because a
 * configuration source added after the instance provider wins — an environment variable in the
 * chart. An operator who saves a value and sees it "not apply" is looking at exactly that, and the
 * page says so instead of letting them conclude the save was lost.</p>
 *
 * <p>What the server refuses to write is shown too, read-only: Argon costs drive the pod's memory
 * limit, the trust anchors must not be learnable from data the process can write, and the topology
 * decides where the OAuth2 client this console is authenticated through points.</p>
 */

interface Settings {
  max_login_attempts: number;
  lockout_minutes: number;
  otp_ttl_seconds: number;
  max_sms_per_window: number;
  sms_window_minutes: number;
  audit_retention_days: number;
  invite_expiry_hours: number;
  pat_cache_ttl_minutes: number;
  smtp_host: string;
  smtp_port: number;
  smtp_start_tls: boolean;
  smtp_username: string;
  smtp_from_address: string;
  smtp_from_name: string;
}

interface Config {
  config_version: number;
  settings: Settings;
  stored: Settings;
  environment_only: Record<string, string | number>;
}

/** The numeric settings, with the label the operator reads and the bound the server will apply. */
const NUMBERS: { key: keyof Settings; label: string; hint: string }[] = [
  { key: 'max_login_attempts',    label: 'Failed logins before lockout', hint: '1–10' },
  { key: 'lockout_minutes',       label: 'Lockout duration (minutes)',   hint: '1–1440' },
  { key: 'otp_ttl_seconds',       label: 'One-time code lifetime (s)',   hint: '60–3600' },
  { key: 'max_sms_per_window',    label: 'SMS codes per window',         hint: '1–20' },
  { key: 'sms_window_minutes',    label: 'SMS window (minutes)',         hint: '1–1440' },
  { key: 'audit_retention_days',  label: 'Audit retention (days)',       hint: '90–3650' },
  { key: 'invite_expiry_hours',   label: 'Invitation expiry (hours)',    hint: '1–720' },
  { key: 'pat_cache_ttl_minutes', label: 'Token cache freshness (min)',  hint: '0–15' },
];

export default function DeploymentSettings() {
  const [config, setConfig] = useState<Config | null>(null);
  const [draft, setDraft] = useState<Partial<Settings>>({});
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState('');

  const load = useCallback(() => {
    getInstanceConfig()
      .then((r: Config) => { setConfig(r); setDraft({}); })
      .catch(() => setError('Could not read the deployment settings.'));
  }, []);

  useEffect(load, [load]);

  const save = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (Object.keys(draft).length === 0) return;
    setSaving(true);
    setError('');
    setSaved('');
    try {
      const res = await updateInstanceConfig(draft);
      // The answer says what was stored, which is not always what was typed: out of range is
      // clamped rather than refused, so echoing the request would show a number nothing holds.
      setSaved(`Saved ${(res.changed ?? []).length} setting(s).`);
      load();
    } catch {
      setError('Could not save. Nothing was changed.');
    } finally { setSaving(false); }
  };

  if (!config) {
    return (
      <div>
        <PageHeader title="Settings" description="Runtime configuration of this deployment" />
        {error && <div className="iam-alert iam-alert-danger" style={{ margin: '0 24px' }}>{error}</div>}
      </div>
    );
  }

  const value = <K extends keyof Settings>(key: K): Settings[K] =>
    draft[key] ?? config.settings[key];

  const differs = (key: keyof Settings) => config.settings[key] !== config.stored[key];

  return (
    <div>
      <PageHeader
        title="Settings"
        description={`Runtime configuration of this deployment · version ${config.config_version}`}
      />

      {error && <div className="iam-alert iam-alert-danger" style={{ margin: '0 24px 12px' }}>{error}</div>}
      {saved && <div className="iam-alert iam-alert-success" style={{ margin: '0 24px 12px' }}>{saved}</div>}

      <div className="iam-page">
        <form onSubmit={save}>
          <div className="iam-card" style={{ padding: 16 }}>
            <h3 style={{ marginTop: 0 }}>Sign-in and lockout</h3>
            {NUMBERS.map(({ key, label, hint }) => (
              <div key={key} style={{ marginBottom: 12 }}>
                <label className="iam-label" htmlFor={`s-${key}`}>{label}</label>
                <input
                  id={`s-${key}`}
                  className="iam-input"
                  type="number"
                  value={String(value(key))}
                  // Clearing a type="number" yields '', and Number('') is 0 — which for a lockout
                  // threshold is "never lock anybody out", written by somebody who only emptied a
                  // field. An empty field means nothing to send, so the key leaves the draft.
                  onChange={e => setDraft(d => {
                    if (e.target.value === '') { const { [key]: _dropped, ...rest } = d; return rest; }
                    return { ...d, [key]: Number(e.target.value) };
                  })}
                />
                <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>
                  {hint}
                  {differs(key) && (
                    <>
                      {' · '}
                      <b>stored {String(config.stored[key])}</b>, overridden by the environment
                    </>
                  )}
                </div>
              </div>
            ))}
          </div>

          <div className="iam-card" style={{ padding: 16, marginTop: 16 }}>
            <h3 style={{ marginTop: 0 }}>Mail relay</h3>
            <div style={{ marginBottom: 12 }}>
              <label className="iam-label" htmlFor="s-smtp_host">SMTP host</label>
              <input id="s-smtp_host" className="iam-input" value={value('smtp_host')}
                onChange={e => setDraft(d => ({ ...d, smtp_host: e.target.value }))} />
            </div>
            <div style={{ marginBottom: 12 }}>
              <label className="iam-label" htmlFor="s-smtp_port">SMTP port</label>
              <input id="s-smtp_port" className="iam-input" type="number" value={String(value('smtp_port'))}
                onChange={e => setDraft(d => {
                  if (e.target.value === '') { const { smtp_port: _dropped, ...rest } = d; return rest; }
                  return { ...d, smtp_port: Number(e.target.value) };
                })} />
            </div>
            <div style={{ marginBottom: 12 }}>
              <label className="iam-label" htmlFor="s-smtp_from_address">From address</label>
              <input id="s-smtp_from_address" className="iam-input" value={value('smtp_from_address')}
                onChange={e => setDraft(d => ({ ...d, smtp_from_address: e.target.value }))} />
            </div>
            <div>
              <label className="iam-label" htmlFor="s-smtp_start_tls">STARTTLS</label>
              <input id="s-smtp_start_tls" type="checkbox" checked={value('smtp_start_tls')}
                onChange={e => setDraft(d => ({ ...d, smtp_start_tls: e.target.checked }))} />
            </div>
          </div>

          <div style={{ marginTop: 16 }}>
            <button className="iam-btn iam-btn-primary" type="submit"
              disabled={saving || Object.keys(draft).length === 0}>
              {saving ? 'Saving…' : 'Save changes'}
            </button>
          </div>
        </form>

        {/* Shown, not hidden. An operator hunting a value that will not change needs to see the
            ones this page can never change, and why. */}
        <div className="iam-card" style={{ padding: 16, marginTop: 16 }}>
          <h3 style={{ marginTop: 0 }}>Set by the deployment only</h3>
          <p style={{ fontSize: 12, color: 'var(--fg-muted)', marginTop: 0 }}>
            Argon2 costs drive the pod&apos;s memory limit; the Ory URLs and trusted proxies are trust
            anchors, which a process must not learn from data it can write; the URLs decide where the
            OAuth2 client this console signed in through points.
          </p>
          <table className="iam-tbl">
            <tbody>
              {Object.entries(config.environment_only).map(([key, v]) => (
                <tr key={key}>
                  <td style={{ width: 220 }}>{key}</td>
                  <td className="iam-mono" style={{ fontSize: 12 }}>{String(v) || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
