import { useEffect, useState } from 'react';
import { ScrollText, ChevronLeft, ChevronRight, Download } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { getAuditLog, exportSystemAuditLog } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface AuditEntry {
  id: string;
  action: string;
  actor_id: string | null;
  actor_email: string | null;
  target_type: string | null;
  target_id: string | null;
  org_id: string | null;
  project_id: string | null;
  ip_address: string | null;
  created_at: string;
  metadata: Record<string, string> | null;
}

const ACTION_COLORS: Record<string, 'default' | 'destructive' | 'success' | 'warning' | 'secondary'> = {
  login: 'success',
  login_failed: 'destructive',
  logout: 'secondary',
  user_created: 'default',
  user_deleted: 'destructive',
  user_disabled: 'warning',
  role_assigned: 'default',
  role_removed: 'secondary',
  org_suspended: 'warning',
  org_created: 'default',
  org_deleted: 'destructive',
};

const PAGE_SIZE = 50;

export default function AuditLog() {
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [offset, setOffset] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [exporting, setExporting] = useState(false);

  const handleExport = async () => {
    setExporting(true);
    try {
      const blob = await exportSystemAuditLog();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `audit-log-${new Date().toISOString().slice(0, 10)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
    } finally { setExporting(false); }
  };

  const load = (off: number) => {
    setLoading(true);
    getAuditLog({ limit: PAGE_SIZE, offset: off })
      .then(res => {
        const rows = Array.isArray(res) ? res : (res?.entries ?? []);
        setEntries(rows);
        setHasMore(rows.length === PAGE_SIZE);
      })
      .catch(console.error)
      .finally(() => setLoading(false));
  };

  useEffect(() => { load(0); }, []);

  const prev = () => { const o = Math.max(0, offset - PAGE_SIZE); setOffset(o); load(o); };
  const next = () => { const o = offset + PAGE_SIZE; setOffset(o); load(o); };

  return (
    <div>
      <PageHeader
        title="Audit Log"
        description="Complete history of all administrative actions"
        action={
          <Button variant="outline" size="sm" onClick={handleExport} disabled={exporting}>
            <Download className="h-4 w-4" />{exporting ? 'Exporting…' : 'Export CSV'}
          </Button>
        }
      />
      <div className="p-6 space-y-4">
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Time</TableHead>
                <TableHead>Action</TableHead>
                <TableHead>Actor</TableHead>
                <TableHead>Target</TableHead>
                <TableHead>IP</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 8 }).map((_, i) => (
                    <TableRow key={i}>
                      {Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}
                    </TableRow>
                  ))
                : entries.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                        <ScrollText className="h-8 w-8 mx-auto mb-2 opacity-40" />
                        No audit events found
                      </TableCell>
                    </TableRow>
                  )
                : entries.map(e => (
                    <TableRow key={e.id}>
                      <TableCell className="text-xs text-muted-foreground whitespace-nowrap">{fmtDate(e.created_at)}</TableCell>
                      <TableCell>
                        <Badge variant={ACTION_COLORS[e.action] ?? 'secondary'} className="font-mono text-xs">
                          {e.action}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <p className="text-sm">{e.actor_email ?? '—'}</p>
                        {e.actor_id && <p className="text-xs text-muted-foreground font-mono">{e.actor_id.slice(0, 8)}…</p>}
                      </TableCell>
                      <TableCell>
                        {e.target_type && <Badge variant="secondary" className="text-xs">{e.target_type}</Badge>}
                        {e.target_id && <p className="text-xs text-muted-foreground font-mono mt-0.5">{e.target_id.slice(0, 8)}…</p>}
                      </TableCell>
                      <TableCell className="text-xs text-muted-foreground font-mono">{e.ip_address ?? '—'}</TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>

        <div className="flex items-center justify-between text-sm text-muted-foreground">
          <span>Showing {offset + 1}–{offset + entries.length}</span>
          <div className="flex gap-2">
            <Button variant="outline" size="sm" onClick={prev} disabled={offset === 0 || loading}>
              <ChevronLeft className="h-4 w-4" />Previous
            </Button>
            <Button variant="outline" size="sm" onClick={next} disabled={!hasMore || loading}>
              Next<ChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
