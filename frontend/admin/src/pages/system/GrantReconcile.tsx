import { useCallback, useEffect, useState } from 'react';
import { IamChip, IamDialog } from '@/components/iam';
import PageHeader from '@/components/layout/PageHeader';
import { scanGrantReconcile, repairGrantReconcile } from '@/api';
import { ApiError } from '@/auth';

/**
 * Where the Keto tuple store and the database disagree about who has been granted what.
 *
 * Every grant is written twice — the tuple first, the row second — and that pair is best effort,
 * not a transaction. This page is the only place a divergence is visible, so it shows each one in
 * full: the namespace, the object, the relation, the subject, and which store is missing it. A
 * count alone cannot tell an operator whether repairing is safe.
 */

interface Tuple {
  namespace: string;
  object: string;
  relation: string;
  subject: string;
}

interface Report {
  orphan_tuples: Tuple[] | null;
  orphan_rows: Tuple[] | null;
  tuples_revoked: number;
  rows_removed: number;
  /** Set when the server declined to act. It comes back with a 200, not with a rejection. */
  repair_refused: string | null;
}

type Side = 'keto' | 'db';

interface Divergence extends Tuple { side: Side }

const SIDE_LABEL: Record<Side, string> = {
  keto: 'Keto only',
  db: 'Database only',
};

/**
 * What the two classes mean for an operator, since the repair is not symmetric: a tuple with no row
 * authorises requests today, a row with no tuple authorises nothing.
 */
const SIDE_DESC: Record<Side, string> = {
  keto: 'Live privilege — nothing records who granted it. Repair revokes it.',
  db: 'Not live. The consent path still reads it for scopes. Repair deletes the row.',
};

function apiErrorMessage(e: unknown, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return body?.detail ?? body?.error ?? fallback;
}

function rowsOf(report: Report | null): Divergence[] {
  if (!report) return [];
  return [
    ...(report.orphan_tuples ?? []).map(t => ({ ...t, side: 'keto' as const })),
    ...(report.orphan_rows ?? []).map(t => ({ ...t, side: 'db' as const })),
  ];
}

function Cell({ children }: Readonly<{ children: React.ReactNode }>) {
  return <td className="iam-mono" style={{ fontSize: 12 }}>{children}</td>;
}

