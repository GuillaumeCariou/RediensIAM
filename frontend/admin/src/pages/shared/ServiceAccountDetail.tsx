import { useEffect, useState, useCallback } from 'react';
import { useParams, useNavigate, useLocation, useSearchParams } from 'react-router';
import { ArrowLeft, Plus, Trash2, MoreHorizontal, Copy, Check, KeyRound } from 'lucide-react';
import {
  getServiceAccount, deleteServiceAccount,
  generatePat, revokePat,
  assignSaRole, removeSaRole, listSaRoles,
  getSaApiKeys, addSaApiKey, removeSaApiKey,
  listOrgs, listProjects, listRoles,
} from '@/api';
import { ApiError } from '@/auth';
import { useOrgContext, useProjectContext } from '@/hooks/useOrgContext';
import { scopeFromPath, hrefFor } from '@/scope';
import { useAuth } from '@/context/AuthContext';
import { fmtDateShort } from '@/lib/utils';
import { IamChip, IamDialog, IamMenu } from '@/components/iam';

interface Sa {
  id: string; name: string; description: string | null;
  active: boolean; last_used_at: string | null; created_at: string;
  pats: Pat[]; roles: SaRole[];
}
interface Pat { id: string; name: string; expires_at: string | null; last_used_at: string | null; created_at: string; }
/** Doit rester aligné sur src/Config/Roles.cs — tout le reste est un rôle de locataire. */
const MANAGEMENT_ROLES = ['super_admin', 'org_admin', 'project_admin'];

interface SaRole { id: string; role: string; org_id: string | null; project_id: string | null; granted_at: string; }

