import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { useProjectContext } from '@/hooks/useOrgContext';
import { useAuth } from '@/context/AuthContext';
import { IamChip } from '@/components/iam';
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
  const [loading, setLoading] = useState(Boolean(projectId));

  const orgId = oid ?? tokenOrgId;
  const defaultRoleId = project?.default_role_id ?? null;
  const assignedListId = project?.assigned_user_list_id ?? null;
  const assignedListName = project?.assigned_user_list_name
    ?? userLists.find(ul => ul.id === assignedListId)?.name
    ?? null;
  const movableLists = userLists.filter(ul => !ul.immovable);

  /** The fetch alone. Sets no state synchronously, so an effect may call it directly. */
  const fetchAll = useCallback(() => {
    if (!projectId) return;
    const fetches: Promise<unknown>[] = [
      getProjectInfo(projectId).then(p => setProject(p)).catch(() => null),
    ];
    if (isOrgAdmin && orgId) {
      fetches.push(listUserLists(orgId).then(r => setUserLists(r.user_lists ?? r ?? [])).catch(() => null));
    }
    Promise.all(fetches).catch(console.error).finally(() => setLoading(false));
    // isOrgAdmin and orgId come from context and are not both known on the first render. With
    // [projectId] alone the effect never re-ran once they arrived, so an org admin opening the
    // page directly got an empty "assign a user list" dropdown until a manual refresh.
  }, [projectId, isOrgAdmin, orgId]);

  /** What a user-triggered refresh calls: the spinner comes back, then the fetch. */
  const load = () => { setLoading(true); fetchAll(); };

  useEffect(fetchAll, [fetchAll]);

  const handleAssignList = async (ulId: string) => {
    if (!projectId) return;
    const isAdmin = isSystemCtx || isSuperAdmin;
    if (ulId === '__none__') {
      await (isAdmin ? adminUnassignUserList(projectId) : unassignUserList(projectId));
    } else {
      await (isAdmin ? adminAssignUserList(projectId, ulId) : assignUserList(projectId, ulId));
    }
    getProjectInfo(projectId).then(p => setProject(p)).catch(() => null);
  };

  return (
    <div>
      <PageHeader
        title="Project Users"
        description="Users and their role assignments in this project"
      />
      <div className="iam-page" style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <div className="iam-card iam-card-pad">
          <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--fg-subtle)', marginBottom: 12 }}>Assigned User List</div>
          {(() => {
            if (loading) return <div style={{ height: 36, width: 288, background: 'var(--surface-2)', borderRadius: 6 }} />;
            if (isOrgAdmin) return (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <select className="iam-input" style={{ maxWidth: 288 }}
                value={assignedListId ?? '__none__'} onChange={e => handleAssignList(e.target.value)}>
                <option value="__none__">— No user list assigned —</option>
                {movableLists.map(ul => (
                  <option key={ul.id} value={ul.id}>{ul.name}</option>
                ))}
              </select>
              {!assignedListId && (
                <p style={{ fontSize: 12, color: 'var(--warn)' }}>No user list assigned — users cannot log in to this project.</p>
              )}
            </div>
            );
            return (
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              {assignedListName
                ? <IamChip tone="accent">{assignedListName}</IamChip>
                : <span style={{ fontSize: 13, color: 'var(--fg-muted)', fontStyle: 'italic' }}>No user list assigned</span>}
            </div>
            );
          })()}
        </div>

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
