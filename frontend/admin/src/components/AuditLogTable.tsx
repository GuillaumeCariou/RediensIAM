import { ScrollText, ChevronLeft, ChevronRight, Download } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { fmtDate } from '@/lib/utils';

export interface AuditEntry {
  id: string;
  action: string;
  actor_id: string | null;
  target_type: string | null;
  target_id: string | null;
  ip_address: string | null;
  created_at: string;
}

interface Props {
  entries: AuditEntry[];
  loading: boolean;
  offset: number;
  hasMore: boolean;
  exporting: boolean;
  onPrev: () => void;
  onNext: () => void;
  onExport: () => void;
  actionColors?: Record<string, 'default' | 'destructive' | 'success' | 'warning' | 'secondary'>;
}

export default function AuditLogTable({ entries, loading, offset, hasMore, exporting, onPrev, onNext, onExport, actionColors }: Readonly<Props>) {
  return (
    <div className="p-6 space-y-4">
      <div className="flex justify-end">
        <Button variant="outline" size="sm" onClick={onExport} disabled={exporting}>
          <Download className="h-4 w-4" />{exporting ? 'Exporting…' : 'Export CSV'}
        </Button>
      </div>
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
            {(() => {
              if (loading) return (
                Array.from({ length: 8 }, (_, i) => `sk-row-${i}`).map(rowId => (
                  <TableRow key={rowId}>
                    {Array.from({ length: 5 }, (_, j) => `sk-cell-${j}`).map(cellId => (
                      <TableCell key={cellId}><Skeleton className="h-4 w-full" /></TableCell>
                    ))}
                  </TableRow>
                ))
              );
              if (entries.length === 0) return (
                <TableRow>
                  <TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                    <ScrollText className="h-8 w-8 mx-auto mb-2 opacity-40" />
                    No audit events found
                  </TableCell>
                </TableRow>
              );
              return entries.map(e => (
                <TableRow key={e.id}>
                  <TableCell className="text-xs text-muted-foreground whitespace-nowrap">{fmtDate(e.created_at)}</TableCell>
                  <TableCell>
                    <Badge variant={actionColors?.[e.action] ?? 'secondary'} className="font-mono text-xs">
                      {e.action}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    {e.actor_id
                      ? <p className="text-xs text-muted-foreground font-mono">{e.actor_id.slice(0, 8)}…</p>
                      : <span className="text-muted-foreground">—</span>}
                  </TableCell>
                  <TableCell>
                    {e.target_type && <Badge variant="secondary" className="text-xs">{e.target_type}</Badge>}
                    {e.target_id && <p className="text-xs text-muted-foreground font-mono mt-0.5">{e.target_id.slice(0, 8)}…</p>}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground font-mono">{e.ip_address ?? '—'}</TableCell>
                </TableRow>
              ));
            })()}
          </TableBody>
        </Table>
      </div>

      <div className="flex items-center justify-between text-sm text-muted-foreground">
        <span>{entries.length === 0 ? 'No results' : `Showing ${offset + 1}–${offset + entries.length}`}</span>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" onClick={onPrev} disabled={offset === 0 || loading}>
            <ChevronLeft className="h-4 w-4" />Previous
          </Button>
          <Button variant="outline" size="sm" onClick={onNext} disabled={!hasMore || loading}>
            Next<ChevronRight className="h-4 w-4" />
          </Button>
        </div>
      </div>
    </div>
  );
}
