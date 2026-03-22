import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Plus, Trash2, Users, UserPlus, ChevronDown, ChevronUp, MoreHorizontal } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { listUserLists, createUserList, deleteUserList, listUserListMembers, addUserToList, removeUserFromList } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface UserList { id: string; name: string; immovable: boolean; user_count: number; created_at: string; }
interface Member { id: string; email: string; username: string; discriminator: string; display_name: string | null; active: boolean; last_login_at: string | null; }

export default function UserLists() {
  const [params] = useSearchParams();
  const orgId = params.get('org_id') ?? '';
  const [lists, setLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [members, setMembers] = useState<Record<string, Member[]>>({});
  const [createOpen, setCreateOpen] = useState(false);
  const [addUserTarget, setAddUserTarget] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<UserList | null>(null);
  const [listForm, setListForm] = useState({ name: '' });
  const [userForm, setUserForm] = useState({ email: '', username: '', password: '' });
  const [saving, setSaving] = useState(false);

  const load = () => {
    setLoading(true);
    listUserLists(orgId).then(r => setLists(r.user_lists ?? r ?? [])).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [orgId]);

  const toggleExpand = async (id: string) => {
    if (expanded === id) { setExpanded(null); return; }
    setExpanded(id);
    if (!members[id]) {
      const res = await listUserListMembers(id);
      setMembers(m => ({ ...m, [id]: res.users ?? res ?? [] }));
    }
  };

  const handleCreateList = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      await createUserList({ name: listForm.name, org_id: orgId });
      setCreateOpen(false);
      setListForm({ name: '' });
      load();
    } finally { setSaving(false); }
  };

  const handleAddUser = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!addUserTarget) return;
    setSaving(true);
    try {
      await addUserToList(addUserTarget, userForm);
      setAddUserTarget(null);
      setUserForm({ email: '', username: '', password: '' });
      const res = await listUserListMembers(addUserTarget);
      setMembers(m => ({ ...m, [addUserTarget]: res.users ?? res ?? [] }));
    } finally { setSaving(false); }
  };

  const handleRemoveUser = async (listId: string, userId: string) => {
    await removeUserFromList(listId, userId);
    setMembers(m => ({ ...m, [listId]: (m[listId] ?? []).filter(u => u.id !== userId) }));
  };

  const handleDeleteList = async () => {
    if (!deleteTarget) return;
    await deleteUserList(deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="User Lists"
        description="Reusable pools of users that can be assigned to projects"
        action={
          orgId
            ? <Button onClick={() => setCreateOpen(true)}><Plus className="h-4 w-4" />New User List</Button>
            : undefined
        }
      />
      <div className="p-6 space-y-4">
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-8"></TableHead>
                <TableHead>Name</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Users</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 4 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : lists.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                        No user lists yet
                      </TableCell>
                    </TableRow>
                  )
                : lists.flatMap(list => [
                    <TableRow key={list.id} className="cursor-pointer" onClick={() => toggleExpand(list.id)}>
                      <TableCell className="text-muted-foreground">
                        {expanded === list.id ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
                      </TableCell>
                      <TableCell className="font-medium">{list.name}</TableCell>
                      <TableCell>
                        {list.immovable ? <Badge variant="secondary">Immovable</Badge> : <Badge variant="outline">Movable</Badge>}
                      </TableCell>
                      <TableCell>
                        <div className="flex items-center gap-1">
                          <Users className="h-3 w-3 text-muted-foreground" />
                          <span className="text-sm">{list.user_count}</span>
                        </div>
                      </TableCell>
                      <TableCell onClick={e => e.stopPropagation()}>
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent>
                            <DropdownMenuItem onClick={() => setAddUserTarget(list.id)}>
                              <UserPlus className="h-4 w-4" />Add User
                            </DropdownMenuItem>
                            {!list.immovable && (
                              <>
                                <DropdownMenuSeparator />
                                <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setDeleteTarget(list)}>
                                  <Trash2 className="h-4 w-4" />Delete
                                </DropdownMenuItem>
                              </>
                            )}
                          </DropdownMenuContent>
                        </DropdownMenu>
                      </TableCell>
                    </TableRow>,
                    ...(expanded === list.id
                      ? [(
                          <TableRow key={`${list.id}-members`} className="bg-muted/30">
                            <TableCell colSpan={5} className="py-0">
                              <div className="px-4 py-3">
                                {!members[list.id] ? (
                                  <div className="space-y-2">{Array.from({ length: 2 }).map((_, i) => <Skeleton key={i} className="h-8 w-full" />)}</div>
                                ) : members[list.id]!.length === 0 ? (
                                  <p className="text-sm text-muted-foreground py-2">No members in this list.</p>
                                ) : (
                                  <div className="space-y-1">
                                    {members[list.id]!.map(m => (
                                      <div key={m.id} className="flex items-center justify-between py-1.5 border-b last:border-0">
                                        <div>
                                          <span className="text-sm font-medium">{m.display_name ?? m.username}#{m.discriminator}</span>
                                          <span className="text-xs text-muted-foreground ml-2">{m.email}</span>
                                          {!m.active && <Badge variant="destructive" className="ml-2 text-xs">Disabled</Badge>}
                                        </div>
                                        <div className="text-xs text-muted-foreground flex items-center gap-2">
                                          <span>Last login: {fmtDate(m.last_login_at)}</span>
                                          <Button size="sm" variant="ghost" className="h-6 px-2 text-destructive hover:text-destructive" onClick={() => handleRemoveUser(list.id, m.id)}>
                                            Remove
                                          </Button>
                                        </div>
                                      </div>
                                    ))}
                                  </div>
                                )}
                              </div>
                            </TableCell>
                          </TableRow>
                        )]
                      : [])
                  ])
              }
            </TableBody>
          </Table>
        </div>
      </div>

      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Create User List</DialogTitle><DialogDescription>A movable pool of users you can assign to projects.</DialogDescription></DialogHeader>
          <form onSubmit={handleCreateList} className="space-y-4">
            <div className="space-y-2"><Label>Name</Label><Input value={listForm.name} onChange={e => setListForm({ name: e.target.value })} required placeholder="Team Alpha" /></div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Creating…' : 'Create'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={!!addUserTarget} onOpenChange={v => !v && setAddUserTarget(null)}>
        <DialogContent>
          <DialogHeader><DialogTitle>Add User</DialogTitle><DialogDescription>Create a new user account in this list.</DialogDescription></DialogHeader>
          <form onSubmit={handleAddUser} className="space-y-4">
            <div className="space-y-2"><Label>Email</Label><Input type="email" value={userForm.email} onChange={e => setUserForm(f => ({ ...f, email: e.target.value }))} required /></div>
            <div className="space-y-2"><Label>Username</Label><Input value={userForm.username} onChange={e => setUserForm(f => ({ ...f, username: e.target.value }))} required /></div>
            <div className="space-y-2"><Label>Password</Label><Input type="password" value={userForm.password} onChange={e => setUserForm(f => ({ ...f, password: e.target.value }))} required minLength={8} /></div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAddUserTarget(null)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Adding…' : 'Add User'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!deleteTarget} onOpenChange={v => !v && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete "{deleteTarget?.name}"?</AlertDialogTitle>
            <AlertDialogDescription>All users in this list will be permanently deleted.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleDeleteList} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Delete</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
