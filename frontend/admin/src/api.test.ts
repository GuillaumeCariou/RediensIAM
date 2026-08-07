import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Every function in `api.ts` is a one-line wrapper whose whole content is a URL, a verb and a
 * body. That makes the routes the only thing worth testing, and the only thing that has ever
 * broken: `updateOrgInfo` used to PATCH a GET-only route and answered 405 to a caller with no
 * catch, `getAuditLog` used to send `org_id` to a super-admin-only route that ignored it and
 * showed every tenant's entries under one org's heading, and `createSamlProvider` used to send
 * `name_attribute_name`, which the backend dropped without a word.
 *
 * So this is a table of the wire contract, asserted call by call. A route changed by accident
 * fails here rather than in a browser.
 */

const apiFetch = vi.hoisted(() => vi.fn());
vi.mock('./auth', () => ({ apiFetch }));

const api = await import('./api');

const BLOB = new Blob(['id,email\n']);

beforeEach(() => {
  apiFetch.mockReset();
  apiFetch.mockResolvedValue({ json: async () => ({ ok: true }), blob: async () => BLOB });
});

const json = (body: unknown) => JSON.stringify(body);

/** [what it is, the call, the path it must hit, the init it must send (absent = a bare GET)]. */
type Route = readonly [string, () => Promise<unknown>, string, RequestInit?];

