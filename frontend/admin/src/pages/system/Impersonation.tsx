import { useCallback, useEffect, useState } from 'react';
import { IamChip, IamDialog } from '@/components/iam';
import PageHeader from '@/components/layout/PageHeader';
import { listImpersonations, revokeImpersonation } from '@/api';
import { fmtDate } from '@/lib/utils';

/**
 * Live delegated sessions — who is acting for which tenant, since when, and why.
 *
 * This page is the second half of a feature that shipped without it. `IMPERSONATION.md` §5 states
 * the rule it exists to satisfy: *an impersonation nobody can list is an impersonation nobody can
 * stop*. The routes were there from 0.7.0 and the console had no way to reach them.
 *
 * <p>It supervises and does not create. Opening a session mints a credential and is refused to a
 * browser session on purpose; what an operator needs here is to see what is running and to end
 * it.</p>
 */

interface Session {
  session_id: string;
  act_sub: string;
  act_level: string;
  org_id: string;
  project_id: string;
  mode: string;
  reason: string;
  created_at: string;
  expires_at: string;
  last_used_at: string | null;
}

export default function Impersonation() {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [revokeTarget, setRevokeTarget] = useState<Session | null>(null);
  const [error, setError] = useState('');

  const load = useCallback(() => {
    setLoading(true);
    listImpersonations()
      .then((r: Session[]) => setSessions(r ?? []))
      .catch(() => setError('Could not read the live sessions.'))
      .finally(() => setLoading(false));
  }, []);

  useEffect(load, [load]);

  const handleRevoke = async () => {
    if (!revokeTarget) return;
    try {
      await revokeImpersonation(revokeTarget.session_id);
      setRevokeTarget(null);
      load();
    } catch {
      setError('Could not end that session. It may have ended on its own.');
      setRevokeTarget(null);
    }
  };

  return (
    <div>
      <PageHeader
        title="Impersonation"
        description="Operators currently acting for a customer organisation"
      />

      {error && <div className="iam-alert iam-alert-danger" style={{ margin: '0 24px 12px' }}>{error}</div>}

      <div className="iam-page">
        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Operator</th><th>Organisation</th><th>Mode</th><th>Reason</th>
                <th>Opened</th><th>Expires</th><th style={{ width: 80 }}></th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <tr>{Array.from({ length: 7 }, (_, j) => (
                  <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>
                ))}</tr>
              )}

              {!loading && sessions.length === 0 && (
                <tr><td colSpan={7}>
                  <div className="iam-empty">
                    <div className="iam-empty-title">No live sessions</div>
                    <div className="iam-empty-desc">
                      Sessions are opened by a service account and end on their own within the hour.
                    </div>
                  </div>
                </td></tr>
              )}

              {!loading && sessions.map(s => (
                <tr key={s.session_id}>
                  <td>
                    <div className="iam-mono" style={{ fontSize: 12 }}>{s.act_sub}</div>
                    <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{s.act_level}</div>
                  </td>
                  <td className="iam-mono" style={{ fontSize: 12 }}>{s.org_id}</td>
                  <td>
                    {/* read is the weaker capability, and the one that must be visible at a glance. */}
                    <IamChip tone={s.mode === 'write' ? 'warning' : 'default'}>{s.mode}</IamChip>
                  </td>
                  <td style={{ fontSize: 12 }}>{s.reason}</td>
                  <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(s.created_at)}</td>
                  <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(s.expires_at)}</td>
                  <td>
                    <button className="iam-btn iam-btn-ghost iam-btn-sm" style={{ color: 'var(--danger)' }}
                      onClick={() => setRevokeTarget(s)}>End</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <IamDialog
        open={!!revokeTarget}
        onClose={() => setRevokeTarget(null)}
        title="End this session?"
        desc="The delegated token stops working immediately, not at its expiry."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setRevokeTarget(null)}>Cancel</button>
            <button className="iam-btn iam-btn-danger" onClick={handleRevoke}>End session</button>
          </>
        }
      >
        <div style={{ fontSize: 13 }}>
          <div><b>{revokeTarget?.act_sub}</b> acting for <b>{revokeTarget?.org_id}</b></div>
          <div style={{ color: 'var(--fg-muted)', marginTop: 4 }}>{revokeTarget?.reason}</div>
        </div>
      </IamDialog>
    </div>
  );
}
