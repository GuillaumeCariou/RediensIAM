import { useEffect, useState, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, Plus, Trash2, MoreHorizontal, Copy, Check, KeyRound } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import {
  getServiceAccount, deleteServiceAccount,
  generatePat, revokePat,
  assignSaRole, removeSaRole,
  getSaApiKeys, addSaApiKey, removeSaApiKey,
  listOrgs, listProjects,
} from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import { useAuth } from '@/context/AuthContext';
import { fmtDateShort } from '@/lib/utils';

interface Sa {
  id: string; name: string; description: string | null;
  active: boolean; last_used_at: string | null; created_at: string;
  pats: Pat[]; roles: SaRole[];
}
interface Pat { id: string; name: string; expires_at: string | null; last_used_at: string | null; created_at: string; }
interface SaRole { id: string; role: string; org_id: string | null; project_id: string | null; granted_at: string; }

// ── JWT Profile section ────────────────────────────────────────────────────────
function JwtProfileSection({ saId }: { saId: string }) {
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
          ? <Button size="sm" variant="outline" className="text-destructive border-destructive/40 hover:bg-destructive/10" onClick={handleRemove} disabled={removing}>
              <Trash2 className="h-4 w-4" />{removing ? 'Removing…' : 'Remove key'}
            </Button>
          : <Button size="sm" onClick={handleGenerate} disabled={generating}>
              <KeyRound className="h-4 w-4" />{generating ? 'Generating…' : 'Generate keypair'}
            </Button>
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
export default function ServiceAccountDetail() {
  // Support both :id (system routes) and :saId (org routes)
  const { id, saId: saIdParam } = useParams<{ id?: string; saId?: string }>();
  const saId = id ?? saIdParam ?? '';
  const navigate = useNavigate();

  const { orgId, orgBase } = useOrgContext();
  const { isSuperAdmin } = useAuth();

  const [sa, setSa] = useState<Sa | null>(null);
  const [loading, setLoading] = useState(true);

  // PAT
  const [patOpen, setPatOpen] = useState(false);
  const [newPat, setNewPat] = useState({ name: '', expires_at: '' });
  const [patSaving, setPatSaving] = useState(false);
  const [rawToken, setRawToken] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [revokeTarget, setRevokeTarget] = useState<Pat | null>(null);

  // Roles
  const [roleOpen, setRoleOpen] = useState(false);
  const [roleSaving, setRoleSaving] = useState(false);
  const [roleForm, setRoleForm] = useState({ role: '', org_id: '', project_id: '' });
  const [orgs, setOrgs] = useState<{ id: string; name: string }[]>([]);
  const [projects, setProjects] = useState<{ id: string; name: string }[]>([]);
  const [removeRoleTarget, setRemoveRoleTarget] = useState<SaRole | null>(null);

  // Delete
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
    navigate(`${orgBase}/service-accounts`);
  };

  const handleGeneratePat = async (e: React.FormEvent) => {
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
    setRoleForm({ role: '', org_id: prefilledOrg, project_id: '' });
    setProjects([]);
    if (isSuperAdmin) {
      setOrgs([]);
      listOrgs().then((r: { id: string; name: string }[]) => setOrgs(r ?? [])).catch(console.error);
    } else if (prefilledOrg) {
      listProjects(prefilledOrg).then(r => setProjects(r.projects ?? r ?? [])).catch(console.error);
    }
    setRoleOpen(true);
  };

  const handleRoleChange = (role: string) => {
    setRoleForm(f => ({ ...f, role, project_id: '' }));
    // If org already pre-filled and role requires project, projects already loaded
  };

  const handleOrgChange = (org_id: string) => {
    setRoleForm(f => ({ ...f, org_id, project_id: '' }));
    setProjects([]);
    if (org_id) listProjects(org_id).then(r => setProjects(r.projects ?? r ?? [])).catch(console.error);
  };

  const handleAssignRole = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!saId || !roleForm.role) return;
    setRoleSaving(true);
    try {
      await assignSaRole(saId, {
        role: roleForm.role,
        org_id: roleForm.org_id || undefined,
        project_id: roleForm.project_id || undefined,
      });
      setRoleOpen(false);
      load();
    } finally { setRoleSaving(false); }
  };

  const handleRemoveRole = async () => {
    if (!removeRoleTarget || !saId) return;
    await removeSaRole(saId, removeRoleTarget.id);
    setRemoveRoleTarget(null);
    load();
  };

  const copyToken = () => {
    if (!rawToken) return;
    navigator.clipboard.writeText(rawToken);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const closeTokenDialog = () => { setRawToken(null); load(); };

  const roleSubmitDisabled = roleSaving || !roleForm.role
    || (roleForm.role === 'org_admin' && !roleForm.org_id)
    || (roleForm.role === 'project_admin' && (!roleForm.org_id || !roleForm.project_id));

  return (
    <div className="p-6 space-y-4">
      <Button variant="ghost" size="sm" className="-ml-1" onClick={() => navigate(`${orgBase}/service-accounts`)}>
        <ArrowLeft className="h-4 w-4" />Back to Service Accounts
      </Button>

      {/* SA Card */}
      <div className="rounded-xl border bg-card p-6">
        {loading
          ? <div className="space-y-2"><Skeleton className="h-6 w-48" /><Skeleton className="h-4 w-72" /></div>
          : sa && (
            <div className="flex items-start justify-between gap-4">
              <div>
                <h1 className="text-xl font-bold">{sa.name}</h1>
                {sa.description && <p className="text-sm text-muted-foreground">{sa.description}</p>}
                <p className="text-xs text-muted-foreground mt-1">Created {fmtDateShort(sa.created_at)}</p>
              </div>
              <div className="flex items-center gap-2 shrink-0">
                <Badge variant={sa.active ? 'success' : 'secondary'}>{sa.active ? 'Active' : 'Inactive'}</Badge>
                <Button variant="outline" size="sm" className="text-destructive border-destructive/40 hover:bg-destructive/10" onClick={() => setDeleteOpen(true)}>
                  <Trash2 className="h-4 w-4" />Delete
                </Button>
              </div>
            </div>
          )
        }
      </div>

      {/* Roles */}
      <div className="rounded-xl border bg-card overflow-hidden">
        <div className="flex items-center justify-between px-4 py-3 border-b">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Assigned Roles</h2>
          <Button size="sm" onClick={openRoleDialog}><Plus className="h-4 w-4" />Assign Role</Button>
        </div>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Role</TableHead>
              <TableHead>Scope</TableHead>
              <TableHead>Granted</TableHead>
              <TableHead className="w-12"></TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading
              ? Array.from({ length: 1 }).map((_, i) => (
                  <TableRow key={i}>{Array.from({ length: 4 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                ))
              : (sa?.roles ?? []).length === 0
              ? <TableRow><TableCell colSpan={4} className="text-center text-muted-foreground py-8">No roles assigned.</TableCell></TableRow>
              : (sa?.roles ?? []).map(r => (
                  <TableRow key={r.id}>
                    <TableCell><Badge variant="outline" className="font-mono">{r.role}</Badge></TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {r.project_id ? `project: ${r.project_id}` : r.org_id ? `org: ${r.org_id}` : '—'}
                    </TableCell>
                    <TableCell className="text-sm text-muted-foreground">{fmtDateShort(r.granted_at)}</TableCell>
                    <TableCell>
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent>
                          <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setRemoveRoleTarget(r)}>
                            <Trash2 className="h-4 w-4" />Remove
                          </DropdownMenuItem>
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </TableCell>
                  </TableRow>
                ))
            }
          </TableBody>
        </Table>
      </div>

      {/* PATs */}
      <div className="rounded-xl border bg-card overflow-hidden">
        <div className="flex items-center justify-between px-4 py-3 border-b">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Personal Access Tokens</h2>
          <Button size="sm" onClick={() => setPatOpen(true)}><Plus className="h-4 w-4" />Generate PAT</Button>
        </div>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Expires</TableHead>
              <TableHead>Last Used</TableHead>
              <TableHead>Created</TableHead>
              <TableHead className="w-12"></TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading
              ? Array.from({ length: 2 }).map((_, i) => (
                  <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                ))
              : (sa?.pats ?? []).length === 0
              ? <TableRow><TableCell colSpan={5} className="text-center text-muted-foreground py-8">No tokens generated yet.</TableCell></TableRow>
              : (sa?.pats ?? []).map(p => (
                  <TableRow key={p.id}>
                    <TableCell className="font-medium">{p.name}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">{p.expires_at ? fmtDateShort(p.expires_at) : 'Never'}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">{fmtDateShort(p.last_used_at)}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">{fmtDateShort(p.created_at)}</TableCell>
                    <TableCell>
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent>
                          <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setRevokeTarget(p)}>
                            <Trash2 className="h-4 w-4" />Revoke
                          </DropdownMenuItem>
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </TableCell>
                  </TableRow>
                ))
            }
          </TableBody>
        </Table>
      </div>

      {/* JWT Profile */}
      {saId && <JwtProfileSection saId={saId} />}

      {/* Generate PAT dialog */}
      <Dialog open={patOpen} onOpenChange={setPatOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Generate PAT</DialogTitle>
            <DialogDescription>The raw token will be shown once — copy it before closing.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleGeneratePat} className="space-y-4">
            <div className="space-y-2">
              <Label>Name</Label>
              <Input value={newPat.name} onChange={e => setNewPat(p => ({ ...p, name: e.target.value }))} required placeholder="ci-pipeline" />
            </div>
            <div className="space-y-2">
              <Label>Expiry date <span className="text-muted-foreground">(optional)</span></Label>
              <Input type="datetime-local" value={newPat.expires_at} onChange={e => setNewPat(p => ({ ...p, expires_at: e.target.value }))} />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setPatOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={patSaving}>{patSaving ? 'Generating…' : 'Generate'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Raw token reveal */}
      <Dialog open={!!rawToken} onOpenChange={v => !v && closeTokenDialog()}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Token Generated</DialogTitle>
            <DialogDescription className="text-amber-500 font-medium">This token will not be shown again. Copy it now.</DialogDescription>
          </DialogHeader>
          <div className="flex gap-2">
            <Input readOnly value={rawToken ?? ''} className="font-mono text-xs" />
            <Button type="button" variant="outline" size="icon" onClick={copyToken}>
              {copied ? <Check className="h-4 w-4 text-green-500" /> : <Copy className="h-4 w-4" />}
            </Button>
          </div>
          <DialogFooter>
            <Button onClick={closeTokenDialog}>Done</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Revoke PAT */}
      <AlertDialog open={!!revokeTarget} onOpenChange={v => !v && setRevokeTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Revoke "{revokeTarget?.name}"?</AlertDialogTitle>
            <AlertDialogDescription>Any integration using this token will lose access immediately.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleRevokePat} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Revoke</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      {/* Assign Role dialog */}
      <Dialog open={roleOpen} onOpenChange={setRoleOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Assign Role</DialogTitle>
            <DialogDescription>Grant a management role to this service account.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAssignRole} className="space-y-4">
            <div className="space-y-2">
              <Label>Role</Label>
              <Select value={roleForm.role} onValueChange={handleRoleChange} disabled={roleSaving}>
                <SelectTrigger><SelectValue placeholder="Select a role" /></SelectTrigger>
                <SelectContent>
                  {isSuperAdmin && <SelectItem value="super_admin">super_admin</SelectItem>}
                  <SelectItem value="org_admin">org_admin</SelectItem>
                  <SelectItem value="project_admin">project_admin</SelectItem>
                </SelectContent>
              </Select>
            </div>
            {/* Org picker: SuperAdmin sees a picker; OrgAdmin org is pre-filled (hidden) */}
            {isSuperAdmin && (roleForm.role === 'org_admin' || roleForm.role === 'project_admin') && (
              <div className="space-y-2">
                <Label>Organisation</Label>
                <Select value={roleForm.org_id} onValueChange={handleOrgChange} disabled={roleSaving}>
                  <SelectTrigger><SelectValue placeholder="Select an organisation" /></SelectTrigger>
                  <SelectContent>
                    {orgs.map(o => <SelectItem key={o.id} value={o.id}>{o.name}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
            )}
            {roleForm.role === 'project_admin' && roleForm.org_id && (
              <div className="space-y-2">
                <Label>Project</Label>
                <Select value={roleForm.project_id} onValueChange={v => setRoleForm(f => ({ ...f, project_id: v }))} disabled={roleSaving}>
                  <SelectTrigger><SelectValue placeholder="Select a project" /></SelectTrigger>
                  <SelectContent>
                    {projects.map(p => <SelectItem key={p.id} value={p.id}>{p.name}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
            )}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setRoleOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={roleSubmitDisabled}>{roleSaving ? 'Assigning…' : 'Assign'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Remove Role */}
      <AlertDialog open={!!removeRoleTarget} onOpenChange={v => !v && setRemoveRoleTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove role "{removeRoleTarget?.role}"?</AlertDialogTitle>
            <AlertDialogDescription>This will revoke this management role from the service account.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleRemoveRole} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Remove</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      {/* Delete SA */}
      <AlertDialog open={deleteOpen} onOpenChange={setDeleteOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete "{sa?.name}"?</AlertDialogTitle>
            <AlertDialogDescription>All PATs will be revoked. This cannot be undone.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleDelete} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Delete</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
