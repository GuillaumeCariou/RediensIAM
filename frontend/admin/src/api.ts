import { apiFetch } from './auth';

// ── Organisations ─────────────────────────────────────────────────
export async function listOrgs() {
  return (await apiFetch('/admin/organizations')).json();
}
export async function createOrg(body: { name: string; slug: string; metadata?: Record<string, string> }) {
  return (await apiFetch('/admin/organizations', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function getOrg(id: string) {
  return (await apiFetch(`/admin/organizations/${id}`)).json();
}
export async function getOrgInfo() {
  return (await apiFetch('/org/info')).json();
}
export async function updateOrg(id: string, body: { name?: string; metadata?: Record<string, string> }) {
  return (await apiFetch(`/admin/organizations/${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function deleteOrg(id: string) {
  return apiFetch(`/admin/organizations/${id}`, { method: 'DELETE' });
}
export async function suspendOrg(id: string) {
  return (await apiFetch(`/admin/organizations/${id}/suspend`, { method: 'POST' })).json();
}
export async function unsuspendOrg(id: string) {
  return (await apiFetch(`/admin/organizations/${id}/unsuspend`, { method: 'POST' })).json();
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
  return (await apiFetch(`/admin/users/${id}/sessions`, { method: 'DELETE' })).json();
}
export async function adminGetUser(id: string) {
  return (await apiFetch(`/admin/users/${id}`)).json();
}
export async function adminUpdateUser(id: string, body: {
  email?: string; username?: string; display_name?: string; phone?: string;
  active?: boolean; email_verified?: boolean; clear_lock?: boolean; new_password?: string;
}) {
  return (await apiFetch(`/admin/users/${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
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
export async function addUserToList(listId: string, body: { email: string; username: string; password: string; email_verified?: boolean }) {
  return (await apiFetch(`/admin/userlists/${listId}/users`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function removeUserFromList(listId: string, userId: string) {
  return apiFetch(`/org/userlists/${listId}/users/${userId}`, { method: 'DELETE' });
}
export async function cleanupUserList(listId: string, body: { remove_orphaned_roles?: boolean; remove_inactive_users?: boolean; inactive_threshold_days?: number; dry_run?: boolean }) {
  return (await apiFetch(`/org/userlists/${listId}/cleanup`, { method: 'POST', body: JSON.stringify(body) })).json();
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
export async function getProjectInfo(projectId: string) {
  return (await apiFetch(`/project/info?project_id=${projectId}`)).json();
}
export async function updateProject(id: string, body: {
  name?: string; require_role_to_login?: boolean; allow_self_registration?: boolean;
  email_verification_enabled?: boolean; sms_verification_enabled?: boolean; active?: boolean;
  allowed_email_domains?: string[]; default_role_id?: string; clear_default_role?: boolean;
  login_theme?: Record<string, unknown>; min_password_length?: number;
  password_require_uppercase?: boolean; password_require_lowercase?: boolean;
  password_require_digit?: boolean; password_require_special?: boolean;
}) {
  return (await apiFetch(`/project/info?project_id=${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function deleteProject(id: string) {
  return apiFetch(`/org/projects/${id}`, { method: 'DELETE' });
}
export async function getProjectStats(projectId: string) {
  return (await apiFetch(`/project/stats?project_id=${projectId}`)).json();
}
export async function createProjectUser(projectId: string, body: { email: string; username?: string; password: string }) {
  return (await apiFetch(`/project/users?project_id=${projectId}`, { method: 'POST', body: JSON.stringify(body) })).json();
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
  return (await apiFetch(`/project/users/${userId}/roles?project_id=${projectId}`, { method: 'POST', body: JSON.stringify({ role_id: roleId }) })).json();
}
export async function removeRole(projectId: string, userId: string, roleId: string) {
  return apiFetch(`/project/users/${userId}/roles/${roleId}?project_id=${projectId}`, { method: 'DELETE' });
}
export async function forceLogoutProjectUser(projectId: string, userId: string) {
  return apiFetch(`/project/users/${userId}/sessions?project_id=${projectId}`, { method: 'DELETE' });
}

// ── Role definitions ──────────────────────────────────────────────
export async function listRoles(projectId: string) {
  return (await apiFetch(`/project/roles?project_id=${projectId}`)).json();
}
export async function createRole(projectId: string, body: { name: string; description?: string; rank?: number }) {
  return (await apiFetch(`/project/roles?project_id=${projectId}`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function deleteRole(projectId: string, roleId: string) {
  return apiFetch(`/project/roles/${roleId}?project_id=${projectId}`, { method: 'DELETE' });
}

// ── Service Accounts (unified) ────────────────────────────────────
export async function listServiceAccounts() {
  return (await apiFetch('/service-accounts')).json();
}
export async function createServiceAccount(body: { user_list_id: string; name: string; description?: string }) {
  return (await apiFetch('/service-accounts', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function getServiceAccount(id: string) {
  return (await apiFetch(`/service-accounts/${id}`)).json();
}
export async function deleteServiceAccount(id: string) {
  return apiFetch(`/service-accounts/${id}`, { method: 'DELETE' });
}
export async function listPats(saId: string) {
  return (await apiFetch(`/service-accounts/${saId}/pat`)).json();
}
export async function generatePat(saId: string, body: { name: string; expires_at?: string }) {
  return (await apiFetch(`/service-accounts/${saId}/pat`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function revokePat(saId: string, patId: string) {
  return apiFetch(`/service-accounts/${saId}/pat/${patId}`, { method: 'DELETE' });
}
export async function listSaRoles(saId: string) {
  return (await apiFetch(`/service-accounts/${saId}/roles`)).json();
}
export async function assignSaRole(saId: string, body: { role: string; org_id?: string; project_id?: string }) {
  return (await apiFetch(`/service-accounts/${saId}/roles`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function removeSaRole(saId: string, roleId: string) {
  return apiFetch(`/service-accounts/${saId}/roles/${roleId}`, { method: 'DELETE' });
}

// ── Account (self) ────────────────────────────────────────────────
export async function getSessions() {
  return (await apiFetch('/account/sessions')).json();
}
export async function revokeSession(clientId: string) {
  return apiFetch(`/account/sessions/${encodeURIComponent(clientId)}`, { method: 'DELETE' });
}
export async function revokeAllSessions() {
  return apiFetch('/account/sessions', { method: 'DELETE' });
}
export async function getMe() {
  return (await apiFetch('/account/me')).json();
}
export async function updateMe(body: { display_name?: string }) {
  return (await apiFetch('/account/me', { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function changePassword(body: { current_password: string; new_password: string }) {
  return (await apiFetch('/account/password', { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function setupPhone(phone: string) {
  return (await apiFetch('/account/mfa/phone/setup', { method: 'POST', body: JSON.stringify({ phone }) })).json();
}
export async function verifyPhone(code: string) {
  return (await apiFetch('/account/mfa/phone/verify', { method: 'POST', body: JSON.stringify({ code }) })).json();
}
export async function removePhone() {
  return apiFetch('/account/mfa/phone', { method: 'DELETE' });
}

// ── WebAuthn / Passkeys ────────────────────────────────────────────────────
export async function beginWebAuthnRegistration() {
  return (await apiFetch('/account/mfa/webauthn/register/begin', { method: 'POST' })).json();
}
export async function completeWebAuthnRegistration(body: object) {
  return (await apiFetch('/account/mfa/webauthn/register/complete', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function listWebAuthnCredentials() {
  return (await apiFetch('/account/mfa/webauthn/credentials')).json();
}
export async function deleteWebAuthnCredential(id: string) {
  return apiFetch(`/account/mfa/webauthn/credentials/${id}`, { method: 'DELETE' });
}

// ── SA API keys (JWK) ─────────────────────────────────────────────────────
export async function getSaApiKeys(saId: string) {
  return (await apiFetch(`/service-accounts/${saId}/api-keys`)).json();
}
export async function addSaApiKey(saId: string, jwk: object) {
  return (await apiFetch(`/service-accounts/${saId}/api-keys`, { method: 'POST', body: JSON.stringify({ jwk }) })).json();
}
export async function removeSaApiKey(saId: string) {
  return (await apiFetch(`/service-accounts/${saId}/api-keys`, { method: 'DELETE' })).json();
}
export async function getMfaStatus() {
  return (await apiFetch('/account/mfa')).json();
}
export async function setupTotp() {
  return (await apiFetch('/account/mfa/totp/setup', { method: 'POST' })).json();
}
export async function confirmTotp(body: { code: string }) {
  return (await apiFetch('/account/mfa/totp/confirm', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function regenerateBackupCodes() {
  return (await apiFetch('/account/mfa/backup-codes', { method: 'POST' })).json();
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

// ── Org-list manager (org-scoped) ─────────────────────────────────
export async function listOrgListManagers() {
  return (await apiFetch('/org/admins')).json();
}
export async function assignOrgListManager(body: { user_id: string; role: string; scope_id?: string }) {
  return (await apiFetch('/org/admins', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function updateOrgListManager(id: string, body: { role?: string; scope_id?: string }) {
  return (await apiFetch(`/org/admins/${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function removeOrgListManager(id: string) {
  return apiFetch(`/org/admins/${id}`, { method: 'DELETE' });
}

// ── Org admin roles ───────────────────────────────────────────────
export async function listOrgAdmins(orgId: string) {
  return (await apiFetch(`/admin/organizations/${orgId}/admins`)).json();
}
export async function assignOrgAdmin(orgId: string, userId: string, role: string, scopeId?: string) {
  return (await apiFetch(`/admin/organizations/${orgId}/admins`, { method: 'POST', body: JSON.stringify({ user_id: userId, role, scope_id: scopeId }) })).json();
}
export async function removeOrgAdmin(orgId: string, roleId: string) {
  return apiFetch(`/admin/organizations/${orgId}/admins/${roleId}`, { method: 'DELETE' });
}

// ── Admin-scoped user list & project creation ─────────────────────
export async function adminCreateUserList(body: { name: string; org_id: string }) {
  return (await apiFetch('/admin/userlists', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function listAdminOrgProjects(orgId: string) {
  return (await apiFetch(`/org/projects?org_id=${orgId}`)).json();
}
export async function adminCreateProject(orgId: string, body: { name: string; slug: string; redirect_uris?: string[]; require_role_to_login?: boolean }) {
  return (await apiFetch(`/admin/organizations/${orgId}/projects`, { method: 'POST', body: JSON.stringify(body) })).json();
}

// ── Admin-scoped project operations ──────────────────────────────
export async function adminListAllProjects() {
  return (await apiFetch('/admin/projects')).json();
}
export async function adminGetProject(id: string) {
  return (await apiFetch(`/org/projects/${id}`)).json();
}
export async function adminGetProjectStats(id: string) {
  return (await apiFetch(`/admin/projects/${id}/stats`)).json();
}
export async function adminAssignUserList(projectId: string, userListId: string) {
  return (await apiFetch(`/admin/projects/${projectId}/userlist`, { method: 'PUT', body: JSON.stringify({ user_list_id: userListId }) })).json();
}
export async function adminUnassignUserList(projectId: string) {
  return apiFetch(`/admin/projects/${projectId}/userlist`, { method: 'DELETE' });
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
export async function adminDeleteProject(projectId: string) {
  return apiFetch(`/admin/projects/${projectId}`, { method: 'DELETE' });
}