// ── JWT Profile section ────────────────────────────────────────────────────────
function JwtProfileSection({ saId }: Readonly<{ saId: string }>) {
  type KeyInfo = { client_id: string | null; has_key: boolean; kid: string | null };
  const [keyInfo, setKeyInfo] = useState<KeyInfo | null>(null);
  const [generating, setGenerating] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [error, setError] = useState('');

  const load = useCallback(() => { getSaApiKeys(saId).then(setKeyInfo).catch(console.error); }, [saId]);
  useEffect(load, [load]);

  const handleGenerate = async () => {
    setError('');
    setGenerating(true);
    try {
      const keyPair = await crypto.subtle.generateKey(
        { name: 'RSASSA-PKCS1-v1_5', modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: 'SHA-256' },
        true, ['sign', 'verify']
      );
      const publicJwk  = await crypto.subtle.exportKey('jwk', keyPair.publicKey);
      const privateJwk = await crypto.subtle.exportKey('jwk', keyPair.privateKey);
      const kid = `${saId}-${Date.now()}`;
      (publicJwk as Record<string, unknown>).kid = kid;
      (publicJwk as Record<string, unknown>).use = 'sig';
      (privateJwk as Record<string, unknown>).kid = kid;
      const res = await addSaApiKey(saId, publicJwk);
      if (res.error) { setError('Failed: ' + res.error); return; }
      const blob = new Blob([JSON.stringify({ private_key: privateJwk, client_id: res.client_id, alg: 'RS256', note: 'Keep this safe — it will not be shown again.' }, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a   = document.createElement('a');
      a.href = url; a.download = `sa-${saId}-private-key.json`; a.click();
      URL.revokeObjectURL(url);
      load();
    } catch (e) {
      setError('Key generation failed: ' + (e instanceof Error ? e.message : String(e)));
    } finally { setGenerating(false); }
  };

  const handleRemove = async () => {
    setRemoving(true);
    try { await removeSaApiKey(saId); load(); }
    finally { setRemoving(false); }
  };

  return (
    <div className="rounded-xl border bg-card overflow-hidden">
      <div className="flex items-center justify-between px-4 py-3 border-b">
        <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">JWT Profile (private_key_jwt)</h2>
        {keyInfo?.has_key
          ? <button className="iam-btn iam-btn-secondary iam-btn-sm text-destructive border-[var(--danger)] hover:bg-[var(--danger-soft)]" onClick={handleRemove} disabled={removing}>
              <Trash2 className="h-4 w-4" />{removing ? 'Removing…' : 'Remove key'}
            </button>
          : <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={handleGenerate} disabled={generating}>
              <KeyRound className="h-4 w-4" />{generating ? 'Generating…' : 'Generate keypair'}
            </button>
        }
      </div>
      <div className="px-4 py-4 space-y-2">
        {keyInfo?.has_key ? (
          <div className="space-y-1 text-sm">
            <div className="flex gap-2 text-muted-foreground"><span className="font-medium w-24">Client ID</span><code className="font-mono text-xs">{keyInfo.client_id}</code></div>
            <div className="flex gap-2 text-muted-foreground"><span className="font-medium w-24">Key ID (kid)</span><code className="font-mono text-xs">{keyInfo.kid ?? '—'}</code></div>
            <div className="flex gap-2 text-muted-foreground"><span className="font-medium w-24">Algorithm</span><code className="font-mono text-xs">RS256</code></div>
            <p className="text-xs text-muted-foreground pt-1">Use <code className="font-mono">client_credentials</code> grant with a signed JWT assertion at Hydra's token endpoint.</p>
          </div>
        ) : (
          <p className="text-sm text-muted-foreground">No key configured. Generate a keypair — the public key is sent to Hydra, the private key is downloaded once.</p>
        )}
        {error && <p className="text-sm text-destructive">{error}</p>}
      </div>
    </div>
  );
}

// ── Main component ─────────────────────────────────────────────────────────────
/**
 * Reached from two route shapes: `:id` under /system/service-accounts and `:saId` under
 * /org/…/service-accounts — hence the two params read below. Dropping either breaks one of them.
 *
 * The organisation picker in the assign-role dialog renders only for a super_admin. For an
 * org_admin the org is pre-filled from `prefilledOrg` and no picker is shown at all, so the
 * absence of the field is not the absence of an org_id.
 */
export default function ServiceAccountDetail() {
  const { id, saId: saIdParam } = useParams<{ id?: string; saId?: string }>();
  const saId = saIdParam ?? id ?? '';
  const navigate = useNavigate();

  const { orgId } = useOrgContext();
  const { projectId: scopeProjectId } = useProjectContext();
  const { pathname } = useLocation();
  const [searchParams] = useSearchParams();

  /**
   * La liste d'où l'on vient, lue dans le chemin plutôt que supposée.
   *
   * Le retour partait de `orgBase`, donc toujours vers les comptes de service de l'ORGANISATION —
   * y compris depuis un projet, et y compris pour un project_admin, à qui `/org/*` est fermé : le
   * bouton « retour » le renvoyait à l'accueil. `scopeFromPath` répond la portée réelle pour les
   * cinq formes d'URL qui mènent ici.
   */
  const listHref = (() => {
    const scope = scopeFromPath(pathname);
    const base = hrefFor(scope, 'service-accounts');
    const fromQuery = searchParams.get('project_id');
    return scope.level === 'project' && !scope.projectId && fromQuery
      ? `${base}?project_id=${fromQuery}`
      : base;
  })();
  const { isSuperAdmin, isOrgAdmin } = useAuth();

  const [sa, setSa] = useState<Sa | null>(null);
  const [loading, setLoading] = useState(true);

  const [patOpen, setPatOpen] = useState(false);
  const [newPat, setNewPat] = useState({ name: '', expires_at: '' });
  const [patSaving, setPatSaving] = useState(false);
  const [rawToken, setRawToken] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [revokeTarget, setRevokeTarget] = useState<Pat | null>(null);

  const [roleOpen, setRoleOpen] = useState(false);
  const [roleSaving, setRoleSaving] = useState(false);
  const [roleForm, setRoleForm] = useState({ role: '', org_id: '', project_id: '' });
  const [orgs, setOrgs] = useState<{ id: string; name: string }[]>([]);
  const [projects, setProjects] = useState<{ id: string; name: string }[]>([]);
  const [projectRoles, setProjectRoles] = useState<{ id: string; name: string }[]>([]);
  const [removeRoleTarget, setRemoveRoleTarget] = useState<SaRole | null>(null);
  const [roleError, setRoleError] = useState('');

  const [deleteOpen, setDeleteOpen] = useState(false);

  const load = useCallback(() => {
    if (!saId) return;
    setLoading(true);
    getServiceAccount(saId)
      .then(setSa)
      .catch(console.error)
      .finally(() => setLoading(false));
  }, [saId]);

  useEffect(() => { load(); }, [load]);

  const handleDelete = async () => {
    if (!saId) return;
    await deleteServiceAccount(saId);
    navigate(listHref);
  };

  const handleGeneratePat = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!saId) return;
    setPatSaving(true);
    try {
      const res = await generatePat(saId, { name: newPat.name, expires_at: newPat.expires_at || undefined });
      setRawToken(res.token);
      setPatOpen(false);
      setNewPat({ name: '', expires_at: '' });
    } finally { setPatSaving(false); }
  };

  const handleRevokePat = async () => {
    if (!revokeTarget || !saId) return;
    await revokePat(saId, revokeTarget.id);
    setRevokeTarget(null);
    load();
  };

  const openRoleDialog = () => {
    const prefilledOrg = isSuperAdmin ? '' : (orgId ?? '');
    // Un project_admin n'a aucun projet à choisir : `ValidateProjectAdminRoleAssignment` exige le
    // sien et refuse tout autre. Il n'a pas non plus le droit d'en lire la liste — `listProjects`
    // frappe `/org/projects`, gardé en OrgAdmin, et son 403 était avalé par un `catch(console.error)`.
    // Le sélecteur restait donc vide, `project_id` aussi, et le bouton « Assign » ne s'activait
    // jamais : attribuer un rôle depuis un projet était impossible sans un mot d'explication.
    const prefilledProject = isOrgAdmin ? '' : (scopeProjectId ?? '');
    setRoleForm({ role: '', org_id: prefilledOrg, project_id: prefilledProject });
    setProjects([]);
    if (isSuperAdmin) {
      setOrgs([]);
      listOrgs().then((r: { id: string; name: string }[]) => setOrgs(r ?? [])).catch(console.error);
    } else if (isOrgAdmin && prefilledOrg) {
      listProjects(prefilledOrg).then(r => setProjects(r.projects ?? r ?? [])).catch(console.error);
    }
    setProjectRoles([]);
    if (prefilledProject) loadProjectRoles(prefilledProject);
    setRoleOpen(true);
  };

  /**
   * Les rôles que CE projet définit. Ils sont assignables à un compte de service depuis 0.9.2 :
   * le modèle les portait déjà, seule la validation les refusait. Ils n'accordent aucune autorité
   * sur la console — ils sortent dans le jeton, qualifiés par le projet, pour que l'application du
   * locataire décide.
   */
  const loadProjectRoles = (projectId: string) => {
    listRoles(projectId)
      .then(r => setProjectRoles(r.roles ?? r ?? []))
      .catch(() => setProjectRoles([]));
  };

  /** No project fetch here on purpose: openRoleDialog already loaded them for a pre-filled org. */
  // Le projet est choisi AVANT le rôle : le changer ne doit plus l'effacer.
  const handleRoleChange = (role: string) => {
    setRoleForm(f => ({ ...f, role }));
  };

  const handleOrgChange = (org_id: string) => {
    setRoleForm(f => ({ ...f, org_id, project_id: '' }));
    setProjects([]);
    setProjectRoles([]);
    if (org_id) listProjects(org_id).then(r => setProjects(r.projects ?? r ?? [])).catch(console.error);
  };

  const handleProjectChange = (project_id: string) => {
    setRoleForm(f => ({ ...f, project_id }));
    setProjectRoles([]);
    if (project_id) loadProjectRoles(project_id);
  };

  /**
   * Recharge les rôles seuls plutôt que le compte entier : `getServiceAccount` ramène aussi les
   * PAT et la clé, et repasse la page en squelette pour une ligne de tableau qui a changé.
   */
  const reloadRoles = useCallback(async () => {
    const roles: SaRole[] = await listSaRoles(saId);
    setSa(s => s ? { ...s, roles: roles ?? [] } : s);
  }, [saId]);

  const handleAssignRole = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!saId || !roleForm.role) return;
    setRoleSaving(true); setRoleError('');
    try {
      // `super_admin` porte le déploiement entier : ni organisation ni projet ne le qualifient.
      // Les sélecteurs se remplissant avant le choix du rôle, ce qu'ils contiennent doit être
      // ignoré ici plutôt qu'écrit sur un grant qui n'a pas de portée.
      const scoped = roleForm.role !== 'super_admin';
      await assignSaRole(saId, {
        role: roleForm.role,
        org_id: (scoped && roleForm.org_id) || undefined,
        project_id: (scoped && roleForm.project_id) || undefined,
      });
      setRoleOpen(false);
      await reloadRoles();
    } catch (e) {
      const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
      setRoleError(body?.detail ?? body?.error ?? 'Failed to assign this role.');
    } finally { setRoleSaving(false); }
  };

  const handleRemoveRole = async () => {
    if (!removeRoleTarget || !saId) return;
    setRoleError('');
    try {
      await removeSaRole(saId, removeRoleTarget.id);
      setRemoveRoleTarget(null);
      await reloadRoles();
    } catch { setRoleError('Failed to remove this role.'); setRemoveRoleTarget(null); }
  };

  const copyToken = () => {
    if (!rawToken) return;
    navigator.clipboard.writeText(rawToken);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const closeTokenDialog = () => { setRawToken(null); load(); };

  /** Un rôle de locataire est porté par un projet, comme `project_admin` : il en exige donc autant. */
  const isTenantRole = !!roleForm.role && !MANAGEMENT_ROLES.includes(roleForm.role);
  const needsProject = roleForm.role === 'project_admin' || isTenantRole;
  const roleSubmitDisabled = roleSaving || !roleForm.role
    || (roleForm.role === 'org_admin' && !roleForm.org_id)
    || (needsProject && (!roleForm.org_id || !roleForm.project_id));

  return (
    <div className="p-6 space-y-4">
      <button className="iam-btn iam-btn-ghost iam-btn-sm -ml-1" onClick={() => navigate(listHref)}>
        <ArrowLeft className="h-4 w-4" />Back to Service Accounts
      </button>

      <div className="rounded-xl border bg-card p-6">
        {loading
          ? <div className="space-y-2"><div className="iam-skeleton h-6 w-48" /><div className="iam-skeleton h-4 w-72" /></div>
          : sa && (
            <div className="flex items-start justify-between gap-4">
              <div>
                <h1 className="text-xl font-bold">{sa.name}</h1>
                {sa.description && <p className="text-sm text-muted-foreground">{sa.description}</p>}
                <p className="text-xs text-muted-foreground mt-1">Created {fmtDateShort(sa.created_at)}</p>
              </div>
              <div className="flex items-center gap-2 shrink-0">
                <IamChip tone={sa.active ? 'success' : 'default'}>{sa.active ? 'Active' : 'Inactive'}</IamChip>
                <button className="iam-btn iam-btn-secondary iam-btn-sm text-destructive border-[var(--danger)] hover:bg-[var(--danger-soft)]" onClick={() => setDeleteOpen(true)}>
                  <Trash2 className="h-4 w-4" />Delete
                </button>
              </div>
            </div>
          )
        }
      </div>

      <div className="rounded-xl border bg-card overflow-hidden">
        <div className="flex items-center justify-between px-4 py-3 border-b">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Assigned Roles</h2>
          <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => { setRoleError(''); openRoleDialog(); }}><Plus className="h-4 w-4" />Assign Role</button>
        </div>
        {roleError && <p className="text-sm text-destructive px-4 pt-3">{roleError}</p>}
        <table className="iam-tbl">
          <thead>
            <tr>
              <th>Role</th>
              <th>Scope</th>
              <th>Granted</th>
              <th className="w-12"></th>
            </tr>
          </thead>
          <tbody>
            {(() => {
              if (loading) return (
                Array.from({ length: 1 }, (_, i) => `sk-row-${i}`).map(rowId => (
                    <tr key={rowId}>{Array.from({ length: 4 }, (_, j) => `sk-cell-${j}`).map(cellId => <td key={cellId}><div className="iam-skeleton h-4 w-full" /></td>)}</tr>
                  ))
              );
              if ((sa?.roles ?? []).length === 0) return (
                <tr><td className="text-center text-muted-foreground py-8" colSpan={4}>No roles assigned.</td></tr>
              );
              return (
                (sa?.roles ?? []).map(r => (
                    <tr key={r.id}>
                      <td><IamChip className="font-mono" tone="default">{r.role}</IamChip></td>
                      <td className="text-sm text-muted-foreground">
                        {(() => {
                          if (r.project_id) return `project: ${r.project_id}`;
                          if (r.org_id) return `org: ${r.org_id}`;
                          return '—';
                        })()}
                      </td>
                      <td className="text-sm text-muted-foreground">{fmtDateShort(r.granted_at)}</td>
                      <td>
                        <IamMenu trigger={<MoreHorizontal className="h-4 w-4" />}>
<button type="button" className="iam-menu-item iam-menu-item-danger" onClick={() => setRemoveRoleTarget(r)}>
                              <Trash2 className="h-4 w-4" />Remove
                            </button>
</IamMenu>
                      </td>
                    </tr>
                  ))
              );
            })()}
          </tbody>
        </table>
      </div>

      <div className="rounded-xl border bg-card overflow-hidden">
        <div className="flex items-center justify-between px-4 py-3 border-b">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Personal Access Tokens</h2>
          <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setPatOpen(true)}><Plus className="h-4 w-4" />Generate PAT</button>
        </div>
        <table className="iam-tbl">
          <thead>
            <tr>
              <th>Name</th>
              <th>Expires</th>
              <th>Last Used</th>
              <th>Created</th>
              <th className="w-12"></th>
            </tr>
          </thead>
          <tbody>
            {(() => {
              if (loading) return (
                Array.from({ length: 2 }, (_, i) => `sk-row-${i}`).map(rowId => (
                    <tr key={rowId}>{Array.from({ length: 5 }, (_, j) => `sk-cell-${j}`).map(cellId => <td key={cellId}><div className="iam-skeleton h-4 w-full" /></td>)}</tr>
                  ))
              );
              if ((sa?.pats ?? []).length === 0) return (
                <tr><td className="text-center text-muted-foreground py-8" colSpan={5}>No tokens generated yet.</td></tr>
              );
              return (
                (sa?.pats ?? []).map(p => (
                    <tr key={p.id}>
                      <td className="font-medium">{p.name}</td>
                      <td className="text-sm text-muted-foreground">{p.expires_at ? fmtDateShort(p.expires_at) : 'Never'}</td>
                      <td className="text-sm text-muted-foreground">{fmtDateShort(p.last_used_at)}</td>
                      <td className="text-sm text-muted-foreground">{fmtDateShort(p.created_at)}</td>
                      <td>
                        <IamMenu trigger={<MoreHorizontal className="h-4 w-4" />}>
<button type="button" className="iam-menu-item iam-menu-item-danger" onClick={() => setRevokeTarget(p)}>
                              <Trash2 className="h-4 w-4" />Revoke
                            </button>
</IamMenu>
                      </td>
                    </tr>
                  ))
              );
            })()}
          </tbody>
        </table>
      </div>

      {saId && <JwtProfileSection saId={saId} />}

      <IamDialog open={patOpen} onClose={() => setPatOpen(false)}
      title="Generate PAT"
      desc="The raw token will be shown once — copy it before closing."
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setPatOpen(false)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="serviceaccountdetail-form-2" disabled={patSaving}>{patSaving ? 'Generating…' : 'Generate'}</button></>}
    >
