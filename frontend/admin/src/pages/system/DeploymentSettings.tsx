import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router';
import PageHeader from '@/components/layout/PageHeader';
import { getInstanceConfig, updateInstanceConfig, getMe } from '@/api';
import { ApiError } from '@/auth';
import { useAuth } from '@/context/AuthContext';
import KeyRotationPanel from './KeyRotationPanel';

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

interface Me { email: string; username: string; discriminator: string; totp_enabled: boolean }

/** The numeric settings, with the label the operator reads and the bound the server will apply. */
const NUMBERS: { key: keyof Settings; label: string; hint: string }[] = [
  { key: 'max_login_attempts',    label: 'Failed logins before lockout', hint: 'Clamped 1–10' },
  { key: 'lockout_minutes',       label: 'Lockout duration (minutes)',   hint: 'Clamped 1–1440' },
  { key: 'otp_ttl_seconds',       label: 'One-time code lifetime (s)',   hint: 'Clamped 60–3600' },
  { key: 'max_sms_per_window',    label: 'SMS codes per window',         hint: 'Clamped 1–20' },
  { key: 'sms_window_minutes',    label: 'SMS window (minutes)',         hint: 'Clamped 1–1440' },
  { key: 'audit_retention_days',  label: 'Audit retention (days)',       hint: 'Clamped 90–3650' },
  { key: 'invite_expiry_hours',   label: 'Invitation expiry (hours)',    hint: 'Clamped 1–720' },
  { key: 'pat_cache_ttl_minutes', label: 'Token cache freshness (min)',  hint: 'Clamped 0–15' },
];

/** The text settings of the relay. The password is deliberately absent — see the card's note. */
const MAIL_TEXT: { key: keyof Settings; label: string }[] = [
  { key: 'smtp_host',         label: 'SMTP host' },
  { key: 'smtp_username',     label: 'Username' },
  { key: 'smtp_from_address', label: 'From address' },
  { key: 'smtp_from_name',    label: 'From name' },
];

/**
 * Why each read-only setting is read-only, in the words of the operator hunting for it.
 *
 * Keyed on what `GET /admin/instance` actually sends, and unknown keys still render: a setting the
 * server starts returning must appear here even before anybody writes a sentence about it.
 */
const ENVIRONMENT_ONLY_NOTES: Record<string, string> = {
  public_url:        'Where this deployment answers from. Changing it invalidates the OAuth2 client registration this console signed in through.',
  admin_spa_origin:  'The browser origin this console is served from; Hydra’s redirect_uri must match it exactly.',
  domain:            'The mail and cookie domain this instance row carries.',
  trusted_proxies:   'Whose X-Forwarded-* headers are honoured. A process must not learn who to trust from data it can write.',
  hydra_admin_url:   'Environment only — a database write must not redirect the authorisation store.',
  hydra_public_url:  'Environment only, same reason.',
  keto_read_url:     'Environment only, same reason.',
  keto_write_url:    'Environment only, same reason.',
  argon_time_cost:   'Drives the pod’s memory limit. Raising the cost from a browser kills the pod that served the request.',
  argon_memory_cost: 'Drives the pod’s memory limit, in KiB. Same reason.',
  argon_parallelism: 'Drives the pod’s memory limit. Same reason.',
};

function apiErrorMessage(e: unknown, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return body?.detail ?? body?.error ?? fallback;
}