const ROUTES: readonly Route[] = [
  // ── Organisations ───────────────────────────────────────────────
  ['listOrgs', () => api.listOrgs(), '/admin/organizations'],
  ['createOrg', () => api.createOrg({ name: 'Acme', slug: 'acme' }),
    '/admin/organizations', { method: 'POST', body: json({ name: 'Acme', slug: 'acme' }) }],
  ['getOrg', () => api.getOrg('o1'), '/admin/organizations/o1'],
  ['getOrgInfo', () => api.getOrgInfo(), '/org/info'],
  ['updateOrg', () => api.updateOrg('o1', { name: 'New' }),
    '/admin/organizations/o1', { method: 'PATCH', body: json({ name: 'New' }) }],
  ['deleteOrg', () => api.deleteOrg('o1'), '/admin/organizations/o1', { method: 'DELETE' }],
  ['suspendOrg', () => api.suspendOrg('o1'), '/admin/organizations/o1/suspend', { method: 'POST' }],
  ['unsuspendOrg', () => api.unsuspendOrg('o1'), '/admin/organizations/o1/unsuspend', { method: 'POST' }],

  // ── Users ───────────────────────────────────────────────────────
  // The query is encoded: an address with a `+` tag would otherwise arrive as a space.
  ['searchUsers', () => api.searchUsers('a+b@acme.test'), '/admin/users?q=a%2Bb%40acme.test'],
  ['adminGetUser', () => api.adminGetUser('u1'), '/admin/users/u1'],
  ['adminUpdateUser', () => api.adminUpdateUser('u1', { active: false }),
    '/admin/users/u1', { method: 'PATCH', body: json({ active: false }) }],
  ['orgGetUser', () => api.orgGetUser('u1'), '/org/users/u1'],
  ['orgUpdateUser', () => api.orgUpdateUser('u1', { clear_lock: true }),
    '/org/users/u1', { method: 'PATCH', body: json({ clear_lock: true }) }],

  // ── User lists ──────────────────────────────────────────────────
  ['listUserLists (all orgs)', () => api.listUserLists(), '/admin/userlists'],
  ['listUserLists (one org)', () => api.listUserLists('o1'), '/admin/userlists?org_id=o1'],
  // La route OrgAdmin prend l'organisation dans le JETON : lui envoyer `org_id` donnait
  // l'illusion qu'elle le lisait, alors qu'elle le jette. Un super-admin y écrivait donc
  // `Guid.Empty` et recevait un 500 sur la clé étrangère.
  ['createUserList', () => api.createUserList({ name: 'Staff' }),
    '/org/userlists', { method: 'POST', body: json({ name: 'Staff' }) }],
  ['createSystemUserList', () => api.createSystemUserList({ name: 'Staff', org_id: 'o1' }),
    '/admin/userlists', { method: 'POST', body: json({ name: 'Staff', org_id: 'o1' }) }],
  ['getUserList', () => api.getUserList('l1'), '/org/userlists/l1'],
  ['listUserListMembers', () => api.listUserListMembers('l1'), '/org/userlists/l1/users'],
  ['getSystemUserList', () => api.getSystemUserList('l1'), '/admin/userlists/l1'],
  ['listSystemUserListMembers', () => api.listSystemUserListMembers('l1'), '/admin/userlists/l1/users'],
  ['addUserToList', () => api.addUserToList('l1', { email: 'a@b.test', username: 'a', password: 'p' }),
    '/admin/userlists/l1/users',
    { method: 'POST', body: json({ email: 'a@b.test', username: 'a', password: 'p' }) }],
  ['removeUserFromList', () => api.removeUserFromList('l1', 'u1'),
    '/org/userlists/l1/users/u1', { method: 'DELETE' }],
  ['cleanupUserList', () => api.cleanupUserList('l1', { dry_run: true }),
    '/org/userlists/l1/cleanup', { method: 'POST', body: json({ dry_run: true }) }],
  ['removeSystemUserFromList', () => api.removeSystemUserFromList('l1', 'u1'),
    '/admin/userlists/l1/users/u1', { method: 'DELETE' }],

  // ── Projects ────────────────────────────────────────────────────
  ['listProjects', () => api.listProjects('o1'), '/org/projects?org_id=o1'],
  ['createProject', () => api.createProject({ org_id: 'o1', name: 'P', slug: 'p', redirect_uris: ['https://p.test/cb'] }),
    '/org/projects',
    { method: 'POST', body: json({ org_id: 'o1', name: 'P', slug: 'p', redirect_uris: ['https://p.test/cb'] }) }],
  // Même raison que createSystemUserList ci-dessus : /org/projects lit le locataire dans le JETON
  // de l'appelant, et un super-admin n'en porte aucun — l'insertion partait avec Guid.Empty et la
  // clé étrangère répondait 500.
  ['createSystemProject', () => api.createSystemProject('o1', { name: 'P', slug: 'p', redirect_uris: ['https://p.test/cb'] }),
    '/admin/organizations/o1/projects',
    { method: 'POST', body: json({ name: 'P', slug: 'p', redirect_uris: ['https://p.test/cb'] }) }],
  ['getProjectInfo', () => api.getProjectInfo('p1'), '/project/info?project_id=p1'],
  // PATCH goes to /project/info too — the project has no separate settings route.
  ['updateProject', () => api.updateProject('p1', { require_mfa: true }),
    '/project/info?project_id=p1', { method: 'PATCH', body: json({ require_mfa: true }) }],
  ['deleteProject', () => api.deleteProject('p1'), '/org/projects/p1', { method: 'DELETE' }],
  ['getProjectStats', () => api.getProjectStats('p1'), '/project/stats?project_id=p1'],
  ['assignUserList', () => api.assignUserList('p1', 'l1'),
    '/org/projects/p1/userlist', { method: 'PUT', body: json({ user_list_id: 'l1' }) }],
  ['unassignUserList', () => api.unassignUserList('p1'), '/org/projects/p1/userlist', { method: 'DELETE' }],

  // ── SAML ────────────────────────────────────────────────────────
  ['listSamlProviders', () => api.listSamlProviders('p1'), '/admin/projects/p1/saml-providers'],
  // display_name_attribute_name, not name_attribute_name: the backend binds only the former.
  ['createSamlProvider', () => api.createSamlProvider('p1', { entity_id: 'e', display_name_attribute_name: 'cn' }),
    '/admin/projects/p1/saml-providers',
    { method: 'POST', body: json({ entity_id: 'e', display_name_attribute_name: 'cn' }) }],
  ['deleteSamlProvider', () => api.deleteSamlProvider('p1', 'i1'),
    '/admin/projects/p1/saml-providers/i1', { method: 'DELETE' }],

  // ── Project users and roles ─────────────────────────────────────
  ['listProjectUsers', () => api.listProjectUsers('p1'), '/project/users?project_id=p1'],
  ['assignRole', () => api.assignRole('p1', 'u1', 'r1'),
    '/project/users/u1/roles?project_id=p1', { method: 'POST', body: json({ role_id: 'r1' }) }],
  ['removeRole', () => api.removeRole('p1', 'u1', 'r1'),
    '/project/users/u1/roles/r1?project_id=p1', { method: 'DELETE' }],
  ['listRoles', () => api.listRoles('p1'), '/project/roles?project_id=p1'],
  ['createRole', () => api.createRole('p1', { name: 'admin', rank: 10 }),
    '/project/roles?project_id=p1', { method: 'POST', body: json({ name: 'admin', rank: 10 }) }],
  ['deleteRole', () => api.deleteRole('p1', 'r1'), '/project/roles/r1?project_id=p1', { method: 'DELETE' }],

  // ── Service accounts ────────────────────────────────────────────
  ['listServiceAccounts', () => api.listServiceAccounts(), '/service-accounts'],
  ['createServiceAccount', () => api.createServiceAccount({ user_list_id: 'l1', name: 'ci' }),
    '/service-accounts', { method: 'POST', body: json({ user_list_id: 'l1', name: 'ci' }) }],
  ['getServiceAccount', () => api.getServiceAccount('s1'), '/service-accounts/s1'],
  ['deleteServiceAccount', () => api.deleteServiceAccount('s1'), '/service-accounts/s1', { method: 'DELETE' }],
  ['listPats', () => api.listPats('s1'), '/service-accounts/s1/pat'],
  ['generatePat', () => api.generatePat('s1', { name: 'deploy' }),
    '/service-accounts/s1/pat', { method: 'POST', body: json({ name: 'deploy' }) }],
  ['revokePat', () => api.revokePat('s1', 't1'), '/service-accounts/s1/pat/t1', { method: 'DELETE' }],

  // Impersonation: the console supervises. Opening is service-account-only at the server and has
  // deliberately no client here — a function that always answers 403 is a trap, not an API.
  ['listImpersonations', () => api.listImpersonations(), '/admin/impersonate'],
  ['listImpersonations by actor', () => api.listImpersonations('usr_1'), '/admin/impersonate?actor_id=usr_1'],
  ['revokeImpersonation', () => api.revokeImpersonation('7f3'), '/admin/impersonate/7f3/revoke', { method: 'POST' }],

  // The instance row: the settings that used to need a manifest edit and a rollout.
  ['getInstanceConfig', () => api.getInstanceConfig(), '/admin/instance'],
  ['updateInstanceConfig', () => api.updateInstanceConfig({ lockout_minutes: 42 }),
    '/admin/instance', { method: 'PATCH', body: json({ lockout_minutes: 42 }) }],
  ['assignSaRole', () => api.assignSaRole('s1', { role: 'org_admin', org_id: 'o1' }),
    '/service-accounts/s1/roles', { method: 'POST', body: json({ role: 'org_admin', org_id: 'o1' }) }],
  ['removeSaRole', () => api.removeSaRole('s1', 'r1'), '/service-accounts/s1/roles/r1', { method: 'DELETE' }],
  ['getSaApiKeys', () => api.getSaApiKeys('s1'), '/service-accounts/s1/api-keys'],
  ['addSaApiKey', () => api.addSaApiKey('s1', { kty: 'RSA' }),
    '/service-accounts/s1/api-keys', { method: 'POST', body: json({ jwk: { kty: 'RSA' } }) }],
  ['removeSaApiKey', () => api.removeSaApiKey('s1'), '/service-accounts/s1/api-keys', { method: 'DELETE' }],

  // ── Account (self) ──────────────────────────────────────────────
  ['getSessions', () => api.getSessions(), '/account/sessions'],
  // The client id is a URL, so it has to survive being a path segment.
  ['revokeSession', () => api.revokeSession('https://app.test/cb'),
    '/account/sessions/https%3A%2F%2Fapp.test%2Fcb', { method: 'DELETE' }],
  ['revokeAllSessions', () => api.revokeAllSessions(), '/account/sessions', { method: 'DELETE' }],
  ['getMe', () => api.getMe(), '/account/me'],
  ['updateMe', () => api.updateMe({ display_name: 'Ada' }),
    '/account/me', { method: 'PATCH', body: json({ display_name: 'Ada' }) }],
  ['getSocialAccounts', () => api.getSocialAccounts(), '/account/social-accounts'],
  ['unlinkSocialAccount', () => api.unlinkSocialAccount('a1'), '/account/social-accounts/a1', { method: 'DELETE' }],
  ['changePassword', () => api.changePassword({ current_password: 'a', new_password: 'b' }),
    '/account/password', { method: 'PATCH', body: json({ current_password: 'a', new_password: 'b' }) }],

  // ── MFA ─────────────────────────────────────────────────────────
  ['getMfaStatus', () => api.getMfaStatus(), '/account/mfa'],
  ['setupPhone', () => api.setupPhone('+33600000000'),
    '/account/mfa/phone/setup', { method: 'POST', body: json({ phone: '+33600000000' }) }],
  ['verifyPhone', () => api.verifyPhone('123456', { totp_code: '000000' }),
    '/account/mfa/phone/verify', { method: 'POST', body: json({ code: '123456', reauth: { totp_code: '000000' } }) }],
  ['removePhone', () => api.removePhone({ current_password: 'pw' }),
    '/account/mfa/phone', { method: 'DELETE', body: json({ current_password: 'pw' }) }],
  // The first attempt carries no proof on purpose: the backend answers 401 with the methods this
  // account can supply, and only then is the user prompted. It must still send a JSON object.
  ['removePhone (unproven first attempt)', () => api.removePhone(),
    '/account/mfa/phone', { method: 'DELETE', body: '{}' }],
  ['setupTotp', () => api.setupTotp(), '/account/mfa/totp/setup', { method: 'POST' }],
  ['confirmTotp', () => api.confirmTotp({ code: '123456' }, { current_password: 'pw' }),
    '/account/mfa/totp/confirm',
    { method: 'POST', body: json({ code: '123456', reauth: { current_password: 'pw' } }) }],
  ['beginWebAuthnRegistration', () => api.beginWebAuthnRegistration(),
    '/account/mfa/webauthn/register/begin', { method: 'POST' }],
  ['completeWebAuthnRegistration', () => api.completeWebAuthnRegistration({ id: 'c1' }, { totp_code: '1' }),
    '/account/mfa/webauthn/register/complete',
    { method: 'POST', body: json({ id: 'c1', reauth: { totp_code: '1' } }) }],
  ['listWebAuthnCredentials', () => api.listWebAuthnCredentials(), '/account/mfa/webauthn/credentials'],
  ['deleteWebAuthnCredential', () => api.deleteWebAuthnCredential('c1', { totp_code: '1' }),
    '/account/mfa/webauthn/credentials/c1', { method: 'DELETE', body: json({ totp_code: '1' }) }],
  ['regenerateBackupCodes', () => api.regenerateBackupCodes({ current_password: 'pw' }),
    '/account/mfa/backup-codes', { method: 'POST', body: json({ current_password: 'pw' }) }],

  // ── Audit log ───────────────────────────────────────────────────
  ['getAuditLog (no params)', () => api.getAuditLog(), '/admin/audit-log?'],
  ['getAuditLog (system, one org)', () => api.getAuditLog({ org_id: 'o1', limit: 50, offset: 100 }),
    '/admin/audit-log?limit=50&offset=100&org_id=o1'],
  ['getAuditLog (project filter)', () => api.getAuditLog({ project_id: 'p1' }),
    '/admin/audit-log?project_id=p1'],
  // The org scope is a different controller action, not the same one with a filter.
  ['getAuditLog (org scope)', () => api.getAuditLog({ scope: 'org', limit: 25 }), '/org/audit-log?limit=25'],
  // …and it must not leak org_id onto that route, which does not bind it.
  ['getAuditLog (org scope ignores org_id)', () => api.getAuditLog({ scope: 'org', org_id: 'o1' }), '/org/audit-log?'],

  // ── Metrics, health, org settings ───────────────────────────────
  ['getMetrics', () => api.getMetrics(), '/admin/metrics'],
  ['getSystemHealth', () => api.getSystemHealth(), '/admin/system/health'],
  // /org/settings, not /org/info: the latter is registered GET-only and answered 405.
  ['updateOrgInfo', () => api.updateOrgInfo({ audit_retention_days: 90 }),
    '/org/settings', { method: 'PATCH', body: json({ audit_retention_days: 90 }) }],

  // ── Webhooks ────────────────────────────────────────────────────
  ['listWebhooks', () => api.listWebhooks(), '/org/webhooks'],
  ['createWebhook', () => api.createWebhook({ url: 'https://h.test', events: ['user.created'] }),
    '/org/webhooks', { method: 'POST', body: json({ url: 'https://h.test', events: ['user.created'] }) }],
  ['updateWebhook', () => api.updateWebhook('w1', { active: false }),
    '/org/webhooks/w1', { method: 'PATCH', body: json({ active: false }) }],
  ['deleteWebhook', () => api.deleteWebhook('w1'), '/org/webhooks/w1', { method: 'DELETE' }],
  ['testWebhook', () => api.testWebhook('w1'), '/org/webhooks/w1/test', { method: 'POST' }],
  ['listWebhookDeliveries', () => api.listWebhookDeliveries('w1'), '/org/webhooks/w1/deliveries'],

  // ── Exports ─────────────────────────────────────────────────────
  ['exportUserList', () => api.exportUserList('l1'), '/org/userlists/l1/export?format=csv'],
  ['exportOrgAuditLog (system context)', () => api.exportOrgAuditLog('o1', true),
    '/admin/organizations/o1/export/audit-log?format=csv'],
  // Deliberately not the mirror of the path above: OrgController registers this one.
  ['exportOrgAuditLog (org context)', () => api.exportOrgAuditLog('o1', false), '/org/audit-log/export?format=csv'],
  ['exportSystemAuditLog', () => api.exportSystemAuditLog(), '/admin/audit-log/export?format=csv'],

  // ── Org admins ──────────────────────────────────────────────────
  ['listOrgListManagers', () => api.listOrgListManagers(), '/org/admins'],
  ['assignOrgListManager', () => api.assignOrgListManager({ user_id: 'u1', role: 'list_manager', scope_id: 'l1' }),
    '/org/admins', { method: 'POST', body: json({ user_id: 'u1', role: 'list_manager', scope_id: 'l1' }) }],
  ['removeOrgListManager', () => api.removeOrgListManager('r1'), '/org/admins/r1', { method: 'DELETE' }],
  ['listOrgAdmins', () => api.listOrgAdmins('o1'), '/admin/organizations/o1/admins'],
  ['assignOrgAdmin', () => api.assignOrgAdmin('o1', 'u1', 'org_admin', 'l1'),
    '/admin/organizations/o1/admins',
    { method: 'POST', body: json({ user_id: 'u1', role: 'org_admin', scope_id: 'l1' }) }],
  ['assignOrgAdmin (no scope)', () => api.assignOrgAdmin('o1', 'u1', 'org_admin'),
    '/admin/organizations/o1/admins',
    { method: 'POST', body: json({ user_id: 'u1', role: 'org_admin', scope_id: undefined }) }],
  ['removeOrgAdmin', () => api.removeOrgAdmin('o1', 'r1'), '/admin/organizations/o1/admins/r1', { method: 'DELETE' }],

  // ── Admin-scoped creation and project operations ────────────────
  ['adminCreateUserList', () => api.adminCreateUserList({ name: 'Staff', org_id: 'o1' }),
    '/admin/userlists', { method: 'POST', body: json({ name: 'Staff', org_id: 'o1' }) }],
  ['adminCreateProject', () => api.adminCreateProject('o1', { name: 'P', slug: 'p' }),
    '/admin/organizations/o1/projects', { method: 'POST', body: json({ name: 'P', slug: 'p' }) }],
  ['adminListAllProjects', () => api.adminListAllProjects(), '/admin/projects'],
  // Reading one project is the org route even for a super admin — /admin/projects/{id} is not a GET.
  ['adminGetProject', () => api.adminGetProject('p1'), '/org/projects/p1'],
  ['adminGetProjectStats', () => api.adminGetProjectStats('p1'), '/admin/projects/p1/stats'],
  ['adminAssignUserList', () => api.adminAssignUserList('p1', 'l1'),
    '/admin/projects/p1/userlist', { method: 'PUT', body: json({ user_list_id: 'l1' }) }],
  ['adminUnassignUserList', () => api.adminUnassignUserList('p1'), '/admin/projects/p1/userlist', { method: 'DELETE' }],
  ['adminDeleteProject', () => api.adminDeleteProject('p1'), '/admin/projects/p1', { method: 'DELETE' }],

  // ── Per-user actions, list-scoped or system-scoped ──────────────
  ['resendInvite', () => api.resendInvite('l1', 'u1'),
    '/org/userlists/l1/users/u1/resend-invite', { method: 'POST' }],
  ['unlockUser (in a list)', () => api.unlockUser('l1', 'u1'),
    '/org/userlists/l1/users/u1/unlock', { method: 'POST' }],
  ['unlockUser (no list)', () => api.unlockUser(null, 'u1'), '/admin/users/u1/unlock', { method: 'POST' }],
  ['getUserSessions (in a list)', () => api.getUserSessions('l1', 'u1'), '/org/userlists/l1/users/u1/sessions'],
  ['getUserSessions (no list)', () => api.getUserSessions(null, 'u1'), '/admin/users/u1/sessions'],
  ['revokeAllUserSessions (in a list)', () => api.revokeAllUserSessions('l1', 'u1'),
    '/org/userlists/l1/users/u1/sessions', { method: 'DELETE' }],
  ['revokeAllUserSessions (no list)', () => api.revokeAllUserSessions(null, 'u1'),
    '/admin/users/u1/sessions', { method: 'DELETE' }],

  // ── Email ───────────────────────────────────────────────────────
  ['getEmailOverview', () => api.getEmailOverview(), '/admin/email/overview'],
  ['getOrgSmtp', () => api.getOrgSmtp(), '/org/smtp'],
  ['upsertOrgSmtp', () => api.upsertOrgSmtp({ host: 'h', port: 587, start_tls: true, from_address: 'a@b.test', from_name: 'A' }),
    '/org/smtp',
    { method: 'PUT', body: json({ host: 'h', port: 587, start_tls: true, from_address: 'a@b.test', from_name: 'A' }) }],
  ['deleteOrgSmtp', () => api.deleteOrgSmtp(), '/org/smtp', { method: 'DELETE' }],
  ['testOrgSmtp', () => api.testOrgSmtp(), '/org/smtp/test', { method: 'POST' }],
  ['adminGetOrgSmtp', () => api.adminGetOrgSmtp('o1'), '/admin/organizations/o1/smtp'],
  ['adminUpsertOrgSmtp', () => api.adminUpsertOrgSmtp('o1', { host: 'h', port: 465, start_tls: false, from_address: 'a@b.test', from_name: 'A' }),
    '/admin/organizations/o1/smtp',
    { method: 'PUT', body: json({ host: 'h', port: 465, start_tls: false, from_address: 'a@b.test', from_name: 'A' }) }],
  ['adminDeleteOrgSmtp', () => api.adminDeleteOrgSmtp('o1'), '/admin/organizations/o1/smtp', { method: 'DELETE' }],
  ['adminTestOrgSmtp', () => api.adminTestOrgSmtp('o1'), '/admin/organizations/o1/smtp/test', { method: 'POST' }],
];

