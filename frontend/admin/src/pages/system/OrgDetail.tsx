import { rowActivation } from '../../components/iam/rowActivation';
import { useEffect, useState, useCallback } from 'react';
import { ApiError } from '@/auth';
import { useParams, useNavigate } from 'react-router';
import { useAuth } from '@/context/AuthContext';
import {
  ArrowLeft, PauseCircle, PlayCircle, Pencil, UserPlus,
  MoreHorizontal, Shield, Trash2, FolderKanban, List, Plus,
} from 'lucide-react';
import {
  getOrg, suspendOrg, unsuspendOrg, updateOrg, deleteOrg,
  listSystemUserListMembers,
  listOrgAdmins, assignOrgAdmin,
  addUserToList, removeSystemUserFromList,
  listServiceAccounts,
  listUserLists, adminCreateUserList,
  listProjects, adminCreateProject,
} from '@/api';
import { fmtDateShort } from '@/lib/utils';
import { IamChip, IamDialog, IamMenu } from '@/components/iam';

interface Org { id: string; name: string; slug: string; active: boolean; suspended_at: string | null; created_at: string; org_list_id: string; }
interface Member { id: string; username: string; discriminator: string; email: string; active: boolean; }
interface OrgRole { id: string; user_id: string; user_name: string; user_email: string; role: string; scope_id: string | null; scope_name: string | null; granted_at: string; }
interface ServiceAccount { id: string; name: string; description: string | null; active: boolean; last_used_at: string | null; org_id: string | null; }
interface UserList { id: string; name: string; immovable: boolean; }
interface Project { id: string; name: string; slug: string; active: boolean; assigned_user_list_id: string | null; }