export default function DeploymentSettings() {
  const { roles } = useAuth();
  const [config, setConfig] = useState<Config | null>(null);
  const [me, setMe] = useState<Me | null>(null);
  const [meError, setMeError] = useState('');
  const [draft, setDraft] = useState<Partial<Settings>>({});
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState('');

  const load = useCallback(() => {
    getInstanceConfig()
      .then((r: Config) => { setConfig(r); setDraft({}); })
      .catch((e: unknown) => setError(apiErrorMessage(e, 'Could not read the deployment settings.')));
  }, []);

  useEffect(load, [load]);

  useEffect(() => {
    getMe()
      .then(setMe)
      .catch((e: unknown) => setMeError(apiErrorMessage(e, 'Could not read your own account.')));
  }, []);

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
    } catch (err) {
      setError(apiErrorMessage(err, 'Could not save. Nothing was changed.'));
    } finally { setSaving(false); }
  };

  const value = <K extends keyof Settings>(key: K): Settings[K] =>
    draft[key] ?? config?.settings[key] as Settings[K];

  const differs = (key: keyof Settings) => !!config && config.settings[key] !== config.stored[key];

  /** Clearing a type="number" yields '', and Number('') is 0 — which for a lockout threshold is
   *  "never lock anybody out", written by somebody who only emptied a field. An empty field means
   *  nothing to send, so the key leaves the draft. */
  const setNumber = (key: keyof Settings, raw: string) => setDraft(d => {
    if (raw !== '') return { ...d, [key]: Number(raw) };
    const rest: Partial<Settings> = { ...d };
    delete rest[key];
    return rest;
  });

  const numberField = (key: keyof Settings, label: string, hint: string) => (
    <div key={key}>
      <label className="iam-label" htmlFor={`s-${key}`}>{label}</label>
      <input
        id={`s-${key}`}
        className="iam-input"
        type="number"
        value={String(value(key))}
        onChange={e => setNumber(key, e.target.value)}
      />
      <p className="iam-help">
        {hint}
        {differs(key) && (
          <>
            {' · '}
            <b>stored {String(config?.stored[key])}</b>, overridden by the environment
          </>
        )}
      </p>
    </div>
  );

  const accountCard = (
    <div className="iam-card iam-card-pad" style={{ marginTop: 16 }}>
      <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4 }}>Your own account</div>
      {meError && <div className="iam-alert iam-alert-danger" style={{ marginBottom: 14 }}>{meError}</div>}
      <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 14 }}>
        {me
          ? <>{me.email} · <span className="iam-mono">{roles.join(', ') || 'no role'}</span> · {me.totp_enabled ? 'authenticator app enrolled' : 'no second factor'}</>
          : '—'}
      </div>
      <Link className="iam-btn iam-btn-primary iam-btn-sm" to="/account">
        Password, second factor and sessions
      </Link>
    </div>
  );

  if (!config) {
    return (
      <div>
        <PageHeader title="Settings" description="Runtime configuration of this deployment" />
        <div className="iam-page">
          {error && <div className="iam-alert iam-alert-danger">{error}</div>}
          {accountCard}
        </div>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title="Deployment settings"
        description={`What this instance is actually running with. Values from the environment are read-only here, on purpose · version ${config.config_version}`}
      />

      <div className="iam-page">
        {error && <div className="iam-alert iam-alert-danger" style={{ marginBottom: 12 }}>{error}</div>}
        {saved && <div className="iam-alert iam-alert-success" style={{ marginBottom: 12 }}>{saved}</div>}

        <form onSubmit={save}>
          <div className="iam-card iam-card-pad">
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 14 }}>Mail</div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 14 }}>
              {MAIL_TEXT.map(({ key, label }) => (
                <div key={key}>
                  <label className="iam-label" htmlFor={`s-${key}`}>{label}</label>
                  <input
                    id={`s-${key}`}
                    className="iam-input"
                    value={String(value(key))}
                    onChange={e => setDraft(d => ({ ...d, [key]: e.target.value }))}
                  />
                  {differs(key) && (
                    <p className="iam-help">
                      <b>stored {String(config.stored[key])}</b>, overridden by the environment
                    </p>
                  )}
                </div>
              ))}
              {numberField('smtp_port', 'SMTP port', 'Clamped 1–65535')}
              <div>
                <label className="iam-label" htmlFor="s-smtp_start_tls">STARTTLS</label>
                <div>
                  <input
                    id="s-smtp_start_tls"
                    className="iam-switch"
                    type="checkbox"
                    checked={value('smtp_start_tls')}
                    onChange={e => setDraft(d => ({ ...d, smtp_start_tls: e.target.checked }))}
                  />
                </div>
                <p className="iam-help">Negotiated on port 587. Turn off for implicit TLS on 465.</p>
              </div>
            </div>
            <p className="iam-help" style={{ marginTop: 14 }}>
              The relay password is not here and cannot be: the instance row has no column for it, so
              it comes from <span className="iam-mono">Smtp:Password</span> in the environment and
              changing it is still a deployment operation.
            </p>
          </div>

          <div className="iam-card iam-card-pad" style={{ marginTop: 16 }}>
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 14 }}>Sign-in policy</div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 14 }}>
              {NUMBERS.map(({ key, label, hint }) => numberField(key, label, hint))}
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
        <div className="iam-card iam-card-pad" style={{ marginTop: 16 }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 4 }}>
            <div style={{ fontSize: 13, fontWeight: 600 }}>Trust anchors and posture</div>
            <span className="iam-chip">read-only · environment</span>
          </div>
          <p style={{ fontSize: 12, color: 'var(--fg-muted)', marginTop: 0 }}>
            Argon2 costs drive the pod&apos;s memory limit; the Ory URLs and trusted proxies are trust
            anchors, which a process must not learn from data it can write; the URLs decide where the
            OAuth2 client this console signed in through points.
          </p>
          <div>
            {Object.entries(config.environment_only).map(([key, v]) => (
              <div
                key={key}
                style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 16, padding: '11px 0', borderTop: '1px solid var(--border)' }}
              >
                <div>
                  <div style={{ fontSize: 13 }}>{key}</div>
                  {ENVIRONMENT_ONLY_NOTES[key] && (
                    <div style={{ fontSize: 11.5, color: 'var(--fg-muted)' }}>{ENVIRONMENT_ONLY_NOTES[key]}</div>
                  )}
                </div>
                <span className="iam-mono" style={{ fontSize: 12, color: 'var(--fg-muted)', textAlign: 'right' }}>
                  {String(v) || '—'}
                </span>
              </div>
            ))}
          </div>
        </div>

        {accountCard}

        <KeyRotationPanel />
      </div>
    </div>
  );
}
