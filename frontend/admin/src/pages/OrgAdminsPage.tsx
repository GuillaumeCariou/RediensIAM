import { useEffect, useState, useCallback } from 'react';
import { Shield, UserPlus, Trash2, Pencil } from 'lucide-react';
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
  listOrgAdmins, assignOrgAdmin, removeOrgAdmin,
  listOrgListManagers, assignOrgListManager, removeOrgListManager,
  adminGetUser, adminUpdateUser, orgGetUser, orgUpdateUser,
  listProjects,
} from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface OrgRole {
  id: string; user_id: string; user_email: string; user_name: string;
  role: string; scope_id: string | null; scope_name: string | null;
  granted_at: string; active?: boolean; last_login_at?: string | null;
}
interface Project { id: string; name: string; }
type EditFields = {
  email: string; username: string; display_name: string; phone: string;
  active: boolean; email_verified: boolean; clear_lock: boolean; new_password: string;
};

export default function OrgAdminsPage() {
  const { orgId, isSystemCtx } = useOrgContext();

  const [roles, setRoles] = useState<OrgRole[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);

  // Assign dialog
  const [assignOpen, setAssignOpen] = useState(false);
  const [assignForm, setAssignForm] = useState({ user_id: '', role: 'org_admin', scope_id: '' });
  const [assignSaving, setAssignSaving] = useState(false);

  // Edit dialog
  const [editTarget, setEditTarget] = useState<{ id: string; label: string } | null>(null);
  const [editForm, setEditForm] = useState<EditFields>({ email: '', username: '', display_name: '', phone: '', active: true, email_verified: false, clear_lock: false, new_password: '' });
  const [editLoading, setEditLoading] = useState(false);
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState('');

  // Remove dialog
  const [removeTarget, setRemoveTarget] = useState<{ id: string; label: string } | null>(null);

  const load = useCallback(async () => {
    if (!orgId) return;
    setLoading(true);
    try {
      const [admins, projs] = await Promise.all([
        isSystemCtx
          ? listOrgAdmins(orgId).then(r => r.admins ?? r ?? [])
          : listOrgListManagers().then(r => r.admins ?? r ?? []),
        listProjects(orgId).then(r => r.projects ?? r ?? []),
      ]);
      setRoles(admins);
      setProjects(projs);
    } finally { setLoading(false); }
  }, [orgId, isSystemCtx]);

  useEffect(() => { load(); }, [load]);

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

  const openEdit = async (userId: string, label: string) => {
    setEditTarget({ id: userId, label });
    setEditError(''); setEditLoading(true);
    try {
      const u = isSystemCtx ? await adminGetUser(userId) : await orgGetUser(userId);
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
      if (isSystemCtx) await adminUpdateUser(editTarget.id, body);
      else await orgUpdateUser(editTarget.id, body);
      setEditTarget(null);
      await load();
    } catch { setEditError('Failed to save changes.'); }
    finally { setEditSaving(false); }
  };

  const handleRemove = async () => {
    if (!removeTarget) return;
    if (isSystemCtx) await removeOrgAdmin(orgId!, removeTarget.id);
    else await removeOrgListManager(removeTarget.id);
    setRemoveTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Organisation Admins"
        description="Manage who administers this organisation and its projects"
        action={
          orgId
            ? <Button onClick={() => setAssignOpen(true)}><UserPlus className="h-4 w-4" />Assign Role</Button>
            : undefined
        }
      />

      <div className="p-6">
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                <TableHead>Role</TableHead>
                <TableHead>Scope</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Granted</TableHead>
                <TableHead className="w-20" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={i}>
                      {Array.from({ length: 6 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}
                    </TableRow>
                  ))
                : roles.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={6} className="text-center text-muted-foreground py-12">
                        <Shield className="h-8 w-8 mx-auto mb-2 opacity-40" />No admins assigned yet.
                      </TableCell>
                    </TableRow>
                  )
                : roles.map(r => (
                    <TableRow key={r.id}>
                      <TableCell>
                        <p className="font-medium text-sm">{r.user_name}</p>
                        <p className="text-xs text-muted-foreground">{r.user_email}</p>
                      </TableCell>
                      <TableCell>
                        <Badge variant={r.role === 'org_admin' ? 'default' : 'secondary'}>{r.role}</Badge>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">
                        {r.scope_name ?? (r.scope_id ? `${r.scope_id.slice(0, 8)}…` : 'Entire org')}
                      </TableCell>
                      <TableCell>
                        {r.active !== undefined
                          ? <Badge variant={r.active ? 'success' : 'secondary'}>{r.active ? 'Active' : 'Disabled'}</Badge>
                          : <span className="text-muted-foreground text-xs">—</span>
                        }
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(r.granted_at)}</TableCell>
                      <TableCell>
                        <div className="flex items-center justify-end gap-1">
                          <Button variant="ghost" size="icon" onClick={() => openEdit(r.user_id, r.user_name)}>
                            <Pencil className="h-4 w-4" />
                          </Button>
                          <Button variant="ghost" size="icon" className="text-destructive hover:text-destructive hover:bg-destructive/10"
                            onClick={() => setRemoveTarget({ id: r.id, label: `${r.user_name} (${r.role})` })}>
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

      {/* Assign role */}
      <Dialog open={assignOpen} onOpenChange={setAssignOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Assign Admin Role</DialogTitle>
            <DialogDescription>Grant a user administrative access to this organisation.</DialogDescription>
          </DialogHeader>
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
                <Label>Project scope</Label>
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

      {/* Remove confirmation */}
      <AlertDialog open={!!removeTarget} onOpenChange={v => !v && setRemoveTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove {removeTarget?.label}?</AlertDialogTitle>
            <AlertDialogDescription>This will revoke this management role from the user.</AlertDialogDescription>
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
