import { useCallback, useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { IamChip, IamAvatar } from '@/components/iam';
import { rowActivation } from '@/components/iam/rowActivation';
import {
  searchUsers, orgSearchUsers, adminGetUser, adminUpdateUser, orgGetUser, orgUpdateUser,
  unlockUser, getUserSessions, revokeAllUserSessions, listOrgs, listUserLists, listOrgUserLists,
} from '@/api';
import type { UserSearchFilters } from '@/api';
import { hrefFor, scopeFromPath } from '@/scope';
import { ApiError } from '@/auth';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';
import EditUserDialog from '@/components/EditUserDialog';
import type { UserEditFields } from '@/components/EditUserDialog';
import SessionsDialog from '@/components/SessionsDialog';
import type { OAuthSession } from '@/components/SessionsDialog';

interface ProjectRole { role_id: string; name: string; project_id: string; project_name: string }
interface User {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
  org_name: string | null; user_list_name: string; org_id: string | null;
  user_list_id: string;
  locked_until?: string | null;
  totp_enabled?: boolean; web_authn_enabled?: boolean;
  roles?: ProjectRole[];
}
interface Org { id: string; name: string }
interface UserListRow { id: string; name: string; org_id: string | null }

/** What `GET /admin/users` answers: the window, and the counts over the whole filtered set. */
interface Results {
  users: User[]; total: number; lists: number; tenants: number; page: number; page_size: number;
}
const NOTHING: Results = { users: [], total: 0, lists: 0, tenants: 0, page: 1, page_size: 50 };

/** Every criterion the server knows. `q` is separate only because it has its own box. */
interface Criteria extends UserSearchFilters { q: string }
const ALL: Criteria = { q: '', page: 1 };

const BLANK_FORM: UserEditFields = { email: '', username: '', display_name: '', phone: '', active: true, email_verified: false, clear_lock: false, new_password: '' };

/**
 * Ce que la recherche peut refuser, dit en clair. Motif `apiErrorMessage` de
 * `pages/project/ProjectRoles.tsx` : une promesse rejetée sans `catch` laissait la page afficher
 * ses derniers résultats sous des critères qui n'ont jamais été servis.
 */
function apiErrorMessage(e: unknown, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string; min_length?: number; filter?: string } | null) : null;
  if (body?.error === 'query_too_short') return `Type at least ${body.min_length ?? 3} characters to search.`;
  if (body?.error === 'invalid_filter') return `The server does not know that value for “${body.filter}”. Nothing was filtered.`;
  return body?.detail ?? body?.error ?? fallback;
}

function MoreMenu({ onEdit, onSessions, onUnlock, locked }: Readonly<{
  onEdit: () => void; onSessions: () => void; onUnlock: () => void; locked: boolean;
}>) {
  const [open, setOpen] = useState(false);

  // On the document, not on the scrim below — see the same note in system/Organisations.tsx.
  useEffect(() => {
    if (!open) return;
    const h = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('keydown', h);
    return () => document.removeEventListener('keydown', h);
  }, [open]);

  return (
    <div style={{ position: 'relative' }}>
      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
        onClick={e => { e.stopPropagation(); setOpen(o => !o); }}>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/></svg>
      </button>
      {open && (
        <>
          <div role="none" style={{ position: 'fixed', inset: 0, zIndex: 49 }} onClick={() => setOpen(false)} />
          <div style={{ position: 'absolute', right: 0, top: '100%', zIndex: 50, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8, boxShadow: 'var(--shadow-md)', minWidth: 140, padding: 4 }}>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }}
              onClick={() => { setOpen(false); onEdit(); }}>Edit</button>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }}
              onClick={() => { setOpen(false); onSessions(); }}>View sessions</button>
            {locked && (
              <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13, color: 'var(--warn)' }}
                onClick={() => { setOpen(false); onUnlock(); }}>Unlock account</button>
            )}
          </div>
        </>
      )}
    </div>
  );
}

/** The second factor an account actually holds. Backup codes are not one on their own. */
function SecondFactor({ user }: Readonly<{ user: User }>) {
  const factors = [
    user.totp_enabled ? 'TOTP' : null,
    user.web_authn_enabled ? 'Passkey' : null,
  ].filter(Boolean) as string[];
  if (factors.length === 0) return <IamChip tone="warn">None</IamChip>;
  return <>{factors.map(f => <IamChip key={f} tone="success">{f}</IamChip>)}</>;
}