export default function OrgDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { isSuperAdmin } = useAuth();

  const [org, setOrg] = useState<Org | null>(null);
  const [deleteOrgOpen, setDeleteOrgOpen] = useState(false);
  const [orgListMembers, setOrgListMembers] = useState<Member[]>([]);
  const [orgRoles, setOrgRoles] = useState<OrgRole[]>([]);
  const [serviceAccounts, setServiceAccounts] = useState<ServiceAccount[]>([]);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);

  const [renameOpen, setRenameOpen] = useState(false);
  const [renameVal, setRenameVal] = useState('');

  const [addUserOpen, setAddUserOpen] = useState(false);
  const [addUserForm, setAddUserForm] = useState({ email: '', username: '', password: '' });
  const [addUserSaving, setAddUserSaving] = useState(false);
  const [addUserError, setAddUserError] = useState('');

  const [assignRoleTarget, setAssignRoleTarget] = useState<Member | null>(null);
  const [assignRoleForm, setAssignRoleForm] = useState({ role: 'org_admin', scope_id: '' });
  const [assignRoleSaving, setAssignRoleSaving] = useState(false);

  const [removeUserTarget, setRemoveUserTarget] = useState<Member | null>(null);

  const [createListOpen, setCreateListOpen] = useState(false);
  const [newListName, setNewListName] = useState('');
  const [createListSaving, setCreateListSaving] = useState(false);

  const [createProjectOpen, setCreateProjectOpen] = useState(false);
  const [newProject, setNewProject] = useState({ name: '', slug: '', redirect_uri: '', post_logout_redirect_uri: '' });
  const [createProjectSaving, setCreateProjectSaving] = useState(false);
  const [createProjectError, setCreateProjectError] = useState('');

  const load = useCallback(async () => {
    if (!id) return;
    setLoading(true);
    try {
      const o = await getOrg(id);
      setOrg(o);
      const [members, roles, sas, lists, projects] = await Promise.all([
        listSystemUserListMembers(o.org_list_id),
        listOrgAdmins(id),
        listServiceAccounts() as Promise<ServiceAccount[]>,
        listUserLists(id),
        listProjects(id),
      ]);
      setOrgListMembers(members ?? []);
      setOrgRoles(roles ?? []);
      setServiceAccounts((sas ?? []).filter((sa: ServiceAccount) => sa.org_id === id));
      const all: UserList[] = lists.user_lists ?? lists ?? [];
      setUserLists(all.filter((l: UserList) => !l.immovable));
      setProjects(projects.projects ?? projects ?? []);
    } catch (e) { console.error(e); }
    finally { setLoading(false); }
  }, [id]);

  useEffect(() => { load(); }, [load]);

  const rolesMap = orgRoles.reduce<Record<string, OrgRole[]>>((acc, r) => {
    if (!acc[r.user_id]) acc[r.user_id] = [];
    acc[r.user_id].push(r);
    return acc;
  }, {});

  const handleSuspend = async () => {
    if (!org) return;
    if (org.suspended_at) await unsuspendOrg(org.id);
    else await suspendOrg(org.id);
    load();
  };

  const handleDeleteOrg = async () => {
    if (!org) return;
    await deleteOrg(org.id);
    navigate('/system/organisations');
  };

  const handleRename = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!org) return;
    await updateOrg(org.id, { name: renameVal });
    setRenameOpen(false);
    load();
  };

  const handleAddUser = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!org) return;
    setAddUserSaving(true);
    setAddUserError('');
    try {
      await addUserToList(org.org_list_id, addUserForm);
      setAddUserOpen(false);
      setAddUserForm({ email: '', username: '', password: '' });
      load();
    } catch {
      setAddUserError('Failed to add user.');
    } finally { setAddUserSaving(false); }
  };

  const handleAssignRole = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!assignRoleTarget || !id) return;
    setAssignRoleSaving(true);
    try {
      await assignOrgAdmin(id, assignRoleTarget.id, assignRoleForm.role, assignRoleForm.scope_id || undefined);
      setAssignRoleTarget(null);
      setAssignRoleForm({ role: 'org_admin', scope_id: '' });
      load();
    } finally { setAssignRoleSaving(false); }
  };

  const handleRemoveUser = async () => {
    if (!removeUserTarget || !org) return;
    await removeSystemUserFromList(org.org_list_id, removeUserTarget.id);
    setRemoveUserTarget(null);
    load();
  };

  const handleCreateList = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!id) return;
    setCreateListSaving(true);
    try {
      await adminCreateUserList({ name: newListName, org_id: id });
      setCreateListOpen(false);
      setNewListName('');
      load();
    } finally { setCreateListSaving(false); }
  };

  const handleCreateProject = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!id) return;
    setCreateProjectSaving(true);
    setCreateProjectError('');
    try {
      await adminCreateProject(id, {
        name: newProject.name,
        slug: newProject.slug,
        redirect_uris: newProject.redirect_uri ? [newProject.redirect_uri] : [],
        post_logout_redirect_uris: newProject.post_logout_redirect_uri ? [newProject.post_logout_redirect_uri] : [],
      });
      setCreateProjectOpen(false);
      setNewProject({ name: '', slug: '', redirect_uri: '', post_logout_redirect_uri: '' });
      load();
    } catch (err) {
      const body = err instanceof ApiError ? (err.body as Record<string, string> | null) : null;
      setCreateProjectError(body?.detail ?? body?.error ?? 'Failed to create project.');
    } finally { setCreateProjectSaving(false); }
  };

  const assignedListName = (ulId: string | null) => {
    if (!ulId) return null;
    return userLists.find(ul => ul.id === ulId)?.name ?? null;
  };

  const skeletonRows = (cols: number, rows = 2) =>
    Array.from({ length: rows }, (_, i) => `sk-row-${i}`).map(rowId => (
      <tr key={rowId}>
        {Array.from({ length: cols }, (_, j) => `sk-cell-${j}`).map(cellId => (
          <td key={cellId}><div className="iam-skeleton h-4 w-full" /></td>
          ))}
      </tr>
    ));

  return (
    <div className="p-6 space-y-4">
      <button className="iam-btn iam-btn-ghost iam-btn-sm -ml-1" onClick={() => navigate('/system/organisations')}>
        <ArrowLeft className="h-4 w-4" />Back to Organisations
      </button>

      {/* ── Org Section ───────────────────────────────────────────────── */}
      <div className="rounded-xl border bg-card p-6 space-y-6">

        <div className="flex items-start justify-between gap-4">
          {loading
            ? <div className="space-y-2"><div className="iam-skeleton h-6 w-48" /><div className="iam-skeleton h-4 w-72" /></div>
            : <div>
                <h1 className="text-xl font-bold">{org?.name}</h1>
                <p className="text-sm text-muted-foreground">
                  /{org?.slug} · Created {fmtDateShort(org?.created_at ?? null)}
                </p>
              </div>
          }
          {!loading && org && (
            <div className="flex items-center gap-2 shrink-0">
              <IamChip tone={org.suspended_at ? 'danger' : 'success'}>
                {org.suspended_at ? 'Suspended' : 'Active'}
              </IamChip>
              <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={() => { setRenameVal(org.name); setRenameOpen(true); }}>
                <Pencil className="h-4 w-4" />Rename
              </button>
              {isSuperAdmin && (
                <button className="iam-btn iam-btn-secondary iam-btn-sm" onClick={handleSuspend}>
                  {org.suspended_at
                    ? <><PlayCircle className="h-4 w-4" />Unsuspend</>
                    : <><PauseCircle className="h-4 w-4" />Suspend</>
                  }
                </button>
              )}
              {isSuperAdmin && (
                <button className="iam-btn iam-btn-secondary iam-btn-sm text-destructive border-[var(--danger)] hover:bg-[var(--danger-soft)]" onClick={() => setDeleteOrgOpen(true)}>
                  <Trash2 className="h-4 w-4" />Delete
                </button>
              )}
            </div>
          )}
        </div>

        <hr className="iam-sep" />

        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Org User List</h2>
            <button className="iam-btn iam-btn-secondary iam-btn-sm" disabled={loading} onClick={() => setAddUserOpen(true)}>
              <UserPlus className="h-4 w-4" />Add User
            </button>
          </div>
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>User</th>
                <th>Email</th>
                <th>Roles</th>
                <th className="w-12"></th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return (
                  skeletonRows(4)
                );
                if (orgListMembers.length === 0) return (
                  <tr><td className="text-center text-muted-foreground py-6" colSpan={4}>No users in org list.</td></tr>
                );
                return (
                  orgListMembers.map(m => {
                      const roles = rolesMap[m.id] ?? [];
                      return (
                        <tr key={m.id}>
                          <td className="font-medium">{m.username}#{m.discriminator}</td>
                          <td className="text-sm text-muted-foreground">{m.email}</td>
                          <td>
                            {roles.length === 0
                              ? <span className="text-xs text-muted-foreground">No role</span>
                              : roles.map(r => (
                                  <IamChip className="mr-1 text-xs" tone={r.role === 'org_admin' ? 'accent' : 'default'} key={r.id}>
                                    {r.role === 'org_admin' ? 'Org Admin' : `PM: ${r.scope_name ?? '…'}`}
                                  </IamChip>
                                ))
                            }
                          </td>
                          <td>
                            <IamMenu trigger={<MoreHorizontal className="h-4 w-4" />}>
<button type="button" className="iam-menu-item" onClick={() => { setAssignRoleTarget(m); setAssignRoleForm({ role: 'org_admin', scope_id: '' }); }}>
                                  <Shield className="h-4 w-4" />Assign role
                                </button>
                                <div className="iam-menu-sep" />
                                <button type="button" className="iam-menu-item iam-menu-item-danger" onClick={() => setRemoveUserTarget(m)}>
                                  <Trash2 className="h-4 w-4" />Remove from org
                                </button>
</IamMenu>
                          </td>
                        </tr>
                      );
                    })
                );
              })()}
            </tbody>
          </table>
        </div>

        <hr className="iam-sep" />

        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Service Accounts</h2>
            <button className="iam-btn iam-btn-secondary iam-btn-sm" disabled>
              <Plus className="h-4 w-4" />New SA
            </button>
          </div>
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Name</th>
                <th>Description</th>
                <th>Status</th>
                <th>Last used</th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return (
                  skeletonRows(4, 1)
                );
                if (serviceAccounts.length === 0) return (
                  <tr><td className="text-center text-muted-foreground py-6" colSpan={4}>No service accounts.</td></tr>
                );
                return (
                  serviceAccounts.map(sa => (
                      <tr key={sa.id}>
                        <td className="font-medium">{sa.name}</td>
                        <td className="text-sm text-muted-foreground">{sa.description ?? '—'}</td>
                        <td>
                          <IamChip tone={sa.active ? 'success' : 'default'}>{sa.active ? 'Active' : 'Inactive'}</IamChip>
                        </td>
                        <td className="text-sm text-muted-foreground">
                          {sa.last_used_at ? fmtDateShort(sa.last_used_at) : '—'}
                        </td>
                      </tr>
                    ))
                );
              })()}
            </tbody>
          </table>
        </div>
      </div>

      {/* ── User Lists + Projects ──────────────────────────────────────── */}
      <div className="grid grid-cols-2 gap-4">

        <div className="rounded-xl border bg-card overflow-hidden">
          <div className="flex items-center justify-between px-4 py-3 border-b">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide flex items-center gap-2">
              <List className="h-4 w-4" />User Lists
            </h2>
            <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateListOpen(true)}>
              <Plus className="h-4 w-4" />New
            </button>
          </div>
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Name</th>
                <th>Users</th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return (
                  skeletonRows(2)
                );
                if (userLists.length === 0) return (
                  <tr><td className="text-center text-muted-foreground py-8" colSpan={2}>No user lists.</td></tr>
                );
                return (
                  userLists.map(ul => (
                      <tr key={ul.id} {...rowActivation(() => navigate(`/system/organisations/${id}/userlists/${ul.id}`))}>
                        <td className="font-medium">{ul.name}</td>
                        <td className="text-sm text-muted-foreground">—</td>
                      </tr>
                    ))
                );
              })()}
            </tbody>
          </table>
        </div>

        <div className="rounded-xl border bg-card overflow-hidden">
          <div className="flex items-center justify-between px-4 py-3 border-b">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide flex items-center gap-2">
              <FolderKanban className="h-4 w-4" />Projects
            </h2>
            <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateProjectOpen(true)}>
              <Plus className="h-4 w-4" />New
            </button>
          </div>
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>Name</th>
                <th>User List</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return (
                  skeletonRows(3)
                );
                if (projects.length === 0) return (
                  <tr><td className="text-center text-muted-foreground py-8" colSpan={3}>No projects.</td></tr>
                );
                return (
                  projects.map(p => (
                      <tr key={p.id} {...rowActivation(() => navigate(`/system/organisations/${id}/projects/${p.id}`))}>
                        <td className="font-medium">{p.name}</td>
                        <td className="text-sm text-muted-foreground">
                          {p.assigned_user_list_id
                            ? (assignedListName(p.assigned_user_list_id) ?? <span className="font-mono text-xs">{p.assigned_user_list_id.slice(0, 8)}…</span>)
                            : <span className="italic">Unassigned</span>
                          }
                        </td>
                        <td>
                          <IamChip tone={p.active ? 'success' : 'default'}>{p.active ? 'Active' : 'Draft'}</IamChip>
                        </td>
                      </tr>
                    ))
                );
              })()}
            </tbody>
          </table>
        </div>
      </div>

      {/* ── Dialogs ───────────────────────────────────────────────────── */}

      <IamDialog open={renameOpen} onClose={() => setRenameOpen(false)}
      title="Rename Organisation"
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setRenameOpen(false)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="orgdetail-form-5">Save</button></>}
    >
