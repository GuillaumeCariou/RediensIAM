import { useEffect, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { IamChip, IamDialog } from '@/components/iam';
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
      await createRole(projectId, { name: form.name, description: form.description || undefined, rank: Number.parseInt(form.rank, 10) });
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
        actions={projectId ? [
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateOpen(true)}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            New Role
          </button>
        ] : []}
      />
      <div className="iam-page" style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <div className="iam-card iam-card-pad">
          <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Default Role</div>
          <div style={{ fontSize: 12.5, color: 'var(--fg-muted)', marginBottom: 12 }}>Automatically assigned to new users on registration and social login.</div>
          {loading ? (
            <div style={{ height: 36, width: 192, background: 'var(--surface-2)', borderRadius: 6 }} />
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <select className="iam-input" style={{ maxWidth: 256 }}
                value={defaultRoleId ?? '__none__'} onChange={e => handleDefaultRole(e.target.value)} disabled={savingDefault}>
                <option value="__none__">No default role</option>
                {[...roles].sort((a, b) => a.rank - b.rank).map(r => (
                  <option key={r.id} value={r.id}>{r.name} (rank {r.rank})</option>
                ))}
              </select>
              {defaultRoleError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{defaultRoleError}</p>}
            </div>
          )}
        </div>

        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Name</th><th>Description</th><th>Rank</th><th>Created</th><th style={{ width: 36 }}></th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return Array.from({ length: 4 }, (_, i) => (
                  <tr key={i}>{Array.from({ length: 5 }, (_, j) => <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>)}</tr>
                ));
                if (roles.length === 0) return (
                  <tr><td colSpan={5}>
                    <div className="iam-empty">
                      <div className="iam-empty-title">No roles defined yet</div>
                      <div className="iam-empty-desc">Create roles to control project access.</div>
                    </div>
                  </td></tr>
                );
                return [...roles].sort((a, b) => a.rank - b.rank).map(role => (
                  <tr key={role.id}>
                    <td style={{ fontWeight: 500 }}>
                      <span className="iam-mono">{role.name}</span>
                      {role.id === defaultRoleId && <span style={{ marginLeft: 8 }}><IamChip tone="accent">Default</IamChip></span>}
                    </td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{role.description ?? '—'}</td>
                    <td><span className="iam-mono" style={{ fontSize: 11, background: 'var(--surface-2)', padding: '2px 6px', borderRadius: 4 }}>{role.rank}</span></td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(role.created_at)}</td>
                    <td>
                      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" style={{ color: 'var(--danger)' }} onClick={() => setDeleteTarget(role)}>
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>
                      </button>
                    </td>
                  </tr>
                ));
              })()}
            </tbody>
          </table>
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
        <div />
      </IamDialog>
    </div>
  );
}
