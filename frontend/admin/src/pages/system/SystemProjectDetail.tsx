import { useEffect, useState, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, Pencil, Plus, Trash2, MoreHorizontal } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Separator } from '@/components/ui/separator';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import {
  adminGetProject, adminUpdateProject, adminAssignUserList, adminUnassignUserList,
  listUserLists, adminListRoles, adminCreateRole, adminDeleteRole,
} from '@/api';
import { fmtDateShort } from '@/lib/utils';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  hydra_client_id: string;
  assigned_user_list_id: string | null;
  require_role_to_login: boolean;
  allow_self_registration: boolean;
  email_verification_enabled: boolean;
  sms_verification_enabled: boolean;
  created_at: string;
}
interface UserList { id: string; name: string; immovable: boolean; }
interface Role { id: string; name: string; description: string | null; }

const SETTINGS: { field: keyof Project; label: string; desc: string }[] = [
  { field: 'require_role_to_login',       label: 'Require role to login',    desc: 'Block users with no assigned role from authenticating' },
  { field: 'allow_self_registration',     label: 'Allow self-registration',  desc: 'Let users create their own accounts on the login page' },
  { field: 'email_verification_enabled',  label: 'Email verification',       desc: 'Require email OTP on registration and password reset' },
  { field: 'sms_verification_enabled',    label: 'SMS verification',         desc: 'Require SMS OTP on registration and password reset' },
];

