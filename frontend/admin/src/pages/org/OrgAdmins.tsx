import { useEffect, useState } from 'react';
import { Plus, Trash2, Shield } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { listOrgAdmins, assignOrgAdmin, removeOrgAdmin, listProjects, listOrgListManagers, assignOrgListManager, removeOrgListManager } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface OrgRole {
  id: string;
  user_id: string;
  user_email: string;
  user_name: string;
  role: string;
  scope_id: string | null;
  scope_name: string | null;
  granted_at: string;
}
interface Project { id: string; name: string; }

export default function OrgAdmins() {
  const { orgId, isSystemCtx } = useOrgContext();
  const [admins, setAdmins] = useState<OrgRole[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [assignOpen, setAssignOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<OrgRole | null>(null);
  const [form, setForm] = useState({ user_id: '', role: 'org_admin', scope_id: '' });
  const [saving, setSaving] = useState(false);

  const load = () => {
    if (!orgId) { setLoading(false); return; }
    setLoading(true);
    const adminsPromise = isSystemCtx
      ? listOrgAdmins(orgId).then(r => r.admins ?? r ?? [])
      : listOrgListManagers().then(r => r.admins ?? r ?? []);
    Promise.all([
      adminsPromise.then(setAdmins),
      listProjects(orgId).then(r => setProjects(r.projects ?? r ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [orgId, isSystemCtx]);

  const handleAssign = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      if (isSystemCtx) {
        await assignOrgAdmin(orgId, form.user_id, form.role, form.scope_id || undefined);
      } else {
        await assignOrgListManager({ user_id: form.user_id, role: form.role, scope_id: form.scope_id || undefined });
      }
      setAssignOpen(false);
      setForm({ user_id: '', role: 'org_admin', scope_id: '' });
      load();
    } finally { setSaving(false); }
  };

  const handleRemove = async () => {
    if (!deleteTarget) return;
    if (isSystemCtx) {
      await removeOrgAdmin(orgId, deleteTarget.id);
    } else {
      await removeOrgListManager(deleteTarget.id);
    }
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Organisation Admins"
        description="Manage who administers this organisation and its projects"
        action={orgId ? <Button onClick={() => setAssignOpen(true)}><Plus className="h-4 w-4" />Assign Role</Button> : undefined}
      />
      <div className="p-6 space-y-4">
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                <TableHead>Role</TableHead>
                <TableHead>Scope</TableHead>
                <TableHead>Granted</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : admins.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                        <Shield className="h-8 w-8 mx-auto mb-2 opacity-40" />No admins assigned
                      </TableCell>
                    </TableRow>
                  )
                : admins.map(a => (
                    <TableRow key={a.id}>
                      <TableCell>
                        <p className="font-medium text-sm">{a.user_name}</p>
                        <p className="text-xs text-muted-foreground">{a.user_email}</p>
                      </TableCell>
                      <TableCell>
                        <Badge variant={a.role === 'org_admin' ? 'default' : 'secondary'}>
                          {a.role === 'org_admin' ? 'Org Admin' : 'Project Manager'}
                        </Badge>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">
                        {a.scope_name ?? (a.scope_id ? a.scope_id.slice(0, 8) + '…' : 'Entire org')}
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(a.granted_at)}</TableCell>
                      <TableCell>
                        <Button variant="ghost" size="icon" className="text-destructive hover:text-destructive" onClick={() => setDeleteTarget(a)}>
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

      <Dialog open={assignOpen} onOpenChange={setAssignOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Assign Admin Role</DialogTitle><DialogDescription>Grant a user administrative access to this organisation.</DialogDescription></DialogHeader>
          <form onSubmit={handleAssign} className="space-y-4">
            <div className="space-y-2"><Label>User ID</Label><Input value={form.user_id} onChange={e => setForm(f => ({ ...f, user_id: e.target.value }))} required placeholder="User UUID" /></div>
            <div className="space-y-2">
              <Label>Role</Label>
              <Select value={form.role} onValueChange={v => setForm(f => ({ ...f, role: v, scope_id: '' }))}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="org_admin">Org Admin</SelectItem>
                  <SelectItem value="project_manager">Project Manager</SelectItem>
                </SelectContent>
              </Select>
            </div>
            {form.role === 'project_manager' && (
              <div className="space-y-2">
                <Label>Project (scope)</Label>
                <Select value={form.scope_id} onValueChange={v => setForm(f => ({ ...f, scope_id: v }))}>
                  <SelectTrigger><SelectValue placeholder="Select project" /></SelectTrigger>
                  <SelectContent>
                    {projects.map(p => <SelectItem key={p.id} value={p.id}>{p.name}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
            )}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAssignOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Assigning…' : 'Assign'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!deleteTarget} onOpenChange={v => !v && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove admin role?</AlertDialogTitle>
            <AlertDialogDescription>This will revoke {deleteTarget?.user_name}'s {deleteTarget?.role} access.</AlertDialogDescription>
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
