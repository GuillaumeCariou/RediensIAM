import { useEffect, useState, useCallback } from 'react';
import { UserCog, UserPlus, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Skeleton } from '@/components/ui/skeleton';
import { listUserLists, listSystemUserListMembers, addUserToList, removeSystemUserFromList } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface Member {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
}

export default function SystemAdmins() {
  const [systemListId, setSystemListId] = useState<string | null>(null);
  const [members, setMembers]           = useState<Member[]>([]);
  const [loading, setLoading]           = useState(true);
  const [error, setError]               = useState('');

  const [addOpen, setAddOpen]           = useState(false);
  const [form, setForm]                 = useState({ email: '', username: '', password: '' });
  const [saving, setSaving]             = useState(false);
  const [addError, setAddError]         = useState('');

  const [removeTarget, setRemoveTarget] = useState<Member | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      // Find the system user list: immovable + no org (org_id == null)
      const res = await listUserLists();
      const all: { id: string; name: string; org_id: string | null; immovable: boolean }[] = res.user_lists ?? res ?? [];
      const syslist = all.find(l => l.org_id === null && l.immovable);
      if (!syslist) { setError('System user list not found.'); return; }
      setSystemListId(syslist.id);
      const mres = await listSystemUserListMembers(syslist.id);
      setMembers(mres.users ?? mres ?? []);
    } catch { setError('Failed to load system admins.'); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleAdd = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!systemListId) return;
    setSaving(true);
    setAddError('');
    try {
      await addUserToList(systemListId, form);
      setAddOpen(false);
      setForm({ email: '', username: '', password: '' });
      await load();
    } catch { setAddError('Failed to add admin. The email or username may already exist.'); }
    finally { setSaving(false); }
  };

  const handleRemove = async () => {
    if (!removeTarget || !systemListId) return;
    await removeSystemUserFromList(systemListId, removeTarget.id);
    setRemoveTarget(null);
    await load();
  };

  return (
    <div>
      <PageHeader
        title="System Admins"
        description="Users with super_admin access across the entire platform"
        action={
          <Button onClick={() => setAddOpen(true)} disabled={!systemListId}>
            <UserPlus className="h-4 w-4" />Add Admin
          </Button>
        }
      />
      <div className="p-6">
        {error && <p className="text-sm text-destructive mb-4">{error}</p>}
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                <TableHead>Email</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Last login</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={i}>
                      {Array.from({ length: 5 }).map((__, j) => (
                        <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>
                      ))}
                    </TableRow>
                  ))
                : members.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                        <UserCog className="h-8 w-8 mx-auto mb-2 opacity-30" />
                        No system admins yet. Add one to grant super_admin access.
                      </TableCell>
                    </TableRow>
                  )
                : members.map(m => (
                    <TableRow key={m.id}>
                      <TableCell>
                        <div>
                          <p className="font-medium text-sm font-mono">{m.username}<span className="text-muted-foreground">#{m.discriminator}</span></p>
                          {m.display_name && <p className="text-xs text-muted-foreground">{m.display_name}</p>}
                        </div>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{m.email}</TableCell>
                      <TableCell>
                        <Badge variant={m.active ? 'success' : 'secondary'}>{m.active ? 'Active' : 'Disabled'}</Badge>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(m.last_login_at)}</TableCell>
                      <TableCell>
                        <Button
                          variant="ghost" size="icon"
                          className="text-destructive hover:text-destructive hover:bg-destructive/10"
                          onClick={() => setRemoveTarget(m)}
                        >
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

      {/* Add Admin dialog */}
      <Dialog open={addOpen} onOpenChange={v => { setAddOpen(v); setAddError(''); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add System Admin</DialogTitle>
            <DialogDescription>
              Creates a new user account with super_admin access. They can log in at <code className="text-xs">/admin</code> immediately.
            </DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAdd} className="space-y-4">
            {addError && <p className="text-sm text-destructive">{addError}</p>}
            <div className="space-y-2">
              <Label>Email</Label>
              <Input type="email" value={form.email} onChange={e => setForm(f => ({ ...f, email: e.target.value }))} required placeholder="admin@example.com" />
            </div>
            <div className="space-y-2">
              <Label>Username</Label>
              <Input value={form.username} onChange={e => setForm(f => ({ ...f, username: e.target.value }))} required placeholder="johndoe" />
            </div>
            <div className="space-y-2">
              <Label>Initial password</Label>
              <Input type="password" value={form.password} onChange={e => setForm(f => ({ ...f, password: e.target.value }))} required minLength={8} placeholder="Min. 8 characters" />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAddOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Adding…' : 'Add Admin'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Remove confirmation */}
      <AlertDialog open={!!removeTarget} onOpenChange={v => !v && setRemoveTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove {removeTarget?.username}#{removeTarget?.discriminator}?</AlertDialogTitle>
            <AlertDialogDescription>
              This removes their super_admin access and deletes their account from the system. This action cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleRemove} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">
              Remove
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