<form id="serviceaccountdetail-form-2" onSubmit={handleGeneratePat} className="space-y-4">
            <div className="space-y-2">
              <label className="iam-label" htmlFor="sa-pat-name">Name</label>
              <input className="iam-input" id="sa-pat-name" value={newPat.name} onChange={e => setNewPat(p => ({ ...p, name: e.target.value }))} required placeholder="ci-pipeline" />
            </div>
            <div className="space-y-2">
              <label className="iam-label" htmlFor="sa-pat-expiry">Expiry date <span className="text-muted-foreground">(optional)</span></label>
              <input className="iam-input" id="sa-pat-expiry" type="datetime-local" value={newPat.expires_at} onChange={e => setNewPat(p => ({ ...p, expires_at: e.target.value }))} />
            </div>
            
          </form>
    </IamDialog>

      <IamDialog open={!!rawToken} onClose={() => (v => !v && closeTokenDialog())(false)}
      title="Token Generated"
      desc="This token will not be shown again. Copy it now."
      footer={<button className="iam-btn iam-btn-primary" onClick={closeTokenDialog}>Done</button>}
    >
<div className="flex gap-2">
            <input className="iam-input font-mono text-xs" readOnly value={rawToken ?? ''} />
            <button className="iam-btn iam-btn-secondary iam-btn-icon" type="button" onClick={copyToken}>
              {copied ? <Check className="h-4 w-4 text-green-500" /> : <Copy className="h-4 w-4" />}
            </button>
          </div>
    </IamDialog>

      <IamDialog open={!!revokeTarget} onClose={() => (v => !v && setRevokeTarget(null))(false)}
      title={<>Revoke "{revokeTarget?.name}"?</>}
      desc="Any integration using this token will lose access immediately."
      footer={<><button type="button" onClick={() => (v => !v && setRevokeTarget(null))(false)} className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-danger" onClick={handleRevokePat}>Revoke</button></>}
    >

    </IamDialog>

      <IamDialog open={roleOpen} onClose={() => setRoleOpen(false)}
      title="Assign Role"
      desc="Grant a management role to this service account."
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setRoleOpen(false)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="serviceaccountdetail-form" disabled={roleSubmitDisabled}>{roleSaving ? 'Assigning…' : 'Assign'}</button></>}
    >
