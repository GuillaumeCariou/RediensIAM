import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { useProjectContext } from '@/hooks/useOrgContext';
import { useAuth } from '@/context/AuthContext';
import { UserPlus, Trash2, CheckCircle, XCircle, LogOut, Plus, List, Pencil } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Switch } from '@/components/ui/switch';
import { Label } from '@/components/ui/label';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import {
  listProjectUsers, listRoles, assignRole, removeRole,
  forceLogoutProjectUser, getProjectInfo, createProjectUser,
  listUserLists, assignUserList, unassignUserList,
  adminAssignUserList, adminUnassignUserList,
  listSystemUserListMembers, listUserListMembers,
  addUserToList, removeSystemUserFromList, removeUserFromList,
  adminGetUser, adminUpdateUser,
} from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface ProjectUser {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
  roles: { id: string; name: string }[];
}
interface Role { id: string; name: string; }
interface UserList { id: string; name: string; immovable?: boolean; }
interface Project {
  assigned_user_list_id: string | null;
  assigned_user_list_name: string | null;
  default_role_id: string | null;
}
interface Member {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
}

export default function ProjectUsers() {
  const { projectId, isSystemCtx } = useProjectContext();
  const { oid } = useParams<{ oid?: string }>();
  const { isOrgAdmin, orgId: tokenOrgId } = useAuth();

  // ── Project / roles state ──────────────────────────────────────
  const [users, setUsers] = useState<ProjectUser[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [project, setProject] = useState<Project | null>(null);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');

  const [assignOpen, setAssignOpen] = useState<ProjectUser | null>(null);
  const [selectedRole, setSelectedRole] = useState('');
  const [saving, setSaving] = useState(false);

  // ── Inline create (project managers only) ─────────────────────
  const [createOpen, setCreateOpen] = useState(false);
  const [newUser, setNewUser] = useState({ email: '', username: '', password: '' });
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState('');

  // ── Userlist members (org/super admin) ────────────────────────
  const [members, setMembers] = useState<Member[]>([]);
  const [membersLoading, setMembersLoading] = useState(false);
  const [addOpen, setAddOpen] = useState(false);
  const [addForm, setAddForm] = useState({ email: '', username: '', password: '', email_verified: false });
  const [addSaving, setAddSaving] = useState(false);
  const [removeTarget, setRemoveTarget] = useState<Member | null>(null);

  // ── Edit user dialog (org/super admin) ────────────────────────
  const [editTarget, setEditTarget] = useState<Member | null>(null);
  const [editForm, setEditForm] = useState({ email: '', username: '', display_name: '', phone: '', active: true, email_verified: false, clear_lock: false, new_password: '' });
  const [editLoading, setEditLoading] = useState(false);
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState('');

  const orgId = oid ?? tokenOrgId;
  const assignedListId = project?.assigned_user_list_id ?? null;
  const assignedListName = project?.assigned_user_list_name
    ?? userLists.find(ul => ul.id === assignedListId)?.name
    ?? null;
  const movableLists = userLists.filter(ul => !ul.immovable);
  const defaultRoleId = project?.default_role_id ?? null;
  const defaultRoleName = roles.find(r => r.id === defaultRoleId)?.name;

  // ── Loaders ───────────────────────────────────────────────────
  const loadMembers = async (listId: string) => {
    setMembersLoading(true);
    try {
      const res = isSystemCtx
        ? await listSystemUserListMembers(listId)
        : await listUserListMembers(listId);
      setMembers(res.users ?? res ?? []);
    } finally { setMembersLoading(false); }
  };

  const load = () => {
    if (!projectId) { setLoading(false); return; }
    setLoading(true);
    const fetches: Promise<unknown>[] = [
      listProjectUsers(projectId).then(r => setUsers(r.users ?? r ?? [])),
      listRoles(projectId).then(r => setRoles(r.roles ?? r ?? [])),
      getProjectInfo(projectId).then(p => {
        setProject(p);
        if (isOrgAdmin && p.assigned_user_list_id) loadMembers(p.assigned_user_list_id);
      }).catch(() => null),
    ];
    if (isOrgAdmin && orgId) {
      fetches.push(listUserLists(orgId).then(r => setUserLists(r.user_lists ?? r ?? [])).catch(() => null));
    }
    Promise.all(fetches).catch(console.error).finally(() => setLoading(false));
  };

  useEffect(load, [projectId]);

  // Reload members when assigned list changes
  useEffect(() => {
    if (isOrgAdmin && assignedListId) loadMembers(assignedListId);
    else setMembers([]);
  }, [assignedListId]);

  // ── Handlers ──────────────────────────────────────────────────
  const handleAssignList = async (ulId: string) => {
    if (!projectId) return;
    if (ulId === '__none__') {
      if (isSystemCtx) await adminUnassignUserList(projectId);
      else await unassignUserList(projectId);
    } else {
      if (isSystemCtx) await adminAssignUserList(projectId, ulId);
      else await assignUserList(projectId, ulId);
    }
    getProjectInfo(projectId).then(p => setProject(p)).catch(() => null);
  };

  const handleAssignRole = async () => {
    if (!assignOpen || !selectedRole) return;
    setSaving(true);
    try {
      await assignRole(projectId, assignOpen.id, selectedRole);
      setAssignOpen(null);
      setSelectedRole('');
      load();
    } finally { setSaving(false); }
  };

  const handleRemoveRole = async (userId: string, roleId: string) => {
    await removeRole(projectId, userId, roleId);
    setUsers(prev => prev.map(u =>
      u.id === userId ? { ...u, roles: u.roles.filter(r => r.id !== roleId) } : u
    ));
  };

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setCreating(true); setCreateError('');
    try {
      await createProjectUser(projectId, { email: newUser.email, username: newUser.username || undefined, password: newUser.password });
      setCreateOpen(false);
      setNewUser({ email: '', username: '', password: '' });
      load();
    } catch { setCreateError('Failed to create user. The email may already be taken.'); }
    finally { setCreating(false); }
  };

  const handleAddToList = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!assignedListId) return;
    setAddSaving(true);
    try {
      await addUserToList(assignedListId, addForm);
      setAddOpen(false);
      setAddForm({ email: '', username: '', password: '', email_verified: false });
      await loadMembers(assignedListId);
      load();
    } finally { setAddSaving(false); }
  };

  const handleRemoveMember = async () => {
    if (!removeTarget || !assignedListId) return;
    if (isSystemCtx) await removeSystemUserFromList(assignedListId, removeTarget.id);
    else await removeUserFromList(assignedListId, removeTarget.id);
    setRemoveTarget(null);
    setMembers(m => m.filter(u => u.id !== removeTarget.id));
    load();
  };

  const openEdit = async (m: Member) => {
    setEditTarget(m); setEditError(''); setEditLoading(true);
    try {
      const u = await adminGetUser(m.id);
      setEditForm({
        email: u.email ?? '', username: u.username ?? '',
        display_name: u.display_name ?? '', phone: u.phone ?? '',
        active: u.active ?? true, email_verified: u.email_verified ?? false,
        clear_lock: false, new_password: '',
      });
    } catch { setEditError('Failed to load user details.'); }
    finally { setEditLoading(false); }
  };

  const handleEdit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!editTarget) return;
    setEditSaving(true); setEditError('');
    try {
      await adminUpdateUser(editTarget.id, {
        email: editForm.email, username: editForm.username,
        display_name: editForm.display_name, phone: editForm.phone,
        active: editForm.active, email_verified: editForm.email_verified,
        clear_lock: editForm.clear_lock,
        new_password: editForm.new_password || undefined,
      });
      setEditTarget(null);
      if (assignedListId) await loadMembers(assignedListId);
      load();
    } catch { setEditError('Failed to save changes.'); }
    finally { setEditSaving(false); }
  };

  const availableRoles = (user: ProjectUser) =>
    roles.filter(r => !user.roles.some(ur => ur.id === r.id));

  const filtered = users.filter(u =>
    u.email.toLowerCase().includes(search.toLowerCase()) ||
    u.username.toLowerCase().includes(search.toLowerCase()) ||
    (u.display_name?.toLowerCase().includes(search.toLowerCase()) ?? false)
  );

  return (
    <div>
      <PageHeader
        title="Project Users"
        description="Users and their role assignments in this project"
        action={
          !isOrgAdmin ? (
            <Button onClick={() => { setCreateOpen(true); setCreateError(''); }}>
              <Plus className="h-4 w-4" />New User
            </Button>
          ) : null
        }
      />
      <div className="p-6 space-y-4">

        {/* ── Assigned User List card ── */}
        <Card>
          <CardHeader className="pb-3">
            <CardTitle className="text-sm text-muted-foreground flex items-center gap-2">
              <List className="h-4 w-4" />Assigned User List
            </CardTitle>
          </CardHeader>
          <CardContent>
            {loading ? (
              <Skeleton className="h-10 w-72" />
            ) : isOrgAdmin ? (
              <div className="space-y-2">
                <Select value={assignedListId ?? '__none__'} onValueChange={handleAssignList}>
                  <SelectTrigger className="w-72 bg-background">
                    <SelectValue placeholder="— No user list assigned —" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__none__">— None —</SelectItem>
                    {movableLists.map(ul => (
                      <SelectItem key={ul.id} value={ul.id}>{ul.name}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {!assignedListId && (
                  <p className="text-xs text-amber-500">No user list assigned — users cannot log in to this project.</p>
                )}
              </div>
            ) : (
              <div className="flex items-center gap-2">
                {assignedListName
                  ? <Badge variant="secondary">{assignedListName}</Badge>
                  : <span className="text-sm text-muted-foreground italic">No user list assigned</span>
                }
              </div>
            )}
          </CardContent>
        </Card>

        {/* ── Inline userlist member management (org/super admin) ── */}
        {isOrgAdmin && assignedListId && (
          <Card>
            <CardHeader className="pb-3 flex flex-row items-center justify-between">
              <CardTitle className="text-sm font-medium">
                {assignedListName ?? 'User List'} — Members
              </CardTitle>
              <Button size="sm" onClick={() => setAddOpen(true)}>
                <UserPlus className="h-4 w-4" />Add User
              </Button>
            </CardHeader>
            <CardContent className="p-0">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>User</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead>Last Login</TableHead>
                    <TableHead className="w-20"></TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {membersLoading
                    ? Array.from({ length: 3 }).map((_, i) => (
                        <TableRow key={i}>{Array.from({ length: 4 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                      ))
                    : members.length === 0
                    ? <TableRow><TableCell colSpan={4} className="text-center text-muted-foreground py-8">No members yet</TableCell></TableRow>
                    : members.map(m => (
                        <TableRow key={m.id}>
                          <TableCell>
                            <p className="font-medium text-sm">{m.display_name ?? m.username}#{m.discriminator}</p>
                            <p className="text-xs text-muted-foreground">{m.email}</p>
                          </TableCell>
                          <TableCell>
                            {m.active
                              ? <Badge variant="success"><CheckCircle className="h-3 w-3 mr-1" />Active</Badge>
                              : <Badge variant="destructive"><XCircle className="h-3 w-3 mr-1" />Disabled</Badge>
                            }
                          </TableCell>
                          <TableCell className="text-sm text-muted-foreground">{fmtDate(m.last_login_at)}</TableCell>
                          <TableCell>
                            <div className="flex items-center justify-end gap-1">
                              <Button size="sm" variant="ghost" onClick={() => openEdit(m)}>
                                <Pencil className="h-3 w-3" />
                              </Button>
                              <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" onClick={() => setRemoveTarget(m)}>
                                <Trash2 className="h-3 w-3" />
                              </Button>
                            </div>
                          </TableCell>
                        </TableRow>
                      ))
                  }
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        )}

        {/* ── Role assignments ── */}
        <div className="flex items-center gap-3">
          <Input
            placeholder="Search users…"
            value={search}
            onChange={e => setSearch(e.target.value)}
            className="max-w-sm"
          />
          {defaultRoleName && (
            <span className="text-sm text-muted-foreground">
              Default role: <Badge variant="secondary">{defaultRoleName}</Badge>
            </span>
          )}
        </div>

        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Roles</TableHead>
                <TableHead>Last Login</TableHead>
                <TableHead>Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 5 }).map((_, i) => (
                    <TableRow key={i}>
                      {Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}
                    </TableRow>
                  ))
                : filtered.length === 0
                ? <TableRow><TableCell colSpan={5} className="text-center text-muted-foreground py-12">No users found</TableCell></TableRow>
                : filtered.map(user => (
                    <TableRow key={user.id}>
                      <TableCell>
                        <p className="font-medium text-sm">{user.display_name ?? user.username}#{user.discriminator}</p>
                        <p className="text-xs text-muted-foreground">{user.email}</p>
                      </TableCell>
                      <TableCell>
                        {user.active
                          ? <Badge variant="success"><CheckCircle className="h-3 w-3 mr-1" />Active</Badge>
                          : <Badge variant="destructive"><XCircle className="h-3 w-3 mr-1" />Disabled</Badge>
                        }
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-wrap gap-1">
                          {user.roles.length === 0
                            ? <span className="text-xs text-muted-foreground">No roles</span>
                            : user.roles.map(r => (
                                <Badge key={r.id} variant="secondary" className="gap-1 pr-1">
                                  {r.name}
                                  {r.id === defaultRoleId && <span className="text-[10px] opacity-60 ml-0.5">default</span>}
                                  <button
                                    onClick={() => handleRemoveRole(user.id, r.id)}
                                    className="ml-0.5 rounded-full hover:bg-muted-foreground/20 p-0.5"
                                  >
                                    <Trash2 className="h-2.5 w-2.5" />
                                  </button>
                                </Badge>
                              ))
                          }
                        </div>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(user.last_login_at)}</TableCell>
                      <TableCell>
                        <div className="flex items-center gap-1">
                          {availableRoles(user).length > 0 && (
                            <Button size="sm" variant="outline" onClick={() => { setAssignOpen(user); setSelectedRole(''); }}>
                              <UserPlus className="h-3 w-3" />Add Role
                            </Button>
                          )}
                          <Button
                            size="sm" variant="ghost"
                            className="text-destructive hover:text-destructive"
                            title="Force logout all sessions"
                            onClick={() => forceLogoutProjectUser(projectId, user.id)}
                          >
                            <LogOut className="h-3 w-3" />
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>
      </div>

      {/* ── Assign Role dialog ── */}
      <Dialog open={!!assignOpen} onOpenChange={v => !v && setAssignOpen(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Assign Role</DialogTitle>
            <DialogDescription>Add a role to {assignOpen?.display_name ?? assignOpen?.username}.</DialogDescription>
          </DialogHeader>
          <div className="py-2 space-y-3">
            <div className="space-y-2">
              <Label>Role</Label>
              <Select value={selectedRole} onValueChange={setSelectedRole}>
                <SelectTrigger><SelectValue placeholder="Select a role" /></SelectTrigger>
                <SelectContent>
                  {assignOpen && availableRoles(assignOpen).map(r => (
                    <SelectItem key={r.id} value={r.id}>
                      {r.name}
                      {r.id === defaultRoleId && <span className="text-muted-foreground ml-1 text-xs">(default)</span>}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setAssignOpen(null)}>Cancel</Button>
            <Button onClick={handleAssignRole} disabled={saving || !selectedRole}>{saving ? 'Assigning…' : 'Assign'}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* ── Create User dialog (project managers only) ── */}
      <Dialog open={createOpen} onOpenChange={v => !v && setCreateOpen(false)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create User</DialogTitle>
            <DialogDescription>
              Add a new user to this project.
              {defaultRoleName && <> They will automatically receive the <strong>{defaultRoleName}</strong> role.</>}
            </DialogDescription>
          </DialogHeader>
          <form onSubmit={handleCreate} className="space-y-4 py-2">
            <div className="space-y-2">
              <Label>Email</Label>
              <Input type="email" value={newUser.email} onChange={e => setNewUser(u => ({ ...u, email: e.target.value }))} required placeholder="user@example.com" />
            </div>
            <div className="space-y-2">
              <Label>Username <span className="text-muted-foreground text-xs">(optional)</span></Label>
              <Input value={newUser.username} onChange={e => setNewUser(u => ({ ...u, username: e.target.value }))} placeholder="johndoe" />
            </div>
            <div className="space-y-2">
              <Label>Password</Label>
              <Input type="password" value={newUser.password} onChange={e => setNewUser(u => ({ ...u, password: e.target.value }))} required placeholder="••••••••" />
            </div>
            {createError && <p className="text-sm text-destructive">{createError}</p>}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={creating}>{creating ? 'Creating…' : 'Create User'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* ── Add to userlist dialog ── */}
      <Dialog open={addOpen} onOpenChange={setAddOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add User</DialogTitle>
            <DialogDescription>Create a new user account in this list.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAddToList} className="space-y-4">
            <div className="space-y-2"><Label>Email</Label><Input type="email" value={addForm.email} onChange={e => setAddForm(f => ({ ...f, email: e.target.value }))} required autoFocus /></div>
            <div className="space-y-2"><Label>Username</Label><Input value={addForm.username} onChange={e => setAddForm(f => ({ ...f, username: e.target.value }))} required /></div>
            <div className="space-y-2"><Label>Password</Label><Input type="password" autoComplete="new-password" value={addForm.password} onChange={e => setAddForm(f => ({ ...f, password: e.target.value }))} required minLength={8} /></div>
            <div className="flex items-center justify-between">
              <Label>Email verified</Label>
              <Switch checked={addForm.email_verified} onCheckedChange={v => setAddForm(f => ({ ...f, email_verified: v }))} />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAddOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={addSaving}>{addSaving ? 'Adding…' : 'Add User'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* ── Edit user dialog ── */}
      <Dialog open={!!editTarget} onOpenChange={v => { if (!v) setEditTarget(null); }}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Edit {editTarget?.username}#{editTarget?.discriminator}</DialogTitle>
            <DialogDescription>Update this account's information. Leave password blank to keep it unchanged.</DialogDescription>
          </DialogHeader>
          {editLoading
            ? <div className="space-y-3 py-2">{Array.from({ length: 5 }).map((_, i) => <Skeleton key={i} className="h-8 w-full" />)}</div>
            : (
              <form onSubmit={handleEdit} className="space-y-4">
                {editError && <p className="text-sm text-destructive">{editError}</p>}
                <div className="grid grid-cols-2 gap-3">
                  <div className="space-y-2"><Label>Email</Label><Input type="email" value={editForm.email} onChange={e => setEditForm(f => ({ ...f, email: e.target.value }))} required /></div>
                  <div className="space-y-2"><Label>Username</Label><Input value={editForm.username} onChange={e => setEditForm(f => ({ ...f, username: e.target.value }))} required /></div>
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div className="space-y-2"><Label>Display name</Label><Input value={editForm.display_name} onChange={e => setEditForm(f => ({ ...f, display_name: e.target.value }))} placeholder="Optional" /></div>
                  <div className="space-y-2"><Label>Phone</Label><Input value={editForm.phone} onChange={e => setEditForm(f => ({ ...f, phone: e.target.value }))} placeholder="Optional" /></div>
                </div>
                <div className="space-y-2">
                  <Label>New password</Label>
                  <Input type="password" autoComplete="new-password" value={editForm.new_password} onChange={e => setEditForm(f => ({ ...f, new_password: e.target.value }))} placeholder="Leave blank to keep current" minLength={8} />
                </div>
                <div className="flex flex-col gap-3 pt-1">
                  <div className="flex items-center justify-between"><Label>Active</Label><Switch checked={editForm.active} onCheckedChange={v => setEditForm(f => ({ ...f, active: v }))} /></div>
                  <div className="flex items-center justify-between"><Label>Email verified</Label><Switch checked={editForm.email_verified} onCheckedChange={v => setEditForm(f => ({ ...f, email_verified: v }))} /></div>
                  <div className="flex items-center justify-between"><Label>Clear account lock</Label><Switch checked={editForm.clear_lock} onCheckedChange={v => setEditForm(f => ({ ...f, clear_lock: v }))} /></div>
                </div>
                <DialogFooter>
                  <Button type="button" variant="outline" onClick={() => setEditTarget(null)}>Cancel</Button>
                  <Button type="submit" disabled={editSaving}>{editSaving ? 'Saving…' : 'Save changes'}</Button>
                </DialogFooter>
              </form>
            )
          }
        </DialogContent>
      </Dialog>

      {/* ── Remove member confirmation ── */}
      <AlertDialog open={!!removeTarget} onOpenChange={v => !v && setRemoveTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove {removeTarget?.email}?</AlertDialogTitle>
            <AlertDialogDescription>This will permanently delete the user account.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleRemoveMember} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Remove</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