describe('the wire contract', () => {
  // The tuple type is not preserved through `it.each`, so the row is taken whole and destructured.
  it.each(ROUTES.map(r => [r] as const))('%s', async ([_name, call, path, init]) => {
    await call();
    if (init === undefined) expect(apiFetch).toHaveBeenCalledWith(path);
    else expect(apiFetch).toHaveBeenCalledWith(path, init);
  });

  it('covers every exported function', () => {
    // Otherwise a route added to api.ts is untested and nothing says so.
    const exported = Object.keys(api).filter(k => typeof (api as Record<string, unknown>)[k] === 'function');
    const called = new Set(ROUTES.map(([name]) => name.split(' ')[0]));
    expect([...exported].filter(name => !called.has(name))).toEqual([]);
  });
});

describe('what the caller gets back', () => {
  it('parses the body for the readers', async () => {
    await expect(api.getMe()).resolves.toEqual({ ok: true });
  });

  it('hands back the raw response where there is no body to read', async () => {
    // A 204 has none, and calling .json() on it throws — these callers await the Response itself.
    await expect(api.deleteOrg('o1')).resolves.toHaveProperty('json');
  });

  it('reads exports as a blob, not as JSON', async () => {
    await expect(api.exportUserList('l1')).resolves.toBe(BLOB);
  });
});
