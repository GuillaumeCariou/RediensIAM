import { useCallback, useEffect, useState } from 'react';
import { IamChip, IamDialog } from '@/components/iam';
import { getKeyRotationStatus, reEncryptKeys } from '@/api';
import { ApiError } from '@/auth';

/**
 * Where a root-key rotation stands, and the sweep that finishes it.
 *
 * <p>Encryption is already lazy — every write goes out under the active key — but a value written
 * once and only read afterwards (a TOTP secret at enrolment) never migrates on its own. So the
 * count that matters is <b>pending</b>: while it is above zero the retired key is still needed to
 * decrypt, and removing it from <c>Security:EncryptionKeys</c> loses data. Zero is the only signal
 * that it can go — and only for ciphertexts, not for the audit hash chain, which has no sweep.</p>
 */

interface Column { column: string; pending: number }

interface Status {
  active_key_id: number;
  configured_key_ids: number[];
  columns: Column[];
  total_pending: number;
}

function apiErrorMessage(e: unknown, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return body?.detail ?? body?.error ?? fallback;
}

export default function KeyRotationPanel() {
  const [status, setStatus] = useState<Status | null>(null);
  const [loading, setLoading] = useState(true);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState('');
  const [done, setDone] = useState('');

  const load = useCallback(() => {
    getKeyRotationStatus()
      .then((s: Status) => { setStatus(s); setError(''); })
      .catch((e: unknown) => setError(apiErrorMessage(e, 'Could not read the key rotation status.')))
      .finally(() => setLoading(false));
  }, []);

  useEffect(load, [load]);

  const handleReEncrypt = async () => {
    setRunning(true);
    setError('');
    setDone('');
    try {
      // The sweep answers with the status it reached, so nothing has to be re-read to know
      // whether it finished — and a partial run still reports what is left.
      const after: Status = await reEncryptKeys();
      setStatus(after);
      setConfirmOpen(false);
      setDone(after.total_pending === 0
        ? 'Sweep complete — every ciphertext is under the active key.'
        : `Sweep ran, ${after.total_pending} value(s) still pending. Run it again.`);
    } catch (e) {
      setError(apiErrorMessage(e, 'The sweep failed. Nothing was lost — re-run it once the cause is fixed.'));
    } finally { setRunning(false); }
  };

  const retired = status ? status.configured_key_ids.filter(id => id !== status.active_key_id) : [];

  return (
    <div className="iam-card" style={{ padding: 16, marginTop: 16 }}>
      <h3 style={{ marginTop: 0 }}>Encryption key rotation</h3>
      <p style={{ fontSize: 12, color: 'var(--fg-muted)', marginTop: 0 }}>
        Stored secrets are encrypted under a root key. A retired key can only be removed from the
        deployment once nothing is left pending — it is still needed to decrypt everything below.
      </p>

      {/* While the dialog is up it carries the refusal itself — behind a modal the alert is text
          the operator cannot see, and two copies of one message read as two failures. */}
      {error && !confirmOpen && <div className="iam-alert iam-alert-danger" style={{ marginBottom: 12 }}>{error}</div>}
      {done && <div className="iam-alert iam-alert-success" style={{ marginBottom: 12 }}>{done}</div>}

      {loading && <div style={{ fontSize: 12, color: 'var(--fg-muted)' }}>Loading…</div>}

      {status && (
        <>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 12, flexWrap: 'wrap' }}>
            <IamChip tone="accent" mono>active key k{status.active_key_id}</IamChip>
            {retired.length > 0
              ? <IamChip tone="warn" mono>also configured: {retired.map(id => `k${id}`).join(', ')}</IamChip>
              : <IamChip mono>no other key configured</IamChip>}
            <IamChip tone={status.total_pending === 0 ? 'success' : 'warn'}>
              {status.total_pending === 0
                ? 'nothing pending'
                : `${status.total_pending} value(s) pending`}
            </IamChip>
          </div>

          <table className="iam-tbl">
            <tbody>
              {status.columns.map(c => (
                <tr key={c.column}>
                  <td style={{ width: 260 }} className="iam-mono">{c.column}</td>
                  <td style={{ fontSize: 12 }}>
                    {c.pending === 0 ? 'up to date' : `${c.pending} pending`}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <div style={{ marginTop: 12 }}>
            <button
              className="iam-btn iam-btn-primary"
              onClick={() => { setDone(''); setConfirmOpen(true); }}
              disabled={running || status.total_pending === 0}
            >
              Re-encrypt now
            </button>
            {status.total_pending === 0 && (
              <span style={{ fontSize: 12, color: 'var(--fg-muted)', marginLeft: 8 }}>
                Nothing to re-encrypt. The keys not marked active may be removed from the deployment.
              </span>
            )}
          </div>
        </>
      )}

      <IamDialog
        open={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        title="Re-encrypt every pending value?"
        desc={`This rewrites ${status?.total_pending ?? 0} stored secret(s) under key k${status?.active_key_id ?? '?'} — TOTP secrets, webhook secrets, SMTP passwords and login themes. It runs in one request and can take minutes on a large deployment. It is safe to re-run: rows already on the active key are not touched. Every key that encrypted a pending row must still be configured, or the sweep stops rather than dropping a value.`}
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setConfirmOpen(false)} disabled={running}>Cancel</button>
            <button className="iam-btn iam-btn-primary" onClick={handleReEncrypt} disabled={running}>
              {running ? 'Re-encrypting…' : 'Re-encrypt'}
            </button>
          </>
        }
      >
        {error && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{error}</p>}
      </IamDialog>
    </div>
  );
}
