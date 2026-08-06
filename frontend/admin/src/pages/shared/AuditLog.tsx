import { useCallback, useEffect, useState } from 'react';
import { exportOrgAuditLog, exportSystemAuditLog, getAuditLog } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import AuditLogTable, { type AuditEntry } from '@/components/AuditLogTable';
import type { Level } from '@/scope';

/**
 * The audit log, at the deployment or at one organisation.
 *
 * The two pages this replaces differed in two calls — which rows to read and which export to
 * ask for — and were otherwise the same file twice, down to an identical comment explaining the
 * export's error handling. What that duplication cost was visible: the action colours existed on
 * the deployment page only, so the same `org_deleted` row was red for a super-admin and grey for
 * the tenant whose organisation it was about.
 */

/**
 * Colours for the actions worth spotting at a glance. Shared by both levels, which is the fix:
 * a destructive action should not change colour with who is reading it.
 */
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
  'impersonation.opened': 'warning',
  'impersonation.revoked': 'secondary',
};

const PAGE_SIZE = 50;

export default function AuditLog({ level }: Readonly<{ level: Level }>) {
  const { orgId, isSystemCtx } = useOrgContext();
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [offset, setOffset] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [exporting, setExporting] = useState(false);

  // `try`/`finally` with no `catch` re-enabled the button and said nothing: a refused export was
  // indistinguishable from a browser that blocked the download, and the rejection escaped as an
  // unhandled promise. The failure now has a name on screen.
  const [exportError, setExportError] = useState('');

  const handleExport = async () => {
    setExporting(true);
    setExportError('');
    try {
      const blob = level === 'deployment'
        ? await exportSystemAuditLog()
        : await exportOrgAuditLog(orgId ?? '', isSystemCtx);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `audit-log-${new Date().toISOString().slice(0, 10)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      setExportError('Could not export the audit log. Nothing was downloaded.');
    } finally { setExporting(false); }
  };

  const load = useCallback((off: number) => {
    setLoading(true);
    // The deployment log is every organisation's rows; an organisation's is its own. `isSystemCtx`
    // picks which of the two an org page reads, and it changes with the scope switcher while
    // `orgId` stays the same — so it belongs in the deps, not only in the closure.
    // Named rather than inlined: a ternary between two string literals widens to `string`, which
    // the query's union type refuses. The annotation is what keeps it narrow, and it does the job
    // an `as` was doing without asserting anything.
    const scope: 'system' | 'org' = isSystemCtx ? 'system' : 'org';
    const query = level === 'deployment'
      ? { limit: PAGE_SIZE, offset: off }
      : { scope, org_id: orgId, limit: PAGE_SIZE, offset: off };

    getAuditLog(query)
      .then(res => {
        const rows = Array.isArray(res) ? res : (res?.entries ?? []);
        setEntries(rows);
        setHasMore(rows.length === PAGE_SIZE);
      })
      .catch(console.error)
      .finally(() => setLoading(false));
  }, [level, orgId, isSystemCtx]);

  useEffect(() => { load(0); setOffset(0); }, [load]);

  const prev = () => { const o = Math.max(0, offset - PAGE_SIZE); setOffset(o); load(o); };
  const next = () => { const o = offset + PAGE_SIZE; setOffset(o); load(o); };

  return (
    <div>
      <PageHeader
        title="Audit log"
        description={level === 'deployment'
          ? 'Complete history of all administrative actions'
          : 'Administrative actions in this organisation'}
      />
      {exportError && (
        <div className="iam-alert iam-alert-danger" style={{ margin: '0 24px 12px' }}>{exportError}</div>
      )}
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
