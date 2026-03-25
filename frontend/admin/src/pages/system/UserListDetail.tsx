import { useEffect, useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { getSystemUserList } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import UserListMembersPanel from '@/components/UserListMembersPanel';

interface UserList {
  id: string; name: string; org_id: string | null; org_name: string | null;
  immovable: boolean; user_count: number; created_at: string;
}

export default function SystemUserListDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [list, setList] = useState<UserList | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!id) return;
    getSystemUserList(id).then(setList).catch(console.error).finally(() => setLoading(false));
  }, [id]);

  return (
    <div>
      <PageHeader
        title={loading ? 'Loading…' : (list?.name ?? 'User List')}
        description={list ? (list.org_name ? `Organisation: ${list.org_name}` : 'System (root)') : undefined}
        action={
          <div className="flex items-center gap-2">
            {list && (list.immovable
              ? <Badge variant="secondary">Immovable</Badge>
              : <Badge variant="outline">Movable</Badge>
            )}
          </div>
        }
      />

      <div className="p-6 space-y-4">
        <Button variant="ghost" size="sm" className="-ml-1" onClick={() => navigate(-1)}>
          <ArrowLeft className="h-4 w-4" />Back
        </Button>

        {loading
          ? <Skeleton className="h-48 w-full" />
          : id && <UserListMembersPanel listId={id} title={list?.name ?? 'Members'} isSystemCtx />
        }
      </div>
    </div>
  );
}
