import { apiFetch } from './auth';

// ── Organisations ─────────────────────────────────────────────────
export async function listOrgs() {
  return (await apiFetch('/admin/organisations')).json();
}
export async function createOrg(body: { name: string; slug: string; metadata?: Record<string, string> }) {
  return (await apiFetch('/admin/organisations', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function getOrg(id: string) {
  return (await apiFetch(`/admin/organisations/${id}`)).json();
}
export async function updateOrg(id: string, body: { name?: string; metadata?: Record<string, string> }) {
  return (await apiFetch(`/admin/organisations/${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function deleteOrg(id: string) {
  return apiFetch(`/admin/organisations/${id}`, { method: 'DELETE' });
}
export async function suspendOrg(id: string) {
  return (await apiFetch(`/admin/organisations/${id}/suspend`, { method: 'POST' })).json();
}
export async function unsuspendOrg(id: string) {
  return (await apiFetch(`/admin/organisations/${id}/unsuspend`, { method: 'POST' })).json();
}

// ── Users (global) ────────────────────────────────────────────────
export async function searchUsers(q: string) {
  return (await apiFetch(`/admin/users?q=${encodeURIComponent(q)}`)).json();
}
export async function disableUser(id: string) {
  return (await apiFetch(`/admin/users/${id}/disable`, { method: 'POST' })).json();
}
export async function enableUser(id: string) {
  return (await apiFetch(`/admin/users/${id}/enable`, { method: 'POST' })).json();
}
export async function forceLogoutUser(id: string) {
  return (await apiFetch(`/admin/users/${id}/force-logout`, { method: 'POST' })).json();
}

// ── UserLists ─────────────────────────────────────────────────────
export async function listUserLists(orgId?: string) {
  const q = orgId ? `?org_id=${orgId}` : '';
  return (await apiFetch(`/admin/userlists${q}`)).json();
}
export async function createUserList(body: { name: string; org_id: string }) {
  return (await apiFetch('/org/userlists', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function getUserList(id: string) {
  return (await apiFetch(`/org/userlists/${id}`)).json();
}
export async function deleteUserList(id: string) {
  return apiFetch(`/org/userlists/${id}`, { method: 'DELETE' });
}
export async function listUserListMembers(id: string) {
  return (await apiFetch(`/org/userlists/${id}/users`)).json();
}
export async function getSystemUserList(id: string) {
  return (await apiFetch(`/admin/userlists/${id}`)).json();
}
export async function listSystemUserListMembers(id: string) {
  return (await apiFetch(`/admin/userlists/${id}/users`)).json();
}
export async function addUserToList(listId: string, body: { email: string; username: string; password: string }) {
  return (await apiFetch(`/admin/userlists/${listId}/users`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function removeUserFromList(listId: string, userId: string) {
  return apiFetch(`/org/userlists/${listId}/users/${userId}`, { method: 'DELETE' });
}
export async function removeSystemUserFromList(listId: string, userId: string) {
  return apiFetch(`/admin/userlists/${listId}/users/${userId}`, { method: 'DELETE' });
}

// ── Projects ──────────────────────────────────────────────────────
export async function listProjects(orgId: string) {
  return (await apiFetch(`/org/projects?org_id=${orgId}`)).json();
}
export async function createProject(body: {
  org_id: string; name: string; slug: string;
  require_role_to_login?: boolean;
  redirect_uris: string[];
}) {
  return (await apiFetch('/org/projects', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function getProject(id: string) {
  return (await apiFetch(`/org/projects/${id}`)).json();
}
export async function updateProject(id: string, body: {
  name?: string; require_role_to_login?: boolean; login_theme?: Record<string, string>;
}) {
  return (await apiFetch(`/org/projects/${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function deleteProject(id: string) {
  return apiFetch(`/org/projects/${id}`, { method: 'DELETE' });
}
export async function assignUserList(projectId: string, userListId: string) {
  return (await apiFetch(`/org/projects/${projectId}/userlist`, { method: 'PUT', body: JSON.stringify({ user_list_id: userListId }) })).json();
}
export async function unassignUserList(projectId: string) {
  return apiFetch(`/org/projects/${projectId}/userlist`, { method: 'DELETE' });
}

// ── Project users & roles ─────────────────────────────────────────
export async function listProjectUsers(projectId: string) {
  return (await apiFetch(`/project/users?project_id=${projectId}`)).json();
}
export async function assignRole(projectId: string, userId: string, roleId: string) {
  return (await apiFetch(`/project/users/${userId}/roles`, { method: 'POST', body: JSON.stringify({ project_id: projectId, role_id: roleId }) })).json();
}
export async function removeRole(projectId: string, userId: string, roleId: string) {
  return apiFetch(`/project/users/${userId}/roles/${roleId}?project_id=${projectId}`, { method: 'DELETE' });
}

// ── Role definitions ──────────────────────────────────────────────
export async function listRoles(projectId: string) {
  return (await apiFetch(`/project/roles?project_id=${projectId}`)).json();
}
export async function createRole(body: { project_id: string; name: string; description?: string; rank?: number }) {
  return (await apiFetch('/project/roles', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function deleteRole(projectId: string, roleId: string) {
  return apiFetch(`/project/roles/${roleId}?project_id=${projectId}`, { method: 'DELETE' });
}

// ── Service Accounts ──────────────────────────────────────────────
export async function listServiceAccounts() {
  return (await apiFetch('/admin/service-accounts')).json();
}
export async function listOrgServiceAccounts(orgId: string) {
  return (await apiFetch(`/org/service-accounts?org_id=${orgId}`)).json();
}
export async function createServiceAccount(body: { user_list_id: string; name: string; description?: string }) {
  return (await apiFetch('/org/service-accounts', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function deleteServiceAccount(id: string) {
  return apiFetch(`/org/service-accounts/${id}`, { method: 'DELETE' });
}
export async function generatePat(saId: string, body: { name: string; expires_at?: string }) {
  return (await apiFetch(`/org/service-accounts/${saId}/pat`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function listPats(saId: string) {
  return (await apiFetch(`/org/service-accounts/${saId}/pat`)).json();
}
export async function revokePat(saId: string, patId: string) {
  return apiFetch(`/org/service-accounts/${saId}/pat/${patId}`, { method: 'DELETE' });
}

// ── Audit log ─────────────────────────────────────────────────────
export async function getAuditLog(params?: { org_id?: string; project_id?: string; limit?: number; offset?: number }) {
  const q = new URLSearchParams();
  if (params?.org_id) q.set('org_id', params.org_id);
  if (params?.project_id) q.set('project_id', params.project_id);
  if (params?.limit) q.set('limit', String(params.limit));
  if (params?.offset) q.set('offset', String(params.offset));
  return (await apiFetch(`/admin/audit-log?${q}`)).json();
}

// ── Metrics ───────────────────────────────────────────────────────
export async function getMetrics() {
  return (await apiFetch('/admin/metrics')).json();
}

// ── Hydra clients ─────────────────────────────────────────────────
export async function listHydraClients() {
  return (await apiFetch('/admin/hydra/clients')).json();
}
export async function deleteHydraClient(id: string) {
  return apiFetch(`/admin/hydra/clients/${id}`, { method: 'DELETE' });
}

// ── Org admin roles ───────────────────────────────────────────────
export async function listOrgAdmins(orgId: string) {
  return (await apiFetch(`/org/admins?org_id=${orgId}`)).json();
}
export async function assignOrgAdmin(orgId: string, userId: string, role: string, scopeId?: string) {
  return (await apiFetch(`/org/admins`, { method: 'POST', body: JSON.stringify({ org_id: orgId, user_id: userId, role, scope_id: scopeId }) })).json();
}
export async function removeOrgAdmin(orgId: string, roleId: string) {
  return apiFetch(`/org/admins/${roleId}?org_id=${orgId}`, { method: 'DELETE' });
}

// ── Admin-scoped org admin management ────────────────────────────
export async function adminListOrgAdmins(orgId: string) {
  return (await apiFetch(`/admin/organisations/${orgId}/admins`)).json();
}
export async function adminAssignOrgAdmin(orgId: string, userId: string, role: string, scopeId?: string) {
  return (await apiFetch(`/admin/organisations/${orgId}/admins`, { method: 'POST', body: JSON.stringify({ user_id: userId, role, scope_id: scopeId }) })).json();
}
export async function adminRemoveOrgAdmin(orgId: string, roleId: string) {
  return apiFetch(`/admin/organisations/${orgId}/admins/${roleId}`, { method: 'DELETE' });
}

// ── Admin-scoped org service accounts ────────────────────────────
export async function adminListOrgServiceAccounts(orgId: string) {
  return (await apiFetch(`/admin/organisations/${orgId}/service-accounts`)).json();
}

// ── Admin-scoped user list & project creation ─────────────────────
export async function adminCreateUserList(body: { name: string; org_id: string }) {
  return (await apiFetch('/admin/userlists', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function adminCreateProject(orgId: string, body: { name: string; slug: string; redirect_uris?: string[]; require_role_to_login?: boolean }) {
  return (await apiFetch(`/admin/organisations/${orgId}/projects`, { method: 'POST', body: JSON.stringify(body) })).json();
}

// ── Admin-scoped project operations ──────────────────────────────
export async function adminGetProject(id: string) {
  return (await apiFetch(`/admin/projects/${id}`)).json();
}
export async function adminUpdateProject(id: string, body: { name?: string; require_role_to_login?: boolean; allow_self_registration?: boolean; email_verification_enabled?: boolean; sms_verification_enabled?: boolean }) {
  return (await apiFetch(`/admin/projects/${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function adminAssignUserList(projectId: string, userListId: string) {
  return (await apiFetch(`/admin/projects/${projectId}/assign-userlist`, { method: 'POST', body: JSON.stringify({ user_list_id: userListId }) })).json();
}
export async function adminUnassignUserList(projectId: string) {
  return apiFetch(`/admin/projects/${projectId}/assign-userlist`, { method: 'DELETE' });
}

// ── Admin-scoped role management ──────────────────────────────────
export async function adminListRoles(projectId: string) {
  return (await apiFetch(`/admin/projects/${projectId}/roles`)).json();
}
export async function adminCreateRole(projectId: string, body: { name: string; description?: string; rank?: number }) {
  return (await apiFetch(`/admin/projects/${projectId}/roles`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function adminDeleteRole(projectId: string, roleId: string) {
  return apiFetch(`/admin/projects/${projectId}/roles/${roleId}`, { method: 'DELETE' });
}
