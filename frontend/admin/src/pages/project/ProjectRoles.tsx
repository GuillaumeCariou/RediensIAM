import { useEffect, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { IamChip, IamDialog } from '@/components/iam';
import {
  listRoles, createRole, updateRole, deleteRole,
  adminListRoles, adminCreateRole, adminDeleteRole,
  updateProject,
} from '@/api';
import { ApiError } from '@/auth';
import PageHeader from '@/components/layout/PageHeader';

interface Role {
  id: string; name: string; description: string | null; rank: number;
  is_default?: boolean; holders?: number;
}

/**
 * Ce que l'API refuse à la création, dit en clair.
 *
 * Le formulaire minuscule le nom et remplace les espaces par des soulignés, si bien que « Super
 * Admin » arrive en `super_admin` — réservé, donc refusé. Le refus était juste ; c'est de ne
 * jamais l'écrire nulle part que l'opérateur ne pouvait pas s'en sortir : la promesse partait en
 * rejet non attrapé, la boîte de dialogue restait ouverte, inchangée, et le 400 n'existait que
 * dans la console du navigateur.
 */
const DEFAULT_ERRORS: Record<string, string> = {
  invalid_default_role: 'One of those roles no longer belongs to this project. Reload and try again.',
};

const CREATE_ERRORS: Record<string, string> = {
  role_name_required:          'Give the role a name.',
  role_name_too_long:          'That name is too long — 64 characters at most.',
  role_name_invalid_character: 'A role name cannot contain "/".',
  role_name_reserved:          'That name is reserved for management roles (super_admin, org_admin, project_admin). Pick another.',
  role_name_exists:            'This project already has a role with that name.',
};

function apiErrorMessage(e: unknown, table: Record<string, string>, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return (body?.error && table[body.error]) ?? body?.detail ?? body?.error ?? fallback;
}

export default function ProjectRoles() {
  const { projectId, isSystemCtx } = useProjectContext();
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<Role | null>(null);
  const [form, setForm] = useState({ name: '', description: '', rank: '100' });
  const [saving, setSaving] = useState(false);
  const [savingDefault, setSavingDefault] = useState(false);
  const [defaultRoleError, setDefaultRoleError] = useState('');
  const [createError, setCreateError] = useState('');
  const [editTarget, setEditTarget] = useState<Role | null>(null);
  const [editForm, setEditForm] = useState({ description: '', rank: '100' });
  const [editError, setEditError] = useState('');
  const [editSaving, setEditSaving] = useState(false);
  const [deleteError, setDeleteError] = useState('');

  const load = () => {
    if (!projectId) { setLoading(false); return; }
    setLoading(true);
    (isSystemCtx ? adminListRoles : listRoles)(projectId)
      .then(r => setRoles(r.roles ?? r ?? []))
      .catch(console.error)
      .finally(() => setLoading(false));
  };
  useEffect(load, [projectId, isSystemCtx]);

  const byRank = [...roles].sort((a, b) => a.rank - b.rank);
  const defaults = byRank.filter(r => r.is_default);

  /**
   * Le PATCH énonce l'ensemble entier, jamais un delta : cocher et décocher passent par le même
   * appel, et la case reflète l'état voulu tout de suite. Un refus la remet où elle était — la
   * laisser cochée sur un 400 afficherait un rôle accordé qui ne l'est pas.
   */
  const saveDefaults = async (ids: string[]) => {
    const before = roles;
    setSavingDefault(true);
    setDefaultRoleError('');
    setRoles(rs => rs.map(r => ({ ...r, is_default: ids.includes(r.id) })));
    try {
      await updateProject(projectId, { default_role_ids: ids });
    } catch (e) {
      setRoles(before);
      setDefaultRoleError(apiErrorMessage(e, DEFAULT_ERRORS, 'Failed to save the default roles.'));
    } finally { setSavingDefault(false); }
  };

  const toggleDefault = (role: Role) => saveDefaults(
    role.is_default
      ? defaults.filter(r => r.id !== role.id).map(r => r.id)
      : [...defaults.map(r => r.id), role.id]);

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true);
    setCreateError('');
    try {
      await (isSystemCtx ? adminCreateRole : createRole)(projectId, { name: form.name, description: form.description || undefined, rank: Number.parseInt(form.rank, 10) });
      setCreateOpen(false);
      setForm({ name: '', description: '', rank: '100' });
      load();
    } catch (e) {
      setCreateError(apiErrorMessage(e, CREATE_ERRORS, 'Failed to create the role.'));
    } finally { setSaving(false); }
  };

  const openEdit = (role: Role) => {
    setEditTarget(role);
    setEditForm({ description: role.description ?? '', rank: String(role.rank) });
    setEditError('');
  };

  // Portée système comprise : `/project/roles/{id}?project_id=` est la seule route qui modifie un
  // rôle, et son `?project_id=` est honoré dès le niveau OrgAdmin.
  const handleEdit = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!editTarget) return;
    setEditSaving(true);
    setEditError('');
    try {
      await updateRole(projectId, editTarget.id, {
        description: editForm.description,
        rank: Number.parseInt(editForm.rank, 10),
      });
      setEditTarget(null);
      load();
    } catch (e) {
      setEditError(apiErrorMessage(e, {}, 'Failed to save the role.'));
    } finally { setEditSaving(false); }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    setDeleteError('');
    try {
      await (isSystemCtx ? adminDeleteRole : deleteRole)(projectId, deleteTarget.id);
      setDeleteTarget(null);
      load();
    } catch (e) {
      setDeleteError(apiErrorMessage(e, {}, 'Failed to delete the role.'));
    }
  };

  return (
    <div>
      <PageHeader
        title="Roles"
        description="Names with a rank. They are emitted into the access token qualified by this project, so two tenants' admin are never the same string."
        actions={projectId ? [
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateOpen(true)}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            New Role
          </button>
        ] : []}
      />
      <div className="iam-page" style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <div className="iam-card">
          <div style={{ padding: '14px 18px', borderBottom: '1px solid var(--border)' }}>
            <div style={{ fontSize: 13, fontWeight: 600 }}>Definitions</div>
            <div style={{ fontSize: 11.5, color: 'var(--fg-muted)', marginTop: 2 }}>
              Tick <strong>Default</strong> on as many roles as you want — every ticked role is granted to a new account. Untick them all for no default at all.
            </div>
          </div>
          <table className="iam-tbl">
            <thead>
              <tr>
                <th style={{ width: 70 }}>Default</th><th>Name</th><th>Description</th>
                <th style={{ width: 80 }}>Rank</th><th style={{ width: 90 }}>Holders</th>
                <th>In the token</th><th style={{ width: 72 }}></th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return Array.from({ length: 4 }, (_, i) => (
                  <tr key={i}>{Array.from({ length: 7 }, (_, j) => <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>)}</tr>
                ));
                if (roles.length === 0) return (
                  <tr><td colSpan={7}>
                    <div className="iam-empty">
                      <div className="iam-empty-title">No roles defined yet</div>
                      <div className="iam-empty-desc">Create roles to control project access.</div>
                    </div>
                  </td></tr>
                );
                return byRank.map(role => (
                  <tr key={role.id}>
                    <td>
                      <input type="checkbox" className="iam-switch" aria-label={`Grant ${role.name} on sign-up`}
                        checked={!!role.is_default} disabled={savingDefault}
                        onChange={() => toggleDefault(role)} />
                    </td>
                    <td style={{ fontWeight: 500 }}><span className="iam-mono">{role.name}</span></td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{role.description ?? '—'}</td>
                    <td><span className="iam-mono" style={{ fontSize: 11, background: 'var(--surface-2)', padding: '2px 6px', borderRadius: 4 }}>{role.rank}</span></td>
                    <td>{role.holders ?? 0}</td>
                    {/* Le nom nu ne veut rien dire d'un locataire à l'autre : c'est la forme
                        qualifiée qu'un serveur de ressources compare (Roles.ProjectRoleClaim). */}
                    <td><span className="iam-mono" style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{projectId}/{role.name}</span></td>
                    <td style={{ display: 'flex', gap: 2 }}>
                      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" aria-label={`Delete role ${role.name}`} style={{ color: 'var(--danger)' }} onClick={() => setDeleteTarget(role)}>
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>
                      </button>
                      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" aria-label={`Edit role ${role.name}`} onClick={() => openEdit(role)}>
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>
                      </button>
                    </td>
                  </tr>
                ));
              })()}
            </tbody>
          </table>
          {!loading && roles.length > 0 && (
            <div style={{ padding: '12px 18px', borderTop: '1px solid var(--border)', display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
              <span style={{ fontSize: 12.5, color: 'var(--fg-muted)' }}>Granted on sign-up:</span>
              {defaults.length === 0
                ? <span style={{ fontSize: 12.5, color: 'var(--fg-muted)' }}>nothing — a new account starts with no role.</span>
                : defaults.map(r => <IamChip key={r.id} tone="accent">{r.name}</IamChip>)}
              {defaults.length > 0 && (
                <button className="iam-btn iam-btn-ghost iam-btn-sm" disabled={savingDefault} onClick={() => saveDefaults([])}>
                  Clear all defaults
                </button>
              )}
              {defaultRoleError && <p style={{ fontSize: 12, color: 'var(--danger)', width: '100%', margin: 0 }}>{defaultRoleError}</p>}
            </div>
          )}
        </div>
        {roles.length > 0 && (
          <p style={{ fontSize: 12, color: 'var(--fg-muted)' }}>Rank: lower number = higher privilege. Used for project_manager assignment restrictions.</p>
        )}
      </div>

      <IamDialog
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        title="Create Role"
        desc="Define a new role that can be assigned to users in this project."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setCreateOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="create-role-form" type="submit" disabled={saving}>
              {saving ? 'Creating…' : 'Create Role'}
            </button>
          </>
        }
      >
        <form id="create-role-form" onSubmit={handleCreate} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div>
            <label className="iam-label" htmlFor="role-name">Name</label>
            <input id="role-name" className="iam-input iam-mono" value={form.name}
              onChange={e => setForm(f => ({ ...f, name: e.target.value.toLowerCase().replaceAll(/\s+/g, '_') }))}
              required placeholder="admin, viewer, editor…" />
          </div>
          <div>
            <label className="iam-label" htmlFor="role-description">Description (optional)</label>
            <input id="role-description" className="iam-input" value={form.description}
              onChange={e => setForm(f => ({ ...f, description: e.target.value }))}
              placeholder="What this role can do" />
          </div>
          <div>
            <label className="iam-label" htmlFor="role-rank">Rank</label>
            <input id="role-rank" className="iam-input" type="number" min="1" value={form.rank}
              onChange={e => setForm(f => ({ ...f, rank: e.target.value }))} />
            <p style={{ fontSize: 11, color: 'var(--fg-muted)', marginTop: 4 }}>Lower = higher privilege. e.g. admin=1, editor=50, viewer=100</p>
          </div>
          {createError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{createError}</p>}
        </form>
      </IamDialog>

      <IamDialog
        open={!!editTarget}
        onClose={() => setEditTarget(null)}
        title={`Edit role "${editTarget?.name}"`}
        desc="The name is fixed: it is what every assignment of this role is written against."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setEditTarget(null)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="edit-role-form" type="submit" disabled={editSaving}>
              {editSaving ? 'Saving…' : 'Save'}
            </button>
          </>
        }
      >
        <form id="edit-role-form" onSubmit={handleEdit} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div>
            <label className="iam-label" htmlFor="edit-role-description">Description</label>
            <input id="edit-role-description" className="iam-input" value={editForm.description}
              onChange={e => setEditForm(f => ({ ...f, description: e.target.value }))}
              placeholder="What this role can do" />
          </div>
          <div>
            <label className="iam-label" htmlFor="edit-role-rank">Rank</label>
            <input id="edit-role-rank" className="iam-input" type="number" min="1" value={editForm.rank}
              onChange={e => setEditForm(f => ({ ...f, rank: e.target.value }))} />
            <p style={{ fontSize: 11, color: 'var(--fg-muted)', marginTop: 4 }}>Lower = higher privilege. e.g. admin=1, editor=50, viewer=100</p>
          </div>
          {editError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{editError}</p>}
        </form>
      </IamDialog>

      <IamDialog
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        title={`Delete role "${deleteTarget?.name}"?`}
        desc="Users currently holding this role will lose it. This action cannot be undone."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setDeleteTarget(null)}>Cancel</button>
            <button className="iam-btn iam-btn-danger" onClick={handleDelete}>Delete</button>
          </>
        }
      >
        {deleteError
          ? <p style={{ fontSize: 12, color: 'var(--danger)' }}>{deleteError}</p>
          : <div />}
      </IamDialog>
    </div>
  );
}
