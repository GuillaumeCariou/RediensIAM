import { useCallback, useEffect, useState } from 'react';
import { formatUriLines, parseUriLines } from '@/lib/projectForm';
import { useNavigate } from 'react-router';
import { useProjectContext } from '@/hooks/useOrgContext';
import { useAuth } from '@/context/AuthContext';
import { IamChip, IamDialog } from '@/components/iam';
import {
  getProjectInfo, updateProject, deleteProject,
  getProjectScopes, updateProjectScopes,
  adminGetProjectScopes, adminUpdateProjectScopes,
} from '@/api';
import { ApiError } from '@/auth';
import PageHeader from '@/components/layout/PageHeader';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  require_role_to_login: boolean; hydra_client_id: string;
}

/**
 * A scope name is validated server-side by a bounded regex, and the refusal names the scopes it
 * rejected. Repeating those names is worth more than restating the rule here, where a second copy
 * of the pattern would drift from the one that actually decides.
 */
function scopeErrorMessage(e: unknown, fallback: string): string {
  const body = e instanceof ApiError
    ? (e.body as { error?: string; detail?: string; invalid?: string[] } | null)
    : null;
  if (body?.error === 'invalid_scope_names' && body.invalid?.length) {
    return `Refused: ${body.invalid.join(', ')}. A scope name may hold only a-z, 0-9, "_", ":", "." and "-".`;
  }
  return body?.detail ?? body?.error ?? fallback;
}

function Toggle({ checked, onChange }: Readonly<{ checked: boolean; onChange: (v: boolean) => void }>) {
  return (
    <input type="checkbox" className="iam-switch" checked={checked} onChange={e => onChange(e.target.checked)} />
  );
}

