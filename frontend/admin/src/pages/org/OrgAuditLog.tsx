import { useEffect, useState } from 'react';
import { getAuditLog, exportOrgAuditLog } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import AuditLogTable from '@/components/AuditLogTable';
import type { AuditEntry } from '@/components/AuditLogTable';

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
      .then(res => {
        const rows = Array.isArray(res) ? res : (res?.entries ?? []);
        setEntries(rows);
        setHasMore(rows.length === PAGE_SIZE);
      })
      .catch(console.error)
      .finally(() => setLoading(false));
  };

  useEffect(() => { load(0); }, [orgId]);

  const prev = () => { const o = Math.max(0, offset - PAGE_SIZE); setOffset(o); load(o); };
  const next = () => { const o = offset + PAGE_SIZE; setOffset(o); load(o); };

  return (
    <div>
      <PageHeader title="Audit Log" description="Actions performed within this organisation" />
      <AuditLogTable
        entries={entries}
        loading={loading}
        offset={offset}
        hasMore={hasMore}
        exporting={exporting}
        onPrev={prev}
        onNext={next}
        onExport={handleExport}
      />
    </div>
  );
}
