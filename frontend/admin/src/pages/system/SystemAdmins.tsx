import { useEffect, useState } from 'react';
import { listUserLists } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import UserListMembersPanel from '@/components/UserListMembersPanel';

export default function SystemAdmins() {
  const [systemListId, setSystemListId] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    listUserLists()
      .then(res => {
        const all: { id: string; org_id: string | null; immovable: boolean }[] = res.user_lists ?? res ?? [];
        const syslist = all.find(l => l.org_id === null && l.immovable);
        if (!syslist) { setError('System user list not found.'); return; }
        setSystemListId(syslist.id);
      })
      .catch(() => setError('Failed to load system admin list.'))
      .finally(() => setLoading(false));
  }, []);

  return (
    <div>
      <PageHeader
        title="System Admins"
        description="Users with super_admin access across the entire platform"
      />
      <div className="iam-page">
        {error && (
          <div style={{ padding: '8px 12px', background: 'var(--danger-soft)', color: 'var(--danger)', borderRadius: 6, fontSize: 13, marginBottom: 14 }}>
            {error}
          </div>
        )}
        {loading && (
          <div className="iam-card">
            {Array.from({ length: 3 }, (_, i) => (
              <div key={i} style={{ padding: '12px 16px', borderBottom: '1px solid var(--border)', display: 'flex', gap: 10, alignItems: 'center' }}>
                <div style={{ width: 32, height: 32, borderRadius: '50%', background: 'var(--surface-2)' }} />
                <div style={{ flex: 1 }}>
                  <div style={{ height: 13, background: 'var(--surface-2)', borderRadius: 4, width: '40%', marginBottom: 5 }} />
                  <div style={{ height: 11, background: 'var(--surface-2)', borderRadius: 4, width: '60%' }} />
                </div>
              </div>
            ))}
          </div>
        )}
        {!loading && systemListId && (
          <UserListMembersPanel
            listId={systemListId}
            title="System Administrators"
            isSystemCtx={true}
          />
        )}
      </div>
    </div>
  );
}
