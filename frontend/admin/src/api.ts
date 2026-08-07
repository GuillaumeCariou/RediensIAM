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
// Mêmes deux portées que la création, et pour la même raison : `/org/userlists/{id}` filtre sur
// l'organisation du JETON, qu'un super-admin n'a pas — il ne pouvait donc supprimer aucune liste,
// et celles sans organisation n'étaient supprimables par personne.
export async function deleteUserList(id: string) {
  return apiFetch(`/org/userlists/${id}`, { method: 'DELETE' });
}
export async function deleteSystemUserList(id: string) {
  return apiFetch(`/admin/userlists/${id}`, { method: 'DELETE' });
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
/**
 * Les deux mêmes opérations en portée organisation. `addUserToList` et `adminUpdateUser` ne
 * portent que `/admin`, réservé au super admin : un org_admin recevait 403 en ajoutant ou en
 * éditant un membre de sa PROPRE liste. La lecture (`listUserListMembers`), le retrait et le
 * déverrouillage avaient leur variante `/org` depuis le début — l'écriture, non.
 *
 * L'organisation vient du JETON de l'appelant, jamais du corps : c'est ce qui empêche un
 * administrateur d'en nommer une autre.
 */
export async function orgAddUserToList(listId: string, body: { email: string; username: string; password: string; email_verified?: boolean }) {
  return (await apiFetch(`/org/userlists/${listId}/users`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function orgUpdateListUser(listId: string, userId: string, body: {
  email?: string; username?: string; display_name?: string; phone?: string;
  active?: boolean; email_verified?: boolean; clear_lock?: boolean; new_password?: string;
}) {
  return (await apiFetch(`/org/userlists/${listId}/users/${userId}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
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
/**
 * The same action on the system surface, where the organisation comes from the URL.
 *
 * `/org/projects` reads the tenant from the CALLER's token, and a super-admin's token names none —
 * so a super-admin creating a project inside a tenant sent `org_id` in a body nobody read, the
 * insert went in with an empty organisation, and the foreign key answered with a 500. The console
 * already draws this distinction for user lists (`createSystemUserList`); projects never got it.
 */
export async function createSystemProject(orgId: string, body: {
  name: string; slug: string;
  require_role_to_login?: boolean;
  redirect_uris: string[];
  post_logout_redirect_uris?: string[];
}) {
  return (await apiFetch(`/admin/organizations/${orgId}/projects`, {
    method: 'POST', body: JSON.stringify(body),
  })).json();
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
export interface SamlProviderCreate {
  entity_id: string; metadata_url?: string; sso_url?: string; certificate_pem?: string;
  email_attribute_name?: string;
  /** Backend field is `display_name_attribute_name`; `name_attribute_name` was silently dropped. */
  display_name_attribute_name?: string;
  jit_provisioning?: boolean; default_role_id?: string;
}
/**
 * What both PATCH routes accept (`SamlProviderInput`): every field optional, and an unmentioned one
 * is left alone. `active` exists only here — creation always enables, so it is not a create field.
 */
export type SamlProviderPatch = Partial<SamlProviderCreate> & { active?: boolean };
export async function createSamlProvider(projectId: string, body: SamlProviderCreate) {
  return (await apiFetch(`/admin/projects/${projectId}/saml-providers`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function updateSamlProvider(projectId: string, idpId: string, body: SamlProviderPatch) {
  return (await apiFetch(`/admin/projects/${projectId}/saml-providers/${idpId}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function deleteSamlProvider(projectId: string, idpId: string) {
  return apiFetch(`/admin/projects/${projectId}/saml-providers/${idpId}`, { method: 'DELETE' });
}
// The same four operations in the organisation scope. The `/admin` routes require system
// authority, which an org_admin's token does not carry: their own project's SAML configuration
// answered 403 on every request.
export async function orgListSamlProviders(projectId: string) {
  return (await apiFetch(`/org/projects/${projectId}/saml-providers`)).json();
}
export async function orgCreateSamlProvider(projectId: string, body: SamlProviderCreate) {
  return (await apiFetch(`/org/projects/${projectId}/saml-providers`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function orgUpdateSamlProvider(projectId: string, idpId: string, body: SamlProviderPatch) {
  return (await apiFetch(`/org/projects/${projectId}/saml-providers/${idpId}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function orgDeleteSamlProvider(projectId: string, idpId: string) {
  return apiFetch(`/org/projects/${projectId}/saml-providers/${idpId}`, { method: 'DELETE' });
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

// ── OAuth2 scopes ─────────────────────────────────────────────────
// The PUT replaces the whole custom list; the built-in scopes come back on the GET and are never
// part of what is sent.
export async function getProjectScopes(projectId: string) {
  return (await apiFetch(`/org/projects/${projectId}/scopes`)).json();
}
export async function updateProjectScopes(projectId: string, scopes: string[]) {
  return (await apiFetch(`/org/projects/${projectId}/scopes`, { method: 'PUT', body: JSON.stringify({ scopes }) })).json();
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
/**
 * Un membre du projet, vu depuis la portée projet. `/org/userlists/{id}/users` dit la même chose
 * mais est gardé en OrgAdmin : c'est cette route-ci, et elle seule, qu'un project_admin peut lire.
 * Le serveur ne la sert que si l'utilisateur appartient à la liste assignée au projet.
 */
/**
 * Crée un compte dans la liste assignée au projet, depuis la portée projet.
 *
 * La console ne savait le faire que par `/admin/userlists/{id}/users`, hors de portée d'un
 * project_admin. Le serveur applique ici la politique de mot de passe du projet et refuse en
 * `password_too_short` (avec `min_length`), `email_already_exists` ou `no_user_list`.
 */
export async function createProjectUser(projectId: string, body: {
  email: string; password: string; username?: string; display_name?: string;
}) {
  return (await apiFetch(`/project/users?project_id=${projectId}`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function getProjectUser(projectId: string, userId: string) {
  return (await apiFetch(`/project/users/${userId}?project_id=${projectId}`)).json();
}
/** Coupe toutes les sessions Hydra du membre. Il devra se reconnecter partout. */
export async function revokeProjectUserSessions(projectId: string, userId: string) {
  return (await apiFetch(`/project/users/${userId}/sessions?project_id=${projectId}`, { method: 'DELETE' })).json();
}
/**
 * Retire les attributions de rôle dont le compte n'est plus dans la liste assignée. `dry_run`
 * compte sans rien supprimer — la console le propose d'abord et n'exécute qu'après.
 */
export async function cleanupProject(projectId: string, dryRun: boolean) {
  return (await apiFetch(`/project/cleanup?project_id=${projectId}`, { method: 'POST', body: JSON.stringify({ dry_run: dryRun }) })).json();
}
/** Le journal du projet. Route distincte de `/admin` et `/org`, la seule ouverte au project_admin. */
export async function getProjectAuditLog(projectId: string, params?: { limit?: number; offset?: number }) {
  const q = new URLSearchParams({ project_id: projectId });
  if (params?.limit) q.set('limit', String(params.limit));
  if (params?.offset) q.set('offset', String(params.offset));
  return (await apiFetch(`/project/audit-log?${q}`)).json();
}

// ── Role definitions ──────────────────────────────────────────────
export async function listRoles(projectId: string) {
  return (await apiFetch(`/project/roles?project_id=${projectId}`)).json();
}
export async function createRole(projectId: string, body: { name: string; description?: string; rank?: number }) {
  return (await apiFetch(`/project/roles?project_id=${projectId}`, { method: 'POST', body: JSON.stringify(body) })).json();
}
// The name is not editable: it is the Keto relation tuple written for every holder of the role,
// so renaming it here would leave those tuples pointing at a role that no longer answers.
export async function updateRole(projectId: string, roleId: string, body: { description?: string; rank?: number }) {
  return (await apiFetch(`/project/roles/${roleId}?project_id=${projectId}`, { method: 'PATCH', body: JSON.stringify(body) })).json();
}
export async function deleteRole(projectId: string, roleId: string) {
  return apiFetch(`/project/roles/${roleId}?project_id=${projectId}`, { method: 'DELETE' });
}
// The same three operations in system scope. `/project/roles` resolves the project from the
// caller's token or from `?project_id=`, which a super admin browsing another tenant does not
// carry; these take the project in the path instead.
export async function adminListRoles(projectId: string) {
  return (await apiFetch(`/admin/projects/${projectId}/roles`)).json();
}
export async function adminCreateRole(projectId: string, body: { name: string; description?: string; rank?: number }) {
  return (await apiFetch(`/admin/projects/${projectId}/roles`, { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function adminDeleteRole(projectId: string, roleId: string) {
  return apiFetch(`/admin/projects/${projectId}/roles/${roleId}`, { method: 'DELETE' });
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
/**
 * Les rôles seuls. `getServiceAccount` les renvoie déjà, mais il ramène aussi les PAT et la liste
 * d'accueil : recharger tout le compte après une assignation faisait clignoter la page entière.
 */
export async function listSaRoles(saId: string) {
  return (await apiFetch(`/service-accounts/${saId}/roles`)).json();
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
/**
 * Le détail d'un webhook : l'URL et la liste d'événements entières, que le tableau tronque, plus
 * ses dix dernières livraisons. Aucun appelant jusqu'ici — la console ne lisait que la liste.
 */
export async function getWebhook(id: string) {
  return (await apiFetch(`/org/webhooks/${id}`)).json();
}
export async function updateWebhook(id: string, patch: { active?: boolean; url?: string; events?: string[] }) {
  return (await apiFetch(`/org/webhooks/${id}`, { method: 'PATCH', body: JSON.stringify(patch) })).json();
}
export async function deleteWebhook(id: string) {
  return apiFetch(`/org/webhooks/${id}`, { method: 'DELETE' });
}
/**
 * Refrappe le secret de signature. Le serveur le renvoie en clair une seule fois — il ne stocke
 * que sa forme chiffrée et aucune route ne le relit. L'appelant doit donc le montrer à
 * l'opérateur, pas seulement recharger la liste.
 */
export async function rotateWebhookSecret(id: string) {
  return (await apiFetch(`/org/webhooks/${id}/rotate-secret`, { method: 'POST' })).json();
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
/**
 * L'export des comptes d'une organisation. Le journal d'audit avait le sien depuis le début, pas
 * celui-ci — même route, même limite de débit côté serveur, aucun appelant.
 */
export async function exportOrgUsers(orgId: string): Promise<Blob> {
  return (await apiFetch(`/admin/organizations/${orgId}/export/users?format=csv`)).blob();
}

// ── Org-list manager (org-scoped) ─────────────────────────────────
export async function listOrgListManagers() {
  return (await apiFetch('/org/admins')).json();
}
export async function assignOrgListManager(body: { user_id: string; role: string; scope_id?: string }) {
  return (await apiFetch('/org/admins', { method: 'POST', body: JSON.stringify(body) })).json();
}
/**
 * Change le rang d'une délégation, ou le projet qu'elle porte. Sans elle, corriger un
 * project_admin nommé sur le mauvais projet demandait de révoquer puis de réassigner — deux
 * écritures Keto et une fenêtre où la personne n'avait plus rien.
 *
 * Le serveur refuse de modifier sa propre délégation et tout rang supérieur à celui de l'appelant.
 * Il n'existe aucune contrepartie `/admin` : cette route est la seule porte des deux portées.
 */
export async function updateOrgListManager(id: string, body: { role?: string; scope_id?: string | null }) {
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
export async function adminCreateProject(orgId: string, body: { name: string; slug: string; redirect_uris?: string[]; post_logout_redirect_uris?: string[]; require_role_to_login?: boolean }) {
  return (await apiFetch(`/admin/organizations/${orgId}/projects`, { method: 'POST', body: JSON.stringify(body) })).json();
}

// ── Admin-scoped project operations ──────────────────────────────
export async function adminListAllProjects() {
  return (await apiFetch('/admin/projects')).json();
}
// The projects of one organisation, in system scope. `listProjects` reaches the same rows through
// `/org/projects?org_id=`, but only via the super-admin escape branch of an OrgAdmin-gated
// controller; a system screen asks the system route.
export async function adminListOrgProjects(orgId: string) {
  return (await apiFetch(`/admin/organizations/${orgId}/projects`)).json();
}
export async function adminGetProject(id: string) {
  return (await apiFetch(`/admin/projects/${id}`)).json();
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
// The org GET filters on the caller's own organisation with no super-admin escape, so a super
// admin reading another tenant's project has to come through here.
export async function adminGetProjectScopes(projectId: string) {
  return (await apiFetch(`/admin/projects/${projectId}/scopes`)).json();
}
export async function adminUpdateProjectScopes(projectId: string, scopes: string[]) {
  return (await apiFetch(`/admin/projects/${projectId}/scopes`, { method: 'PUT', body: JSON.stringify({ scopes }) })).json();
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

// ── Audit chain integrity (S-3) ───────────────────────────────────────────
// One walk per organisation plus the deployment-wide chain. The link is an HMAC whose key lives
// outside the database, so `first_break` names a row that is not what it was written as — or the
// first survivor of a deletion. `unverifiable` is neither pass nor fail: rows written before the
// chain was keyed, or under a key this deployment no longer holds.
export async function verifyAuditChain() {
  return (await apiFetch('/admin/audit-chain')).json();
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

// ── OAuth2 clients (Hydra, super admin) ───────────────────────────────────
// The list is Hydra's own, not a projection: it also contains the clients this console creates
// for a project (`client_` prefix) and for a service account (`sa_`), which is why the page marks
// them and why deleting one is not a console-only change.
export async function listHydraClients() {
  return (await apiFetch('/admin/hydra/clients')).json();
}
export async function createHydraClient(body: {
  client_name: string; grant_types: string[]; redirect_uris: string[];
  scope?: string; client_id?: string; post_logout_redirect_uris?: string[];
}) {
  return (await apiFetch('/admin/hydra/clients', { method: 'POST', body: JSON.stringify(body) })).json();
}
export async function getHydraClient(id: string) {
  return (await apiFetch(`/admin/hydra/clients/${encodeURIComponent(id)}`)).json();
}
export async function deleteHydraClient(id: string) {
  return apiFetch(`/admin/hydra/clients/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

// ── Deployment settings (the instances row) ───────────────────────────────
export async function getInstanceConfig() {
  return (await apiFetch('/admin/instance')).json();
}
export async function updateInstanceConfig(body: Record<string, unknown>) {
  return (await apiFetch('/admin/instance', { method: 'PATCH', body: JSON.stringify(body) })).json();
}

// ── Grant reconciliation (S-8) ────────────────────────────────────────────
export async function scanGrantReconcile() {
  return (await apiFetch('/admin/grant-reconcile')).json();
}
/**
 * Refuses in the body, not in the status: a divergence above the server's bound comes back 200 with
 * `repair_refused` set and nothing written. A caller that only watches for a rejection reads that
 * as a successful repair of zero grants.
 */
export async function repairGrantReconcile() {
  return (await apiFetch('/admin/grant-reconcile/repair', { method: 'POST' })).json();
}

// ── Root key rotation (S-10) ──────────────────────────────────────────────
// The sweep answers with the same status shape as the read, so a caller never has to re-read to
// know where it landed.
export async function getKeyRotationStatus() {
  return (await apiFetch('/admin/key-rotation')).json();
}
export async function reEncryptKeys() {
  return (await apiFetch('/admin/key-rotation/reencrypt', { method: 'POST' })).json();
}
