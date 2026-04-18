import { useEffect, useState, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, Pencil, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { Card, CardContent } from '@/components/ui/card';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { adminGetProject, adminGetProjectStats, updateProject, adminDeleteProject } from '@/api';
import { fmtDateShort } from '@/lib/utils';
import ProjectStatsCards from '@/components/ProjectStatsCards';
import type { ProjectStats } from '@/components/ProjectStatsCards';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  hydra_client_id: string;
  assigned_user_list_id: string | null;
  created_at: string;
}

export default function SystemProjectDetail() {
  const { oid, pid } = useParams<{ oid: string; pid: string }>();
  const navigate = useNavigate();

  const [project, setProject] = useState<Project | null>(null);
  const [stats, setStats] = useState<ProjectStats | null>(null);
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
    ]).catch(console.error).finally(() => setLoading(false));
  }, [oid, pid]);

  useEffect(() => { load(); }, [load]);

  const handleRename = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!pid) return;
    await updateProject(pid, { name: renameVal });
    setRenameOpen(false);
    load();
  };

  const handleDeleteProject = async () => {
    if (!pid || !oid) return;
    await adminDeleteProject(pid);
    navigate(`/system/organisations/${oid}`);
  };

  return (
    <div className="p-6 space-y-4">
      <Button variant="ghost" size="sm" className="-ml-1" onClick={() => navigate(`/system/organisations/${oid}`)}>
        <ArrowLeft className="h-4 w-4" />Back to Organisation
      </Button>

      <ProjectStatsCards stats={stats} loading={loading} />

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
