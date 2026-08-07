import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { useProjectContext } from '@/hooks/useOrgContext';
import { useAuth } from '@/context/AuthContext';
import { IamChip, IamDialog } from '@/components/iam';
import { rowActivation } from '@/components/iam/rowActivation';
import {
  getProjectInfo, listUserLists,
  assignUserList, unassignUserList,
  adminAssignUserList, adminUnassignUserList,
  listProjectUsers, listRoles, assignRole, removeRole,
  getProjectUser, revokeProjectUserSessions, cleanupProject, createProjectUser,
} from '@/api';
import { ApiError } from '@/auth';
import { fmtDate } from '@/lib/utils';
import PageHeader from '@/components/layout/PageHeader';
import UserListMembersPanel from '@/components/UserListMembersPanel';

interface UserList { id: string; name: string; immovable?: boolean; }
interface Project {
  assigned_user_list_id: string | null;
  assigned_user_list_name: string | null;
  default_role_id: string | null;
}
interface Role { id: string; name: string; }
interface Member {
  id: string; username: string; discriminator: string; email: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
  roles?: Role[];
}
/** `/project/users/{id}` nomme la clé `role_id`, la liste la nomme `id`. Deux formes, une page. */
interface MemberDetail {
  id: string; username: string; discriminator: string; email: string; active: boolean;
  roles: { role_id: string; name: string; rank: number }[];
}

/**
 * Le refus de l'API, dit en clair. Même motif que `ProjectRoles.tsx` — recopié plutôt qu'importé
 * parce qu'il y est local à ce fichier-là.
 */
function apiErrorMessage(e: unknown, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return body?.detail ?? body?.error ?? fallback;
}

const label = (m: { username: string; discriminator: string }) => `${m.username}#${m.discriminator}`;

/**
 * Les membres du projet, lus en portée PROJET.
 *
 * `UserListMembersPanel` fait plus, mais il lit `/org/userlists/{id}/users`, gardé en OrgAdmin :
 * un project_admin n'en obtenait que des 403. Ce panneau-ci n'appelle que des routes que
 * `ProjectController` lui ouvre — la liste, le détail d'un membre, ses rôles, ses sessions.
 */
