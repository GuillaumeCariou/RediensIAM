import { rowActivation } from '../../components/iam/rowActivation';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { IamChip, IamDialog } from '@/components/iam';
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

function Toggle({ checked, onChange }: Readonly<{ checked: boolean; onChange: (v: boolean) => void }>) {
  return (
    <input type="checkbox" className="iam-switch" checked={checked} onChange={e => onChange(e.target.checked)} />
  );
}

function ProjectMenu({ onOpen, onAssign, onUnassign, hasUserList, onDelete }: Readonly<{
  onOpen: () => void; onAssign: () => void; onUnassign: () => void; hasUserList: boolean; onDelete: () => void;
}>) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ position: 'relative' }}>
      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
        onClick={e => { e.stopPropagation(); setOpen(o => !o); }}>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/></svg>
      </button>
      {open && (
        <>
          <div role="none" style={{ position: 'fixed', inset: 0, zIndex: 49 }} onClick={() => setOpen(false)} onKeyDown={(e) => { if (e.key === 'Escape') setOpen(false); }} />
          <div style={{ position: 'absolute', right: 0, top: '100%', zIndex: 50, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8, boxShadow: 'var(--shadow-md)', minWidth: 160, padding: 4 }}>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }} onClick={() => { setOpen(false); onOpen(); }}>Open Project</button>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }} onClick={() => { setOpen(false); onAssign(); }}>Assign User List</button>
            {hasUserList && <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }} onClick={() => { setOpen(false); onUnassign(); }}>Unassign User List</button>}
            <div style={{ height: 1, background: 'var(--border)', margin: '4px 0' }} />
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13, color: 'var(--danger)' }} onClick={() => { setOpen(false); onDelete(); }}>Delete</button>
          </div>
        </>
      )}
    </div>
  );
}

