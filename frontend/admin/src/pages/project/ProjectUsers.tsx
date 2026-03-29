import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { useProjectContext } from '@/hooks/useOrgContext';
import { useAuth } from '@/context/AuthContext';
import { List } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import {
  getProjectInfo, listUserLists,
  assignUserList, unassignUserList,
  adminAssignUserList, adminUnassignUserList,
} from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import UserListMembersPanel from '@/components/UserListMembersPanel';

interface UserList { id: string; name: string; immovable?: boolean; }
interface Project {
  assigned_user_list_id: string | null;
  assigned_user_list_name: string | null;
  default_role_id: string | null;
}

export default function ProjectUsers() {
  const { projectId, isSystemCtx } = useProjectContext();
  const { oid } = useParams<{ oid?: string }>();
  const { isOrgAdmin, isSuperAdmin, orgId: tokenOrgId } = useAuth();

  const [project, setProject] = useState<Project | null>(null);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);

  const orgId = oid ?? tokenOrgId;
  const defaultRoleId = project?.default_role_id ?? null;
  const assignedListId = project?.assigned_user_list_id ?? null;
  const assignedListName = project?.assigned_user_list_name
    ?? userLists.find(ul => ul.id === assignedListId)?.name
    ?? null;
  const movableLists = userLists.filter(ul => !ul.immovable);

  const load = () => {
    if (!projectId) { setLoading(false); return; }
    setLoading(true);
    const fetches: Promise<unknown>[] = [
      getProjectInfo(projectId).then(p => setProject(p)).catch(() => null),
    ];
    if (isOrgAdmin && orgId) {
      fetches.push(listUserLists(orgId).then(r => setUserLists(r.user_lists ?? r ?? [])).catch(() => null));
    }
    Promise.all(fetches).catch(console.error).finally(() => setLoading(false));
  };

  useEffect(load, [projectId]);

  const handleAssignList = async (ulId: string) => {
    if (!projectId) return;
    if (ulId === '__none__') {
      if (isSystemCtx || isSuperAdmin) await adminUnassignUserList(projectId);
      else await unassignUserList(projectId);
    } else {
      if (isSystemCtx || isSuperAdmin) await adminAssignUserList(projectId, ulId);
      else await assignUserList(projectId, ulId);
    }
    getProjectInfo(projectId).then(p => setProject(p)).catch(() => null);
  };

  return (
    <div>
      <PageHeader
        title="Project Users"
        description="Users and their role assignments in this project"
      />
      <div className="p-6 space-y-4">

        {/* ── Assigned User List card ── */}
        <Card>
          <CardHeader className="pb-3">
            <CardTitle className="text-sm text-muted-foreground flex items-center gap-2">
              <List className="h-4 w-4" />Assigned User List
            </CardTitle>
          </CardHeader>
          <CardContent>
            {loading ? (
              <Skeleton className="h-10 w-72" />
            ) : isOrgAdmin ? (
              <div className="space-y-2">
                <Select value={assignedListId ?? '__none__'} onValueChange={handleAssignList}>
                  <SelectTrigger className="w-72 bg-background">
                    <SelectValue placeholder="— No user list assigned —" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__none__">— None —</SelectItem>
                    {movableLists.map(ul => (
                      <SelectItem key={ul.id} value={ul.id}>{ul.name}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {!assignedListId && (
                  <p className="text-xs text-amber-500">No user list assigned — users cannot log in to this project.</p>
                )}
              </div>
            ) : (
              <div className="flex items-center gap-2">
                {assignedListName
                  ? <Badge variant="secondary">{assignedListName}</Badge>
                  : <span className="text-sm text-muted-foreground italic">No user list assigned</span>
                }
              </div>
            )}
          </CardContent>
        </Card>

        {/* ── Userlist member management (org/super admin) ── */}
        {isOrgAdmin && assignedListId && (
          <UserListMembersPanel
            key={assignedListId}
            listId={assignedListId}
            title={`${assignedListName ?? 'User List'} — Members`}
            isSystemCtx={isSystemCtx || isSuperAdmin}
            projectId={projectId}
            defaultRoleId={defaultRoleId}
            onChanged={load}
          />
        )}

      </div>
    </div>
  );
}
