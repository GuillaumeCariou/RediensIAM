import { fmtDate } from '@/lib/utils';
import { IamDialog } from '@/components/iam';

export interface OAuthSession {
  client_id: string;
  client_name?: string;
  granted_at?: string;
}

interface Props {
  userEmail: string | null;
  sessions: OAuthSession[];
  loading: boolean;
  revokeAllLoading: boolean;
  onClose: () => void;
  onRevokeAll: () => void;
}

export default function SessionsDialog({ userEmail, sessions, loading, revokeAllLoading, onClose, onRevokeAll }: Readonly<Props>) {
  return (
    <IamDialog open={!!userEmail} onClose={() => onClose()}
      title={<>Active sessions — {userEmail}</>}
      desc="OAuth2 applications this user has granted access to."
      footer={<><button className="iam-btn iam-btn-secondary" onClick={onClose}>Close</button>
          <button className="iam-btn iam-btn-danger" disabled={revokeAllLoading || sessions.length === 0} onClick={onRevokeAll}>
            {revokeAllLoading ? 'Revoking…' : 'Revoke all sessions'}
          </button></>}
    >
{(() => {
          if (loading) return (
            <div className="space-y-2 py-2">{Array.from({ length: 3 }, (_, i) => `sk-${i}`).map(id => <div className="iam-skeleton h-8 w-full" key={id} />)}</div>
          );
          if (sessions.length === 0) return (
            <p className="text-sm text-muted-foreground py-4 text-center">No active sessions.</p>
          );
          return (
            <table className="iam-tbl">
              <thead>
                <tr><th>App</th><th>Granted</th></tr>
              </thead>
              <tbody>
                {sessions.map(s => (
                  <tr key={s.client_id}>
                    <td className="text-sm font-medium">{s.client_name ?? s.client_id}</td>
                    <td className="text-sm text-muted-foreground">{fmtDate(s.granted_at ?? null)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          );
        })()}
    </IamDialog>
  );
}