export default function ProjectSettings() {
  const { projectId, isSystemCtx } = useProjectContext();
  const { isSuperAdmin } = useAuth();
  const navigate = useNavigate();
  // Même choix qu'à `ProjectUsers.handleAssignList` : la route d'organisation filtre sur
  // l'organisation du jeton, qu'un super-admin n'a pas, et la route système est fermée à
  // l'org_admin. Câbler une seule des deux laisse un rôle sur un 403.
  const scopeAdmin = isSystemCtx || isSuperAdmin;
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
  // Held as the textarea's own text, split only on save — the same rule the create forms use.
  const [redirectUris, setRedirectUris] = useState('');
  const [postLogoutUris, setPostLogoutUris] = useState('');

  const [builtInScopes, setBuiltInScopes] = useState<string[]>([]);
  const [customScopes, setCustomScopes] = useState<string[]>([]);
  const [newScope, setNewScope] = useState('');
  const [scopeError, setScopeError] = useState('');
  const [scopeBusy, setScopeBusy] = useState(false);

  const load = useCallback(() => {
    if (!projectId) { setLoading(false); return; }
    setLoading(true);
    setLoadError(false);
    getProjectInfo(projectId).then(p => {
      setProject(p);
      setName(p.name);
      setActive(p.active);
      setRequireRole(p.require_role_to_login);
      setRedirectUris(formatUriLines(p.redirect_uris));
      setPostLogoutUris(formatUriLines(p.post_logout_redirect_uris));
    }).catch(err => { console.error(err); setLoadError(true); }).finally(() => setLoading(false));
  }, [projectId]);

  useEffect(load, [load]);

  const loadScopes = useCallback(() => {
    if (!projectId) return;
    (scopeAdmin ? adminGetProjectScopes : getProjectScopes)(projectId)
      .then(s => { setBuiltInScopes(s.built_in ?? []); setCustomScopes(s.custom_scopes ?? []); })
      .catch(e => setScopeError(scopeErrorMessage(e, 'Could not read the scopes of this project.')));
  }, [projectId, scopeAdmin]);

  useEffect(loadScopes, [loadScopes]);

  /** The PUT replaces the whole custom list, so both add and remove send the list they want. */
  const saveScopes = async (next: string[], failure: string) => {
    setScopeBusy(true);
    setScopeError('');
    try {
      const r = await (scopeAdmin ? adminUpdateProjectScopes : updateProjectScopes)(projectId, next);
      setCustomScopes(r.custom_scopes ?? next);
      return true;
    } catch (e) {
      setScopeError(scopeErrorMessage(e, failure));
      return false;
    } finally { setScopeBusy(false); }
  };

  const handleAddScope = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    const name = newScope.trim();
    if (!name) return;
    if (customScopes.includes(name) || builtInScopes.includes(name)) {
      setScopeError(`"${name}" is already granted by this project.`);
      return;
    }
    if (await saveScopes([...customScopes, name], 'Could not add that scope.')) setNewScope('');
  };

  const handleSave = async () => {
    setSaving(true);
    setActionError('');
    try {
      await updateProject(projectId, {
        name, active, require_role_to_login: requireRole,
        redirect_uris: parseUriLines(redirectUris),
        post_logout_redirect_uris: parseUriLines(postLogoutUris),
      });
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
            <div>
              <label className="iam-label" htmlFor="proj-settings-uris">Redirect URIs (one per line)</label>
              <textarea id="proj-settings-uris" className="iam-input" style={{ minHeight: 80, resize: 'vertical' }}
                value={redirectUris} onChange={e => setRedirectUris(e.target.value)}
                placeholder="https://dashboard.example.com/callback" />
              <p className="iam-help">
                Where sign-in returns the user. Each one also becomes an allowed origin for this
                project — nothing else has to be configured for a new front to work.
              </p>
            </div>
            <div>
              <label className="iam-label" htmlFor="proj-settings-logout-uris">Post-logout redirect URIs (one per line)</label>
              <textarea id="proj-settings-logout-uris" className="iam-input" style={{ minHeight: 60, resize: 'vertical' }}
                value={postLogoutUris} onChange={e => setPostLogoutUris(e.target.value)}
                placeholder="https://dashboard.example.com/" />
              <p className="iam-help">Where sign-out may return the user. A target not listed here is refused, and the sign-out fails.</p>
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

        <div className="iam-card iam-card-pad">
          <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>OAuth2 Scopes</div>
          <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 16 }}>
            What this project's clients may ask for. A scope withdrawn here is refused at the next
            token request, so an application still asking for it stops working.
          </div>

          <div style={{ fontSize: 11, color: 'var(--fg-subtle)', marginBottom: 6 }}>Always granted</div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 16 }}>
            {builtInScopes.map(s => <IamChip key={s} mono>{s}</IamChip>)}
          </div>

          <div style={{ fontSize: 11, color: 'var(--fg-subtle)', marginBottom: 6 }}>Added by this project</div>
          {customScopes.length === 0 ? (
            <p style={{ fontSize: 12, color: 'var(--fg-muted)', fontStyle: 'italic' }}>No custom scopes.</p>
          ) : (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
              {customScopes.map(s => (
                <span key={s} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                  <IamChip tone="accent" mono>{s}</IamChip>
                  <button type="button" className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
                    style={{ color: 'var(--danger)' }} aria-label={`Remove ${s}`} disabled={scopeBusy}
                    onClick={() => saveScopes(customScopes.filter(x => x !== s), 'Could not remove that scope.')}>
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
                  </button>
                </span>
              ))}
            </div>
          )}

          <form onSubmit={handleAddScope} style={{ display: 'flex', gap: 8, marginTop: 14 }}>
            <label className="iam-label" htmlFor="proj-scope-new" style={{ position: 'absolute', width: 1, height: 1, overflow: 'hidden', clip: 'rect(0 0 0 0)' }}>New scope</label>
            <input id="proj-scope-new" className="iam-input iam-mono" style={{ maxWidth: 280 }}
              value={newScope} onChange={e => setNewScope(e.target.value)} placeholder="read:orders" />
            <button type="submit" className="iam-btn iam-btn-sm" disabled={scopeBusy}>Add scope</button>
          </form>
          {scopeError && <p style={{ fontSize: 12, color: 'var(--danger)', marginTop: 8 }}>{scopeError}</p>}
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
