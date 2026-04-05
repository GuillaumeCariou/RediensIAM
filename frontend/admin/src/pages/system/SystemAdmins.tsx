import { useEffect, useState } from 'react';
import { listUserLists } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import UserListMembersPanel from '@/components/UserListMembersPanel';
import { Skeleton } from '@/components/ui/skeleton';

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
      <div className="p-6">
        {error && <p className="text-sm text-destructive mb-4">{error}</p>}
        {loading && (
          <div className="space-y-2">
            {Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-12 w-full" />)}
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