<form id="serviceaccountdetail-form" onSubmit={handleAssignRole} className="space-y-4">
            {/*
              * L'organisation et le projet se choisissent AVANT le rôle, et cet ordre est le
              * correctif.
              *
              * Les rôles qu'un projet définit ne peuvent être listés qu'une fois le projet connu.
              * Tant que le sélecteur de projet n'apparaissait qu'après avoir choisi un rôle, ils
              * étaient inatteignables pour quiconque n'a pas de projet dans son jeton — c'est-à-dire
              * pour un super admin et un org admin : il fallait avoir choisi un rôle pour désigner
              * le projet, et connaître le projet pour voir les rôles.
              */}
            {isSuperAdmin && (
              <div className="space-y-2">
                <label className="iam-label" htmlFor="sa-role-org">Organisation</label>
                <select className="iam-select" id="sa-role-org" value={roleForm.org_id} onChange={e => handleOrgChange(e.target.value)} disabled={roleSaving}>
                  <option value="" disabled>Select an organisation…</option>
                  {orgs.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
                </select>
              </div>
            )}
            {isOrgAdmin && roleForm.org_id && (
              <div className="space-y-2">
                <label className="iam-label" htmlFor="sa-role-project">Project</label>
                <select className="iam-select" id="sa-role-project" value={roleForm.project_id} onChange={e => handleProjectChange(e.target.value)} disabled={roleSaving}>
                  <option value="" disabled>Select a project…</option>
                  {projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
                </select>
                <p style={{ fontSize: 11.5, color: 'var(--fg-muted)' }}>
                  Needed for <span className="iam-mono">project_admin</span> and for any role this project defines.
                </p>
              </div>
            )}
            <div className="space-y-2">
              <label className="iam-label" htmlFor="sa-role">Role</label>
              <select className="iam-select" id="sa-role" value={roleForm.role} onChange={e => handleRoleChange(e.target.value)} disabled={roleSaving}>
                  <option value="" disabled>Select a role…</option>
{isSuperAdmin && <option value="super_admin">super_admin</option>}
                  {isOrgAdmin && <option value="org_admin">org_admin</option>}
                  <option value="project_admin">project_admin</option>
                  {projectRoles.length > 0 && (
                    <optgroup label="Roles this project defines">
                      {projectRoles.map(r => <option key={r.id} value={r.name}>{r.name}</option>)}
                    </optgroup>
                  )}
</select>
            </div>
            {needsProject && !isOrgAdmin && (
              <p style={{ fontSize: 12, color: 'var(--fg-muted)' }}>
                Granted on this project. A project administrator may only grant it here.
              </p>
            )}
            
          </form>
    </IamDialog>

      <IamDialog open={!!removeRoleTarget} onClose={() => (v => !v && setRemoveRoleTarget(null))(false)}
      title={<>Remove role "{removeRoleTarget?.role}"?</>}
      desc="This will revoke this management role from the service account."
      footer={<><button type="button" onClick={() => (v => !v && setRemoveRoleTarget(null))(false)} className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-danger" onClick={handleRemoveRole}>Remove</button></>}
    >

    </IamDialog>

      <IamDialog open={deleteOpen} onClose={() => setDeleteOpen(false)}
      title={<>Delete "{sa?.name}"?</>}
      desc="All PATs will be revoked. This cannot be undone."
      footer={<><button type="button" onClick={() => setDeleteOpen(false)} className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-danger" onClick={handleDelete}>Delete</button></>}
    >

    </IamDialog>
    </div>
  );
}
