import { useEffect, useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, UserPlus, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Skeleton } from '@/components/ui/skeleton';
import { getSystemUserList, listSystemUserListMembers, addUserToList, removeSystemUserFromList } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface UserList {
  id: string; name: string; org_id: string | null; org_name: string | null;
  immovable: boolean; user_count: number; created_at: string;
}
interface Member {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
}

export default function SystemUserListDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [list, setList] = useState<UserList | null>(null);
  const [members, setMembers] = useState<Member[]>([]);
  const [loading, setLoading] = useState(true);
  const [addOpen, setAddOpen] = useState(false);
  const [removeTarget, setRemoveTarget] = useState<Member | null>(null);
  const [form, setForm] = useState({ email: '', username: '', password: '' });
  const [saving, setSaving] = useState(false);

  const loadMembers = async () => {
    if (!id) return;
    const res = await listSystemUserListMembers(id);
    setMembers(res.users ?? res ?? []);
  };

  useEffect(() => {
    if (!id) return;
    Promise.all([
      getSystemUserList(id).then(setList),
      listSystemUserListMembers(id).then(res => setMembers(res.users ?? res ?? [])),
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
    await removeSystemUserFromList(id, removeTarget.id);
    setRemoveTarget(null);
    setMembers(m => m.filter(u => u.id !== removeTarget.id));
  };

  return (
    <div>
      <PageHeader
        title={loading ? 'Loading…' : (list?.name ?? 'User List')}
        description={list ? (list.org_name ? `Organisation: ${list.org_name}` : 'System (root)') : undefined}
        action={
          <div className="flex items-center gap-2">
            {list && (list.immovable
              ? <Badge variant="secondary">Immovable</Badge>
              : <Badge variant="outline">Movable</Badge>
            )}
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