function ProjectMembersPanel({ projectId }: Readonly<{ projectId: string }>) {
  const [members, setMembers] = useState<Member[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  const [detailFor, setDetailFor] = useState<Member | null>(null);
  const [detail, setDetail] = useState<MemberDetail | null>(null);
  const [detailError, setDetailError] = useState('');
  const [selectedRole, setSelectedRole] = useState('');
  const [roleSaving, setRoleSaving] = useState(false);

  const [revokeTarget, setRevokeTarget] = useState<Member | null>(null);
  const [revokeError, setRevokeError] = useState('');
  const [revoking, setRevoking] = useState(false);

  const [addOpen, setAddOpen] = useState(false);
  const [addForm, setAddForm] = useState({ email: '', username: '', password: '' });
  const [addSaving, setAddSaving] = useState(false);
  const [addError, setAddError] = useState('');

  /** Ne pose aucun état de façon synchrone, donc un effet peut l'appeler directement. */
  const fetchAll = useCallback(() => {
    Promise.all([listProjectUsers(projectId), listRoles(projectId)])
      .then(([users, roleRes]) => {
        setMembers(users.users ?? users ?? []);
        setRoles(roleRes.roles ?? roleRes ?? []);
        setError('');
      })
      .catch(e => setError(apiErrorMessage(e, 'Could not read this project’s members.')))
      .finally(() => setLoading(false));
  }, [projectId]);

  useEffect(fetchAll, [fetchAll]);

  const openDetail = async (m: Member) => {
    setDetailFor(m); setDetail(null); setDetailError(''); setSelectedRole('');
    try { setDetail(await getProjectUser(projectId, m.id)); }
    catch (e) { setDetailError(apiErrorMessage(e, 'Could not read this member.')); }
  };

  const refreshDetail = async (userId: string) => {
    try { setDetail(await getProjectUser(projectId, userId)); }
    catch (e) { setDetailError(apiErrorMessage(e, 'Could not re-read this member.')); }
  };

  const handleAssignRole = async () => {
    if (!detailFor || !selectedRole) return;
    setRoleSaving(true); setDetailError('');
    try {
      await assignRole(projectId, detailFor.id, selectedRole);
      setSelectedRole('');
      await refreshDetail(detailFor.id);
      fetchAll();
    } catch (e) { setDetailError(apiErrorMessage(e, 'Failed to assign that role.')); }
    finally { setRoleSaving(false); }
  };

  const handleRemoveRole = async (roleId: string) => {
    if (!detailFor) return;
    setDetailError('');
    try {
      await removeRole(projectId, detailFor.id, roleId);
      await refreshDetail(detailFor.id);
      fetchAll();
    } catch (e) { setDetailError(apiErrorMessage(e, 'Failed to remove that role.')); }
  };

  /**
   * Le serveur applique la politique de mot de passe du projet et renvoie `min_length` avec
   * `password_too_short` : le refus est recopié avec sa longueur plutôt que redeviné côté client,
   * qui ne connaît pas le plancher absolu appliqué par-dessus le réglage du projet.
   */
  const handleAdd = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setAddSaving(true); setAddError('');
    try {
      await createProjectUser(projectId, {
        email: addForm.email,
        password: addForm.password,
        username: addForm.username || undefined,
      });
      setAddOpen(false);
      setAddForm({ email: '', username: '', password: '' });
      fetchAll();
    } catch (error_) {
      const body = error_ instanceof ApiError ? (error_.body as { error?: string; min_length?: number } | null) : null;
      if (body?.error === 'password_too_short' && body.min_length) {
        setAddError(`That password is too short — this project requires at least ${body.min_length} characters.`);
      } else if (body?.error === 'email_already_exists') {
        setAddError('A user with that email is already in this project’s list.');
      } else if (body?.error === 'no_user_list') {
        setAddError('This project has no user list assigned, so it has nowhere to put a new member.');
      } else {
        setAddError(apiErrorMessage(error_, 'Failed to create this member.'));
      }
    } finally { setAddSaving(false); }
  };

  const handleRevoke = async () => {
    if (!revokeTarget) return;
    setRevoking(true); setRevokeError('');
    try {
      await revokeProjectUserSessions(projectId, revokeTarget.id);
      setNotice(`Every session of ${label(revokeTarget)} was revoked.`);
      setRevokeTarget(null);
    } catch (e) { setRevokeError(apiErrorMessage(e, 'Failed to revoke the sessions. Nothing changed.')); }
    finally { setRevoking(false); }
  };

  const held = detail?.roles ?? [];
  const unassigned = roles.filter(r => !held.some(h => h.role_id === r.id));

  return (
    <>
      {error && <div className="iam-alert iam-alert-danger">{error}</div>}
      {notice && <div className="iam-alert">{notice}</div>}

      <div className="iam-card">
        <div className="iam-card-pad pb-3 flex items-center justify-between">
          <h3 className="text-sm font-semibold">Members</h3>
          <button className="iam-btn iam-btn-secondary iam-btn-sm" type="button"
            onClick={() => { setAddError(''); setAddOpen(true); }}>
            Add member
          </button>
        </div>
        <table className="iam-tbl">
          <thead>
            <tr><th>User</th><th>Status</th><th>Roles</th><th>Last login</th></tr>
          </thead>
          <tbody>
            {(() => {
              if (loading) return Array.from({ length: 3 }, (_, i) => (
                <tr key={i}><td colSpan={4}><div className="iam-skeleton h-6 w-full" /></td></tr>
              ));
              if (members.length === 0) return (
                <tr><td colSpan={4}>
                  <div className="iam-empty">
                    <div className="iam-empty-title">No members yet</div>
                    <div className="iam-empty-desc">Users of the assigned list appear here once it has any.</div>
                  </div>
                </td></tr>
              );
              return members.map(m => (
                <tr key={m.id} {...rowActivation(() => openDetail(m))}>
                  <td>
                    <p className="text-sm font-medium">{label(m)}</p>
                    <p className="text-xs text-muted-foreground">{m.email}</p>
                  </td>
                  <td>{m.active ? <IamChip tone="accent">Active</IamChip> : <IamChip tone="danger">Inactive</IamChip>}</td>
                  <td>
                    <div className="flex flex-wrap gap-1">
                      {(m.roles ?? []).map(r => <IamChip tone="default" key={r.id}>{r.name}</IamChip>)}
                      {(m.roles ?? []).length === 0 && <span className="text-xs text-muted-foreground">—</span>}
                    </div>
                  </td>
                  <td className="text-sm text-muted-foreground">{fmtDate(m.last_login_at)}</td>
                </tr>
              ));
            })()}
          </tbody>
        </table>
      </div>

      <IamDialog
        open={addOpen}
        onClose={() => setAddOpen(false)}
        title="Add a member"
        desc="Creates an account in the user list assigned to this project."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" type="button" onClick={() => setAddOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="add-project-member" type="submit" disabled={addSaving}>
              {addSaving ? 'Creating…' : 'Create'}
            </button>
          </>
        }
      >
        <form id="add-project-member" onSubmit={handleAdd} className="space-y-3 py-1">
          <div>
            <label className="iam-label" htmlFor="pm-email">Email</label>
            <input id="pm-email" className="iam-input" type="email" required value={addForm.email}
              onChange={e => setAddForm(f => ({ ...f, email: e.target.value }))} />
          </div>
          <div>
            <label className="iam-label" htmlFor="pm-username">Username (optional)</label>
            <input id="pm-username" className="iam-input" value={addForm.username}
              onChange={e => setAddForm(f => ({ ...f, username: e.target.value }))}
              placeholder="Defaults to the part before @" />
          </div>
          <div>
            <label className="iam-label" htmlFor="pm-password">Password</label>
            <input id="pm-password" className="iam-input" type="password" required value={addForm.password}
              onChange={e => setAddForm(f => ({ ...f, password: e.target.value }))} />
          </div>
          {addError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{addError}</p>}
        </form>
      </IamDialog>

      <IamDialog
        open={!!detailFor}
        onClose={() => setDetailFor(null)}
        title={detailFor ? label(detailFor) : ''}
        desc={detailFor?.email}
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" type="button" onClick={() => setDetailFor(null)}>Close</button>
            <button className="iam-btn iam-btn-danger" type="button"
              onClick={() => { setRevokeError(''); setRevokeTarget(detailFor); setDetailFor(null); }}>
              Revoke sessions
            </button>
          </>
        }
      >
        <div className="space-y-3 py-1">
          {detailError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{detailError}</p>}
          {!detail && !detailError && <div className="iam-skeleton h-16 w-full" />}
          {detail && (
            <>
              <p className="text-sm">
                {detail.active ? <IamChip tone="accent">Active</IamChip> : <IamChip tone="danger">Inactive</IamChip>}
              </p>
              <div className="space-y-2">
                <p className="iam-label">Project roles</p>
                <div className="flex flex-wrap gap-1 min-h-6">
                  {held.map(r => (
                    <IamChip className="gap-1 pr-1" tone="default" key={r.role_id}>
                      {r.name}
                      <button type="button" aria-label={`Remove ${r.name}`} className="ml-0.5"
                        onClick={() => handleRemoveRole(r.role_id)}>×</button>
                    </IamChip>
                  ))}
                  {held.length === 0 && <span className="text-xs text-muted-foreground">No roles assigned</span>}
                </div>
                {unassigned.length > 0 && (
                  <div className="flex gap-2">
                    <select className="iam-input flex-1" aria-label="Project role to add"
                      value={selectedRole} onChange={e => setSelectedRole(e.target.value)}>
                      <option value="">Select a role…</option>
                      {unassigned.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}
                    </select>
                    <button className="iam-btn iam-btn-primary iam-btn-sm" type="button"
                      disabled={!selectedRole || roleSaving} onClick={handleAssignRole}>
                      {roleSaving ? '…' : 'Add'}
                    </button>
                  </div>
                )}
              </div>
            </>
          )}
        </div>
      </IamDialog>

      <IamDialog
        open={!!revokeTarget}
        onClose={() => setRevokeTarget(null)}
        title={`Revoke every session of ${revokeTarget ? label(revokeTarget) : ''}?`}
        desc="They are signed out of this project everywhere and must sign in again. The account, its
              roles and its password are untouched."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" type="button" onClick={() => setRevokeTarget(null)}>Cancel</button>
            <button className="iam-btn iam-btn-danger" type="button" disabled={revoking} onClick={handleRevoke}>
              {revoking ? 'Revoking…' : 'Revoke sessions'}
            </button>
          </>
        }
      >
        {revokeError ? <p style={{ fontSize: 12, color: 'var(--danger)' }}>{revokeError}</p> : <div />}
      </IamDialog>
    </>
  );
}

