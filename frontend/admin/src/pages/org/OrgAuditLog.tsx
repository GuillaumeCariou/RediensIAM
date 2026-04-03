import { useEffect, useState } from 'react';
import { ChevronLeft, ChevronRight, Download } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Skeleton } from '@/components/ui/skeleton';
import { getAuditLog, exportOrgAuditLog } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface AuditEntry {
  id: string; action: string; actor_email: string | null; actor_id: string | null;
  target_type: string | null; target_id: string | null; ip_address: string | null; created_at: string;
}

const PAGE_SIZE = 50;

export default function OrgAuditLog() {
  const { orgId, isSystemCtx } = useOrgContext();
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [offset, setOffset] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [exporting, setExporting] = useState(false);

  const handleExport = async () => {
    setExporting(true);
    try {
      const blob = await exportOrgAuditLog(orgId ?? '', isSystemCtx);
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
    getAuditLog({ org_id: orgId, limit: PAGE_SIZE, offset: off })
      .then(res => { const rows = Array.isArray(res) ? res : (res?.entries ?? []); setEntries(rows); setHasMore(rows.length === PAGE_SIZE); })
      .catch(console.error).finally(() => setLoading(false));
  };
  useEffect(() => { load(0); }, [orgId]);

  const prev = () => { const o = Math.max(0, offset - PAGE_SIZE); setOffset(o); load(o); };
  const next = () => { const o = offset + PAGE_SIZE; setOffset(o); load(o); };

  return (
    <div>
      <PageHeader
        title="Audit Log"
        description="Actions performed within this organisation"
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
                ? Array.from({ length: 6 }).map((_, i) => <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>)
                : entries.length === 0
                ? <TableRow><TableCell colSpan={5} className="text-center text-muted-foreground py-8">No events yet</TableCell></TableRow>
                : entries.map(e => (
                    <TableRow key={e.id}>
                      <TableCell className="text-xs text-muted-foreground whitespace-nowrap">{fmtDate(e.created_at)}</TableCell>
                      <TableCell><Badge variant="secondary" className="font-mono text-xs">{e.action}</Badge></TableCell>
                      <TableCell>
                        <p className="text-sm">{e.actor_email ?? '—'}</p>
                        {e.actor_id && <p className="text-xs text-muted-foreground font-mono">{e.actor_id.slice(0, 8)}…</p>}
                      </TableCell>
                      <TableCell>
                        {e.target_type && <Badge variant="outline" className="text-xs">{e.target_type}</Badge>}
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
            <Button variant="outline" size="sm" onClick={prev} disabled={offset === 0 || loading}><ChevronLeft className="h-4 w-4" />Previous</Button>
            <Button variant="outline" size="sm" onClick={next} disabled={!hasMore || loading}>Next<ChevronRight className="h-4 w-4" /></Button>
          </div>
        </div>
      </div>
    </div>
  );
}