/**
 * Les comptes, une page à la fois — du déploiement entier ou d'un seul locataire.
 *
 * Tous les critères partent au serveur : la recherche, le locataire, la liste, le statut, le
 * second facteur, la dernière connexion. Aucun n'est appliqué aux lignes déjà reçues — le bandeau
 * « Showing » compte l'ENSEMBLE filtré, pas la page, et un filtre posé sur la page l'aurait fait
 * mentir en silence.
 *
 * Les résultats restent groupés par liste parce que la liste EST l'adresse d'un compte : il n'y a
 * pas de compte « du déploiement », seulement des comptes d'une liste, qui appartient à une
 * organisation.
 *
 * La portée vient du CHEMIN, comme partout ailleurs dans la console, et décide de deux choses :
 * quelle route est appelée (`ProjectRoles` fait le même choix), et si le filtre Tenant existe. En
 * portée organisation il n'a pas lieu d'être — le locataire est celui du jeton, le serveur ne lit
 * même pas `org_id` — donc il est masqué plutôt qu'envoyé.
 */
export default function SystemUsers() {
  const navigate = useNavigate();
  const scope = scopeFromPath(useLocation().pathname);
  // `/org/users` est la seule forme dont le locataire vient du jeton ; `/system/organisations/:id`
  // est un super-admin entré dans un locataire, et garde la route système épinglée sur lui.
  const isSystemCtx    = scope.level !== 'org' || !!scope.orgId;
  const pinnedOrg      = scope.level === 'org' ? scope.orgId : undefined;
  const deploymentWide = scope.level === 'deployment';

  const [criteria, setCriteria] = useState<Criteria>(ALL);
  const [draft, setDraft] = useState('');
  const [results, setResults] = useState<Results>(NOTHING);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [orgs, setOrgs] = useState<Org[]>([]);
  const [lists, setLists] = useState<UserListRow[]>([]);
  const [optionsError, setOptionsError] = useState('');

  const [actionMsg, setActionMsg] = useState<{ text: string; error?: boolean } | null>(null);

  const [editTarget, setEditTarget] = useState<User | null>(null);
  const [editForm, setEditForm] = useState<UserEditFields>(BLANK_FORM);
  const [editLoading, setEditLoading] = useState(false);
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState('');

  const [sessionsUser, setSessionsUser] = useState<User | null>(null);
  const [sessions, setSessions] = useState<OAuthSession[]>([]);
  const [sessionsLoading, setSessionsLoading] = useState(false);
  const [revokeAllLoading, setRevokeAllLoading] = useState(false);

  function flash(text: string, error_ = false) {
    setActionMsg({ text, error: error_ });
    setTimeout(() => setActionMsg(null), 3500);
  }

  const isLocked = (u: User) => !!u.locked_until && new Date(u.locked_until) > new Date();

  /** Ne pose aucun état de façon synchrone, donc un effet peut l'appeler directement. */
  const fetchResults = useCallback(() => {
    const { q, ...filters } = criteria;
    const search = isSystemCtx
      ? searchUsers(q, pinnedOrg ? { ...filters, org_id: pinnedOrg } : filters)
      : orgSearchUsers(q, filters);
    search
      .then((r: Results | User[]) => {
        const rows = Array.isArray(r) ? r : (r.users ?? []);
        setResults(Array.isArray(r) ? { ...NOTHING, users: rows, total: rows.length } : r);
        setError('');
      })
      .catch(e => {
        // Pas de repli sur les derniers résultats : ils répondaient à d'autres critères.
        setResults(NOTHING);
        setError(apiErrorMessage(e, deploymentWide
          ? 'Could not search the deployment’s accounts.'
          : 'Could not search this organisation’s accounts.'));
      })
      .finally(() => setLoading(false));
  }, [criteria, isSystemCtx, pinnedOrg, deploymentWide]);

  useEffect(fetchResults, [fetchResults]);

  // Les locataires ne sont demandés que là où on peut en choisir un ; `/admin/organizations` et
  // `/admin/userlists` sont réservés au super-admin, et les demander depuis la portée organisation
  // n'aurait produit qu'un 403 affiché sous un filtre qui n'existe pas.
  const fetchOptions = useCallback(() => {
    Promise.all([
      deploymentWide ? listOrgs() : Promise.resolve([]),
      isSystemCtx ? listUserLists(pinnedOrg) : listOrgUserLists(),
    ])
      .then(([o, l]) => {
        setOrgs(o.organisations ?? o ?? []);
        setLists(l.user_lists ?? l ?? []);
        setOptionsError('');
      })
      .catch(e => setOptionsError(apiErrorMessage(e, deploymentWide
        ? 'Could not read the tenants and lists to filter by.'
        : 'Could not read the lists to filter by.')));
  }, [deploymentWide, isSystemCtx, pinnedOrg]);

  useEffect(fetchOptions, [fetchOptions]);

  /** Tout changement de critère repart de la première page : la page 3 d'un autre filtre est vide. */
  const apply = (patch: Partial<Criteria>) => {
    setLoading(true);
    setCriteria(c => ({ ...c, page: 1, ...patch }));
  };

  const goToPage = (page: number) => { setLoading(true); setCriteria(c => ({ ...c, page })); };

  const reset = () => { setDraft(''); setLoading(true); setCriteria(ALL); };

  /**
   * La liste d'accueil du compte, que seules les routes d'organisation nomment. `null` choisit la
   * route système — voir `unlockUser` dans `api.ts`, qui porte les deux formes.
   */
  const listOf = (u: User) => (isSystemCtx ? null : u.user_list_id);

  const openEdit = async (u: User) => {
    setEditTarget(u); setEditError(''); setEditLoading(true);
    try {
      const data = await (isSystemCtx ? adminGetUser : orgGetUser)(u.id);
      setEditForm({
        email: data.email ?? '', username: data.username ?? '',
        display_name: data.display_name ?? '', phone: data.phone ?? '',
        active: data.active ?? true, email_verified: data.email_verified ?? false,
        clear_lock: false, new_password: '',
      });
    } catch (e) { setEditError(apiErrorMessage(e, 'Failed to load user details.')); }
    finally { setEditLoading(false); }
  };

  const patchRow = (id: string, patch: Partial<User>) =>
    setResults(r => ({ ...r, users: r.users.map(u => u.id === id ? { ...u, ...patch } : u) }));

  const handleEdit = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!editTarget) return;
    setEditSaving(true); setEditError('');
    try {
      await (isSystemCtx ? adminUpdateUser : orgUpdateUser)(editTarget.id, {
        email: editForm.email, username: editForm.username,
        display_name: editForm.display_name, phone: editForm.phone,
        active: editForm.active, email_verified: editForm.email_verified,
        clear_lock: editForm.clear_lock, new_password: editForm.new_password || undefined,
      });
      patchRow(editTarget.id, { active: editForm.active, display_name: editForm.display_name || null });
      setEditTarget(null);
    } catch (e) { setEditError(apiErrorMessage(e, 'Failed to save changes.')); }
    finally { setEditSaving(false); }
  };

  const handleUnlock = async (u: User) => {
    try {
      await unlockUser(listOf(u), u.id);
      patchRow(u.id, { locked_until: null });
      flash('Account unlocked.');
    } catch (e) { flash(apiErrorMessage(e, 'Failed to unlock account.'), true); }
  };

  const openSessions = async (u: User) => {
    setSessionsUser(u); setSessions([]); setSessionsLoading(true);
    try {
      const res = await getUserSessions(listOf(u), u.id);
      setSessions(res.sessions ?? res ?? []);
    } catch { setSessions([]); }
    finally { setSessionsLoading(false); }
  };

  const handleRevokeAllSessions = async () => {
    if (!sessionsUser) return;
    setRevokeAllLoading(true);
    try {
      await revokeAllUserSessions(listOf(sessionsUser), sessionsUser.id);
      setSessions([]);
      flash('All sessions revoked.');
    } catch (e) { flash(apiErrorMessage(e, 'Failed to revoke sessions.'), true); }
    finally { setRevokeAllLoading(false); }
  };

  /** Les lignes de la page, dans l'ordre où le serveur les a rendues, regroupées par liste. */
  const groups = useMemo(() => {
    const by = new Map<string, { name: string; org: string | null; users: User[] }>();
    for (const u of results.users) {
      const g = by.get(u.user_list_id)
        ?? { name: u.user_list_name, org: u.org_name, users: [] };
      g.users.push(u);
      by.set(u.user_list_id, g);
    }
    return [...by.entries()];
  }, [results.users]);

  const listOptions = criteria.org_id ? lists.filter(l => l.org_id === criteria.org_id) : lists;
  const first = (results.page - 1) * results.page_size + 1;
  const last = Math.min(results.page * results.page_size, results.total);
  const filtered = Boolean(criteria.q || criteria.org_id || criteria.user_list_id
    || criteria.status || criteria.mfa || criteria.signed_in);

  return (
    <div>
      <PageHeader
        title="Users"
        description={deploymentWide
          ? 'Search every account in the deployment. Results stay grouped by the list an account belongs to.'
          : 'Search every account in this organisation. Results stay grouped by the list an account belongs to.'}
        actions={[
          <button key="lists" className="iam-btn iam-btn-secondary iam-btn-sm"
            onClick={() => navigate(hrefFor(scope, 'userlists'))}>Browse user lists →</button>,
        ]}
      />
      <div className="iam-page">
        {actionMsg && (
          <div style={{
            padding: '8px 14px', borderRadius: 8, marginBottom: 14, fontSize: 13,
            background: actionMsg.error ? 'var(--danger-soft)' : 'var(--success-soft)',
            color: actionMsg.error ? 'var(--danger)' : 'var(--success)',
            border: `1px solid ${actionMsg.error ? 'oklch(from var(--danger) l c h / 0.3)' : 'oklch(from var(--success) l c h / 0.3)'}`,
          }}>
            {actionMsg.text}
          </div>
        )}

        {/* Ce que la page montre, compté par le serveur sur l'ensemble filtré. */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 16, padding: '9px 14px', border: '1px solid var(--border)', borderRadius: 'var(--iam-radius-sm)', background: 'var(--surface-2)', fontSize: 12.5, flexWrap: 'wrap' }}>
          <span style={{ fontSize: 10.5, fontWeight: 700, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--fg-subtle)' }}>Showing</span>
          {deploymentWide
            ? <IamChip tone="danger">Deployment</IamChip>
            : <IamChip tone="accent">Organisation</IamChip>}
          <span>
            {results.total} {results.total === 1 ? 'account' : 'accounts'}
            {' · '}{results.lists} {results.lists === 1 ? 'list' : 'lists'}
            {/* Le locataire n'est un compteur que là où il peut y en avoir plusieurs : confinée à
                une organisation, la réponse vaut 1 et ne dit rien. */}
            {deploymentWide && <>{' · '}{results.tenants} {results.tenants === 1 ? 'tenant' : 'tenants'}</>}
          </span>
          <div style={{ flex: 1 }} />
          {filtered && (
            <button className="iam-btn iam-btn-ghost iam-btn-sm" onClick={reset}>Clear every filter</button>
          )}
        </div>

        <div className="iam-card" style={{ marginBottom: 16 }}>
          <div className="iam-card-pad" style={{ paddingBottom: 14 }}>
            <div style={{ display: 'flex', gap: 10, alignItems: 'flex-end', marginBottom: 12 }}>
              <div style={{ flex: 1 }}>
                <label className="iam-label" htmlFor="u-q">Search</label>
                <div style={{ position: 'relative' }}>
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
                    style={{ position: 'absolute', left: 11, top: '50%', transform: 'translateY(-50%)', color: 'var(--fg-subtle)', pointerEvents: 'none' }}>
                    <circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>
                  </svg>
                  <input id="u-q" className="iam-input" style={{ paddingLeft: 32 }}
                    placeholder="Email, username, display name, or a user ID"
                    value={draft}
                    onChange={e => setDraft(e.target.value)}
                    onKeyDown={e => e.key === 'Enter' && apply({ q: draft.trim() })} />
                </div>
              </div>
              <button className="iam-btn iam-btn-primary iam-btn-sm" style={{ padding: '9px 16px' }}
                onClick={() => apply({ q: draft.trim() })} disabled={loading}>Search</button>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit,minmax(150px,1fr))', gap: 10 }}>
              {/* Absent hors du déploiement : le locataire y est implicite, et l'envoyer serait
                  une restriction que la route d'organisation ne lit même pas. */}
              {deploymentWide && (
                <div>
                  <label className="iam-label" htmlFor="u-t">Tenant</label>
                  <select id="u-t" className="iam-input" value={criteria.org_id ?? ''}
                    onChange={e => apply({ org_id: e.target.value || undefined, user_list_id: undefined })}>
                    <option value="">All {orgs.length} tenants</option>
                    {orgs.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
                  </select>
                </div>
              )}
              <div>
                <label className="iam-label" htmlFor="u-l">User list</label>
                <select id="u-l" className="iam-input" value={criteria.user_list_id ?? ''}
                  onChange={e => apply({ user_list_id: e.target.value || undefined })}>
                  <option value="">All {listOptions.length} lists</option>
                  {listOptions.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}
                </select>
              </div>
              <div>
                <label className="iam-label" htmlFor="u-s">Status</label>
                <select id="u-s" className="iam-input" value={criteria.status ?? ''}
                  onChange={e => apply({ status: (e.target.value || undefined) as Criteria['status'] })}>
                  <option value="">Any</option>
                  <option value="active">Active</option>
                  <option value="disabled">Disabled</option>
                  <option value="locked">Locked</option>
                </select>
              </div>
              <div>
                <label className="iam-label" htmlFor="u-f">Second factor</label>
                <select id="u-f" className="iam-input" value={criteria.mfa ?? ''}
                  onChange={e => apply({ mfa: (e.target.value || undefined) as Criteria['mfa'] })}>
                  <option value="">Any</option>
                  <option value="yes">Has one</option>
                  <option value="no">Has none</option>
                </select>
              </div>
              <div>
                <label className="iam-label" htmlFor="u-si">Signed in</label>
                <select id="u-si" className="iam-input" value={criteria.signed_in ?? ''}
                  onChange={e => apply({ signed_in: (e.target.value || undefined) as Criteria['signed_in'] })}>
                  <option value="">Ever or never</option>
                  <option value="7d">Last 7 days</option>
                  <option value="30d">Last 30 days</option>
                  <option value="never">Never</option>
                </select>
              </div>
            </div>
            {optionsError && <p style={{ fontSize: 12, color: 'var(--danger)', marginTop: 10 }}>{optionsError}</p>}
          </div>

          {/* Des raccourcis vers un jeu de critères, sans compteur : le serveur n'en tient aucun
              avant qu'on le lui demande, et un nombre inventé ici serait faux dès demain. */}
          <div style={{ padding: '12px 18px', borderTop: '1px solid var(--border)', display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span style={{ fontSize: 11.5, color: 'var(--fg-subtle)', marginRight: 2 }}>Saved searches</span>
            {([
              ['Locked', { status: 'locked' }],
              ['No second factor', { mfa: 'no' }],
              ['Never signed in', { signed_in: 'never' }],
              ['Disabled', { status: 'disabled' }],
            ] as [string, Partial<Criteria>][]).map(([name, patch]) => (
              <button key={name} className="iam-chip" style={{ cursor: 'pointer', borderStyle: 'dashed' }}
                onClick={() => { setDraft(''); setLoading(true); setCriteria({ ...ALL, ...patch }); }}>{name}</button>
            ))}
          </div>
        </div>

        {error && <div className="iam-alert iam-alert-danger" style={{ marginBottom: 12 }}>{error}</div>}

        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
          <span style={{ fontSize: 12.5, color: 'var(--fg-muted)' }}>
            {results.total > 0
              ? `${first}–${last} of ${results.total} in ${results.lists} ${results.lists === 1 ? 'list' : 'lists'}`
              : 'No matches'}
            {criteria.q && <> for <strong style={{ color: 'var(--fg)' }}>{criteria.q}</strong></>}
          </span>
          <div style={{ flex: 1 }} />
          <button className="iam-btn iam-btn-secondary iam-btn-sm" disabled={loading || results.page <= 1}
            onClick={() => goToPage(results.page - 1)}>Previous</button>
          <button className="iam-btn iam-btn-secondary iam-btn-sm" disabled={loading || last >= results.total}
            onClick={() => goToPage(results.page + 1)}>Next</button>
        </div>

        {(() => {
          if (loading) return (
            <div className="iam-card">
              <table className="iam-tbl"><tbody>
                {Array.from({ length: 3 }, (_, i) => (
                  <tr key={i}><td><div className="iam-skeleton" style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td></tr>
                ))}
              </tbody></table>
            </div>
          );
          if (groups.length === 0) return (
            <div className="iam-card" style={{ borderStyle: 'dashed' }}>
              <div className="iam-empty" style={{ padding: '26px 20px' }}>
                <div className="iam-empty-title">No users found</div>
                <div className="iam-empty-desc">
                  {(() => {
                    if (filtered) return deploymentWide
                      ? 'Every list in the deployment was searched under these criteria.'
                      : 'Every list in this organisation was searched under these criteria.';
                    return deploymentWide
                      ? 'This deployment holds no accounts yet.'
                      : 'This organisation holds no accounts yet.';
                  })()}
                </div>
              </div>
            </div>
          );
          return (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
              {groups.map(([listId, g]) => (
                <div className="iam-card" key={listId}>
                  <div style={{ padding: '11px 18px', borderBottom: '1px solid var(--border)', background: 'var(--surface-2)', display: 'flex', alignItems: 'center', gap: 8 }}>
                    <span style={{ fontSize: 12, color: 'var(--fg-muted)' }}>
                      {g.org ?? 'No organisation'} <span style={{ color: 'var(--fg-subtle)' }}>›</span>
                    </span>
                    <span style={{ fontSize: 12.5, fontWeight: 600 }}>{g.name}</span>
                    <IamChip>{g.users.length} on this page</IamChip>
                  </div>
                  <table className="iam-tbl">
                    <thead>
                      <tr>
                        <th>Person</th><th>Roles</th><th>Second factor</th><th>Last sign-in</th>
                        <th style={{ width: 36 }}></th>
                      </tr>
                    </thead>
                    <tbody>
                      {g.users.map(user => (
                        <tr key={user.id} {...rowActivation(() => openEdit(user))}>
                          <td>
                            <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                              <IamAvatar name={user.display_name ?? user.username} size="sm" />
                              <div>
                                <div style={{ fontWeight: 500, display: 'flex', alignItems: 'center', gap: 6 }}>
                                  {user.display_name ?? user.username}
                                  {isLocked(user) && <IamChip tone="danger">Locked</IamChip>}
                                  {!user.active && <IamChip tone="danger">Disabled</IamChip>}
                                </div>
                                <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>
                                  {user.email} · <span className="iam-mono">{user.username}#{user.discriminator}</span>
                                </div>
                              </div>
                            </div>
                          </td>
                          <td>
                            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                              {(user.roles ?? []).map(r => (
                                <IamChip key={r.role_id} tone="accent" mono>
                                  {r.project_name} / {r.name}
                                </IamChip>
                              ))}
                              {(user.roles ?? []).length === 0 && <span style={{ fontSize: 12, color: 'var(--fg-muted)' }}>No role</span>}
                            </div>
                          </td>
                          <td><SecondFactor user={user} /></td>
                          <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(user.last_login_at)}</td>
                          <td onClick={e => e.stopPropagation()}>
                            <MoreMenu
                              onEdit={() => openEdit(user)}
                              onSessions={() => openSessions(user)}
                              onUnlock={() => handleUnlock(user)}
                              locked={isLocked(user)}
                            />
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ))}
            </div>
          );
        })()}
      </div>

      <EditUserDialog
        open={!!editTarget}
        targetLabel={editTarget ? `${editTarget.username}#${editTarget.discriminator}` : ''}
        form={editForm}
        loading={editLoading}
        saving={editSaving}
        error={editError}
        onChange={(field, value) => setEditForm(f => ({ ...f, [field]: value }))}
        onSubmit={handleEdit}
        onClose={() => setEditTarget(null)}
      />

      <SessionsDialog
        userEmail={sessionsUser?.email ?? null}
        sessions={sessions}
        loading={sessionsLoading}
        revokeAllLoading={revokeAllLoading}
        onClose={() => { setSessionsUser(null); setSessions([]); }}
        onRevokeAll={handleRevokeAllSessions}
      />
    </div>
  );
}
