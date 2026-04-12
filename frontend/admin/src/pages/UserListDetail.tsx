import { useEffect, useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, Sparkles, Download } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { Skeleton } from '@/components/ui/skeleton';
import { getUserList, getSystemUserList, cleanupUserList, exportUserList } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import UserListMembersPanel from '@/components/UserListMembersPanel';

interface UserList {
  id: string; name: string; org_id?: string | null; org_name?: string | null;
  immovable: boolean; user_count: number; created_at: string;
}

export default function UserListDetail() {
  // Handles all three route shapes: :id, :listId (org under system org), and standalone
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
    try {
      const blob = await exportUserList(resolvedId);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `userlist-${resolvedId.slice(0, 8)}-${new Date().toISOString().slice(0, 10)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
    } finally { setExporting(false); }
  };

  const handleCleanup = async () => {
    if (!resolvedId) return;
    setCleanupRunning(true); setCleanupResult(null);
    try {
      const res = await cleanupUserList(resolvedId, {
        remove_orphaned_roles: true,
        remove_inactive_users: cleanupInactive,
        inactive_threshold_days: cleanupDays,
        dry_run: cleanupDryRun,
      });
      setCleanupResult(res);
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
              ? <Badge variant="secondary">Immovable</Badge>
              : <Badge variant="outline">Movable</Badge>
            )}
            <Button variant="outline" size="sm" onClick={handleExport} disabled={exporting}>
              <Download className="h-4 w-4" />{exporting ? 'Exporting…' : 'Export CSV'}
            </Button>
            <Button variant="outline" onClick={() => { setCleanupResult(null); setCleanupOpen(true); }}>
              <Sparkles className="h-4 w-4" />Cleanup
            </Button>
          </div>
        }
      />

      <div className="p-6 space-y-4">
        <Button variant="ghost" size="sm" className="-ml-1" onClick={() => navigate(-1)}>
          <ArrowLeft className="h-4 w-4" />Back
        </Button>

        {loading
          ? <Skeleton className="h-48 w-full" />
          : resolvedId && <UserListMembersPanel listId={resolvedId} title={list?.name ?? 'Members'} isSystemCtx={isSystemCtx} />
        }
      </div>

      <Dialog open={cleanupOpen} onOpenChange={v => { setCleanupOpen(v); if (!v) setCleanupResult(null); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Cleanup User List</DialogTitle>
            <DialogDescription>Remove orphaned role assignments and optionally purge inactive users.</DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-2">
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={cleanupInactive} onChange={e => setCleanupInactive(e.target.checked)} />
              {' '}Remove users inactive for more than{' '}
              <input type="number" min={1} max={3650} value={cleanupDays} onChange={e => setCleanupDays(Number(e.target.value))} className="w-16 border rounded px-2 py-0.5 text-sm" />
              {' days'}
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={cleanupDryRun} onChange={e => setCleanupDryRun(e.target.checked)} />
              {' '}Dry run (preview only, no deletions)
            </label>
            {cleanupResult && (
              <div className="rounded-lg border bg-muted p-3 text-sm space-y-1">
                {cleanupResult.dry_run && <p className="font-medium text-muted-foreground">Preview (dry run):</p>}
                <p>Orphaned role assignments: <strong>{cleanupResult.orphaned_roles_found}</strong>{!cleanupResult.dry_run && ` (${cleanupResult.orphaned_roles_removed} removed)`}</p>
                {cleanupInactive && <p>Inactive users: <strong>{cleanupResult.inactive_users_found}</strong>{!cleanupResult.dry_run && ` (${cleanupResult.inactive_users_removed} removed)`}</p>}
              </div>
            )}
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setCleanupOpen(false)}>Close</Button>
            <Button type="button" disabled={cleanupRunning} onClick={handleCleanup}>
              {cleanupLabel}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
