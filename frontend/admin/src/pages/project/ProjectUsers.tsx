import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { useProjectContext } from '@/hooks/useOrgContext';
import { useAuth } from '@/context/AuthContext';
import { UserPlus, Trash2, CheckCircle, XCircle, LogOut, Plus, List } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Label } from '@/components/ui/label';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import {
  listProjectUsers, listRoles, assignRole, removeRole,
  forceLogoutProjectUser, getProject, createProjectUser,
  listUserLists, assignUserList, unassignUserList,
  adminAssignUserList, adminUnassignUserList,
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

export default function ProjectUsers() {
  const { projectId, isSystemCtx } = useProjectContext();
  const { oid } = useParams<{ oid?: string }>();
  const { isOrgAdmin, orgId: tokenOrgId } = useAuth();

  const [users, setUsers] = useState<ProjectUser[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [project, setProject] = useState<Project | null>(null);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');

  const [assignOpen, setAssignOpen] = useState<ProjectUser | null>(null);
  const [selectedRole, setSelectedRole] = useState('');
  const [saving, setSaving] = useState(false);

  const [createOpen, setCreateOpen] = useState(false);
  const [newUser, setNewUser] = useState({ email: '', username: '', password: '' });
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState('');

  const orgId = oid ?? tokenOrgId;

  const load = () => {
    if (!projectId) { setLoading(false); return; }
    setLoading(true);
    const fetches: Promise<unknown>[] = [
      listProjectUsers(projectId).then(r => setUsers(r.users ?? r ?? [])),
      listRoles(projectId).then(r => setRoles(r.roles ?? r ?? [])),
      getProject(projectId).then(p => setProject(p)).catch(() => null),
    ];
    if (isOrgAdmin && orgId) {
      fetches.push(listUserLists(orgId).then(r => setUserLists(r.user_lists ?? r ?? [])).catch(() => null));
    }
    Promise.all(fetches).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [projectId]);

  const defaultRoleId = project?.default_role_id ?? null;
  const defaultRoleName = roles.find(r => r.id === defaultRoleId)?.name;
  const assignedListId = project?.assigned_user_list_id ?? null;
  const assignedListName = project?.assigned_user_list_name
    ?? userLists.find(ul => ul.id === assignedListId)?.name
    ?? null;
  const movableLists = userLists.filter(ul => !ul.immovable);

  const filtered = users.filter(u =>
    u.email.toLowerCase().includes(search.toLowerCase()) ||
    u.username.toLowerCase().includes(search.toLowerCase()) ||
    (u.display_name?.toLowerCase().includes(search.toLowerCase()) ?? false)
  );

  const handleAssignList = async (ulId: string) => {
    if (!projectId) return;
    if (ulId === '__none__') {
      if (isSystemCtx) await adminUnassignUserList(projectId);
      else await unassignUserList(projectId);
    } else {
      if (isSystemCtx) await adminAssignUserList(projectId, ulId);
      else await assignUserList(projectId, ulId);
    }
    getProject(projectId).then(p => setProject(p)).catch(() => null);
  };

  const handleAssign = async () => {
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

  const handleForceLogout = async (userId: string) => {
    await forceLogoutProjectUser(projectId, userId);
  };

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setCreating(true);
    setCreateError('');
    try {
      await createProjectUser(projectId, {
        email: newUser.email,
        username: newUser.username || undefined,
        password: newUser.password,
      });
      setCreateOpen(false);
      setNewUser({ email: '', username: '', password: '' });
      load();
    } catch {
      setCreateError('Failed to create user. The email may already be taken.');
    } finally { setCreating(false); }
  };

  const availableRoles = (user: ProjectUser) =>
    roles.filter(r => !user.roles.some(ur => ur.id === r.id));

  return (
    <div>
      <PageHeader
        title="Project Users"
        description="Users and their role assignments in this project"
        action={
          <Button onClick={() => { setCreateOpen(true); setCreateError(''); }}>
            <Plus className="h-4 w-4" />New User
          </Button>
        }
      />
      <div className="p-6 space-y-4">
        {/* ── User List card ── */}
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
                                  {r.id === defaultRoleId && (
                                    <span className="text-[10px] opacity-60 ml-0.5">default</span>
                                  )}
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
                            <Button
                              size="sm" variant="outline"
                              onClick={() => { setAssignOpen(user); setSelectedRole(''); }}
                            >
                              <UserPlus className="h-3 w-3" />Add Role
                            </Button>
                          )}
                          <Button
                            size="sm" variant="ghost"
                            className="text-destructive hover:text-destructive"
                            title="Force logout all sessions"
                            onClick={() => handleForceLogout(user.id)}
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
            <Button onClick={handleAssign} disabled={saving || !selectedRole}>{saving ? 'Assigning…' : 'Assign'}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* ── Create User dialog ── */}
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
              <Input
                type="email"
                value={newUser.email}
                onChange={e => setNewUser(u => ({ ...u, email: e.target.value }))}
                required
                placeholder="user@example.com"
              />
            </div>
            <div className="space-y-2">
              <Label>Username <span className="text-muted-foreground text-xs">(optional, defaults to email prefix)</span></Label>
              <Input
                value={newUser.username}
                onChange={e => setNewUser(u => ({ ...u, username: e.target.value }))}
                placeholder="johndoe"
              />
            </div>
            <div className="space-y-2">
              <Label>Password</Label>
              <Input
                type="password"
                value={newUser.password}
                onChange={e => setNewUser(u => ({ ...u, password: e.target.value }))}
                required
                placeholder="••••••••"
              />
            </div>
            {createError && <p className="text-sm text-destructive">{createError}</p>}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={creating}>{creating ? 'Creating…' : 'Create User'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  );
}
