import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { IamChip, IamDialog } from '@/components/iam';
import { listServiceAccounts, createServiceAccount, deleteServiceAccount, listUserLists } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface SA { id: string; name: string; description: string | null; active: boolean; last_used_at: string | null; created_at: string; org_id: string | null; }
interface UserList { id: string; name: string; }

export default function OrgServiceAccounts() {
  const { orgId, orgBase } = useOrgContext();
  const navigate = useNavigate();
  const [accounts, setAccounts] = useState<SA[]>([]);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<SA | null>(null);
  const [form, setForm] = useState({ name: '', description: '', user_list_id: '' });
  const [saving, setSaving] = useState(false);

  const load = () => {
    if (!orgId) { setLoading(false); return; }
    setLoading(true);
    Promise.all([
      listServiceAccounts().then(r => setAccounts((r ?? []).filter((sa: SA) => sa.org_id === orgId))),
      listUserLists(orgId).then(r => setUserLists(r.user_lists ?? r ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [orgId]);

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true);
    try {
      await createServiceAccount({ name: form.name, description: form.description || undefined, user_list_id: form.user_list_id });
      setCreateOpen(false);
      setForm({ name: '', description: '', user_list_id: '' });
      load();
    } finally { setSaving(false); }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteServiceAccount(deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Service Accounts"
        description="Non-human identities for automation and integrations"
        actions={orgId ? [
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateOpen(true)}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            New Service Account
          </button>
        ] : []}
      />
      <div className="iam-page">
        <div className="iam-card">
          <table className="iam-tbl">
            <thead><tr>
              <th>Name</th><th>Status</th><th>Last Used</th><th>Created</th><th style={{ width: 36 }}></th>
            </tr></thead>
            <tbody>
              {(() => {
                if (loading) return Array.from({ length: 3 }, (_, i) => (
                  <tr key={i}>{Array.from({ length: 5 }, (_, j) => <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>)}</tr>
                ));
                if (accounts.length === 0) return (
                  <tr><td colSpan={5}>
                    <div className="iam-empty">
                      <div className="iam-empty-title">No service accounts</div>
                      <div className="iam-empty-desc">Create one for automation and integrations.</div>
                    </div>
                  </td></tr>
                );
                return accounts.map(sa => (
                  <tr key={sa.id} style={{ cursor: 'pointer' }} onClick={() => navigate(`${orgBase}/service-accounts/${sa.id}`)}>
                    <td>
                      <div style={{ fontWeight: 500 }}>{sa.name}</div>
                      {sa.description && <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{sa.description}</div>}
                    </td>
                    <td><IamChip tone={sa.active ? 'success' : 'default'}>{sa.active ? 'Active' : 'Inactive'}</IamChip></td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(sa.last_used_at)}</td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(sa.created_at)}</td>
                    <td onClick={e => e.stopPropagation()}>
                      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" style={{ color: 'var(--danger)' }}
                        onClick={() => setDeleteTarget(sa)}>
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>
                      </button>
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
        onClose={() => setCreateOpen(false)}
        title="Create Service Account"
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setCreateOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="create-sa-org-form" type="submit" disabled={saving}>
              {saving ? 'Creating…' : 'Create'}
            </button>
          </>
        }
      >
        <form id="create-sa-org-form" onSubmit={handleCreate} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div>
            <label className="iam-label" htmlFor="org-sa-name">Name</label>
            <input id="org-sa-name" className="iam-input" value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} required placeholder="ci-deploy-bot" />
          </div>
          <div>
            <label className="iam-label" htmlFor="org-sa-description">Description (optional)</label>
            <input id="org-sa-description" className="iam-input" value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} />
          </div>
          <div>
            <label className="iam-label" htmlFor="org-sa-user-list">User List</label>
            <select id="org-sa-user-list" className="iam-input" value={form.user_list_id} onChange={e => setForm(f => ({ ...f, user_list_id: e.target.value }))} required>
              <option value="">Select list…</option>
              {userLists.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}
            </select>
          </div>
        </form>
      </IamDialog>

      <IamDialog
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        title={`Delete ${deleteTarget?.name}?`}
        desc="All PATs for this service account will also be revoked."
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
