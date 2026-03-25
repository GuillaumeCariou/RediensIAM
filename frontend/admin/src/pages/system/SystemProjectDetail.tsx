import { useEffect, useState, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, Pencil, Trash2, Users, UserCheck, Shield } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import {
  adminGetProject, adminGetProjectStats, adminUpdateProject,
  adminAssignUserList, adminUnassignUserList,
  listUserLists, adminDeleteProject,
} from '@/api';
import { fmtDateShort } from '@/lib/utils';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  hydra_client_id: string;
  assigned_user_list_id: string | null;
  created_at: string;
}
interface UserList { id: string; name: string; immovable: boolean; }
interface Stats {
  total_users: number; active_users: number;
  users_by_role: { role_id: string; role_name: string; count: number }[];
}

export default function SystemProjectDetail() {
  const { oid, pid } = useParams<{ oid: string; pid: string }>();
  const navigate = useNavigate();

  const [project, setProject] = useState<Project | null>(null);
  const [stats, setStats] = useState<Stats | null>(null);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);

  const [renameOpen, setRenameOpen] = useState(false);
  const [renameVal, setRenameVal] = useState('');
  const [deleteProjectOpen, setDeleteProjectOpen] = useState(false);

  const load = useCallback(() => {
    if (!oid || !pid) return;
    setLoading(true);
    Promise.all([
      adminGetProject(pid).then(setProject),
      adminGetProjectStats(pid).then(setStats).catch(() => null),
      listUserLists(oid).then(r => setUserLists(r.user_lists ?? r ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  }, [oid, pid]);

  useEffect(() => { load(); }, [load]);

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

  const handleDeleteProject = async () => {
    if (!pid || !oid) return;
    await adminDeleteProject(pid);
    navigate(`/system/organisations/${oid}`);
  };

  const movableLists = userLists.filter(ul => !ul.immovable);

  return (
    <div className="p-6 space-y-4">
      <Button variant="ghost" size="sm" className="-ml-1" onClick={() => navigate(`/system/organisations/${oid}`)}>
        <ArrowLeft className="h-4 w-4" />Back to Organisation
      </Button>

      {/* ── Stats ── */}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm text-muted-foreground flex items-center gap-2">
              <Users className="h-4 w-4" />Total Users
            </CardTitle>
          </CardHeader>
          <CardContent>
            {loading ? <Skeleton className="h-8 w-16" /> : <p className="text-3xl font-bold">{stats?.total_users ?? '—'}</p>}
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm text-muted-foreground flex items-center gap-2">
              <UserCheck className="h-4 w-4" />Active Users
            </CardTitle>
          </CardHeader>
          <CardContent>
            {loading ? <Skeleton className="h-8 w-16" /> : (
              <>
                <p className="text-3xl font-bold">{stats?.active_users ?? '—'}</p>
                {stats && stats.total_users > 0 && (
                  <p className="text-xs text-muted-foreground mt-1">
                    {Math.round((stats.active_users / stats.total_users) * 100)}% active
                  </p>
                )}
              </>
            )}
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm text-muted-foreground flex items-center gap-2">
              <Shield className="h-4 w-4" />Roles
            </CardTitle>
          </CardHeader>
          <CardContent>
            {loading ? <Skeleton className="h-8 w-16" /> : <p className="text-3xl font-bold">{stats?.users_by_role.length ?? '—'}</p>}
          </CardContent>
        </Card>
      </div>

      {stats && stats.users_by_role.length > 0 && (
        <Card>
          <CardHeader><CardTitle className="text-base">Users by Role</CardTitle></CardHeader>
          <CardContent className="space-y-2">
            {[...stats.users_by_role].sort((a, b) => b.count - a.count).map(r => (
              <div key={r.role_id} className="flex items-center justify-between text-sm">
                <span className="font-mono text-muted-foreground">{r.role_name}</span>
                <div className="flex items-center gap-3">
                  <div className="w-32 h-1.5 rounded-full bg-muted overflow-hidden">
                    <div
                      className="h-full bg-primary rounded-full"
                      style={{ width: stats.total_users > 0 ? `${(r.count / stats.total_users) * 100}%` : '0%' }}
                    />
                  </div>
                  <span className="font-medium w-6 text-right">{r.count}</span>
                </div>
              </div>
            ))}
          </CardContent>
        </Card>
      )}

      {/* ── Project info ── */}
      <Card>
        <CardContent className="pt-6 space-y-4">
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
                <Button variant="outline" size="sm" className="text-destructive border-destructive/40 hover:bg-destructive/10" onClick={() => setDeleteProjectOpen(true)}>
                  <Trash2 className="h-4 w-4" />Delete
                </Button>
                <Button variant="outline" size="sm" onClick={() => { setRenameVal(project.name); setRenameOpen(true); }}>
                  <Pencil className="h-4 w-4" />Rename
                </Button>
              </div>
            )}
          </div>
        </CardContent>
      </Card>

      {/* ── Settings ── */}
      <Card>
        <CardHeader><CardTitle className="text-base">Settings</CardTitle></CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-2">
            <Label className="text-sm">Assigned User List</Label>
            {loading
              ? <Skeleton className="h-10 w-72" />
              : <Select value={project?.assigned_user_list_id ?? '__none__'} onValueChange={handleAssignList}>
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
            }
            {!loading && !project?.assigned_user_list_id && (
              <p className="text-xs text-amber-500">No user list assigned — users cannot log in to this project.</p>
            )}
          </div>
        </CardContent>
      </Card>

      {/* ── Dialogs ── */}
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

      <AlertDialog open={deleteProjectOpen} onOpenChange={setDeleteProjectOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete project "{project?.name}"?</AlertDialogTitle>
            <AlertDialogDescription>
              The OAuth2 client for this project will also be deleted. All role assignments will be lost. This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleDeleteProject} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