export default function ProjectUsers() {
  const { projectId, isSystemCtx } = useProjectContext();
  const { oid } = useParams<{ oid?: string }>();
  const { isOrgAdmin, isSuperAdmin, orgId: tokenOrgId } = useAuth();

  const [project, setProject] = useState<Project | null>(null);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(Boolean(projectId));

  // Le nettoyage est destructif, donc il se propose avant de s'exécuter : un `dry_run` compte ce
  // qui partirait, et seul un second clic, sur un bouton qui nomme ce compte, le supprime.
  const [cleanupOpen, setCleanupOpen] = useState(false);
  const [cleanupRunning, setCleanupRunning] = useState(false);
  const [cleanupError, setCleanupError] = useState('');
  const [cleanupResult, setCleanupResult] = useState<{ orphaned_roles_removed: number; dry_run: boolean } | null>(null);
  const [membersKey, setMembersKey] = useState(0);

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

  const runCleanup = async (dryRun: boolean) => {
    if (!projectId) return;
    setCleanupRunning(true); setCleanupError('');
    if (!dryRun) setCleanupResult(null);
    try {
      setCleanupResult(await cleanupProject(projectId, dryRun));
      if (!dryRun) setMembersKey(k => k + 1);
    } catch (e) {
      setCleanupError(apiErrorMessage(e, 'The cleanup could not run. Nothing was changed.'));
    } finally { setCleanupRunning(false); }
  };

  const previewed = cleanupResult?.dry_run === true;

  return (
    <div>
      <PageHeader
        title="Project Users"
        description="Users and their role assignments in this project"
        actions={projectId ? [
          <button key="cleanup" className="iam-btn iam-btn-secondary iam-btn-sm"
            onClick={() => { setCleanupResult(null); setCleanupError(''); setCleanupOpen(true); }}>
            Cleanup
          </button>,
        ] : []}
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

        {/* Un project_admin n'a pas de portée organisation : le panneau ci-dessus rendrait 403 pour
            lui. Il voit le sien, servi par les routes de son propre contrôleur. */}
        {!isOrgAdmin && projectId && <ProjectMembersPanel key={`${projectId}-${membersKey}`} projectId={projectId} />}
      </div>

      <IamDialog
        open={cleanupOpen}
        onClose={() => setCleanupOpen(false)}
        title="Clean up this project"
        desc="Removes the role assignments held by accounts that are no longer in the project's user list. Preview first — nothing is deleted until you confirm."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" type="button" onClick={() => setCleanupOpen(false)}>Close</button>
            <button className="iam-btn iam-btn-secondary" type="button" disabled={cleanupRunning}
              onClick={() => runCleanup(true)}>
              {cleanupRunning ? 'Running…' : 'Preview'}
            </button>
            {previewed && (cleanupResult?.orphaned_roles_removed ?? 0) > 0 && (
              <button className="iam-btn iam-btn-danger" type="button" disabled={cleanupRunning}
                onClick={() => runCleanup(false)}>
                Remove {cleanupResult?.orphaned_roles_removed} role assignments
              </button>
            )}
          </>
        }
      >
        <div className="space-y-3 py-1">
          {cleanupError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{cleanupError}</p>}
          {cleanupResult && (
            <div className="iam-alert">
              {cleanupResult.dry_run
                ? <p>Preview: <strong>{cleanupResult.orphaned_roles_removed}</strong> orphaned role assignments would be removed. Nothing has been deleted.</p>
                : <p><strong>{cleanupResult.orphaned_roles_removed}</strong> orphaned role assignments were removed.</p>}
            </div>
          )}
        </div>
      </IamDialog>
    </div>
  );
}
