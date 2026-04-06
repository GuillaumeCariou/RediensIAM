import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useProjectContext } from '@/hooks/useOrgContext';
import { Save, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { getProjectInfo, updateProject, deleteProject } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  require_role_to_login: boolean; hydra_client_id: string;
}

export default function ProjectSettings() {
  const { projectId } = useProjectContext();
  const navigate = useNavigate();
  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const [name, setName] = useState('');
  const [active, setActive] = useState(true);
  const [requireRole, setRequireRole] = useState(false);

  useEffect(() => {
    if (!projectId) { setLoading(false); return; }
    getProjectInfo(projectId).then(p => {
      setProject(p);
      setName(p.name);
      setActive(p.active);
      setRequireRole(p.require_role_to_login);
    }).catch(console.error).finally(() => setLoading(false));
  }, [projectId]);

  const handleSave = async () => {
    setSaving(true);
    try {
      await updateProject(projectId, { name, active, require_role_to_login: requireRole });
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } finally { setSaving(false); }
  };

  const handleDelete = async () => {
    setDeleting(true);
    try {
      await deleteProject(projectId);
      navigate('/org/projects');
    } finally { setDeleting(false); }
  };

  if (loading) return (
    <div>
      <PageHeader title="Settings" />
      <div className="p-6 space-y-4">{Array.from({ length: 3 }, (_, i) => `sk-${i}`).map(id => <Skeleton key={id} className="h-20 rounded-lg" />)}</div>
    </div>
  );

  let saveLabel: string;
  if (saving) saveLabel = 'Saving…';
  else if (saved) saveLabel = 'Saved!';
  else saveLabel = 'Save Changes';

  return (
    <div>
      <PageHeader
        title="Project Settings"
        description="Manage general project configuration"
        action={
          <Button onClick={handleSave} disabled={saving}>
            <Save className="h-4 w-4" />{saveLabel}
          </Button>
        }
      />
      <div className="p-6 space-y-6">

        <Card>
          <CardHeader><CardTitle className="text-base">General</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <Label>Project Name</Label>
              <Input value={name} onChange={e => setName(e.target.value)} placeholder="My App" />
            </div>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium">Active</p>
                <p className="text-xs text-muted-foreground">Inactive projects reject all login attempts</p>
              </div>
              <Switch checked={active} onCheckedChange={setActive} />
            </div>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium">Require role to login</p>
                <p className="text-xs text-muted-foreground">Users without any project role cannot sign in</p>
              </div>
              <Switch checked={requireRole} onCheckedChange={setRequireRole} />
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="text-base">Hydra OAuth2 Client</CardTitle>
            <CardDescription>Read-only — managed automatically</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="space-y-1">
              <Label className="text-xs text-muted-foreground">Client ID</Label>
              <p className="font-mono text-sm bg-muted px-3 py-2 rounded-md">{project?.hydra_client_id ?? '—'}</p>
            </div>
            <div className="space-y-1">
              <Label className="text-xs text-muted-foreground">Slug</Label>
              <p className="font-mono text-sm bg-muted px-3 py-2 rounded-md">{project?.slug ?? '—'}</p>
            </div>
          </CardContent>
        </Card>

        <Card className="border-destructive/40">
          <CardHeader>
            <CardTitle className="text-base text-destructive">Danger Zone</CardTitle>
            <CardDescription>Irreversible actions — proceed with caution</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium">Delete this project</p>
                <p className="text-xs text-muted-foreground">Removes all roles, user assignments, and the Hydra OAuth2 client</p>
              </div>
              <Button variant="destructive" size="sm" onClick={() => setDeleteOpen(true)}>
                <Trash2 className="h-4 w-4" />Delete
              </Button>
            </div>
          </CardContent>
        </Card>

      </div>

      <AlertDialog open={deleteOpen} onOpenChange={v => !v && setDeleteOpen(false)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete "{project?.name}"?</AlertDialogTitle>
            <AlertDialogDescription>
              This will permanently delete the project, all role assignments, and the Hydra OAuth2 client. This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={handleDelete}
              disabled={deleting}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              {deleting ? 'Deleting…' : 'Delete Project'}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
