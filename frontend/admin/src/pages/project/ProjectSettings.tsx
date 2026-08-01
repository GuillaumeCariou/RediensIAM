import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { useProjectContext } from '@/hooks/useOrgContext';
import { IamDialog } from '@/components/iam';
import { getProjectInfo, updateProject, deleteProject } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  require_role_to_login: boolean; hydra_client_id: string;
}

function Toggle({ checked, onChange }: Readonly<{ checked: boolean; onChange: (v: boolean) => void }>) {
  return (
    <button onClick={() => onChange(!checked)} style={{
      width: 36, height: 20, borderRadius: 10,
      background: checked ? 'var(--ia-accent)' : 'var(--border-strong)',
      position: 'relative', border: 'none', cursor: 'pointer', flexShrink: 0, transition: 'background 150ms',
    }}>
      <span style={{ position: 'absolute', top: 2, left: checked ? 18 : 2, width: 16, height: 16, borderRadius: '50%', background: '#fff', transition: 'left 150ms' }} />
    </button>
  );
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
      <div className="iam-page" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {Array.from({ length: 3 }, (_, i) => <div key={i} style={{ height: 80, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8 }} />)}
      </div>
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
        actions={[
          <button key="save" className="iam-btn iam-btn-primary iam-btn-sm" onClick={handleSave} disabled={saving}>
            {saveLabel}
          </button>
        ]}
      />
      <div className="iam-page" style={{ maxWidth: 640, display: 'flex', flexDirection: 'column', gap: 16 }}>

        <div className="iam-card iam-card-pad">
          <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 16 }}>General</div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <div>
              <label className="iam-label" htmlFor="proj-name">Project Name</label>
              <input id="proj-name" className="iam-input" value={name} onChange={e => setName(e.target.value)} placeholder="My App" />
            </div>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div>
                <p style={{ fontSize: 13, fontWeight: 500 }}>Active</p>
                <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Inactive projects reject all login attempts</p>
              </div>
              <Toggle checked={active} onChange={setActive} />
            </div>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div>
                <p style={{ fontSize: 13, fontWeight: 500 }}>Require role to login</p>
                <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Users without any project role cannot sign in</p>
              </div>
              <Toggle checked={requireRole} onChange={setRequireRole} />
            </div>
          </div>
        </div>

        <div className="iam-card iam-card-pad">
          <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Hydra OAuth2 Client</div>
          <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 16 }}>Read-only — managed automatically</div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <div>
              <div style={{ fontSize: 11, color: 'var(--fg-muted)', marginBottom: 4 }}>Client ID</div>
              <div className="iam-mono" style={{ fontSize: 12, background: 'var(--surface-2)', padding: '8px 12px', borderRadius: 6 }}>{project?.hydra_client_id ?? '—'}</div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--fg-muted)', marginBottom: 4 }}>Slug</div>
              <div className="iam-mono" style={{ fontSize: 12, background: 'var(--surface-2)', padding: '8px 12px', borderRadius: 6 }}>{project?.slug ?? '—'}</div>
            </div>
          </div>
        </div>

        <div className="iam-card iam-card-pad" style={{ border: '1px solid color-mix(in oklch, var(--danger) 30%, transparent)' }}>
          <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--danger)', marginBottom: 4 }}>Danger Zone</div>
          <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 16 }}>Irreversible actions — proceed with caution</div>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <div>
              <p style={{ fontSize: 13, fontWeight: 500 }}>Delete this project</p>
              <p style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Removes all roles, user assignments, and the Hydra OAuth2 client</p>
            </div>
            <button className="iam-btn iam-btn-danger iam-btn-sm" onClick={() => setDeleteOpen(true)}>
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>
              Delete
            </button>
          </div>
        </div>

      </div>

      <IamDialog
        open={deleteOpen}
        onClose={() => setDeleteOpen(false)}
        title={`Delete "${project?.name}"?`}
        desc="This will permanently delete the project, all role assignments, and the Hydra OAuth2 client. This cannot be undone."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setDeleteOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-danger" onClick={handleDelete} disabled={deleting}>
              {deleting ? 'Deleting…' : 'Delete Project'}
            </button>
          </>
        }
      >
        <div />
      </IamDialog>
    </div>
  );
}