<form id="orgdetail-form-5" onSubmit={handleRename} className="space-y-4">
            <div className="space-y-2">
              <label className="iam-label" htmlFor="rename">Name</label>
              <input className="iam-input" id="rename" value={renameVal} onChange={e => setRenameVal(e.target.value)} required />
            </div>
            
          </form>
    </IamDialog>

      <IamDialog open={addUserOpen} onClose={() => setAddUserOpen(false)}
      title="Add User to Org List"
      desc="Creates a new user in the organisation's admin user list."
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setAddUserOpen(false)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="orgdetail-form-4" disabled={addUserSaving}>{addUserSaving ? 'Adding…' : 'Add User'}</button></>}
    >
<form id="orgdetail-form-4" onSubmit={handleAddUser} className="space-y-4">
            {addUserError && <p className="text-sm text-destructive">{addUserError}</p>}
            <div className="space-y-2"><label className="iam-label" htmlFor="org-add-user-email">Email</label><input className="iam-input" id="org-add-user-email" value={addUserForm.email} onChange={e => setAddUserForm(f => ({ ...f, email: e.target.value }))} required type="email" /></div>
            <div className="space-y-2"><label className="iam-label" htmlFor="org-add-user-username">Username</label><input className="iam-input" id="org-add-user-username" value={addUserForm.username} onChange={e => setAddUserForm(f => ({ ...f, username: e.target.value }))} required /></div>
            <div className="space-y-2"><label className="iam-label" htmlFor="org-add-user-password">Password</label><input className="iam-input" id="org-add-user-password" autoComplete="new-password" value={addUserForm.password} onChange={e => setAddUserForm(f => ({ ...f, password: e.target.value }))} required type="password" /></div>
            
          </form>
    </IamDialog>

      <IamDialog open={!!assignRoleTarget} onClose={() => (v => !v && setAssignRoleTarget(null))(false)}
      title="Assign Role"
      desc={<>Assign an admin role to {assignRoleTarget?.username}#{assignRoleTarget?.discriminator}.</>}
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setAssignRoleTarget(null)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="orgdetail-form-3" disabled={assignRoleSaving}>{assignRoleSaving ? 'Assigning…' : 'Assign'}</button></>}
    >
