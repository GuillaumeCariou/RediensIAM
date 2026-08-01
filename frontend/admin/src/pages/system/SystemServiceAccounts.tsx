import { useEffect, useState, useCallback } from 'react';
import { useNavigate } from 'react-router';
import { IamChip, IamDialog } from '@/components/iam';
import { listServiceAccounts, createServiceAccount, deleteServiceAccount, listUserLists } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface ServiceAccount {
  id: string;
  name: string;
  description: string | null;
  active: boolean;
  last_used_at: string | null;
  created_at: string;
}

export default function SystemServiceAccounts() {
  const navigate = useNavigate();
  const [accounts, setAccounts] = useState<ServiceAccount[]>([]);
  const [systemListId, setSystemListId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const [createOpen, setCreateOpen] = useState(false);
  const [newSa, setNewSa] = useState({ name: '', description: '' });
  const [createSaving, setCreateSaving] = useState(false);

  const [deleteTarget, setDeleteTarget] = useState<ServiceAccount | null>(null);

  const load = useCallback(() => {
    setLoading(true);
    Promise.all([
      listServiceAccounts().then((res: (ServiceAccount & { is_system: boolean })[]) =>
        setAccounts((res ?? []).filter(sa => sa.is_system))
      ),
      listUserLists().then((res: { id: string; org_id: string | null; immovable: boolean }[]) => {
        const syslist = (res ?? []).find(l => l.org_id == null && l.immovable);
        if (syslist) setSystemListId(syslist.id);
      }),
    ]).catch(console.error).finally(() => setLoading(false));
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!systemListId) return;
    setCreateSaving(true);
    try {
      await createServiceAccount({ name: newSa.name, description: newSa.description || undefined, user_list_id: systemListId });
      setCreateOpen(false);
      setNewSa({ name: '', description: '' });
      load();
    } finally { setCreateSaving(false); }
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
        title="System Service Accounts"
        description="Service accounts with system-level access"
        actions={[
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateOpen(true)} disabled={!systemListId}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            New Service Account
          </button>
        ]}
      />
      <div className="iam-page">
        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Name</th>
                <th>Status</th>
                <th>Last Used</th>
                <th>Created</th>
                <th style={{ width: 36 }}></th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return Array.from({ length: 3 }, (_, i) => (
                  <tr key={i}>{Array.from({ length: 5 }, (_, j) => (
                    <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>
                  ))}</tr>
                ));
                if (accounts.length === 0) return (
                  <tr><td colSpan={5}>
                    <div className="iam-empty">
                      <div className="iam-empty-icon">
                        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5"><circle cx="12" cy="8" r="4"/><path d="M4 20c0-4 3.6-7 8-7s8 3 8 7"/></svg>
                      </div>
                      <div className="iam-empty-title">No system service accounts yet</div>
                    </div>
                  </td></tr>
                );
                return accounts.map(sa => (
                  <tr key={sa.id} style={{ cursor: 'pointer' }} onClick={() => navigate(`/system/service-accounts/${sa.id}`)}>
                    <td>
                      <div style={{ fontWeight: 500 }}>{sa.name}</div>
                      {sa.description && <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{sa.description}</div>}
                    </td>
                    <td><IamChip tone={sa.active ? 'success' : 'default'}>{sa.active ? 'Active' : 'Inactive'}</IamChip></td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDateShort(sa.last_used_at)}</td>
                    <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDateShort(sa.created_at)}</td>
                    <td onClick={e => e.stopPropagation()}>
                      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
                        style={{ color: 'var(--danger)' }}
                        onClick={() => setDeleteTarget(sa)}>
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6M14 11v6"/><path d="M9 6V4h6v2"/></svg>
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
        title="New System Service Account"
        desc="Create a service account with system-level access."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setCreateOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="create-sa-form" type="submit" disabled={createSaving}>
              {createSaving ? 'Creating…' : 'Create'}
            </button>
          </>
        }
      >
        <form id="create-sa-form" onSubmit={handleCreate} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div>
            <label className="iam-label" htmlFor="sys-sa-name">Name</label>
            <input id="sys-sa-name" className="iam-input" value={newSa.name} onChange={e => setNewSa(s => ({ ...s, name: e.target.value }))} required placeholder="ci-deploy-bot" />
          </div>
          <div>
            <label className="iam-label" htmlFor="sys-sa-description">Description</label>
            <input id="sys-sa-description" className="iam-input" value={newSa.description} onChange={e => setNewSa(s => ({ ...s, description: e.target.value }))} placeholder="Used by CI pipeline" />
          </div>
        </form>
      </IamDialog>

      <IamDialog
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        title={`Delete "${deleteTarget?.name}"?`}
        desc="This will revoke all PATs and remove system access. This cannot be undone."
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
