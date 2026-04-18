import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Skeleton } from '@/components/ui/skeleton';
import { fmtDate } from '@/lib/utils';

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
    <Dialog open={!!userEmail} onOpenChange={v => { if (!v) onClose(); }}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Active sessions — {userEmail}</DialogTitle>
          <DialogDescription>OAuth2 applications this user has granted access to.</DialogDescription>
        </DialogHeader>
        {(() => {
          if (loading) return (
            <div className="space-y-2 py-2">{Array.from({ length: 3 }, (_, i) => `sk-${i}`).map(id => <Skeleton key={id} className="h-8 w-full" />)}</div>
          );
          if (sessions.length === 0) return (
            <p className="text-sm text-muted-foreground py-4 text-center">No active sessions.</p>
          );
          return (
            <Table>
              <TableHeader>
                <TableRow><TableHead>App</TableHead><TableHead>Granted</TableHead></TableRow>
              </TableHeader>
              <TableBody>
                {sessions.map(s => (
                  <TableRow key={s.client_id}>
                    <TableCell className="text-sm font-medium">{s.client_name ?? s.client_id}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">{fmtDate(s.granted_at ?? null)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          );
        })()}
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>Close</Button>
          <Button variant="destructive" disabled={revokeAllLoading || sessions.length === 0} onClick={onRevokeAll}>
            {revokeAllLoading ? 'Revoking…' : 'Revoke all sessions'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
