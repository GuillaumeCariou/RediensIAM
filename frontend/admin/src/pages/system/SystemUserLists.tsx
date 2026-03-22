import { useEffect, useState } from 'react';
import { List, Plus, UserPlus, MoreHorizontal, Trash2, ChevronDown, ChevronUp } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { listUserLists, listSystemUserListMembers, addUserToList, removeSystemUserFromList, deleteUserList } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface UserList {
  id: string;
  name: string;
  org_id: string | null;
  immovable: boolean;
  created_at: string;
}
interface Member {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
}

export default function SystemUserLists() {
  const [lists, setLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');
  const [expanded, setExpanded] = useState<string | null>(null);
  const [members, setMembers] = useState<Record<string, Member[]>>({});
  const [addUserOpen, setAddUserOpen] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<UserList | null>(null);
  const [form, setForm] = useState({ email: '', username: '', password: '' });
  const [saving, setSaving] = useState(false);

  const load = () => {
    setLoading(true);
    listUserLists().then(res => setLists(res.user_lists ?? res ?? [])).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const toggleExpand = async (id: string) => {
    if (expanded === id) { setExpanded(null); return; }
    setExpanded(id);
    if (!members[id]) {
      const res = await listSystemUserListMembers(id);
      setMembers(m => ({ ...m, [id]: res.users ?? res ?? [] }));
    }
  };

  const filtered = lists.filter(l => l.name.toLowerCase().includes(search.toLowerCase()));

  const handleAddUser = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!addUserOpen) return;
    setSaving(true);
    try {
      await addUserToList(addUserOpen, form);
      setAddUserOpen(null);
      setForm({ email: '', username: '', password: '' });
      const res = await listSystemUserListMembers(addUserOpen);
      setMembers(m => ({ ...m, [addUserOpen]: res.users ?? res ?? [] }));
      load();
    } finally { setSaving(false); }
  };

  const handleRemoveUser = async (listId: string, userId: string) => {
    await removeSystemUserFromList(listId, userId);
    setMembers(m => ({ ...m, [listId]: (m[listId] ?? []).filter(u => u.id !== userId) }));
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteUserList(deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader title="User Lists" description="All user lists across the system" />
      <div className="p-6 space-y-4">
        <Input placeholder="Search by name…" value={search} onChange={e => setSearch(e.target.value)} className="max-w-sm" />
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-8"></TableHead>
                <TableHead>Name</TableHead>
                <TableHead>Organisation</TableHead>
                <TableHead>Type</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 5 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : filtered.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                        <List className="h-8 w-8 mx-auto mb-2 opacity-40" />No user lists found
                      </TableCell>
                    </TableRow>
                  )
                : filtered.flatMap(list => [
                    <TableRow key={list.id} className="cursor-pointer" onClick={() => toggleExpand(list.id)}>
                      <TableCell className="text-muted-foreground">
                        {expanded === list.id ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
                      </TableCell>
                      <TableCell className="font-medium">{list.name}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">
                        {list.org_id ? list.org_id : 'System (root)'}
                      </TableCell>
                      <TableCell>
                        {list.immovable ? <Badge variant="secondary">Immovable</Badge> : <Badge variant="outline">Movable</Badge>}
                      </TableCell>
                      <TableCell onClick={e => e.stopPropagation()}>
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent>
                            <DropdownMenuItem onClick={() => { setAddUserOpen(list.id); }}>
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
                                <Button size="sm" variant="ghost" className="mt-2 -ml-1 text-xs" onClick={() => setAddUserOpen(list.id)}>
                                  <Plus className="h-3 w-3" />Add user
                                </Button>
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

      <Dialog open={!!addUserOpen} onOpenChange={v => !v && setAddUserOpen(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add User</DialogTitle>
            <DialogDescription>Create a new user account in this list.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAddUser} className="space-y-4">
            <div className="space-y-2"><Label>Email</Label><Input type="email" value={form.email} onChange={e => setForm(f => ({ ...f, email: e.target.value }))} required /></div>
            <div className="space-y-2"><Label>Username</Label><Input value={form.username} onChange={e => setForm(f => ({ ...f, username: e.target.value }))} required /></div>
            <div className="space-y-2"><Label>Password</Label><Input type="password" value={form.password} onChange={e => setForm(f => ({ ...f, password: e.target.value }))} required minLength={8} /></div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAddUserOpen(null)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Adding…' : 'Add User'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!deleteTarget} onOpenChange={v => !v && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete User List?</AlertDialogTitle>
            <AlertDialogDescription>This will delete "{deleteTarget?.name}" and all users in it. This action cannot be undone.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleDelete} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Delete</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
