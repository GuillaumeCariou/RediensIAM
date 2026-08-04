import { useEffect, useState } from 'react';
import { useParams, useNavigate } from 'react-router';
import { ArrowLeft, Sparkles, Download } from 'lucide-react';
import { getUserList, getSystemUserList, cleanupUserList, exportUserList } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import UserListMembersPanel from '@/components/UserListMembersPanel';
import { IamChip, IamDialog } from '@/components/iam';

interface UserList {
  id: string; name: string; org_id?: string | null; org_name?: string | null;
  immovable: boolean; user_count: number; created_at: string;
}

/**
 * Registered under three route shapes: `:id` (standalone /system/userlists/:id and /org/userlists/:id)
 * and `:listId` (a list of an org viewed from the system context). Both params are read below;
 * dropping either breaks one of the routes with no compile error.
 */
export default function UserListDetail() {
  const { id, listId } = useParams<{ id?: string; listId?: string }>();
  const resolvedId = listId ?? id ?? '';
  const navigate = useNavigate();
  const { isSystemCtx } = useOrgContext();

  const [list, setList] = useState<UserList | null>(null);
  const [loading, setLoading] = useState(true);

  const [exporting, setExporting] = useState(false);
  const [cleanupOpen, setCleanupOpen]         = useState(false);
  const [cleanupDryRun, setCleanupDryRun]     = useState(true);
  const [cleanupInactive, setCleanupInactive] = useState(false);
  const [cleanupDays, setCleanupDays]         = useState(90);
  const [cleanupRunning, setCleanupRunning]   = useState(false);
  // `try`/`finally` with no `catch` re-enabled the button and told the operator nothing: a refused
  // export looked exactly like a browser that blocked the download, and the rejection escaped
  // unhandled. Two states because the two actions are read in two different places — the export
  // from the page, the cleanup from inside its dialog, which covers the page.
  const [exportError, setExportError]         = useState('');
  const [cleanupError, setCleanupError]       = useState('');
  const [cleanupResult, setCleanupResult]     = useState<{
    orphaned_roles_found: number; inactive_users_found: number;
    orphaned_roles_removed: number; inactive_users_removed: number; dry_run: boolean;
  } | null>(null);

  useEffect(() => {
    if (!resolvedId) return;
    const fetch = isSystemCtx ? getSystemUserList(resolvedId) : getUserList(resolvedId);
    fetch.then(setList).catch(console.error).finally(() => setLoading(false));
  }, [resolvedId, isSystemCtx]);

  const handleExport = async () => {
    if (!resolvedId) return;
    setExporting(true);
    setExportError('');
    try {
      const blob = await exportUserList(resolvedId);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `userlist-${resolvedId.slice(0, 8)}-${new Date().toISOString().slice(0, 10)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      setExportError('Could not export this list. Nothing was downloaded.');
    } finally { setExporting(false); }
  };

  const handleCleanup = async () => {
    if (!resolvedId) return;
    setCleanupRunning(true); setCleanupResult(null); setCleanupError('');
    try {
      const res = await cleanupUserList(resolvedId, {
        remove_orphaned_roles: true,
        remove_inactive_users: cleanupInactive,
        inactive_threshold_days: cleanupDays,
        dry_run: cleanupDryRun,
      });
      setCleanupResult(res);
    } catch {
      setCleanupError('The cleanup could not run. Nothing was changed.');
    } finally { setCleanupRunning(false); }
  };

  let cleanupLabel: string;
  if (cleanupRunning) cleanupLabel = 'Running…';
  else if (cleanupDryRun) cleanupLabel = 'Preview';
  else cleanupLabel = 'Run Cleanup';

  return (
    <div>
      <PageHeader
        title={loading ? 'Loading…' : (list?.name ?? 'User List')}
        description={list?.org_name ? `Organisation: ${list.org_name}` : undefined}
        action={
          <div className="flex items-center gap-2">
            {list && (list.immovable
              ? <IamChip tone="default">Immovable</IamChip>
              : <IamChip tone="default">Movable</IamChip>
            )}
            <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={handleExport} disabled={exporting}>
              <Download className="h-4 w-4" />{exporting ? 'Exporting…' : 'Export CSV'}
            </button>
            <button className="iam-btn iam-btn-secondary" onClick={() => { setCleanupResult(null); setCleanupOpen(true); }}>
              <Sparkles className="h-4 w-4" />Cleanup
            </button>
          </div>
        }
      />

      <div className="p-6 space-y-4">
        {exportError && (
          <div className="iam-alert iam-alert-danger">{exportError}</div>
        )}
        <button className="iam-btn iam-btn-ghost iam-btn-sm -ml-1" onClick={() => navigate(-1)}>
          <ArrowLeft className="h-4 w-4" />Back
        </button>

        {loading
          ? <div className="iam-skeleton h-48 w-full" />
          : resolvedId && <UserListMembersPanel listId={resolvedId} title={list?.name ?? 'Members'} isSystemCtx={isSystemCtx} />
        }
      </div>

      <IamDialog open={cleanupOpen} onClose={() => (v => { setCleanupOpen(v); if (!v) setCleanupResult(null); })(false)}
      title="Cleanup User List"
      desc="Remove orphaned role assignments and optionally purge inactive users."
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setCleanupOpen(false)}>Close</button>
            <button className="iam-btn iam-btn-primary" type="button" disabled={cleanupRunning} onClick={handleCleanup}>
              {cleanupLabel}
            </button></>}
    >
<div className="space-y-4 py-2">
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={cleanupInactive} onChange={e => setCleanupInactive(e.target.checked)} />
              {' '}Remove users inactive for more than{' '}
              <input type="number" min={1} max={3650} value={cleanupDays} onChange={e => setCleanupDays(Math.max(1, Number(e.target.value) || 90))} className="w-16 border rounded px-2 py-0.5 text-sm" />
              {' days'}
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={cleanupDryRun} onChange={e => setCleanupDryRun(e.target.checked)} />
              {' '}Dry run (preview only, no deletions)
            </label>
            {cleanupError && (
              <div className="iam-alert iam-alert-danger">{cleanupError}</div>
            )}
            {cleanupResult && (
              <div className="rounded-lg border bg-muted p-3 text-sm space-y-1">
                {cleanupResult.dry_run && <p className="font-medium text-muted-foreground">Preview (dry run):</p>}
                <p>Orphaned role assignments: <strong>{cleanupResult.orphaned_roles_found}</strong>{!cleanupResult.dry_run && ` (${cleanupResult.orphaned_roles_removed} removed)`}</p>
                {cleanupInactive && <p>Inactive users: <strong>{cleanupResult.inactive_users_found}</strong>{!cleanupResult.dry_run && ` (${cleanupResult.inactive_users_removed} removed)`}</p>}
              </div>
            )}
          </div>
    </IamDialog>
    </div>
  );
}
