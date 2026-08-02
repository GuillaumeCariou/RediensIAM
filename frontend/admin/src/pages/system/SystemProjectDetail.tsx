import { useEffect, useState, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router';
import { ArrowLeft, Pencil, Trash2 } from 'lucide-react';
import { adminGetProject, adminGetProjectStats, updateProject, adminDeleteProject } from '@/api';
import { fmtDateShort } from '@/lib/utils';
import ProjectStatsCards from '@/components/ProjectStatsCards';
import type { ProjectStats } from '@/components/ProjectStatsCards';
import { IamChip, IamDialog } from '@/components/iam';

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

  /** The fetch alone. Sets no state synchronously, so an effect may call it directly. */
  const fetchProject = useCallback(() => {
    if (!oid || !pid) return;
    Promise.all([
      adminGetProject(pid).then(setProject),
      adminGetProjectStats(pid).then(setStats).catch(() => null),
    ]).catch(console.error).finally(() => setLoading(false));
  }, [oid, pid]);

  /** What a user-triggered refresh calls: the spinner comes back, then the fetch. */
  const load = useCallback(() => { setLoading(true); fetchProject(); }, [fetchProject]);

  useEffect(() => { fetchProject(); }, [fetchProject]);

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
      <button className="iam-btn iam-btn-ghost iam-btn-sm -ml-1" onClick={() => navigate(`/system/organisations/${oid}`)}>
        <ArrowLeft className="h-4 w-4" />Back to Organisation
      </button>

      <ProjectStatsCards stats={stats} loading={loading} />

      {/* ── Project info ── */}
      <div className="iam-card">
        <div className="iam-card-pad pt-6 space-y-4">
          <div className="flex items-start justify-between gap-4">
            {loading
              ? <div className="space-y-2"><div className="iam-skeleton h-6 w-48" /><div className="iam-skeleton h-4 w-80" /></div>
              : <div>
                  <h1 className="text-xl font-bold">{project?.name}</h1>
                  <p className="text-sm text-muted-foreground">
                    /{project?.slug} · <span className="font-mono text-xs">{project?.hydra_client_id}</span> · Created {fmtDateShort(project?.created_at ?? null)}
                  </p>
                </div>
            }
            {!loading && project && (
              <div className="flex items-center gap-2 shrink-0">
                <IamChip tone={project.active ? 'success' : 'default'}>
                  {project.active ? 'Active' : 'Inactive'}
                </IamChip>
                <button className="iam-btn iam-btn-secondary iam-btn-sm text-destructive border-[var(--danger)] hover:bg-[var(--danger-soft)]" onClick={() => setDeleteProjectOpen(true)}>
                  <Trash2 className="h-4 w-4" />Delete
                </button>
                <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={() => { setRenameVal(project.name); setRenameOpen(true); }}>
                  <Pencil className="h-4 w-4" />Rename
                </button>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* ── Dialogs ── */}
      <IamDialog open={renameOpen} onClose={() => setRenameOpen(false)}
      title="Rename Project"
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setRenameOpen(false)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="systemprojectdetail-form">Save</button></>}
    >
<form id="systemprojectdetail-form" onSubmit={handleRename} className="space-y-4">
            <div className="space-y-2">
              <label className="iam-label" htmlFor="system-project-rename">Name</label>
              <input className="iam-input" id="system-project-rename" value={renameVal} onChange={e => setRenameVal(e.target.value)} required />
            </div>
            
          </form>
    </IamDialog>

      <IamDialog open={deleteProjectOpen} onClose={() => setDeleteProjectOpen(false)}
      title={<>Delete project "{project?.name}"?</>}
      desc="The OAuth2 client for this project will also be deleted. All role assignments will be lost. This cannot be undone."
      footer={<><button type="button" onClick={() => setDeleteProjectOpen(false)} className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-danger" onClick={handleDeleteProject}>Delete</button></>}
    >

    </IamDialog>
    </div>
  );
}
