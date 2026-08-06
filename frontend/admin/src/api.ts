import { apiFetch } from './auth';
import type { MfaReauth } from './auth';

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
export async function updateOrg(id: string, body: { name?: string; metadata?: Record<string, string>; audit_retention_days?: number | null }) {
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
export async function adminGetUser(id: string) {
  return (await apiFetch(`/admin/users/${id}`)).json();
}
export async function adminUpdateUser(id: string, body: {
  email?: string; username?: string; display_name?: string; phone?: string;
  active?: boolean; email_verified?: boolean; clear_lock?: boolean; new_password?: string;
}) {
  return (await apiFetch(`/admin/users/${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function orgGetUser(id: string) {
  return (await apiFetch(`/org/users/${id}`)).json();
}
export async function orgUpdateUser(id: string, body: {
  email?: string; username?: string; display_name?: string; phone?: string;
  active?: boolean; email_verified?: boolean; clear_lock?: boolean; new_password?: string;
}) {
  return (await apiFetch(`/org/users/${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}

// ── UserLists ─────────────────────────────────────────────────────
export async function listUserLists(orgId?: string) {
  const q = orgId ? `?org_id=${orgId}` : '';
  return (await apiFetch(`/admin/userlists${q}`)).json();
}
// Deux routes, comme pour la lecture (`getUserList` / `getSystemUserList`) : la création n'avait
// que la variante OrgAdmin. `/org/userlists` prend l'organisation dans le JETON de l'appelant et
// ignore le corps — un super-admin, dont le jeton n'en porte aucune, y écrivait `Guid.Empty`, ce
// qui viole `FK_user_lists_organisations_OrgId` et rendait 500. Il envoyait pourtant `org_id` :
// c'est la route qui ne pouvait pas le lire.
export async function createUserList(body: { name: string }) {
  return (await apiFetch('/org/userlists', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function createSystemUserList(body: { name: string; org_id: string }) {
  return (await apiFetch('/admin/userlists', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function getUserList(id: string) {
  return (await apiFetch(`/org/userlists/${id}`)).json();
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
  // Hydra refuses any post_logout_redirect_uri the client has not whitelisted, so a project
  // created without one can be signed into and not out of.
  post_logout_redirect_uris?: string[];
}) {
  return (await apiFetch('/org/projects', { method: 'POST', body: JSON.stringify(body) })).json();
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
  email_from_name?: string; clear_email_from_name?: boolean;
  require_mfa?: boolean; check_breached_passwords?: boolean;
  ip_allowlist?: string[]; allowed_scopes?: string[];
  // Registered in Hydra rather than stored here, but written through this same route: a project's
  // redirect URIs were settable at creation and never again.
  redirect_uris?: string[]; post_logout_redirect_uris?: string[];
}) {
  return (await apiFetch(`/project/info?project_id=${id}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}

// ── SAML providers ────────────────────────────────────────────────
export async function listSamlProviders(projectId: string) {
  return (await apiFetch(`/admin/projects/${projectId}/saml-providers`)).json();
}
export async function createSamlProvider(projectId: string, body: {
  entity_id: string; metadata_url?: string; sso_url?: string; certificate_pem?: string;
  email_attribute_name?: string;
  /** Backend field is `display_name_attribute_name`; `name_attribute_name` was silently dropped. */
  display_name_attribute_name?: string;
  jit_provisioning?: boolean; default_role_id?: string;
}) {
  return (await apiFetch(`/admin/projects/${projectId}/saml-providers`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function deleteSamlProvider(projectId: string, idpId: string) {
  return apiFetch(`/admin/projects/${projectId}/saml-providers/${idpId}`, { method: 'DELETE' });
}
export async function deleteProject(id: string) {
  return apiFetch(`/org/projects/${id}`, { method: 'DELETE' });
}
export async function getProjectStats(projectId: string) {
  return (await apiFetch(`/project/stats?project_id=${projectId}`)).json();
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
export async function updateMe(body: { display_name?: string; new_device_alerts_enabled?: boolean }) {
  return (await apiFetch('/account/me', { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function getSocialAccounts() {
  return (await apiFetch('/account/social-accounts')).json();
}
export async function unlinkSocialAccount(id: string) {
  return (await apiFetch(`/account/social-accounts/${id}`, { method: 'DELETE' })).json();
}
export async function changePassword(body: { current_password: string; new_password: string }) {
  return (await apiFetch('/account/password', { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function setupPhone(phone: string) {
  return (await apiFetch('/account/mfa/phone/setup', { method: 'POST', body: JSON.stringify({ phone }) })).json();
}
export async function verifyPhone(code: string, reauth?: MfaReauth) {
  return (await apiFetch('/account/mfa/phone/verify', { method: 'POST', body: JSON.stringify({ code, reauth }) })).json();
}
// ── MFA mutations that add, replace or destroy a factor ───────────────────
/**
 * Every mutation that adds, replaces or destroys an MFA factor — this one and every other
 * `reauth?: MfaReauth` signature in this file — takes a re-authentication proof. A bearer token
 * alone must not be enough to overwrite the victim's second factor (R-24), nor to enrol the
 * attacker's own alongside it. Do not drop the parameter to simplify a call site.
 *
 * The proof is omitted on the first attempt: the backend answers 401 reauthentication_required
 * with the methods this account can actually supply, and only then is the user prompted.
 * Enrolling the FIRST factor on an account that has none needs no proof. See ReauthDialog.
 */
export async function removePhone(reauth?: MfaReauth) {
  return apiFetch('/account/mfa/phone', { method: 'DELETE', body: JSON.stringify(reauth ?? {}) });
}

// ── WebAuthn / Passkeys ────────────────────────────────────────────────────
export async function beginWebAuthnRegistration() {
  return (await apiFetch('/account/mfa/webauthn/register/begin', { method: 'POST' })).json();
}
export async function completeWebAuthnRegistration(body: Record<string, unknown>, reauth?: MfaReauth) {
  return (await apiFetch('/account/mfa/webauthn/register/complete', { method: 'POST', body: JSON.stringify({ ...body, reauth }) })).json();
}
export async function listWebAuthnCredentials() {
  return (await apiFetch('/account/mfa/webauthn/credentials')).json();
}
export async function deleteWebAuthnCredential(id: string, reauth?: MfaReauth) {
  return apiFetch(`/account/mfa/webauthn/credentials/${id}`, { method: 'DELETE', body: JSON.stringify(reauth ?? {}) });
}

// ── SA API keys (JWK) ─────────────────────────────────────────────────────
export async function getSaApiKeys(saId: string) {
  return (await apiFetch(`/service-accounts/${saId}/api-keys`)).json();
}
export async function addSaApiKey(saId: string, jwk: JsonWebKey) {
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
export async function confirmTotp(body: { code: string }, reauth?: MfaReauth) {
  return (await apiFetch('/account/mfa/totp/confirm', { method: 'POST', body: JSON.stringify({ ...body, reauth }) })).json();
}
export async function regenerateBackupCodes(reauth?: MfaReauth) {
  return (await apiFetch('/account/mfa/backup-codes', { method: 'POST', body: JSON.stringify(reauth ?? {}) })).json();
}

// ── Audit log ─────────────────────────────────────────────────────
/**
 * The audit log for one scope.
 *
 * `scope: 'org'` is not a convenience — it is a different route. `/admin/audit-log` is
 * super-admin-only and binds nothing but limit/offset, so passing `org_id` to it filtered
 * nothing: an org admin got a 403, and a super admin browsing an organisation saw *every*
 * tenant's entries under a heading that said otherwise.
 */
export async function getAuditLog(
  params?: { org_id?: string; project_id?: string; limit?: number; offset?: number; scope?: 'system' | 'org' },
) {
  const q = new URLSearchParams();
  if (params?.project_id) q.set('project_id', params.project_id);
  if (params?.limit) q.set('limit', String(params.limit));
  if (params?.offset) q.set('offset', String(params.offset));
  if (params?.scope === 'org') {
    return (await apiFetch(`/org/audit-log?${q}`)).json();
  }
  if (params?.org_id) q.set('org_id', params.org_id);
  return (await apiFetch(`/admin/audit-log?${q}`)).json();
}

// ── Metrics ───────────────────────────────────────────────────────
export async function getMetrics() {
  return (await apiFetch('/admin/metrics')).json();
}

// ── Org info (update) ─────────────────────────────────────────────
export async function updateOrgInfo(patch: { audit_retention_days?: number | null }) {
  // PATCH /org/settings, not /org/info: the latter is registered GET-only, so this answered 405
  // and the caller — which has no catch — showed nothing. Retention could never be saved.
  return (await apiFetch('/org/settings', { method: 'PATCH', body: JSON.stringify(patch) })).json();
}

// ── Webhooks ──────────────────────────────────────────────────────
export async function listWebhooks() {
  return (await apiFetch('/org/webhooks')).json();
}
export async function createWebhook(body: { url: string; events: string[] }) {
  return (await apiFetch('/org/webhooks', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function updateWebhook(id: string, patch: { active?: boolean; url?: string; events?: string[] }) {
  return (await apiFetch(`/org/webhooks/${id}`, { method: 'PATCH', body: JSON.stringify(patch) })).json();
}
export async function deleteWebhook(id: string) {
  return apiFetch(`/org/webhooks/${id}`, { method: 'DELETE' });
}
export async function testWebhook(id: string) {
  return (await apiFetch(`/org/webhooks/${id}/test`, { method: 'POST' })).json();
}
export async function listWebhookDeliveries(id: string) {
  return (await apiFetch(`/org/webhooks/${id}/deliveries`)).json();
}

// ── Data export ───────────────────────────────────────────────────
export async function exportUserList(listId: string): Promise<Blob> {
  return (await apiFetch(`/org/userlists/${listId}/export?format=csv`)).blob();
}
/**
 * The two branches below are not symmetrical: the org-scoped route is `/org/audit-log/export`
 * (OrgController), not `/org/export/audit-log`. Do not "tidy" it to match the system-scoped path.
 */
export async function exportOrgAuditLog(orgId: string, isSystemCtx: boolean): Promise<Blob> {
  const path = isSystemCtx
    ? `/admin/organizations/${orgId}/export/audit-log?format=csv`
    : `/org/audit-log/export?format=csv`;
  return (await apiFetch(path)).blob();
}
export async function exportSystemAuditLog(): Promise<Blob> {
  return (await apiFetch('/admin/audit-log/export?format=csv')).blob();
}

// ── Org-list manager (org-scoped) ─────────────────────────────────
export async function listOrgListManagers() {
  return (await apiFetch('/org/admins')).json();
}
export async function assignOrgListManager(body: { user_id: string; role: string; scope_id?: string }) {
  return (await apiFetch('/org/admins', { method: 'POST', body: JSON.stringify(body) })).json();
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
export async function adminCreateProject(orgId: string, body: { name: string; slug: string; redirect_uris?: string[]; post_logout_redirect_uris?: string[]; require_role_to_login?: boolean }) {
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

export async function adminDeleteProject(projectId: string) {
  return apiFetch(`/admin/projects/${projectId}`, { method: 'DELETE' });
}

// ── User management actions ───────────────────────────────────────
export async function resendInvite(listId: string, userId: string) {
  return (await apiFetch(`/org/userlists/${listId}/users/${userId}/resend-invite`, { method: 'POST' })).json();
}
export async function unlockUser(listId: string | null, userId: string) {
  const path = listId
    ? `/org/userlists/${listId}/users/${userId}/unlock`
    : `/admin/users/${userId}/unlock`;
  return (await apiFetch(path, { method: 'POST' })).json();
}
export async function getUserSessions(listId: string | null, userId: string) {
  const path = listId
    ? `/org/userlists/${listId}/users/${userId}/sessions`
    : `/admin/users/${userId}/sessions`;
  return (await apiFetch(path)).json();
}
export async function revokeAllUserSessions(listId: string | null, userId: string) {
  const path = listId
    ? `/org/userlists/${listId}/users/${userId}/sessions`
    : `/admin/users/${userId}/sessions`;
  return apiFetch(path, { method: 'DELETE' });
}

// ── Email overview (super admin) ─────────────────────────────────
export async function getEmailOverview() {
  return (await apiFetch('/admin/email/overview')).json();
}

// ── Org SMTP (org admin) ──────────────────────────────────────────
export async function getOrgSmtp() {
  return (await apiFetch('/org/smtp')).json();
}
export async function upsertOrgSmtp(body: { host: string; port: number; start_tls: boolean; username?: string; password?: string; from_address: string; from_name: string }) {
  return (await apiFetch('/org/smtp', { method: 'PUT', body: JSON.stringify(body) })).json();
}
export async function deleteOrgSmtp() {
  return apiFetch('/org/smtp', { method: 'DELETE' });
}
export async function testOrgSmtp() {
  return (await apiFetch('/org/smtp/test', { method: 'POST' })).json();
}

// ── Org SMTP (super admin) ────────────────────────────────────────
export async function adminGetOrgSmtp(orgId: string) {
  return (await apiFetch(`/admin/organizations/${orgId}/smtp`)).json();
}
export async function adminUpsertOrgSmtp(orgId: string, body: { host: string; port: number; start_tls: boolean; username?: string; password?: string; from_address: string; from_name: string }) {
  return (await apiFetch(`/admin/organizations/${orgId}/smtp`, { method: 'PUT', body: JSON.stringify(body) })).json();
}
export async function adminDeleteOrgSmtp(orgId: string) {
  return apiFetch(`/admin/organizations/${orgId}/smtp`, { method: 'DELETE' });
}
export async function adminTestOrgSmtp(orgId: string) {
  return (await apiFetch(`/admin/organizations/${orgId}/smtp/test`, { method: 'POST' })).json();
}

export async function getSystemHealth() {
  return (await apiFetch('/admin/system/health')).json();
}

// ── Impersonation (0.7.0) ─────────────────────────────────────────────────
// Opening a session is refused to a browser by design — it mints a credential, and that gate is
// what keeps this surface from being an oracle. The console supervises: it lists and it revokes.
export async function listImpersonations(actorId?: string) {
  const q = actorId ? `?actor_id=${encodeURIComponent(actorId)}` : '';
  return (await apiFetch(`/admin/impersonate${q}`)).json();
}
export async function revokeImpersonation(sessionId: string) {
  return apiFetch(`/admin/impersonate/${sessionId}/revoke`, { method: 'POST' });
}
