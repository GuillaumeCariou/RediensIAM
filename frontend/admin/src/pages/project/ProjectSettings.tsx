import { useCallback, useEffect, useState } from 'react';
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
    <input type="checkbox" className="iam-switch" checked={checked} onChange={e => onChange(e.target.checked)} />
  );
}

export default function ProjectSettings() {
  const { projectId } = useProjectContext();
  const navigate = useNavigate();
  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);
  // A save or a delete that the server refuses used to leave `try`/`finally` with no `catch`: the
  // button simply re-enabled itself, the operator read that as success, and the rejection escaped
  // as an unhandled promise. Same treatment as OrgSettings — say what happened, and say that
  // nothing changed.
  const [actionError, setActionError] = useState('');
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const [name, setName] = useState('');
  const [active, setActive] = useState(true);
  const [requireRole, setRequireRole] = useState(false);

  const load = useCallback(() => {
    if (!projectId) { setLoading(false); return; }
    setLoading(true);
    setLoadError(false);
    getProjectInfo(projectId).then(p => {
      setProject(p);
      setName(p.name);
      setActive(p.active);
      setRequireRole(p.require_role_to_login);
    }).catch(err => { console.error(err); setLoadError(true); }).finally(() => setLoading(false));
  }, [projectId]);

  useEffect(load, [load]);

  const handleSave = async () => {
    setSaving(true);
    setActionError('');
    try {
      await updateProject(projectId, { name, active, require_role_to_login: requireRole });
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch {
      setActionError('Could not save. Nothing was changed.');
    } finally { setSaving(false); }
  };

  const handleDelete = async () => {
    setDeleting(true);
    setActionError('');
    try {
      await deleteProject(projectId);
      navigate('/org/projects');
    } catch {
      setActionError('Could not delete this project. It still exists.');
    } finally { setDeleting(false); }
  };

  // A load that failed leaves every field at its useState default. Rendering the form anyway means
  // the next Save PATCHes those defaults over the tenant's real configuration — MFA off, allowlist
  // empty — and reports success. Refusing to render is the whole fix.
  if (loadError) return (
    <div>
      <PageHeader title="Settings" />
      <div className="iam-page">
        <div className="iam-empty">
          <p>This configuration could not be loaded, so it is not safe to edit.</p>
          {/* Re-reads the project rather than reloading the page: the operator keeps their place,
              and a transient 500 costs one request instead of a full boot. */}
          <button type="button" className="iam-btn" onClick={load}>Retry</button>
        </div>
      </div>
    </div>
  );

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
        {actionError && !deleteOpen && (
          <div className="iam-alert iam-alert-danger">{actionError}</div>
        )}

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
        {/* The delete error belongs INSIDE the dialog: the dialog stays open on failure and covers
            the page, so an alert in the page body would be reported to nobody. */}
        {deleteOpen && actionError
          ? <div className="iam-alert iam-alert-danger">{actionError}</div>
          : <div />}
      </IamDialog>
    </div>
  );
}