export default function GrantReconcile() {
  const [report, setReport] = useState<Report | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [repairing, setRepairing] = useState(false);
  const [repairError, setRepairError] = useState('');
  const [refusal, setRefusal] = useState('');
  const [outcome, setOutcome] = useState('');

  /** The scan alone. Sets no state synchronously, so an effect may call it directly. */
  const scan = useCallback(() => {
    scanGrantReconcile()
      .then((r: Report) => { setReport(r); setError(''); })
      .catch((e: unknown) => {
        setReport(null);
        setError(apiErrorMessage(e, 'Could not scan the grant stores.'));
      })
      .finally(() => setLoading(false));
  }, []);

  /** What Rescan calls: the placeholders come back, then the scan. */
  const load = useCallback(() => { setLoading(true); scan(); }, [scan]);

  useEffect(scan, [scan]);

  const divergences = rowsOf(report);
  const tupleCount = report?.orphan_tuples?.length ?? 0;
  const rowCount = report?.orphan_rows?.length ?? 0;

  const handleRepair = async () => {
    setRepairing(true);
    setRepairError('');
    try {
      const r: Report = await repairGrantReconcile();
      setConfirmOpen(false);
      // A refusal arrives as a 200 with a reason. Treating it as success would tell the operator
      // the divergence was repaired while nothing was written.
      setRefusal(r.repair_refused ?? '');
      setOutcome(r.repair_refused
        ? ''
        : `Revoked ${r.tuples_revoked} tuple(s) and removed ${r.rows_removed} row(s).`);
      load();
    } catch (e) {
      setRepairError(apiErrorMessage(e, 'The repair failed. Nothing is guaranteed to have changed.'));
    } finally {
      setRepairing(false);
    }
  };

  return (
    <div>
      <PageHeader
        title="Grant reconciliation"
        description="Grants the Keto tuple store and the database disagree about"
        actions={[
          <button key="scan" className="iam-btn iam-btn-secondary iam-btn-sm" onClick={load} disabled={loading || repairing}>
            Rescan
          </button>,
          <button key="repair" className="iam-btn iam-btn-danger iam-btn-sm"
            onClick={() => { setConfirmOpen(true); setRepairError(''); }}
            disabled={loading || repairing || divergences.length === 0}>
            Repair
          </button>,
        ]}
      />

      {error && <div className="iam-alert iam-alert-danger" style={{ margin: '0 24px 12px' }}>{error}</div>}
      {refusal && <div className="iam-alert iam-alert-warn" style={{ margin: '0 24px 12px' }}>{refusal}</div>}
      {outcome && <div className="iam-alert iam-alert-success" style={{ margin: '0 24px 12px' }}>{outcome}</div>}

      <div className="iam-page" style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        {!loading && report && (
          <div style={{ display: 'flex', gap: 24, fontSize: 13 }}>
            <div>
              <div style={{ color: 'var(--fg-muted)', fontSize: 11 }}>Divergent grants</div>
              <div style={{ fontSize: 20, fontWeight: 600 }}>{divergences.length}</div>
            </div>
            <div>
              <div style={{ color: 'var(--fg-muted)', fontSize: 11 }}>In Keto only</div>
              <div style={{ fontSize: 20, fontWeight: 600 }}>{tupleCount}</div>
            </div>
            <div>
              <div style={{ color: 'var(--fg-muted)', fontSize: 11 }}>In the database only</div>
              <div style={{ fontSize: 20, fontWeight: 600 }}>{rowCount}</div>
            </div>
          </div>
        )}

        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Missing from</th><th>Namespace</th><th>Object</th><th>Relation</th><th>Subject</th>
              </tr>
            </thead>
            <tbody>
              {loading && (
                <tr>{Array.from({ length: 5 }, (_, j) => (
                  <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>
                ))}</tr>
              )}

              {!loading && !error && divergences.length === 0 && (
                <tr><td colSpan={5}>
                  <div className="iam-empty">
                    <div className="iam-empty-title">Nothing to reconcile</div>
                    <div className="iam-empty-desc">
                      Every grant Keto holds has its backing row, and every row has its tuple.
                    </div>
                  </div>
                </td></tr>
              )}

              {!loading && divergences.map(d => (
                <tr key={`${d.side}:${d.namespace}:${d.object}:${d.relation}:${d.subject}`}>
                  <td>
                    <IamChip tone={d.side === 'keto' ? 'danger' : 'warn'}>{SIDE_LABEL[d.side]}</IamChip>
                    <div style={{ fontSize: 11, color: 'var(--fg-muted)', marginTop: 4 }}>{SIDE_DESC[d.side]}</div>
                  </td>
                  <Cell>{d.namespace}</Cell>
                  <Cell>{d.object}</Cell>
                  <Cell>{d.relation}</Cell>
                  <Cell>{d.subject}</Cell>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <IamDialog
        open={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        title="Repair the divergence?"
        desc="This writes to authorisation. It cannot be undone from here."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setConfirmOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-danger" onClick={handleRepair} disabled={repairing}>
              {repairing ? 'Repairing…' : 'Repair'}
            </button>
          </>
        }
      >
        <div style={{ fontSize: 13, display: 'flex', flexDirection: 'column', gap: 8 }}>
          <div>
            <b>{tupleCount}</b> tuple(s) will be deleted from Keto. Whoever holds them loses that
            privilege immediately.
          </div>
          <div>
            <b>{rowCount}</b> row(s) will be deleted from the database. The scopes they resolve stop
            being minted into tokens.
          </div>
          <div style={{ color: 'var(--fg-muted)' }}>
            No tuple is ever created from a row: authority only converges downward. Each item is
            re-checked against the other store before it is touched.
          </div>
          {repairError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{repairError}</p>}
        </div>
      </IamDialog>
    </div>
  );
}
