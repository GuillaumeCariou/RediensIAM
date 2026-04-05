import { useEffect, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { Plus, Trash2, Shield } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { listRoles, createRole, deleteRole, getProjectInfo, updateProject } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface Role {
  id: string; name: string; description: string | null; rank: number; created_at: string;
}

export default function ProjectRoles() {
  const { projectId } = useProjectContext();
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<Role | null>(null);
  const [form, setForm] = useState({ name: '', description: '', rank: '100' });
  const [saving, setSaving] = useState(false);
  const [defaultRoleId, setDefaultRoleId] = useState<string | null>(null);
  const [savingDefault, setSavingDefault] = useState(false);
  const [defaultRoleError, setDefaultRoleError] = useState('');

  const load = () => {
    if (!projectId) { setLoading(false); return; }
    setLoading(true);
    Promise.all([
      listRoles(projectId).then(r => setRoles(r.roles ?? r ?? [])),
      getProjectInfo(projectId).then(p => setDefaultRoleId(p.default_role_id ?? null)),
    ]).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [projectId]);

  const handleDefaultRole = async (value: string) => {
    setSavingDefault(true);
    setDefaultRoleError('');
    try {
      if (value === '__none__') {
        await updateProject(projectId, { clear_default_role: true });
        setDefaultRoleId(null);
      } else {
        await updateProject(projectId, { default_role_id: value });
        setDefaultRoleId(value);
      }
    } catch { setDefaultRoleError('Failed to save default role.'); }
    finally { setSavingDefault(false); }
  };

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true);
    try {
      await createRole(projectId, { name: form.name, description: form.description || undefined, rank: parseInt(form.rank) });
      setCreateOpen(false);
      setForm({ name: '', description: '', rank: '100' });
      load();
    } finally { setSaving(false); }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteRole(projectId, deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Role Definitions"
        description="Custom roles for this project — assigned to users to control access"
        action={projectId ? <Button onClick={() => setCreateOpen(true)}><Plus className="h-4 w-4" />New Role</Button> : undefined}
      />
      <div className="p-6 space-y-4">
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Default Role</CardTitle>
            <CardDescription>Automatically assigned to new users on registration and social login.</CardDescription>
          </CardHeader>
          <CardContent>
            {loading ? <Skeleton className="h-9 w-48" /> : (
              <div className="space-y-1">
                <Select value={defaultRoleId ?? '__none__'} onValueChange={handleDefaultRole} disabled={savingDefault}>
                  <SelectTrigger className="w-64 bg-background">
                    <SelectValue placeholder="No default role" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__none__">No default role</SelectItem>
                    {[...roles].sort((a, b) => a.rank - b.rank).map(r => (
                      <SelectItem key={r.id} value={r.id}>{r.name} <span className="text-muted-foreground ml-1 text-xs">(rank {r.rank})</span></SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {defaultRoleError && <p className="text-xs text-destructive">{defaultRoleError}</p>}
              </div>
            )}
          </CardContent>
        </Card>

        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Description</TableHead>
                <TableHead>Rank</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 4 }).map((_, i) => <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>)
                : roles.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                        <Shield className="h-8 w-8 mx-auto mb-2 opacity-40" />No roles defined yet
                      </TableCell>
                    </TableRow>
                  )
                : [...roles].sort((a, b) => a.rank - b.rank).map(role => (
                    <TableRow key={role.id}>
                      <TableCell className="font-medium font-mono">{role.name}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">{role.description ?? '—'}</TableCell>
                      <TableCell>
                        <span className="text-sm font-mono bg-muted px-2 py-0.5 rounded">{role.rank}</span>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(role.created_at)}</TableCell>
                      <TableCell>
                        <Button variant="ghost" size="icon" className="text-destructive hover:text-destructive" onClick={() => setDeleteTarget(role)}>
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>
        {roles.length > 0 && (
          <p className="text-xs text-muted-foreground">Rank: lower number = higher privilege. Used for project_manager assignment restrictions.</p>
        )}
      </div>

      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create Role</DialogTitle>
            <DialogDescription>Define a new role that can be assigned to users in this project.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleCreate} className="space-y-4">
            <div className="space-y-2">
              <Label>Name</Label>
              <Input value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value.toLowerCase().replaceAll(/\s+/g, '_') }))} required placeholder="admin, viewer, editor…" />
            </div>
            <div className="space-y-2">
              <Label>Description (optional)</Label>
              <Input value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} placeholder="What this role can do" />
            </div>
            <div className="space-y-2">
              <Label>Rank</Label>
              <Input type="number" min="1" value={form.rank} onChange={e => setForm(f => ({ ...f, rank: e.target.value }))} />
              <p className="text-xs text-muted-foreground">Lower = higher privilege. e.g. admin=1, editor=50, viewer=100</p>
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Creating…' : 'Create Role'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!deleteTarget} onOpenChange={v => !v && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete role "{deleteTarget?.name}"?</AlertDialogTitle>
            <AlertDialogDescription>Users currently holding this role will lose it. This action cannot be undone.</AlertDialogDescription>
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
