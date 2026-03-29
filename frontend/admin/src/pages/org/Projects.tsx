import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Plus, MoreHorizontal, Trash2, ArrowRight, CheckCircle, XCircle, Link2, Link2Off } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Switch } from '@/components/ui/switch';
import { Skeleton } from '@/components/ui/skeleton';
import { listProjects, createProject, deleteProject, listUserLists, assignUserList, unassignUserList } from '@/api';
import { ApiError } from '@/auth';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  assigned_user_list_id: string | null; assigned_user_list_name: string | null;
  require_role_to_login: boolean; created_at: string;
}
interface UserList { id: string; name: string; }

export default function Projects() {
  const navigate = useNavigate();
  const { orgId, projectUrl } = useOrgContext();
  const [projects, setProjects] = useState<Project[]>([]);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [assignOpen, setAssignOpen] = useState<Project | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<Project | null>(null);
  const [selectedList, setSelectedList] = useState('');
  const [form, setForm] = useState({ name: '', slug: '', redirect_uris: '', require_role_to_login: false });
  const [saving, setSaving] = useState(false);
  const [createError, setCreateError] = useState('');

  const load = () => {
    if (!orgId) { setLoading(false); return; }
    setLoading(true);
    Promise.all([
      listProjects(orgId).then(r => setProjects(r.projects ?? r ?? [])),
      listUserLists(orgId).then(r => setUserLists(r.user_lists ?? r ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [orgId]);

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setCreateError('');
    try {
      await createProject({
        org_id: orgId,
        name: form.name,
        slug: form.slug,
        require_role_to_login: form.require_role_to_login,
        redirect_uris: form.redirect_uris.split('\n').map(s => s.trim()).filter(Boolean),
      });
      setCreateOpen(false);
      setForm({ name: '', slug: '', redirect_uris: '', require_role_to_login: false });
      load();
    } catch (err) {
      const body = err instanceof ApiError ? (err.body as Record<string, string> | null) : null;
      setCreateError(body?.detail ?? body?.error ?? 'Failed to create project.');
    } finally { setSaving(false); }
  };

  const handleAssign = async () => {
    if (!assignOpen) return;
    setSaving(true);
    try {
      if (selectedList === '__none__') {
        await unassignUserList(assignOpen.id);
      } else {
        await assignUserList(assignOpen.id, selectedList);
      }
      setAssignOpen(null);
      load();
    } finally { setSaving(false); }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteProject(deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Projects"
        description="OAuth2-authenticated applications within this organisation"
        action={orgId ? <Button onClick={() => setCreateOpen(true)}><Plus className="h-4 w-4" />New Project</Button> : undefined}
      />
      <div className="p-6 space-y-4">
        {!orgId && <p className="text-sm text-muted-foreground">Select an organisation first.</p>}
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Slug</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>User List</TableHead>
                <TableHead>Role Required</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 4 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: 7 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : projects.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={7} className="text-center text-muted-foreground py-12">No projects yet</TableCell>
                    </TableRow>
                  )
                : projects.map(p => (
                    <TableRow key={p.id} className="cursor-pointer hover:bg-muted/50" onClick={() => navigate(projectUrl(p.id))}>
                      <TableCell className="font-medium">{p.name}</TableCell>
                      <TableCell className="font-mono text-sm text-muted-foreground">{p.slug}</TableCell>
                      <TableCell>
                        {p.active
                          ? <Badge variant="success"><CheckCircle className="h-3 w-3 mr-1" />Active</Badge>
                          : <Badge variant="secondary"><XCircle className="h-3 w-3 mr-1" />Inactive</Badge>
                        }
                      </TableCell>
                      <TableCell className="text-sm">
                        {p.assigned_user_list_name
                          ? <Badge variant="default">{p.assigned_user_list_name}</Badge>
                          : <span className="text-muted-foreground">None</span>
                        }
                      </TableCell>
                      <TableCell>
                        {p.require_role_to_login
                          ? <Badge variant="warning">Required</Badge>
                          : <Badge variant="secondary">Optional</Badge>
                        }
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDateShort(p.created_at)}</TableCell>
                      <TableCell onClick={e => e.stopPropagation()}>
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent>
                            <DropdownMenuItem asChild>
                              <Link to={projectUrl(p.id)}>
                                <ArrowRight className="h-4 w-4" />Open Project
                              </Link>
                            </DropdownMenuItem>
                            <DropdownMenuItem onClick={() => { setAssignOpen(p); setSelectedList(p.assigned_user_list_id ?? ''); }}>
                              <Link2 className="h-4 w-4" />Assign User List
                            </DropdownMenuItem>
                            {p.assigned_user_list_id && (
                              <DropdownMenuItem onClick={() => { setAssignOpen(p); setSelectedList('__none__'); }}>
                                <Link2Off className="h-4 w-4" />Unassign User List
                              </DropdownMenuItem>
                            )}
                            <DropdownMenuSeparator />
                            <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setDeleteTarget(p)}>
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
      </div>

      {/* Create Project */}
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent className="max-w-xl">
          <DialogHeader><DialogTitle>Create Project</DialogTitle><DialogDescription>A new OAuth2 client will be automatically registered in Hydra.</DialogDescription></DialogHeader>
          <form onSubmit={handleCreate} className="space-y-4">
            {createError && <p className="text-sm text-destructive">{createError}</p>}
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2"><Label>Name</Label><Input value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} required placeholder="My Dashboard" /></div>
              <div className="space-y-2">
                <Label>Slug</Label>
                <Input value={form.slug} onChange={e => setForm(f => ({ ...f, slug: e.target.value.toLowerCase().replace(/\s+/g, '-') }))} required placeholder="my-dashboard" pattern="[a-z0-9]+(-[a-z0-9]+)*" />
              </div>
            </div>
            <div className="space-y-2">
              <Label>Redirect URIs (one per line)</Label>
              <textarea
                className="flex min-h-[80px] w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-sm placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                value={form.redirect_uris}
                onChange={e => setForm(f => ({ ...f, redirect_uris: e.target.value }))}
                placeholder="https://dashboard.example.com/callback"
              />
            </div>
            <div className="flex items-center gap-3">
              <Switch
                checked={form.require_role_to_login}
                onCheckedChange={v => setForm(f => ({ ...f, require_role_to_login: v }))}
                id="require-role"
              />
              <Label htmlFor="require-role">Require a role to log in</Label>
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Creating…' : 'Create Project'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Assign User List */}
      <Dialog open={!!assignOpen} onOpenChange={v => !v && setAssignOpen(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Assign User List</DialogTitle>
            <DialogDescription>Choose which user list will authenticate into {assignOpen?.name}.</DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-2">
            <Select value={selectedList} onValueChange={setSelectedList}>
              <SelectTrigger><SelectValue placeholder="Select a user list" /></SelectTrigger>
              <SelectContent>
                <SelectItem value="__none__">— No user list (unassign)</SelectItem>
                {userLists.map(l => <SelectItem key={l.id} value={l.id}>{l.name}</SelectItem>)}
              </SelectContent>
            </Select>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setAssignOpen(null)}>Cancel</Button>
            <Button onClick={handleAssign} disabled={saving}>{saving ? 'Saving…' : 'Save'}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!deleteTarget} onOpenChange={v => !v && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete {deleteTarget?.name}?</AlertDialogTitle>
            <AlertDialogDescription>The Hydra OAuth2 client for this project will also be deleted. This action is irreversible.</AlertDialogDescription>
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
