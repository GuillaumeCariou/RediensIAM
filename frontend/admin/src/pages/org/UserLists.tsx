import { rowActivation } from '../../components/iam/rowActivation';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { IamChip, IamDialog } from '@/components/iam';
import { listUserLists, createUserList, createSystemUserList } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';

interface UserList {
  id: string; name: string; org_id: string | null; org_name: string | null;
  immovable: boolean; user_count?: number; created_at: string;
}

export default function UserLists() {
  const navigate = useNavigate();
  const { orgId, isSystemCtx, userListBase } = useOrgContext();
  const isGlobal = !orgId;
  const navigateBase = isGlobal ? '/system/userlists' : userListBase;

  const [lists, setLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [listForm, setListForm] = useState({ name: '' });
  const [saving, setSaving] = useState(false);

  const load = () => {
    setLoading(true);
    listUserLists(orgId ?? undefined)
      .then(r => setLists(r.user_lists ?? r ?? []))
      .catch(console.error)
      .finally(() => setLoading(false));
  };
  useEffect(load, [orgId]);

  const filtered = isGlobal
    ? lists.filter(l =>
        l.name.toLowerCase().includes(search.toLowerCase()) ||
        (l.org_name?.toLowerCase().includes(search.toLowerCase()) ?? false)
      )
    : lists;

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true);
    try {
      // En portée système, l'organisation vient de l'URL et doit voyager dans le corps : le jeton
      // d'un super-admin n'en porte aucune. En portée organisation, c'est le jeton qui fait foi.
      await (isSystemCtx
        ? createSystemUserList({ name: listForm.name, org_id: orgId })
        : createUserList({ name: listForm.name }));
      setCreateOpen(false); setListForm({ name: '' }); load();
    } finally { setSaving(false); }
  };

  return (
    <div>
      <PageHeader
        title="User Lists"
        description={isGlobal ? 'All user lists across the system' : 'Reusable pools of users that can be assigned to projects'}
        actions={isGlobal ? [] : [
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateOpen(true)}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            New User List
          </button>
        ]}
      />
      <div className="iam-page">
        {isGlobal && (
          <div style={{ marginBottom: 14 }}>
            <div style={{ position: 'relative', maxWidth: 320 }}>
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
                style={{ position: 'absolute', left: 10, top: '50%', transform: 'translateY(-50%)', color: 'var(--fg-subtle)', pointerEvents: 'none' }}>
                <circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>
              </svg>
              <input className="iam-input" style={{ paddingLeft: 30 }}
                placeholder="Search by name or organisation…"
                value={search} onChange={e => setSearch(e.target.value)} />
            </div>
          </div>
        )}

        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Name</th>
                {isGlobal ? <th>Organisation</th> : <th>Users</th>}
                <th>Type</th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return Array.from({ length: 4 }, (_, i) => (
                  <tr key={i}>{Array.from({ length: 3 }, (_, j) => <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>)}</tr>
                ));
                if (filtered.length === 0) return (
                  <tr><td colSpan={3}>
                    <div className="iam-empty">
                      <div className="iam-empty-title">{isGlobal ? 'No user lists found' : 'No user lists yet'}</div>
                    </div>
                  </td></tr>
                );
                return filtered.map(list => (
                  <tr key={list.id} {...rowActivation(() => navigate(`${navigateBase}/${list.id}`))}>
                    <td style={{ fontWeight: 500 }}>{list.name}</td>
                    {isGlobal
                      ? <td style={{ fontSize: 13, color: 'var(--fg-muted)' }}>{list.org_name ?? 'System (root)'}</td>
                      : <td style={{ fontSize: 13 }}>{list.user_count ?? '—'}</td>}
                    <td>
                      {list.immovable
                        ? <IamChip tone="default">Immovable</IamChip>
                        : <IamChip tone="accent">Movable</IamChip>}
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
        title="Create User List"
        desc="A movable pool of users you can assign to projects."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setCreateOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="create-list-form" type="submit" disabled={saving}>
              {saving ? 'Creating…' : 'Create'}
            </button>
          </>
        }
      >
        <form id="create-list-form" onSubmit={handleCreate}>
          <label className="iam-label" htmlFor="list-name">Name</label>
          <input id="list-name" className="iam-input" value={listForm.name} onChange={e => setListForm({ name: e.target.value })} required placeholder="Team Alpha" />
        </form>
      </IamDialog>
    </div>
  );
}
