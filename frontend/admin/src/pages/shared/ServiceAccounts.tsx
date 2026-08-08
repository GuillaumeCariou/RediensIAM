import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { IamChip, IamDialog } from '@/components/iam';
import { rowActivation } from '@/components/iam/rowActivation';
import PageHeader from '@/components/layout/PageHeader';
import {
  createServiceAccount, deleteServiceAccount, generatePat, getProjectInfo,
  listPats, listServiceAccounts, listUserLists, revokePat,
} from '@/api';
import { ApiError } from '@/auth';
import { useOrgContext, useProjectContext } from '@/hooks/useOrgContext';
import { hrefFor, type Level } from '@/scope';
import { fmtDate } from '@/lib/utils';

/**
 * Service accounts, at whichever level you are looking from.
 *
 * This replaced three pages — `system/SystemServiceAccounts`, `org/OrgServiceAccounts`,
 * `project/ProjectServiceAccounts` — that rendered the same table over the same endpoint and
 * differed in three answers: which accounts belong here, which user list a new one goes on, and
 * whether the operator picks that list. Everything else was written three times, and had drifted:
 * only the project page offered token management, and only the org page offered a list picker.
 *
 * <p>The drift was not cosmetic. `/service-accounts` scopes to the <b>caller</b>, not to a place —
 * a super-admin gets every account in the deployment — so a page that forgets to narrow the answer
 * shows other tenants' automation identities with a delete button beside each. That is precisely
 * what the project page did until 0.6.1. Here, narrowing is not something a page remembers: it is
 * the one thing each level has to supply.</p>
 */

interface ServiceAccount {
  id: string;
  name: string;
  description: string | null;
  active: boolean;
  last_used_at: string | null;
  created_at: string;
  user_list_id: string;
  org_id: string | null;
  is_system: boolean;
}

interface Pat {
  id: string; name: string; expires_at: string | null; last_used_at: string | null; created_at: string;
}

/** What a level has to answer for the shared page to be correct. */
interface Placement {
  /** Whether an account the API returned belongs on this page. */
  belongs: (sa: ServiceAccount) => boolean;
  /** The list a new account is created on, or null while it is not known yet. */
  createListId: string | null;
  /** Lists the operator may choose between; empty means the level chooses for them. */
  choices: { id: string; name: string }[];
  ready: boolean;
}

function usePlacement(level: Level): Placement {
  const { orgId } = useOrgContext();
  const { projectId } = useProjectContext();
  const [systemListId, setSystemListId] = useState<string | null>(null);
  const [orgLists, setOrgLists] = useState<{ id: string; name: string }[]>([]);
  const [assignedListId, setAssignedListId] = useState<string | null>(null);
  const [resolved, setResolved] = useState(false);

  useEffect(() => {
    setResolved(false);
    if (level === 'deployment') {
      listUserLists()
        .then((r: { id: string; org_id: string | null; immovable: boolean }[]) =>
          setSystemListId((r ?? []).find(l => l.org_id == null && l.immovable)?.id ?? null))
        .catch(console.error)
        .finally(() => setResolved(true));
      return;
    }
    if (level === 'org') {
      if (!orgId) return;
      listUserLists(orgId)
        .then((r: { user_lists?: { id: string; name: string }[] } | { id: string; name: string }[]) =>
          setOrgLists(Array.isArray(r) ? r : (r.user_lists ?? [])))
        .catch(console.error)
        .finally(() => setResolved(true));
      return;
    }
    if (!projectId) return;
    getProjectInfo(projectId)
      .then((r: { assigned_user_list_id?: string | null }) => setAssignedListId(r.assigned_user_list_id ?? null))
      .catch(console.error)
      .finally(() => setResolved(true));
  }, [level, orgId, projectId]);

  /**
   * Memoised on the ids it closes over, not rebuilt per render.
   *
   * A fresh predicate every render is a fresh `load` every render, and `useEffect(load, [load])`
   * then re-fetches forever — React stops it with "Maximum update depth exceeded", which reads as a
   * mysterious crash rather than as the closure-identity problem it is.
   */
  const belongs = useMemo(() => {
    // A deployment account is one on the immovable list with no organisation — the `__system__`
    // list. `is_system` is the server's own word for it.
    if (level === 'deployment') return (sa: ServiceAccount) => sa.is_system;
    if (level === 'org') return (sa: ServiceAccount) => sa.org_id === orgId;
    // A ServiceAccount carries no ProjectId: it belongs to a project exactly when it sits on that
    // project's assigned user list. With no list assigned nothing can belong here yet — which is
    // why the empty answer is `false`, never "show everything".
    return (sa: ServiceAccount) => assignedListId !== null && sa.user_list_id === assignedListId;
  }, [level, orgId, assignedListId]);

  const choices = level === 'org' ? orgLists : NO_CHOICES;

  // The list a new account lands on, by level. Deployment and project each have exactly one, so
  // there is nothing to choose; an organisation has several, which is why `choices` is populated
  // for it alone and this is null.
  let createListId: string | null = null;
  if (level === 'deployment') createListId = systemListId;
  else if (level === 'project') createListId = assignedListId;

  return { belongs, createListId, choices, ready: resolved };
}

