import { useEffect, useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, UserPlus, Trash2, Sparkles } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Skeleton } from '@/components/ui/skeleton';
import { getUserList, listUserListMembers, addUserToList, removeUserFromList, cleanupUserList } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface UserList {
  id: string; name: string; immovable: boolean; user_count: number; created_at: string;
}
interface Member {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
}

export default function OrgUserListDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [list, setList] = useState<UserList | null>(null);
  const [members, setMembers] = useState<Member[]>([]);
  const [loading, setLoading] = useState(true);
  const [addOpen, setAddOpen]           = useState(false);
  const [removeTarget, setRemoveTarget] = useState<Member | null>(null);
  const [form, setForm]                 = useState({ email: '', username: '', password: '' });
  const [saving, setSaving]             = useState(false);
  const [cleanupOpen, setCleanupOpen]   = useState(false);
  const [cleanupDryRun, setCleanupDryRun]       = useState(true);
  const [cleanupInactive, setCleanupInactive]   = useState(false);
  const [cleanupDays, setCleanupDays]           = useState(90);
  const [cleanupRunning, setCleanupRunning]     = useState(false);
  const [cleanupResult, setCleanupResult]       = useState<{ orphaned_roles_found: number; inactive_users_found: number; orphaned_roles_removed: number; inactive_users_removed: number; dry_run: boolean } | null>(null);

  const loadMembers = async () => {
    if (!id) return;
    const res = await listUserListMembers(id);
    setMembers(res.users ?? res ?? []);
  };

  useEffect(() => {
    if (!id) return;
    Promise.all([
      getUserList(id).then(setList),
      listUserListMembers(id).then(res => setMembers(res.users ?? res ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  }, [id]);

  const handleAdd = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!id) return;
    setSaving(true);
    try {
      await addUserToList(id, form);
      setAddOpen(false);
      setForm({ email: '', username: '', password: '' });
      await loadMembers();
    } finally { setSaving(false); }
  };

  const handleRemove = async () => {
    if (!removeTarget || !id) return;
    await removeUserFromList(id, removeTarget.id);
    setRemoveTarget(null);
    setMembers(m => m.filter(u => u.id !== removeTarget.id));
  };

  const handleCleanup = async () => {
    if (!id) return;
    setCleanupRunning(true);
    setCleanupResult(null);
    try {
      const res = await cleanupUserList(id, {
        remove_orphaned_roles: true,
        remove_inactive_users: cleanupInactive,
        inactive_threshold_days: cleanupDays,
        dry_run: cleanupDryRun,
      });
      setCleanupResult(res);
      if (!cleanupDryRun) await loadMembers();
    } finally { setCleanupRunning(false); }
  };

  return (
    <div>
      <PageHeader
        title={loading ? 'Loading…' : (list?.name ?? 'User List')}
        action={
          <div className="flex items-center gap-2">
            {list && (list.immovable
              ? <Badge variant="secondary">Immovable</Badge>
              : <Badge variant="outline">Movable</Badge>
            )}
            <Button variant="outline" onClick={() => { setCleanupResult(null); setCleanupOpen(true); }}><Sparkles className="h-4 w-4" />Cleanup</Button>
            <Button onClick={() => setAddOpen(true)}><UserPlus className="h-4 w-4" />Add User</Button>
          </div>
        }
      />

      <div className="p-6 space-y-4">
        <Button variant="ghost" size="sm" className="-ml-1" onClick={() => navigate(-1)}>
          <ArrowLeft className="h-4 w-4" />Back
        </Button>

        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Username</TableHead>
                <TableHead>Email</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Last Login</TableHead>
                <TableHead className="w-16"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : members.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                        No users in this list yet.
                      </TableCell>
                    </TableRow>
                  )
                : members.map(m => (
                    <TableRow key={m.id}>
                      <TableCell className="font-medium">{m.display_name ?? m.username}#{m.discriminator}</TableCell>
                      <TableCell className="text-muted-foreground">{m.email}</TableCell>
                      <TableCell>
                        {m.active
                          ? <Badge variant="success">Active</Badge>
                          : <Badge variant="destructive">Disabled</Badge>
                        }
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(m.last_login_at)}</TableCell>
                      <TableCell>
                        <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" onClick={() => setRemoveTarget(m)}>
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>
      </div>

      <Dialog open={addOpen} onOpenChange={setAddOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add User</DialogTitle>
            <DialogDescription>Create a new user account in this list.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAdd} className="space-y-4">
            <div className="space-y-2"><Label>Email</Label><Input type="email" value={form.email} onChange={e => setForm(f => ({ ...f, email: e.target.value }))} required autoFocus /></div>
            <div className="space-y-2"><Label>Username</Label><Input value={form.username} onChange={e => setForm(f => ({ ...f, username: e.target.value }))} required /></div>
            <div className="space-y-2"><Label>Password</Label><Input type="password" value={form.password} onChange={e => setForm(f => ({ ...f, password: e.target.value }))} required minLength={8} /></div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAddOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Adding…' : 'Add User'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={cleanupOpen} onOpenChange={v => { setCleanupOpen(v); if (!v) setCleanupResult(null); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Cleanup User List</DialogTitle>
            <DialogDescription>Remove orphaned role assignments and optionally purge inactive users.</DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-2">
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={cleanupInactive} onChange={e => setCleanupInactive(e.target.checked)} />
              Remove users inactive for more than
              <input type="number" min={1} max={3650} value={cleanupDays} onChange={e => setCleanupDays(Number(e.target.value))} className="w-16 border rounded px-2 py-0.5 text-sm" />
              days
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={cleanupDryRun} onChange={e => setCleanupDryRun(e.target.checked)} />
              Dry run (preview only, no deletions)
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
              {cleanupRunning ? 'Running…' : cleanupDryRun ? 'Preview' : 'Run Cleanup'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!removeTarget} onOpenChange={v => !v && setRemoveTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove {removeTarget?.email}?</AlertDialogTitle>
            <AlertDialogDescription>This will permanently delete the user account.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleRemove} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Remove</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
