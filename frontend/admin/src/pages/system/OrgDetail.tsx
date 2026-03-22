import { useEffect, useState, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import {
  ArrowLeft, PauseCircle, PlayCircle, Pencil, UserPlus,
  MoreHorizontal, Shield, Trash2, FolderKanban, List, Plus,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Separator } from '@/components/ui/separator';
import { Skeleton } from '@/components/ui/skeleton';
import {
  getOrg, suspendOrg, unsuspendOrg, updateOrg,
  listSystemUserListMembers,
  adminListOrgAdmins, adminAssignOrgAdmin,
  addUserToList, removeSystemUserFromList,
  adminListOrgServiceAccounts,
  listUserLists, adminCreateUserList,
  listProjects, adminCreateProject,
} from '@/api';
import { fmtDateShort } from '@/lib/utils';

interface Org { id: string; name: string; slug: string; active: boolean; suspended_at: string | null; created_at: string; org_list_id: string; }
interface Member { id: string; username: string; discriminator: string; email: string; active: boolean; }
interface OrgRole { id: string; user_id: string; user_name: string; user_email: string; role: string; scope_id: string | null; scope_name: string | null; granted_at: string; }
interface ServiceAccount { id: string; name: string; description: string | null; active: boolean; last_used_at: string | null; }
interface UserList { id: string; name: string; immovable: boolean; }
interface Project { id: string; name: string; slug: string; active: boolean; assigned_user_list_id: string | null; }

export default function OrgDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  const [org, setOrg] = useState<Org | null>(null);
  const [orgListMembers, setOrgListMembers] = useState<Member[]>([]);
  const [orgRoles, setOrgRoles] = useState<OrgRole[]>([]);
  const [serviceAccounts, setServiceAccounts] = useState<ServiceAccount[]>([]);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);

  // rename
  const [renameOpen, setRenameOpen] = useState(false);
  const [renameVal, setRenameVal] = useState('');

  // add user to org list
  const [addUserOpen, setAddUserOpen] = useState(false);
  const [addUserForm, setAddUserForm] = useState({ email: '', username: '', password: '' });
  const [addUserSaving, setAddUserSaving] = useState(false);
  const [addUserError, setAddUserError] = useState('');

  // assign role
  const [assignRoleTarget, setAssignRoleTarget] = useState<Member | null>(null);
  const [assignRoleForm, setAssignRoleForm] = useState({ role: 'org_admin', scope_id: '' });
  const [assignRoleSaving, setAssignRoleSaving] = useState(false);

  // remove user
  const [removeUserTarget, setRemoveUserTarget] = useState<Member | null>(null);

  // create user list
  const [createListOpen, setCreateListOpen] = useState(false);
  const [newListName, setNewListName] = useState('');
  const [createListSaving, setCreateListSaving] = useState(false);

  // create project
  const [createProjectOpen, setCreateProjectOpen] = useState(false);
  const [newProject, setNewProject] = useState({ name: '', slug: '', redirect_uri: '' });
  const [createProjectSaving, setCreateProjectSaving] = useState(false);
  const [createProjectError, setCreateProjectError] = useState('');

  const load = useCallback(() => {
    if (!id) return;
    setLoading(true);
    getOrg(id)
      .then(o => {
        setOrg(o);
        return Promise.all([
          listSystemUserListMembers(o.org_list_id).then(r => setOrgListMembers(r ?? [])),
          adminListOrgAdmins(id).then(r => setOrgRoles(r ?? [])),
          adminListOrgServiceAccounts(id).then(r => setServiceAccounts(r ?? [])),
          listUserLists(id).then(r => {
            const all: UserList[] = r.user_lists ?? r ?? [];
            setUserLists(all.filter(l => !l.immovable));
          }),
          listProjects(id).then(r => setProjects(r.projects ?? r ?? [])),
        ]);
      })
      .catch(console.error)
      .finally(() => setLoading(false));
  }, [id]);

  useEffect(() => { load(); }, [load]);

  const rolesMap = orgRoles.reduce<Record<string, OrgRole[]>>((acc, r) => {
    (acc[r.user_id] ??= []).push(r);
    return acc;
  }, {});

  const handleSuspend = async () => {
    if (!org) return;
    if (org.suspended_at) await unsuspendOrg(org.id);
    else await suspendOrg(org.id);
    load();
  };

  const handleRename = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!org) return;
    await updateOrg(org.id, { name: renameVal });
    setRenameOpen(false);
    load();
  };

  const handleAddUser = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!org) return;
    setAddUserSaving(true);
    setAddUserError('');
    try {
      await addUserToList(org.org_list_id, addUserForm);
      setAddUserOpen(false);
      setAddUserForm({ email: '', username: '', password: '' });
      load();
    } catch {
      setAddUserError('Failed to add user.');
    } finally { setAddUserSaving(false); }
  };

  const handleAssignRole = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!assignRoleTarget || !id) return;
    setAssignRoleSaving(true);
    try {
      await adminAssignOrgAdmin(id, assignRoleTarget.id, assignRoleForm.role, assignRoleForm.scope_id || undefined);
      setAssignRoleTarget(null);
      setAssignRoleForm({ role: 'org_admin', scope_id: '' });
      load();
    } finally { setAssignRoleSaving(false); }
  };

  const handleRemoveUser = async () => {
    if (!removeUserTarget || !org) return;
    await removeSystemUserFromList(org.org_list_id, removeUserTarget.id);
    setRemoveUserTarget(null);
    load();
  };

  const handleCreateList = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!id) return;
    setCreateListSaving(true);
    try {
      await adminCreateUserList({ name: newListName, org_id: id });
      setCreateListOpen(false);
      setNewListName('');
      load();
    } finally { setCreateListSaving(false); }
  };

  const handleCreateProject = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!id) return;
    setCreateProjectSaving(true);
    setCreateProjectError('');
    try {
      await adminCreateProject(id, {
        name: newProject.name,
        slug: newProject.slug,
        redirect_uris: newProject.redirect_uri ? [newProject.redirect_uri] : [],
      });
      setCreateProjectOpen(false);
      setNewProject({ name: '', slug: '', redirect_uri: '' });
      load();
    } catch {
      setCreateProjectError('Failed to create project.');
    } finally { setCreateProjectSaving(false); }
  };

  const assignedListName = (ulId: string | null) => {
    if (!ulId) return null;
    return userLists.find(ul => ul.id === ulId)?.name ?? null;
  };

  const skeletonRows = (cols: number, rows = 2) =>
    Array.from({ length: rows }).map((_, i) => (
      <TableRow key={i}>
        {Array.from({ length: cols }).map((__, j) => (
          <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>
        ))}
      </TableRow>
    ));

  return (
    <div className="p-6 space-y-4">
      <Button variant="ghost" size="sm" className="-ml-1" onClick={() => navigate('/system/organisations')}>
        <ArrowLeft className="h-4 w-4" />Back to Organisations
      </Button>

      {/* ── Org Section ───────────────────────────────────────────────── */}
      <div className="rounded-xl border bg-card p-6 space-y-6">

        {/* Header */}
        <div className="flex items-start justify-between gap-4">
          {loading
            ? <div className="space-y-2"><Skeleton className="h-6 w-48" /><Skeleton className="h-4 w-72" /></div>
            : <div>
                <h1 className="text-xl font-bold">{org?.name}</h1>
                <p className="text-sm text-muted-foreground">
                  /{org?.slug} · Created {fmtDateShort(org?.created_at ?? null)}
                </p>
              </div>
          }
          {!loading && org && (
            <div className="flex items-center gap-2 shrink-0">
              <Badge variant={org.suspended_at ? 'destructive' : 'success'}>
                {org.suspended_at ? 'Suspended' : 'Active'}
              </Badge>
              <Button variant="outline" size="sm" onClick={() => { setRenameVal(org.name); setRenameOpen(true); }}>
                <Pencil className="h-4 w-4" />Rename
              </Button>
              <Button variant="outline" size="sm" onClick={handleSuspend}>
                {org.suspended_at
                  ? <><PlayCircle className="h-4 w-4" />Unsuspend</>
                  : <><PauseCircle className="h-4 w-4" />Suspend</>
                }
              </Button>
            </div>
          )}
        </div>

        <Separator />

        {/* Org User List */}
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Org User List</h2>
            <Button size="sm" variant="outline" disabled={loading} onClick={() => setAddUserOpen(true)}>
              <UserPlus className="h-4 w-4" />Add User
            </Button>
          </div>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                <TableHead>Email</TableHead>
                <TableHead>Roles</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? skeletonRows(4)
                : orgListMembers.length === 0
                ? <TableRow><TableCell colSpan={4} className="text-center text-muted-foreground py-6">No users in org list.</TableCell></TableRow>
                : orgListMembers.map(m => {
                    const roles = rolesMap[m.id] ?? [];
                    return (
                      <TableRow key={m.id}>
                        <TableCell className="font-medium">{m.username}#{m.discriminator}</TableCell>
                        <TableCell className="text-sm text-muted-foreground">{m.email}</TableCell>
                        <TableCell>
                          {roles.length === 0
                            ? <span className="text-xs text-muted-foreground">No role</span>
                            : roles.map(r => (
                                <Badge key={r.id} variant={r.role === 'org_admin' ? 'default' : 'secondary'} className="mr-1 text-xs">
                                  {r.role === 'org_admin' ? 'Org Admin' : `PM: ${r.scope_name ?? '…'}`}
                                </Badge>
                              ))
                          }
                        </TableCell>
                        <TableCell>
                          <DropdownMenu>
                            <DropdownMenuTrigger asChild>
                              <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                            </DropdownMenuTrigger>
                            <DropdownMenuContent>
                              <DropdownMenuItem onClick={() => { setAssignRoleTarget(m); setAssignRoleForm({ role: 'org_admin', scope_id: '' }); }}>
                                <Shield className="h-4 w-4" />Assign role
                              </DropdownMenuItem>
                              <DropdownMenuSeparator />
                              <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setRemoveUserTarget(m)}>
                                <Trash2 className="h-4 w-4" />Remove from org
                              </DropdownMenuItem>
                            </DropdownMenuContent>
                          </DropdownMenu>
                        </TableCell>
                      </TableRow>
                    );
                  })
              }
            </TableBody>
          </Table>
        </div>

        <Separator />

        {/* Service Accounts */}
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Service Accounts</h2>
            <Button size="sm" variant="outline" disabled>
              <Plus className="h-4 w-4" />New SA
            </Button>
          </div>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Description</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Last used</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? skeletonRows(4, 1)
                : serviceAccounts.length === 0
                ? <TableRow><TableCell colSpan={4} className="text-center text-muted-foreground py-6">No service accounts.</TableCell></TableRow>
                : serviceAccounts.map(sa => (
                    <TableRow key={sa.id}>
                      <TableCell className="font-medium">{sa.name}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">{sa.description ?? '—'}</TableCell>
                      <TableCell>
                        <Badge variant={sa.active ? 'success' : 'secondary'}>{sa.active ? 'Active' : 'Inactive'}</Badge>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">
                        {sa.last_used_at ? fmtDateShort(sa.last_used_at) : '—'}
                      </TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>
      </div>

      {/* ── User Lists + Projects ──────────────────────────────────────── */}
      <div className="grid grid-cols-2 gap-4">

        {/* User Lists (movable) */}
        <div className="rounded-xl border bg-card overflow-hidden">
          <div className="flex items-center justify-between px-4 py-3 border-b">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide flex items-center gap-2">
              <List className="h-4 w-4" />User Lists
            </h2>
            <Button size="sm" onClick={() => setCreateListOpen(true)}>
              <Plus className="h-4 w-4" />New
            </Button>
          </div>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Users</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? skeletonRows(2)
                : userLists.length === 0
                ? <TableRow><TableCell colSpan={2} className="text-center text-muted-foreground py-8">No user lists.</TableCell></TableRow>
                : userLists.map(ul => (
                    <TableRow key={ul.id} className="cursor-pointer hover:bg-muted/50" onClick={() => navigate(`/system/userlists/${ul.id}`)}>
                      <TableCell className="font-medium">{ul.name}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">—</TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>

        {/* Projects */}
        <div className="rounded-xl border bg-card overflow-hidden">
          <div className="flex items-center justify-between px-4 py-3 border-b">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide flex items-center gap-2">
              <FolderKanban className="h-4 w-4" />Projects
            </h2>
            <Button size="sm" onClick={() => setCreateProjectOpen(true)}>
              <Plus className="h-4 w-4" />New
            </Button>
          </div>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>User List</TableHead>
                <TableHead>Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? skeletonRows(3)
                : projects.length === 0
                ? <TableRow><TableCell colSpan={3} className="text-center text-muted-foreground py-8">No projects.</TableCell></TableRow>
                : projects.map(p => (
                    <TableRow
                      key={p.id}
                      className="cursor-pointer hover:bg-muted/50"
                      onClick={() => navigate(`/system/organisations/${id}/projects/${p.id}`)}
                    >
                      <TableCell className="font-medium">{p.name}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">
                        {p.assigned_user_list_id
                          ? (assignedListName(p.assigned_user_list_id) ?? <span className="font-mono text-xs">{p.assigned_user_list_id.slice(0, 8)}…</span>)
                          : <span className="italic">Unassigned</span>
                        }
                      </TableCell>
                      <TableCell>
                        <Badge variant={p.active ? 'success' : 'secondary'}>{p.active ? 'Active' : 'Draft'}</Badge>
                      </TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>
      </div>

      {/* ── Dialogs ───────────────────────────────────────────────────── */}

      {/* Rename */}
      <Dialog open={renameOpen} onOpenChange={setRenameOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Rename Organisation</DialogTitle></DialogHeader>
          <form onSubmit={handleRename} className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="rename">Name</Label>
              <Input id="rename" value={renameVal} onChange={e => setRenameVal(e.target.value)} required />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setRenameOpen(false)}>Cancel</Button>
              <Button type="submit">Save</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Add User to org list */}
      <Dialog open={addUserOpen} onOpenChange={setAddUserOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add User to Org List</DialogTitle>
            <DialogDescription>Creates a new user in the organisation's admin user list.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAddUser} className="space-y-4">
            {addUserError && <p className="text-sm text-destructive">{addUserError}</p>}
            <div className="space-y-2"><Label>Email</Label><Input value={addUserForm.email} onChange={e => setAddUserForm(f => ({ ...f, email: e.target.value }))} required type="email" /></div>
            <div className="space-y-2"><Label>Username</Label><Input value={addUserForm.username} onChange={e => setAddUserForm(f => ({ ...f, username: e.target.value }))} required /></div>
            <div className="space-y-2"><Label>Password</Label><Input value={addUserForm.password} onChange={e => setAddUserForm(f => ({ ...f, password: e.target.value }))} required type="password" /></div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAddUserOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={addUserSaving}>{addUserSaving ? 'Adding…' : 'Add User'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Assign Role */}
      <Dialog open={!!assignRoleTarget} onOpenChange={v => !v && setAssignRoleTarget(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Assign Role</DialogTitle>
            <DialogDescription>
              Assign an admin role to {assignRoleTarget?.username}#{assignRoleTarget?.discriminator}.
            </DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAssignRole} className="space-y-4">
            <div className="space-y-2">
              <Label>Role</Label>
              <Select value={assignRoleForm.role} onValueChange={v => setAssignRoleForm(f => ({ ...f, role: v, scope_id: '' }))}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="org_admin">Org Admin</SelectItem>
                  <SelectItem value="project_manager">Project Manager</SelectItem>
                </SelectContent>
              </Select>
            </div>
            {assignRoleForm.role === 'project_manager' && (
              <div className="space-y-2">
                <Label>Project (scope)</Label>
                <Select value={assignRoleForm.scope_id} onValueChange={v => setAssignRoleForm(f => ({ ...f, scope_id: v }))}>
                  <SelectTrigger><SelectValue placeholder="Select project" /></SelectTrigger>
                  <SelectContent>
                    {projects.map(p => <SelectItem key={p.id} value={p.id}>{p.name}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
            )}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAssignRoleTarget(null)}>Cancel</Button>
              <Button type="submit" disabled={assignRoleSaving}>{assignRoleSaving ? 'Assigning…' : 'Assign'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Remove User */}
      <AlertDialog open={!!removeUserTarget} onOpenChange={v => !v && setRemoveUserTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove {removeUserTarget?.username}#{removeUserTarget?.discriminator}?</AlertDialogTitle>
            <AlertDialogDescription>
              This removes the user from the org list and permanently deletes their account.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleRemoveUser} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">
              Remove
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      {/* Create User List */}
      <Dialog open={createListOpen} onOpenChange={setCreateListOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>New User List</DialogTitle>
            <DialogDescription>Creates a movable user list in this organisation.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleCreateList} className="space-y-4">
            <div className="space-y-2">
              <Label>Name</Label>
              <Input value={newListName} onChange={e => setNewListName(e.target.value)} required placeholder="Acme Employees" />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateListOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={createListSaving}>{createListSaving ? 'Creating…' : 'Create'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Create Project */}
      <Dialog open={createProjectOpen} onOpenChange={setCreateProjectOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>New Project</DialogTitle>
            <DialogDescription>Create a new project in this organisation.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleCreateProject} className="space-y-4">
            {createProjectError && <p className="text-sm text-destructive">{createProjectError}</p>}
            <div className="space-y-2">
              <Label>Name</Label>
              <Input value={newProject.name} onChange={e => setNewProject(p => ({ ...p, name: e.target.value }))} required placeholder="Main App" />
            </div>
            <div className="space-y-2">
              <Label>Slug</Label>
              <Input
                value={newProject.slug}
                onChange={e => setNewProject(p => ({ ...p, slug: e.target.value.toLowerCase().replace(/\s+/g, '-') }))}
                required placeholder="main-app" pattern="[a-z0-9][a-z0-9-]*"
              />
              <p className="text-xs text-muted-foreground">Lowercase letters, numbers and hyphens only.</p>
            </div>
            <div className="space-y-2">
              <Label>Redirect URI</Label>
              <Input value={newProject.redirect_uri} onChange={e => setNewProject(p => ({ ...p, redirect_uri: e.target.value }))} placeholder="https://app.example.com/callback" />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateProjectOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={createProjectSaving}>{createProjectSaving ? 'Creating…' : 'Create'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  );
}