/** One array, so a level that offers no choice does not hand the page a new one each render. */
const NO_CHOICES: { id: string; name: string }[] = [];

/**
 * Ce que `POST /service-accounts` refuse, dit en clair.
 *
 * Les trois écritures de cette page — créer, révoquer un jeton, supprimer — partaient en rejet non
 * attrapé : la boîte restait ouverte, inchangée, et le refus n'existait que dans la console du
 * navigateur. La création est la seule des trois qui nomme ses refus ; suppression et révocation
 * répondent 404 nu, d'où la table vide et le repli sur `detail` puis `error`.
 *
 * `forbidden` n'est délibérément pas traduit : c'est le code générique du filtre de niveau, et son
 * `detail` (`role_no_longer_granted`) en dit plus que ne le ferait une phrase fixe.
 */
const CREATE_ERRORS: Record<string, string> = {
  user_list_not_found:                     'That user list no longer exists. Reload the page and try again.',
  list_not_in_your_org:                    'That list belongs to another organisation.',
  no_project_context:                      'Your session carries no project, so there is nowhere to put this account. Sign in again from the project.',
  can_only_create_sa_in_your_project_list: 'A project admin can only create accounts on the list assigned to their own project.',
};

function apiErrorMessage(e: unknown, table: Record<string, string>, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return (body?.error && table[body.error]) ?? body?.detail ?? body?.error ?? fallback;
}

