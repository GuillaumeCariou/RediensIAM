import { useState } from 'react';
import { ShieldCheck } from 'lucide-react';
import { IamChip, IamDialog } from '@/components/iam';
import { verifyAuditChain } from '@/api';
import { ApiError } from '@/auth';

interface Chain {
  org_id: string | null;
  first_break: number | null;
  verified: number;
  unverifiable: number;
  intact: boolean;
  fully_verified: boolean;
}

interface Report {
  chains: Chain[];
  broken: number;
}

function apiErrorMessage(e: unknown, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return body?.detail ?? body?.error ?? fallback;
}

/** `null` is the deployment-wide chain — the rows that belong to no organisation. */
const scopeLabel = (orgId: string | null) => orgId ?? 'Deployment-wide';

/**
 * Verification of the audit hash chain, from the audit log page.
 *
 * Each link is an HMAC-SHA256 whose key lives in the application's environment and not in the
 * database, so a break is not a cosmetic flag: it means a row is no longer what it was written
 * as, or that a row before it is gone. What is reported per chain is therefore the row id where
 * the walk stopped and how far it got — a green tick alone would tell an operator nothing about
 * what to go and look at.
 *
 * `unverifiable` is a third answer, neither pass nor fail: rows written before the chain existed
 * or before it was keyed, or under a key id this deployment no longer holds. Absence of evidence,
 * reported as such rather than counted as verified.
 */
export default function AuditChainCheck() {
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [report, setReport] = useState<Report | null>(null);

  const run = async () => {
    setOpen(true);
    setLoading(true);
    setError('');
    setReport(null);
    try {
      setReport(await verifyAuditChain());
    } catch (e) {
      setError(apiErrorMessage(e, 'Could not verify the audit chain. Nothing was checked.'));
    } finally { setLoading(false); }
  };

  // Broken chains first: on a deployment with many tenants, the one that matters must not be
  // somewhere down a list sorted by anything else.
  const chains = [...(report?.chains ?? [])].sort((a, b) => Number(a.intact) - Number(b.intact));

  return (
    <>
      <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={run} disabled={loading}>
        <ShieldCheck className="h-4 w-4" />{loading ? 'Verifying…' : 'Verify integrity'}
      </button>

      <IamDialog
        open={open}
        onClose={() => setOpen(false)}
        wide
        title="Audit chain integrity"
        desc="Each entry carries a keyed hash of the one before it. Editing or deleting an entry cannot be prevented — it can be seen here."
        footer={<button className="iam-btn iam-btn-secondary" onClick={() => setOpen(false)}>Close</button>}
      >
        {error && <div className="iam-alert iam-alert-danger">{error}</div>}
        {loading && <div className="iam-skeleton h-4 w-full" />}
        {report && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {report.broken > 0 ? (
              <div className="iam-alert iam-alert-danger">
                {report.broken} of {report.chains.length} chains break. An entry has been rewritten or removed
                since it was written. Treat the entries after the break as unproven and preserve the database.
              </div>
            ) : (
              <div className="iam-alert">
                No broken link in {report.chains.length} chains. Entries counted unverifiable are not vouched
                for by this deployment — they predate the chain, its keying, or a key since retired.
              </div>
            )}
            <table className="iam-tbl">
              <thead>
                <tr>
                  <th>Chain</th>
                  <th>State</th>
                  <th>First break</th>
                  <th>Verified</th>
                  <th>Unverifiable</th>
                </tr>
              </thead>
              <tbody>
                {chains.map(c => (
                  <tr key={c.org_id ?? 'deployment'}>
                    <td><IamChip mono>{scopeLabel(c.org_id)}</IamChip></td>
                    <td>
                      {(() => {
                        if (!c.intact) return <IamChip tone="danger">Broken</IamChip>;
                        return c.fully_verified
                          ? <IamChip tone="success">Fully verified</IamChip>
                          : <IamChip tone="warn">Intact, partly unverifiable</IamChip>;
                      })()}
                    </td>
                    <td>{c.first_break === null ? '—' : <IamChip tone="danger" mono>entry #{c.first_break}</IamChip>}</td>
                    <td>{c.verified}</td>
                    <td>{c.unverifiable}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div style={{ fontSize: 12.5, color: 'var(--fg-muted)' }}>
              {report.broken > 0
                ? 'On a broken chain the counts are the height walked before the break, not the height of the chain.'
                : 'Verified plus unverifiable is the height of each chain.'}
            </div>
          </div>
        )}
      </IamDialog>
    </>
  );
}
