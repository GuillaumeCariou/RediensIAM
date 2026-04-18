import { useEffect, useState } from 'react';
import { getAuditLog, exportSystemAuditLog } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import AuditLogTable from '@/components/AuditLogTable';
import type { AuditEntry } from '@/components/AuditLogTable';

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
      <PageHeader title="Audit Log" description="Complete history of all administrative actions" />
      <AuditLogTable
        entries={entries}
        loading={loading}
        offset={offset}
        hasMore={hasMore}
        exporting={exporting}
        onPrev={prev}
        onNext={next}
        onExport={handleExport}
        actionColors={ACTION_COLORS}
      />
    </div>
  );
}