export default function ServiceAccounts({ level }: Readonly<{ level: Level }>) {
  const navigate = useNavigate();
  const { orgBase } = useOrgContext();
  const { projectId, isSystemCtx: projectSystemCtx, projectBase } = useProjectContext();
  const placement = usePlacement(level);

  const [accounts, setAccounts] = useState<ServiceAccount[]>([]);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [form, setForm] = useState({ name: '', description: '', user_list_id: '' });
  const [saving, setSaving] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<ServiceAccount | null>(null);
  const [patsFor, setPatsFor] = useState<ServiceAccount | null>(null);
  const [pats, setPats] = useState<Pat[]>([]);
  const [genFor, setGenFor] = useState<ServiceAccount | null>(null);
  const [patForm, setPatForm] = useState({ name: '', expires_at: '' });
  const [issued, setIssued] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [createError, setCreateError] = useState('');
  const [patError, setPatError] = useState('');
  const [deleteError, setDeleteError] = useState('');

  const { belongs, ready } = placement;
  const load = useCallback(() => {
    if (!ready) return;
    setLoading(true);
    listServiceAccounts()
      .then((r: ServiceAccount[]) => setAccounts((r ?? []).filter(belongs)))
      .catch(console.error)
      .finally(() => setLoading(false));
  }, [belongs, ready]);

  useEffect(load, [load]);

  const targetList = placement.choices.length > 0 ? form.user_list_id : placement.createListId;

  /**
   * Le lien vers la fiche, construit depuis la portée AFFICHÉE et non depuis les ids ambiants.
   *
   * `basePath` rend la forme système dès que `orgId` ET `projectId` sont renseignés — et ils le
   * sont toujours pour un project_admin, dont le jeton porte les deux. Le lien pointait donc sur
   * `/system/organisations/…`, réservé au super-admin, et le garde renvoyait à l'accueil : cliquer
   * un compte de service depuis un projet ne faisait rien du tout. `projectBase` vient des
   * paramètres de route, qui disent la portée réelle.
   *
   * Hors contexte système, l'identité du projet vit dans `?project_id=` — c'est ainsi qu'un
   * org_admin atteint un projet — et la perdre viderait la page d'arrivée.
   */
  const detailHref = (id: string) => {
    if (level === 'deployment') return `${hrefFor({ level: 'deployment' }, 'service-accounts')}/${id}`;
    if (level === 'org') return `${orgBase}/service-accounts/${id}`;
    const query = projectSystemCtx || !projectId ? '' : `?project_id=${projectId}`;
    return `${projectBase}/service-accounts/${id}${query}`;
  };

  const handleCreate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!targetList) return;
    setSaving(true);
    setCreateError('');
    try {
      await createServiceAccount({
        name: form.name,
        description: form.description || undefined,
        user_list_id: targetList,
      });
      setCreateOpen(false);
      setForm({ name: '', description: '', user_list_id: '' });
      load();
    } catch (e) {
      setCreateError(apiErrorMessage(e, CREATE_ERRORS, 'Failed to create the service account.'));
    } finally { setSaving(false); }
  };

  const openPats = async (sa: ServiceAccount) => {
    setPatsFor(sa);
    setPatError('');
    const res = await listPats(sa.id);
    setPats(res.pats ?? res ?? []);
  };

  const handleGenerate = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!genFor) return;
    setSaving(true);
    try {
      const res = await generatePat(genFor.id, { name: patForm.name, expires_at: patForm.expires_at || undefined });
      setIssued(res.token);
      setPatForm({ name: '', expires_at: '' });
      setGenFor(null);
    } finally { setSaving(false); }
  };

  const copyIssued = async () => {
    if (!issued) return;
    await navigator.clipboard.writeText(issued);
    setCopied(true);
  };

  // La ligne du jeton ne part qu'une fois la révocation acquise : la retirer d'abord afficherait un
  // jeton révoqué qui accepte toujours des requêtes.
  const handleRevoke = async (patId: string) => {
    if (!patsFor) return;
    setPatError('');
    try {
      await revokePat(patsFor.id, patId);
      setPats(p => p.filter(x => x.id !== patId));
    } catch (e) {
      setPatError(apiErrorMessage(e, {}, 'Failed to revoke this token. It is still valid.'));
    }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    setDeleteError('');
    try {
      await deleteServiceAccount(deleteTarget.id);
      setDeleteTarget(null);
      load();
    } catch (e) {
      setDeleteError(apiErrorMessage(e, {}, 'Failed to delete this service account. Reload the page and try again.'));
    }
  };

  // Creation needs somewhere to put the account. At project level with no list assigned, and at
  // deployment level before the system list is known, there is nowhere — so the control is absent
  // rather than present and failing.
  const canCreate = placement.choices.length > 0 || placement.createListId !== null;

  return (
    <div>
      <PageHeader
        title="Service accounts"
        description="Non-human identities for automation and integrations"
        actions={canCreate ? [
          <button key="new" className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setCreateOpen(true)}>
            New service account
          </button>,
        ] : []}
      />

      <div className="iam-page">
        <div className="iam-card">
          <table className="iam-tbl">
            <thead>
              <tr><th>Name</th><th>Status</th><th>Last used</th><th>Created</th><th style={{ width: 80 }}></th></tr>
            </thead>
            <tbody>
              {loading && Array.from({ length: 3 }, (_, i) => (
                <tr key={i}>{Array.from({ length: 5 }, (_, j) => (
                  <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>
                ))}</tr>
              ))}

              {!loading && accounts.length === 0 && (
                <tr><td colSpan={5}>
                  <div className="iam-empty">
                    <div className="iam-empty-title">No service accounts</div>
                    <div className="iam-empty-desc">Create one for automation and integrations.</div>
                  </div>
                </td></tr>
              )}

              {!loading && accounts.map(sa => (
                <tr key={sa.id} {...rowActivation(() => navigate(detailHref(sa.id)))}>
                  <td>
                    <div style={{ fontWeight: 500 }}>{sa.name}</div>
                    {sa.description && <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{sa.description}</div>}
                  </td>
                  <td><IamChip tone={sa.active ? 'success' : 'default'}>{sa.active ? 'Active' : 'Inactive'}</IamChip></td>
                  <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(sa.last_used_at)}</td>
                  <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(sa.created_at)}</td>
                  <td onClick={e => e.stopPropagation()}>
                    <button className="iam-btn iam-btn-ghost iam-btn-sm" onClick={() => openPats(sa)}>Tokens</button>
                    <button className="iam-btn iam-btn-ghost iam-btn-sm" style={{ color: 'var(--danger)' }}
                      onClick={() => { setDeleteError(''); setDeleteTarget(sa); }}>Delete</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <IamDialog
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        title="Create service account"
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setCreateOpen(false)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="create-sa" type="submit" disabled={saving}>
              {saving ? 'Creating…' : 'Create'}
            </button>
          </>
        }
      >
        <form id="create-sa" onSubmit={handleCreate} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div>
            <label className="iam-label" htmlFor="sa-name">Name</label>
            <input id="sa-name" className="iam-input" required placeholder="ci-deploy-bot"
              value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} />
          </div>
          <div>
            <label className="iam-label" htmlFor="sa-description">Description (optional)</label>
            <input id="sa-description" className="iam-input"
              value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} />
          </div>
          {placement.choices.length > 0 && (
            <div>
              <label className="iam-label" htmlFor="sa-list">User list</label>
              <select id="sa-list" className="iam-input" required
                value={form.user_list_id} onChange={e => setForm(f => ({ ...f, user_list_id: e.target.value }))}>
                <option value="">Select list…</option>
                {placement.choices.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}
              </select>
            </div>
          )}
          {createError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{createError}</p>}
        </form>
      </IamDialog>

      <IamDialog
        open={!!patsFor}
        onClose={() => setPatsFor(null)}
        title={`Tokens · ${patsFor?.name ?? ''}`}
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setPatsFor(null)}>Close</button>
            <button className="iam-btn iam-btn-primary"
              onClick={() => { setGenFor(patsFor); setPatsFor(null); }}>Generate token</button>
          </>
        }
      >
        {patError && <p style={{ fontSize: 12, color: 'var(--danger)' }}>{patError}</p>}
        {pats.length === 0
          ? <div className="iam-empty-desc">No tokens.</div>
          : (
            <table className="iam-tbl">
              <thead><tr><th>Name</th><th>Expires</th><th>Last used</th><th></th></tr></thead>
              <tbody>
                {pats.map(p => (
                  <tr key={p.id}>
                    <td>{p.name}</td>
                    <td style={{ fontSize: 12 }}>{p.expires_at ? fmtDate(p.expires_at) : '—'}</td>
                    <td style={{ fontSize: 12 }}>{p.last_used_at ? fmtDate(p.last_used_at) : '—'}</td>
                    <td>
                      <button className="iam-btn iam-btn-ghost iam-btn-sm" style={{ color: 'var(--danger)' }}
                        onClick={() => handleRevoke(p.id)}>Revoke</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
      </IamDialog>

      <IamDialog
        open={!!genFor}
        onClose={() => setGenFor(null)}
        title="Generate token"
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setGenFor(null)}>Cancel</button>
            <button className="iam-btn iam-btn-primary" form="gen-pat" type="submit" disabled={saving}>Generate</button>
          </>
        }
      >
        <form id="gen-pat" onSubmit={handleGenerate} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div>
            <label className="iam-label" htmlFor="pat-name">Name</label>
            <input id="pat-name" className="iam-input" required value={patForm.name}
              onChange={e => setPatForm(f => ({ ...f, name: e.target.value }))} />
          </div>
          <div>
            <label className="iam-label" htmlFor="pat-expires">Expires (optional)</label>
            <input id="pat-expires" className="iam-input" type="date" value={patForm.expires_at}
              onChange={e => setPatForm(f => ({ ...f, expires_at: e.target.value }))} />
          </div>
        </form>
      </IamDialog>

      {/* Shown once, and said so: there is no endpoint that returns it again. */}
      <IamDialog
        open={!!issued}
        onClose={() => { setIssued(null); setCopied(false); }}
        title="Token created"
        desc="Copy it now — it is not shown again."
        footer={<button className="iam-btn iam-btn-primary" onClick={() => { setIssued(null); setCopied(false); }}>Done</button>}
      >
        <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
          <code className="iam-mono" style={{ wordBreak: 'break-all', flex: 1 }}>{issued}</code>
          <button className="iam-btn iam-btn-ghost iam-btn-sm" onClick={copyIssued}>
            {copied ? 'Copied' : 'Copy'}
          </button>
        </div>
      </IamDialog>

      <IamDialog
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        title={`Delete ${deleteTarget?.name}?`}
        desc="All tokens for this service account will also be revoked."
        footer={
          <>
            <button className="iam-btn iam-btn-ghost" onClick={() => setDeleteTarget(null)}>Cancel</button>
            <button className="iam-btn iam-btn-danger" onClick={handleDelete}>Delete</button>
          </>
        }
      >
        {deleteError
          ? <p style={{ fontSize: 12, color: 'var(--danger)' }}>{deleteError}</p>
          : <div />}
      </IamDialog>
    </div>
  );
}