export default function SystemProjectDetail() {
  const { oid, pid } = useParams<{ oid: string; pid: string }>();
  const navigate = useNavigate();

  const [project, setProject] = useState<Project | null>(null);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);

  // rename
  const [renameOpen, setRenameOpen] = useState(false);
  const [renameVal, setRenameVal] = useState('');

  // create role
  const [createRoleOpen, setCreateRoleOpen] = useState(false);
  const [newRole, setNewRole] = useState({ name: '', description: '' });
  const [createRoleSaving, setCreateRoleSaving] = useState(false);

  // delete role
  const [deleteRoleTarget, setDeleteRoleTarget] = useState<Role | null>(null);

  const load = useCallback(() => {
    if (!oid || !pid) return;
    setLoading(true);
    Promise.all([
      adminGetProject(pid).then(setProject),
      listUserLists(oid).then(r => setUserLists(r.user_lists ?? r ?? [])),
      adminListRoles(pid).then(r => setRoles(r ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  }, [oid, pid]);

  useEffect(() => { load(); }, [load]);

  const handleToggle = async (field: string, value: boolean) => {
    if (!pid) return;
    await adminUpdateProject(pid, { [field]: value });
    setProject(p => p ? { ...p, [field]: value } : p);
  };

  const handleAssignList = async (ulId: string) => {
    if (!pid) return;
    if (ulId === '__none__') await adminUnassignUserList(pid);
    else await adminAssignUserList(pid, ulId);
    adminGetProject(pid).then(setProject);
  };

  const handleRename = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!pid) return;
    await adminUpdateProject(pid, { name: renameVal });
    setRenameOpen(false);
    load();
  };

  const handleCreateRole = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!pid) return;
    setCreateRoleSaving(true);
    try {
      await adminCreateRole(pid, { name: newRole.name, description: newRole.description || undefined });
      setCreateRoleOpen(false);
      setNewRole({ name: '', description: '' });
      adminListRoles(pid).then(r => setRoles(r ?? []));
    } finally { setCreateRoleSaving(false); }
  };

  const handleDeleteRole = async () => {
    if (!deleteRoleTarget || !pid) return;
    await adminDeleteRole(pid, deleteRoleTarget.id);
    setDeleteRoleTarget(null);
    adminListRoles(pid).then(r => setRoles(r ?? []));
  };

  const movableLists = userLists.filter(ul => !ul.immovable);

  return (
    <div className="p-6 space-y-4">
      <Button variant="ghost" size="sm" className="-ml-1" onClick={() => navigate(`/system/organisations/${oid}`)}>
        <ArrowLeft className="h-4 w-4" />Back to Organisation
      </Button>

      {/* ── Project card ──────────────────────────────────────────────── */}
      <div className="rounded-xl border bg-card p-6 space-y-6">

        {/* Header */}
        <div className="flex items-start justify-between gap-4">
          {loading
            ? <div className="space-y-2"><Skeleton className="h-6 w-48" /><Skeleton className="h-4 w-80" /></div>
            : <div>
                <h1 className="text-xl font-bold">{project?.name}</h1>
                <p className="text-sm text-muted-foreground">
                  /{project?.slug} · <span className="font-mono text-xs">{project?.hydra_client_id}</span> · Created {fmtDateShort(project?.created_at ?? null)}
                </p>
              </div>
          }
          {!loading && project && (
            <div className="flex items-center gap-2 shrink-0">
              <Badge variant={project.active ? 'success' : 'secondary'}>
                {project.active ? 'Active' : 'Inactive'}
              </Badge>
              <Button variant="outline" size="sm" onClick={() => { setRenameVal(project.name); setRenameOpen(true); }}>
                <Pencil className="h-4 w-4" />Rename
              </Button>
            </div>
          )}
        </div>

        <Separator />

        {/* User List assignment */}
        <div className="space-y-2">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Assigned User List</h2>
          {loading
            ? <Skeleton className="h-10 w-72" />
            : <Select value={project?.assigned_user_list_id ?? '__none__'} onValueChange={handleAssignList}>
                <SelectTrigger className="w-72">
                  <SelectValue placeholder="— No user list assigned —" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="__none__">— None —</SelectItem>
                  {movableLists.map(ul => (
                    <SelectItem key={ul.id} value={ul.id}>{ul.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
          }
          {!loading && !project?.assigned_user_list_id && (
            <p className="text-xs text-amber-500">No user list assigned — users cannot log in to this project.</p>
          )}
        </div>

        <Separator />

        {/* Settings toggles */}
        <div className="space-y-4">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Settings</h2>
          {loading
            ? Array.from({ length: 4 }).map((_, i) => <Skeleton key={i} className="h-10 w-full" />)
            : project && SETTINGS.map(({ field, label, desc }) => (
                <div key={field} className="flex items-center justify-between py-1">
                  <div>
                    <p className="text-sm font-medium">{label}</p>
                    <p className="text-xs text-muted-foreground">{desc}</p>
                  </div>
                  <Switch
                    checked={project[field] as boolean}
                    onCheckedChange={v => handleToggle(field, v)}
                  />
                </div>
              ))
          }
        </div>
      </div>

      {/* ── Roles ─────────────────────────────────────────────────────── */}
      <div className="rounded-xl border bg-card overflow-hidden">
        <div className="flex items-center justify-between px-4 py-3 border-b">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Roles</h2>
          <Button size="sm" onClick={() => setCreateRoleOpen(true)}>
            <Plus className="h-4 w-4" />New Role
          </Button>
        </div>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Description</TableHead>
              <TableHead className="w-12"></TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading
              ? Array.from({ length: 2 }).map((_, i) => (
                  <TableRow key={i}>{Array.from({ length: 3 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                ))
              : roles.length === 0
              ? <TableRow><TableCell colSpan={3} className="text-center text-muted-foreground py-8">No roles defined yet.</TableCell></TableRow>
              : roles.map(r => (
                  <TableRow key={r.id}>
                    <TableCell className="font-mono text-sm font-medium">{r.name}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">{r.description ?? '—'}</TableCell>
                    <TableCell>
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent>
                          <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setDeleteRoleTarget(r)}>
                            <Trash2 className="h-4 w-4" />Delete
                          </DropdownMenuItem>
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </TableCell>
                  </TableRow>
                ))
            }
          </TableBody>
        </Table>
      </div>

      {/* ── Dialogs ───────────────────────────────────────────────────── */}

      {/* Rename */}
      <Dialog open={renameOpen} onOpenChange={setRenameOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Rename Project</DialogTitle></DialogHeader>
          <form onSubmit={handleRename} className="space-y-4">
            <div className="space-y-2">
              <Label>Name</Label>
              <Input value={renameVal} onChange={e => setRenameVal(e.target.value)} required />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setRenameOpen(false)}>Cancel</Button>
              <Button type="submit">Save</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Create Role */}
      <Dialog open={createRoleOpen} onOpenChange={setCreateRoleOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>New Role</DialogTitle>
            <DialogDescription>Define a role that can be assigned to users in this project.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleCreateRole} className="space-y-4">
            <div className="space-y-2">
              <Label>Name</Label>
              <Input
                value={newRole.name}
                onChange={e => setNewRole(r => ({ ...r, name: e.target.value }))}
                required placeholder="admin" pattern="[a-z0-9_-]+"
              />
              <p className="text-xs text-muted-foreground">Lowercase letters, numbers, hyphens and underscores only.</p>
            </div>
            <div className="space-y-2">
              <Label>Description</Label>
              <Input
                value={newRole.description}
                onChange={e => setNewRole(r => ({ ...r, description: e.target.value }))}
                placeholder="Full access"
              />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateRoleOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={createRoleSaving}>{createRoleSaving ? 'Creating…' : 'Create'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Delete Role */}
      <AlertDialog open={!!deleteRoleTarget} onOpenChange={v => !v && setDeleteRoleTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete role "{deleteRoleTarget?.name}"?</AlertDialogTitle>
            <AlertDialogDescription>
              All user role assignments for this role will be removed. This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleDeleteRole} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
