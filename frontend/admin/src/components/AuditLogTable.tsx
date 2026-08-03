import { ScrollText, ChevronLeft, ChevronRight, Download } from 'lucide-react';
import { fmtDate } from '@/lib/utils';
import { IamChip } from '@/components/iam';

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

/** The prop still speaks in the old variant names; this is the one place that translates. */
const ACTION_TONE = {
  default: 'accent', destructive: 'danger', success: 'success', warning: 'warn', secondary: 'default',
} as const;

export default function AuditLogTable({ entries, loading, offset, hasMore, exporting, onPrev, onNext, onExport, actionColors }: Readonly<Props>) {
  return (
    <div className="p-6 space-y-4">
      <div className="flex justify-end">
        <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={onExport} disabled={exporting}>
          <Download className="h-4 w-4" />{exporting ? 'Exporting…' : 'Export CSV'}
        </button>
      </div>
      <div className="rounded-xl border bg-card overflow-hidden">
        <table className="iam-tbl">
          <thead>
            <tr>
              <th>Time</th>
              <th>Action</th>
              <th>Actor</th>
              <th>Target</th>
              <th>IP</th>
            </tr>
          </thead>
          <tbody>
            {(() => {
              if (loading) return (
                Array.from({ length: 8 }, (_, i) => `sk-row-${i}`).map(rowId => (
                  <tr key={rowId}>
                    {Array.from({ length: 5 }, (_, j) => `sk-cell-${j}`).map(cellId => (
                      <td key={cellId}><div className="iam-skeleton h-4 w-full" /></td>
                    ))}
                  </tr>
                ))
              );
              if (entries.length === 0) return (
                <tr>
                  <td className="text-center text-muted-foreground py-12" colSpan={5}>
                    <ScrollText className="h-8 w-8 mx-auto mb-2 opacity-40" />
                    No audit events found
                  </td>
                </tr>
              );
              return entries.map(e => (
                <tr key={e.id}>
                  <td className="text-xs text-muted-foreground whitespace-nowrap">{fmtDate(e.created_at)}</td>
                  <td>
                    <IamChip className="font-mono text-xs" tone={ACTION_TONE[actionColors?.[e.action] ?? 'secondary']}>
                      {e.action}
                    </IamChip>
                  </td>
                  <td>
                    {e.actor_id
                      ? <p className="text-xs text-muted-foreground font-mono">{e.actor_id.slice(0, 8)}…</p>
                      : <span className="text-muted-foreground">—</span>}
                  </td>
                  <td>
                    {e.target_type && <IamChip className="text-xs" tone="default">{e.target_type}</IamChip>}
                    {e.target_id && <p className="text-xs text-muted-foreground font-mono mt-0.5">{e.target_id.slice(0, 8)}…</p>}
                  </td>
                  <td className="text-xs text-muted-foreground font-mono">{e.ip_address ?? '—'}</td>
                </tr>
              ));
            })()}
          </tbody>
        </table>
      </div>

      <div className="flex items-center justify-between text-sm text-muted-foreground">
        <span>{entries.length === 0 ? 'No results' : `Showing ${offset + 1}–${offset + entries.length}`}</span>
        <div className="flex gap-2">
          <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={onPrev} disabled={offset === 0 || loading}>
            <ChevronLeft className="h-4 w-4" />Previous
          </button>
          <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={onNext} disabled={!hasMore || loading}>
            Next<ChevronRight className="h-4 w-4" />
          </button>
        </div>
      </div>
    </div>
  );
}