<form id="orgdetail-form-3" onSubmit={handleAssignRole} className="space-y-4">
            <div className="space-y-2">
              <label className="iam-label" htmlFor="org-assign-role">Role</label>
              <select className="iam-select" id="org-assign-role" value={assignRoleForm.role} onChange={e => (v => setAssignRoleForm(f => ({ ...f, role: v, scope_id: '' })))(e.target.value)}>
<option value="org_admin">Org Admin</option>
                  <option value="project_admin">Project Admin</option>
</select>
            </div>
            {assignRoleForm.role === 'project_admin' && (
              <div className="space-y-2">
                <label className="iam-label" htmlFor="org-assign-role-scope">Project (scope)</label>
                <select className="iam-select" id="org-assign-role-scope" value={assignRoleForm.scope_id} onChange={e => (v => setAssignRoleForm(f => ({ ...f, scope_id: v })))(e.target.value)}>
                  <option value="" disabled>Select a project…</option>
{projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
</select>
              </div>
            )}
            
          </form>
    </IamDialog>

      <IamDialog open={!!removeUserTarget} onClose={() => (v => !v && setRemoveUserTarget(null))(false)}
      title={<>Remove {removeUserTarget?.username}#{removeUserTarget?.discriminator}?</>}
      desc="This removes the user from the org list and permanently deletes their account."
      footer={<><button type="button" onClick={() => (v => !v && setRemoveUserTarget(null))(false)} className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-danger" onClick={handleRemoveUser}>Remove</button></>}
    >

    </IamDialog>

      <IamDialog open={createListOpen} onClose={() => setCreateListOpen(false)}
      title="New User List"
      desc="Creates a movable user list in this organisation."
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setCreateListOpen(false)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="orgdetail-form-2" disabled={createListSaving}>{createListSaving ? 'Creating…' : 'Create'}</button></>}
    >
<form id="orgdetail-form-2" onSubmit={handleCreateList} className="space-y-4">
            <div className="space-y-2">
              <label className="iam-label" htmlFor="org-new-list-name">Name</label>
              <input className="iam-input" id="org-new-list-name" value={newListName} onChange={e => setNewListName(e.target.value)} required placeholder="Acme Employees" />
            </div>
            
          </form>
    </IamDialog>

      <IamDialog open={createProjectOpen} onClose={() => setCreateProjectOpen(false)}
      title="New Project"
      desc="Create a new project in this organisation."
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setCreateProjectOpen(false)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="orgdetail-form" disabled={createProjectSaving}>{createProjectSaving ? 'Creating…' : 'Create'}</button></>}
    >
<form id="orgdetail-form" onSubmit={handleCreateProject} className="space-y-4">
            {createProjectError && <p className="text-sm text-destructive">{createProjectError}</p>}
            <div className="space-y-2">
              <label className="iam-label" htmlFor="org-new-project-name">Name</label>
              <input className="iam-input" id="org-new-project-name" value={newProject.name} onChange={e => setNewProject(p => ({ ...p, name: e.target.value }))} required placeholder="Main App" />
            </div>
            <div className="space-y-2">
              <label className="iam-label" htmlFor="org-new-project-slug">Slug</label>
              <input className="iam-input" id="org-new-project-slug" value={newProject.slug} onChange={e => setNewProject(p => ({ ...p, slug: e.target.value.toLowerCase().replaceAll(/\s+/g, '-') }))} required placeholder="main-app" pattern="[a-z0-9]+(-[a-z0-9]+)*" />
              <p className="text-xs text-muted-foreground">Lowercase letters, numbers and hyphens only.</p>
            </div>
            <div className="space-y-2">
              <label className="iam-label" htmlFor="org-new-project-redirect-uri">Redirect URI</label>
              <input className="iam-input" id="org-new-project-redirect-uri" value={newProject.redirect_uri} onChange={e => setNewProject(p => ({ ...p, redirect_uri: e.target.value }))} placeholder="https://app.example.com/callback" />
            </div>
            <div>
              <label className="iam-label" htmlFor="org-proj-logout-uri">Post-logout redirect URI</label>
              <input id="org-proj-logout-uri" className="iam-input" value={newProject.post_logout_redirect_uri} onChange={e => setNewProject(p => ({ ...p, post_logout_redirect_uri: e.target.value }))} placeholder="https://app.example.com/" />
              <p className="iam-help">Where sign-out may return the user. A target not listed here is refused, and the sign-out fails.</p>
            </div>
            
          </form>
    </IamDialog>

      <IamDialog open={deleteOrgOpen} onClose={() => setDeleteOrgOpen(false)}
      title={<>Delete organisation "{org?.name}"?</>}
      desc="All user lists, projects, and service accounts belonging to this organisation will be permanently deleted. This cannot be undone."
      footer={<><button type="button" onClick={() => setDeleteOrgOpen(false)} className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-danger" onClick={handleDeleteOrg}>Delete</button></>}
    >

    </IamDialog>
    </div>
  );
}