export default function Projects() {
  const navigate = useNavigate();
  const { orgId, projectUrl } = useOrgContext();
  const [projects, setProjects] = useState<Project[]>([]);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [assignOpen, setAssignOpen] = useState<Project | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<Project | null>(null);
  // '__none__' is the option that means "no user list"; '' matches no option at all, so it would
  // paint the first entry as chosen while state said otherwise.
  const [selectedList, setSelectedList] = useState('__none__');
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

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true); setCreateError('');
    try {
      await createProject({
        org_id: orgId, name: form.name, slug: form.slug,
        require_role_to_login: form.require_role_to_login,
        redirect_uris: form.redirect_uris.split('\n').map(s => s.trim()).filter(Boolean),
      });
      setCreateOpen(false); setForm({ name: '', slug: '', redirect_uris: '', require_role_to_login: false }); load();
    } catch (err) {
      const body = err instanceof ApiError ? (err.body as Record<string, string> | null) : null;
      setCreateError(body?.detail ?? body?.error ?? 'Failed to create project.');
    } finally { setSaving(false); }
  };

  const handleAssign = async () => {
    if (!assignOpen) return;
    setSaving(true);
    try {
      if (selectedList === '__none__') await unassignUserList(assignOpen.id);
      else await assignUserList(assignOpen.id, selectedList);
      setAssignOpen(null); load();
    } finally { setSaving(false); }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteProject(deleteTarget.id);
    setDeleteTarget(null); load();
  };

  return (
    <div>
      <PageHeader
        title="Projects"
        description="OAuth2-authenticated applications within this organisation"
        actions={orgId ? [
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateOpen(true)}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            New Project
          </button>
        ] : []}
      />
      <div className="iam-page">
        {!orgId && <div style={{ fontSize: 13, color: 'var(--fg-muted)' }}>Select an organisation first.</div>}
        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Name</th><th>Slug</th><th>Status</th><th>User List</th><th>Role Required</th><th>Created</th><th style={{ width: 36 }}></th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return Array.from({ length: 4 }, (_, i) => (
                  <tr key={i}>{Array.from({ length: 7 }, (_, j) => <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>)}</tr>
                ));
                if (projects.length === 0) return (
                  <tr><td colSpan={7}>
                    <div className="iam-empty"><div className="iam-empty-title">No projects yet</div></div>
                  </td></tr>
                );
                return projects.map(p => (
                  <tr key={p.id} {...rowActivation(() => navigate(projectUrl(p.id)))}>
                    <td style={{ fontWeight: 500 }}>{p.name}</td>
                    <td><span className="iam-mono" style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{p.slug}</span></td>
                    <td>{p.active ? <IamChip tone="success">Active</IamChip> : <IamChip tone="default">Inactive</IamChip>}</td>
                    <td>
                      {p.assigned_user_list_name
                        ? <IamChip tone="accent">{p.assigned_user_list_name}</IamChip>
                        : <span style={{ fontSize: 12, color: 'var(--fg-muted)' }}>None</span>}
                    </td>
                    <td>
                      {p.require_role_to_login
                        ? <IamChip tone="warn">Required</IamChip>
                        : <IamChip tone="default">Optional</IamChip>}
                    </td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDateShort(p.created_at)}</td>
                    <td onClick={e => e.stopPropagation()}>
                      <ProjectMenu
                        onOpen={() => navigate(projectUrl(p.id))}
                        onAssign={() => { setAssignOpen(p); setSelectedList(p.assigned_user_list_id ?? '__none__'); }}
                        onUnassign={() => { setAssignOpen(p); setSelectedList('__none__'); }}
                        hasUserList={!!p.assigned_user_list_id}
                        onDelete={() => setDeleteTarget(p)}
                      />
                    </td>
                  </tr>
                ));
              })()}
            </tbody>
          </table>
        </div>
      </div>

      <IamDialog
        open={createOpen}
        onClose={() => { setCreateOpen(false); setCreateError(''); }}
        title="Create Project"
        desc="A new OAuth2 client will be automatically registered in Hydra."
        wide
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => { setCreateOpen(false); setCreateError(''); }}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="create-project-form" type="submit" disabled={saving}>
              {saving ? 'Creating…' : 'Create Project'}
            </button>
          </>
        }
      >
        <form id="create-project-form" onSubmit={handleCreate} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          {createError && <div style={{ padding: '8px 12px', background: 'var(--danger-soft)', color: 'var(--danger)', borderRadius: 6, fontSize: 13 }}>{createError}</div>}
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <div><label className="iam-label" htmlFor="proj-create-name">Name</label><input id="proj-create-name" className="iam-input" value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} required placeholder="My Dashboard" /></div>
            <div>
              <label className="iam-label" htmlFor="proj-create-slug">Slug</label>
              <input id="proj-create-slug" className="iam-input iam-mono" value={form.slug} onChange={e => setForm(f => ({ ...f, slug: e.target.value.toLowerCase().replaceAll(/\s+/g, '-') }))} required placeholder="my-dashboard" pattern="[a-z0-9]+(-[a-z0-9]+)*" />
            </div>
          </div>
          <div>
            <label className="iam-label" htmlFor="proj-create-uris">Redirect URIs (one per line)</label>
            <textarea id="proj-create-uris" className="iam-input" style={{ minHeight: 80, resize: 'vertical' }} value={form.redirect_uris} onChange={e => setForm(f => ({ ...f, redirect_uris: e.target.value }))} placeholder="https://dashboard.example.com/callback" />
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <Toggle checked={form.require_role_to_login} onChange={v => setForm(f => ({ ...f, require_role_to_login: v }))} />
            <div>
              <div style={{ fontSize: 13, fontWeight: 500 }}>Require a role to log in</div>
              <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>Users without a role cannot authenticate</div>
            </div>
          </div>
        </form>
      </IamDialog>

      <IamDialog
        open={!!assignOpen}
        onClose={() => setAssignOpen(null)}
        title="Assign User List"
        desc={`Choose which user list will authenticate into ${assignOpen?.name}.`}
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setAssignOpen(null)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" onClick={handleAssign} disabled={saving}>
              {saving ? 'Saving…' : 'Save'}
            </button>
          </>
        }
      >
        <select className="iam-input" value={selectedList} onChange={e => setSelectedList(e.target.value)}>
          <option value="__none__">— No user list (unassign)</option>
          {userLists.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}
        </select>
      </IamDialog>

      <IamDialog
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        title={`Delete ${deleteTarget?.name}?`}
        desc="The Hydra OAuth2 client for this project will also be deleted. This action is irreversible."
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
