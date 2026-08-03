import { useCallback, useEffect, useState } from 'react';
import { getOrg, getOrgInfo, updateOrg, updateOrgInfo } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';

const RETENTION_OPTIONS = [
  { value: '30', label: '30 days' },
  { value: '60', label: '60 days' },
  { value: '90', label: '90 days' },
  { value: '180', label: '180 days' },
  { value: '365', label: '1 year' },
  { value: '', label: 'Forever' },
];

export default function OrgSettings() {
  // This page is routed at both /org/settings and /system/organisations/:id/settings. It used to
  // call the token-scoped /org routes in both cases, so a super admin opening another
  // organisation's settings read and wrote their OWN org's retention while the URL said otherwise.
  const { orgId, isSystemCtx } = useOrgContext();

  const [retentionDays, setRetentionDays] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const d: { audit_retention_days?: number | null } =
        isSystemCtx && orgId ? await getOrg(orgId) : await getOrgInfo();
      setRetentionDays(d.audit_retention_days ?? null);
      setError('');
    } catch {
      // Without this the page rendered "Forever" — the value null also means — so a failed load
      // was indistinguishable from a configured setting, and saving anything wiped the real one.
      setError('Could not load these settings. Reload before changing anything.');
    } finally { setLoading(false); }
  }, [orgId, isSystemCtx]);

  useEffect(() => { void load(); }, [load]);

  const save = async () => {
    setSaving(true);
    try {
      if (isSystemCtx && orgId) await updateOrg(orgId, { audit_retention_days: retentionDays });
      else await updateOrgInfo({ audit_retention_days: retentionDays });
      setSaved(true);
      setTimeout(() => setSaved(false), 3000);
    } catch {
      setError('Could not save. Nothing was changed.');
    } finally { setSaving(false); }
  };

  return (
    <div>
      <PageHeader title="Settings" description="Organisation-level configuration" />
      <div className="iam-page" style={{ maxWidth: 600 }}>
        {error && (
          <div className="iam-alert iam-alert-danger" style={{ marginBottom: 12 }}>{error}</div>
        )}
        {loading ? (
          <div style={{ height: 140, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8 }} />
        ) : (
          <div className="iam-card iam-card-pad">
            <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Audit Log Retention</div>
            <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 18 }}>
              Audit logs older than the retention period are automatically deleted.
              Set to "Forever" to disable automatic deletion.
            </div>
            <div style={{ marginBottom: 18 }}>
              <label className="iam-label" htmlFor="org-retention-period">Retention period</label>
              <select
                id="org-retention-period"
                className="iam-input"
                style={{ maxWidth: 200 }}
                value={retentionDays == null ? '' : String(retentionDays)}
                onChange={e => setRetentionDays(e.target.value === '' ? null : Number(e.target.value))}
              >
                {RETENTION_OPTIONS.map(o => (
                  <option key={o.value} value={o.value}>{o.label}</option>
                ))}
              </select>
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 12, paddingTop: 16, borderTop: '1px solid var(--border)' }}>
              <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={save} disabled={saving}>
                {saving ? 'Saving…' : 'Save'}
              </button>
              {saved && <span style={{ fontSize: 13, color: 'var(--success)' }}>Saved!</span>}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
