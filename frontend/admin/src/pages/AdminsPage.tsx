import { useEffect, useState, useCallback } from 'react';
import { UserCog, UserPlus, Trash2, Pencil, Shield } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Switch } from '@/components/ui/switch';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import {
  listUserLists, listSystemUserListMembers, addUserToList, removeSystemUserFromList,
  adminGetUser, adminUpdateUser,
  listOrgAdmins, assignOrgAdmin, removeOrgAdmin,
  listOrgListManagers, assignOrgListManager, removeOrgListManager,
  orgGetUser, orgUpdateUser,
  listProjects,
} from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import { useAuth } from '@/context/AuthContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

// ── Types ──────────────────────────────────────────────────────────────────────
interface Member {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
}
interface OrgRole {
  id: string; user_id: string; user_email: string; user_name: string;
  role: string; scope_id: string | null; scope_name: string | null; granted_at: string;
}
interface Project { id: string; name: string; }
type EditFields = {
  email: string; username: string; display_name: string; phone: string;
  active: boolean; email_verified: boolean; clear_lock: boolean; new_password: string;
};

// ── Main component ─────────────────────────────────────────────────────────────
export default function AdminsPage() {
  const { orgId, isSystemCtx } = useOrgContext();
  const { isSuperAdmin } = useAuth();
  const isSystem = isSuperAdmin && !orgId; // true only on /system/admins route

  // System state
  const [systemListId, setSystemListId] = useState<string | null>(null);
  const [members, setMembers] = useState<Member[]>([]);

  // Org state
  const [roles, setRoles] = useState<OrgRole[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Add admin (system only)
  const [addOpen, setAddOpen] = useState(false);
  const [addForm, setAddForm] = useState({ email: '', username: '', password: '', email_verified: false });
  const [addSaving, setAddSaving] = useState(false);
  const [addError, setAddError] = useState('');

  // Assign role (org only)
  const [assignOpen, setAssignOpen] = useState(false);
  const [assignForm, setAssignForm] = useState({ user_id: '', role: 'org_admin', scope_id: '' });
  const [assignSaving, setAssignSaving] = useState(false);

  // Edit user account (both contexts)
  const [editTarget, setEditTarget] = useState<{ id: string; label: string } | null>(null);
  const [editForm, setEditForm] = useState<EditFields>({ email: '', username: '', display_name: '', phone: '', active: true, email_verified: false, clear_lock: false, new_password: '' });
  const [editRoles, setEditRoles] = useState<{ role: string; scope_id: string | null }[]>([]);
  const [editLoading, setEditLoading] = useState(false);
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState('');

  // Remove
  const [removeTarget, setRemoveTarget] = useState<{ id: string; label: string; isRole?: boolean } | null>(null);

  // ── Load ────────────────────────────────────────────────────────────────────
  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      if (isSystem) {
        const res = await listUserLists();
        const all: { id: string; org_id: string | null; immovable: boolean }[] = res.user_lists ?? res ?? [];
        const syslist = all.find(l => l.org_id === null && l.immovable);
        if (!syslist) { setError('System user list not found.'); return; }
        setSystemListId(syslist.id);
        const mres = await listSystemUserListMembers(syslist.id);
        setMembers(mres.users ?? mres ?? []);
      } else {
        const adminsPromise = isSystemCtx
          ? listOrgAdmins(orgId!).then(r => r.admins ?? r ?? [])
          : listOrgListManagers().then(r => r.admins ?? r ?? []);
        const [admins, projs] = await Promise.all([
          adminsPromise,
          listProjects(orgId!).then(r => r.projects ?? r ?? []),
        ]);
        setRoles(admins);
        setProjects(projs);
      }
    } catch { setError('Failed to load.'); }
    finally { setLoading(false); }
  }, [isSystem, isSystemCtx, orgId]);

  useEffect(() => { load(); }, [load]);

  // ── Add system admin ────────────────────────────────────────────────────────
  const handleAdd = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!systemListId) return;
    setAddSaving(true); setAddError('');
    try {
      await addUserToList(systemListId, addForm);
      setAddOpen(false);
      setAddForm({ email: '', username: '', password: '', email_verified: false });
      await load();
    } catch { setAddError('Failed to add admin. The email or username may already exist.'); }
    finally { setAddSaving(false); }
  };

  // ── Assign org role ─────────────────────────────────────────────────────────
  const handleAssign = async (e: React.FormEvent) => {
    e.preventDefault();
    setAssignSaving(true);
    try {
      if (isSystemCtx) {
        await assignOrgAdmin(orgId!, assignForm.user_id, assignForm.role, assignForm.scope_id || undefined);
      } else {
        await assignOrgListManager({ user_id: assignForm.user_id, role: assignForm.role, scope_id: assignForm.scope_id || undefined });
      }
      setAssignOpen(false);
      setAssignForm({ user_id: '', role: 'org_admin', scope_id: '' });
      load();
    } finally { setAssignSaving(false); }
  };

  // ── Edit user ───────────────────────────────────────────────────────────────
  const openEdit = async (userId: string, label: string) => {
    setEditTarget({ id: userId, label });
    setEditError(''); setEditLoading(true);
    try {
      const u = isSystem ? await adminGetUser(userId) : await orgGetUser(userId);
      setEditRoles(u.roles ?? []);
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
      const body = {
        email: editForm.email, username: editForm.username,
        display_name: editForm.display_name, phone: editForm.phone,
        active: editForm.active, email_verified: editForm.email_verified,
        clear_lock: editForm.clear_lock,
        new_password: editForm.new_password || undefined,
      };
      if (isSystem) await adminUpdateUser(editTarget.id, body);
      else await orgUpdateUser(editTarget.id, body);
      setEditTarget(null);
      await load();
    } catch { setEditError('Failed to save changes.'); }
    finally { setEditSaving(false); }
  };

  // ── Remove ──────────────────────────────────────────────────────────────────
  const handleRemove = async () => {
    if (!removeTarget) return;
    if (isSystem) {
      if (systemListId) await removeSystemUserFromList(systemListId, removeTarget.id);
    } else {
      if (isSystemCtx) await removeOrgAdmin(orgId!, removeTarget.id);
      else await removeOrgListManager(removeTarget.id);
    }
    setRemoveTarget(null);
    load();
  };

  // ── Render ──────────────────────────────────────────────────────────────────
  return (
    <div>
      <PageHeader
        title={isSystem ? 'System Admins' : 'Organisation Admins'}
        description={isSystem
          ? 'Users with super_admin access across the entire platform'
          : 'Manage who administers this organisation and its projects'}
        action={isSystem
          ? <Button onClick={() => setAddOpen(true)} disabled={!systemListId}><UserPlus className="h-4 w-4" />Add Admin</Button>
          : (orgId ? <Button onClick={() => setAssignOpen(true)}><UserPlus className="h-4 w-4" />Assign Role</Button> : undefined)
        }
      />

      <div className="p-6">
        {error && <p className="text-sm text-destructive mb-4">{error}</p>}
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                {!isSystem && <TableHead>Role</TableHead>}
                {!isSystem && <TableHead>Scope</TableHead>}
                <TableHead>Status</TableHead>
                <TableHead>Last login</TableHead>
                <TableHead className="w-24"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={i}>
                      {Array.from({ length: isSystem ? 4 : 6 }).map((__, j) => (
                        <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>
                      ))}
                    </TableRow>
                  ))
                : isSystem
                  ? members.length === 0
                    ? <TableRow><TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                        <UserCog className="h-8 w-8 mx-auto mb-2 opacity-30" />No system admins yet.
                      </TableCell></TableRow>
                    : members.map(m => (
                        <TableRow key={m.id}>
                          <TableCell>
                            <p className="font-medium text-sm font-mono">{m.username}<span className="text-muted-foreground">#{m.discriminator}</span></p>
                            {m.display_name && <p className="text-xs text-muted-foreground">{m.display_name}</p>}
                          </TableCell>
                          <TableCell><Badge variant={m.active ? 'success' : 'secondary'}>{m.active ? 'Active' : 'Disabled'}</Badge></TableCell>
                          <TableCell className="text-sm text-muted-foreground">{fmtDate(m.last_login_at)}</TableCell>
                          <TableCell>
                            <div className="flex items-center justify-end gap-1">
                              <Button variant="ghost" size="icon" onClick={() => openEdit(m.id, `${m.username}#${m.discriminator}`)}>
                                <Pencil className="h-4 w-4" />
                              </Button>
                              <Button variant="ghost" size="icon" className="text-destructive hover:text-destructive hover:bg-destructive/10"
                                onClick={() => setRemoveTarget({ id: m.id, label: `${m.username}#${m.discriminator}` })}>
                                <Trash2 className="h-4 w-4" />
                              </Button>
                            </div>
                          </TableCell>
                        </TableRow>
                      ))
                  : roles.length === 0
                    ? <TableRow><TableCell colSpan={6} className="text-center text-muted-foreground py-12">
                        <Shield className="h-8 w-8 mx-auto mb-2 opacity-40" />No admins assigned.
                      </TableCell></TableRow>
                    : roles.map(r => (
                        <TableRow key={r.id}>
                          <TableCell>
                            <p className="font-medium text-sm">{r.user_name}</p>
                            <p className="text-xs text-muted-foreground">{r.user_email}</p>
                          </TableCell>
                          <TableCell><Badge variant={r.role === 'org_admin' ? 'default' : 'secondary'}>{r.role}</Badge></TableCell>
                          <TableCell className="text-sm text-muted-foreground">
                            {r.scope_name ?? (r.scope_id ? r.scope_id.slice(0, 8) + '…' : 'Entire org')}
                          </TableCell>
                          <TableCell>—</TableCell>
                          <TableCell className="text-sm text-muted-foreground">{fmtDate(r.granted_at)}</TableCell>
                          <TableCell>
                            <div className="flex items-center justify-end gap-1">
                              <Button variant="ghost" size="icon" onClick={() => openEdit(r.user_id, r.user_name)}>
                                <Pencil className="h-4 w-4" />
                              </Button>
                              <Button variant="ghost" size="icon" className="text-destructive hover:text-destructive hover:bg-destructive/10"
                                onClick={() => setRemoveTarget({ id: r.id, label: `${r.user_name} (${r.role})`, isRole: true })}>
                                <Trash2 className="h-4 w-4" />
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

      {/* Add system admin */}
      <Dialog open={addOpen} onOpenChange={v => { setAddOpen(v); setAddError(''); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add System Admin</DialogTitle>
            <DialogDescription>Creates a new user account with super_admin access.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAdd} className="space-y-4">
            {addError && <p className="text-sm text-destructive">{addError}</p>}
            <div className="space-y-2"><Label>Email</Label><Input type="email" value={addForm.email} onChange={e => setAddForm(f => ({ ...f, email: e.target.value }))} required placeholder="admin@example.com" /></div>
            <div className="space-y-2"><Label>Username</Label><Input value={addForm.username} onChange={e => setAddForm(f => ({ ...f, username: e.target.value }))} required placeholder="johndoe" /></div>
            <div className="space-y-2"><Label>Initial password</Label><Input type="password" value={addForm.password} onChange={e => setAddForm(f => ({ ...f, password: e.target.value }))} required minLength={8} /></div>
            <div className="flex items-center justify-between"><Label>Email verified</Label><Switch checked={addForm.email_verified} onCheckedChange={v => setAddForm(f => ({ ...f, email_verified: v }))} /></div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAddOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={addSaving}>{addSaving ? 'Adding…' : 'Add Admin'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Assign org role */}
      <Dialog open={assignOpen} onOpenChange={setAssignOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Assign Admin Role</DialogTitle><DialogDescription>Grant a user administrative access to this organisation.</DialogDescription></DialogHeader>
          <form onSubmit={handleAssign} className="space-y-4">
            <div className="space-y-2"><Label>User ID</Label><Input value={assignForm.user_id} onChange={e => setAssignForm(f => ({ ...f, user_id: e.target.value }))} required placeholder="User UUID" /></div>
            <div className="space-y-2">
              <Label>Role</Label>
              <Select value={assignForm.role} onValueChange={v => setAssignForm(f => ({ ...f, role: v, scope_id: '' }))}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="org_admin">Org Admin</SelectItem>
                  <SelectItem value="project_admin">Project Admin</SelectItem>
                </SelectContent>
              </Select>
            </div>
            {assignForm.role === 'project_admin' && (
              <div className="space-y-2">
                <Label>Project</Label>
                <Select value={assignForm.scope_id} onValueChange={v => setAssignForm(f => ({ ...f, scope_id: v }))}>
                  <SelectTrigger><SelectValue placeholder="Select project" /></SelectTrigger>
                  <SelectContent>{projects.map(p => <SelectItem key={p.id} value={p.id}>{p.name}</SelectItem>)}</SelectContent>
                </Select>
              </div>
            )}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAssignOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={assignSaving}>{assignSaving ? 'Assigning…' : 'Assign'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Edit user account */}
      <Dialog open={!!editTarget} onOpenChange={v => { if (!v) setEditTarget(null); }}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Edit {editTarget?.label}</DialogTitle>
            <DialogDescription>Update account details. Leave password blank to keep unchanged.</DialogDescription>
          </DialogHeader>
          {editLoading
            ? <div className="space-y-3 py-2">{Array.from({ length: 5 }).map((_, i) => <Skeleton key={i} className="h-8 w-full" />)}</div>
            : (
              <form onSubmit={handleEdit} className="space-y-4">
                {editError && <p className="text-sm text-destructive">{editError}</p>}
                {editRoles.length > 0 && (
                  <div className="flex flex-wrap gap-1">
                    {editRoles.map((r, i) => <Badge key={i} variant="outline">{r.role}{r.scope_id ? ` (project)` : ''}</Badge>)}
                  </div>
                )}
                <div className="grid grid-cols-2 gap-3">
                  <div className="space-y-2"><Label>Email</Label><Input type="email" value={editForm.email} onChange={e => setEditForm(f => ({ ...f, email: e.target.value }))} required /></div>
                  <div className="space-y-2"><Label>Username</Label><Input value={editForm.username} onChange={e => setEditForm(f => ({ ...f, username: e.target.value }))} required /></div>
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div className="space-y-2"><Label>Display name</Label><Input value={editForm.display_name} onChange={e => setEditForm(f => ({ ...f, display_name: e.target.value }))} placeholder="Optional" /></div>
                  <div className="space-y-2"><Label>Phone</Label><Input value={editForm.phone} onChange={e => setEditForm(f => ({ ...f, phone: e.target.value }))} placeholder="Optional" /></div>
                </div>
                <div className="space-y-2"><Label>New password</Label><Input type="password" value={editForm.new_password} onChange={e => setEditForm(f => ({ ...f, new_password: e.target.value }))} placeholder="Leave blank to keep current" minLength={8} /></div>
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

      {/* Remove confirmation */}
      <AlertDialog open={!!removeTarget} onOpenChange={v => !v && setRemoveTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove {removeTarget?.label}?</AlertDialogTitle>
            <AlertDialogDescription>
              {removeTarget?.isRole
                ? 'This will revoke this management role.'
                : 'This removes their admin access and deletes their account. This cannot be undone.'}
            </AlertDialogDescription>
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
