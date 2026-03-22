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
  getSystemServiceAccount, deleteSystemServiceAccount,
  generateSystemPat, revokeSystemPat,
  assignSystemSaRole, removeSystemSaRole,
  getSystemSaKeys, addSystemSaKey, removeSystemSaKey,
} from '@/api';
import { fmtDateShort } from '@/lib/utils';

function JwtProfileSection({ saId }: { saId: string }) {
  type KeyInfo = { client_id: string | null; has_key: boolean; kid: string | null };
  const [keyInfo, setKeyInfo] = useState<KeyInfo | null>(null);
  const [generating, setGenerating] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [error, setError] = useState('');

  const load = useCallback(() => { getSystemSaKeys(saId).then(setKeyInfo).catch(console.error); }, [saId]);
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
      const res = await addSystemSaKey(saId, publicJwk);
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
    try { await removeSystemSaKey(saId); load(); }
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

interface Sa {
  id: string; name: string; description: string | null;
  active: boolean; last_used_at: string | null; created_at: string;
  pats: Pat[]; roles: SaRole[];
}
interface Pat { id: string; name: string; expires_at: string | null; last_used_at: string | null; created_at: string; }
interface SaRole { id: string; role: string; granted_at: string; }

export default function SystemServiceAccountDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  const [sa, setSa] = useState<Sa | null>(null);
  const [loading, setLoading] = useState(true);

  // PAT generation
  const [patOpen, setPatOpen] = useState(false);
  const [newPat, setNewPat] = useState({ name: '', expires_at: '' });
  const [patSaving, setPatSaving] = useState(false);
  const [rawToken, setRawToken] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  // PAT revoke
  const [revokeTarget, setRevokeTarget] = useState<Pat | null>(null);

  // Role assign
  const [roleOpen, setRoleOpen] = useState(false);
  const [roleSaving, setRoleSaving] = useState(false);

  // Role remove
  const [removeRoleTarget, setRemoveRoleTarget] = useState<SaRole | null>(null);

  // Delete SA
  const [deleteOpen, setDeleteOpen] = useState(false);

  const load = useCallback(() => {
    if (!id) return;
    setLoading(true);
    getSystemServiceAccount(id)
      .then(setSa)
      .catch(console.error)
      .finally(() => setLoading(false));
  }, [id]);

  useEffect(() => { load(); }, [load]);

  const handleDelete = async () => {
    if (!id) return;
    await deleteSystemServiceAccount(id);
    navigate('/system/service-accounts');
  };

  const handleGeneratePat = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!id) return;
    setPatSaving(true);
    try {
      const res = await generateSystemPat(id, {
        name: newPat.name,
        expires_at: newPat.expires_at || undefined,
      });
      setRawToken(res.token);
      setPatOpen(false);
      setNewPat({ name: '', expires_at: '' });
    } finally { setPatSaving(false); }
  };

  const handleRevokePat = async () => {
    if (!revokeTarget || !id) return;
    await revokeSystemPat(id, revokeTarget.id);
    setRevokeTarget(null);
    load();
  };

  const handleAssignRole = async (role: string) => {
    if (!id) return;
    setRoleSaving(true);
    try {
      await assignSystemSaRole(id, role);
      setRoleOpen(false);
      load();
    } finally { setRoleSaving(false); }
  };

  const handleRemoveRole = async () => {
    if (!removeRoleTarget || !id) return;
    await removeSystemSaRole(id, removeRoleTarget.id);
    setRemoveRoleTarget(null);
    load();
  };

  const copyToken = () => {
    if (!rawToken) return;
    navigator.clipboard.writeText(rawToken);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const closeTokenDialog = () => {
    setRawToken(null);
    load();
  };

  return (
    <div className="p-6 space-y-4">
      <Button variant="ghost" size="sm" className="-ml-1" onClick={() => navigate('/system/service-accounts')}>
        <ArrowLeft className="h-4 w-4" />Back to Service Accounts
      </Button>

      {/* ── SA Card ───────────────────────────────────────────────────── */}
      <div className="rounded-xl border bg-card p-6 space-y-4">
        {loading
          ? <div className="space-y-2"><Skeleton className="h-6 w-48" /><Skeleton className="h-4 w-72" /></div>
          : sa && (
            <>
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
            </>
          )
        }
      </div>

      {/* ── Roles ─────────────────────────────────────────────────────── */}
      <div className="rounded-xl border bg-card overflow-hidden">
        <div className="flex items-center justify-between px-4 py-3 border-b">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Assigned Roles</h2>
          <Button size="sm" onClick={() => setRoleOpen(true)}>
            <Plus className="h-4 w-4" />Assign Role
          </Button>
        </div>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Role</TableHead>
              <TableHead>Granted At</TableHead>
              <TableHead className="w-12"></TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading
              ? Array.from({ length: 1 }).map((_, i) => (
                  <TableRow key={i}>{Array.from({ length: 3 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                ))
              : (sa?.roles ?? []).length === 0
              ? <TableRow><TableCell colSpan={3} className="text-center text-muted-foreground py-8">No roles assigned.</TableCell></TableRow>
              : (sa?.roles ?? []).map(r => (
                  <TableRow key={r.id}>
                    <TableCell><Badge variant="outline" className="font-mono">{r.role}</Badge></TableCell>
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

      {/* ── PATs ──────────────────────────────────────────────────────── */}
      <div className="rounded-xl border bg-card overflow-hidden">
        <div className="flex items-center justify-between px-4 py-3 border-b">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Personal Access Tokens</h2>
          <Button size="sm" onClick={() => setPatOpen(true)}>
            <Plus className="h-4 w-4" />Generate PAT
          </Button>
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

      {/* ── JWT Profile ───────────────────────────────────────────────── */}
      {id && <JwtProfileSection saId={id} />}

      {/* ── Dialogs ───────────────────────────────────────────────────── */}

      {/* Generate PAT */}
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

      {/* Show raw token */}
      <Dialog open={!!rawToken} onOpenChange={v => !v && closeTokenDialog()}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Token Generated</DialogTitle>
            <DialogDescription className="text-amber-500 font-medium">
              This token will not be shown again. Copy it now.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-3">
            <div className="flex gap-2">
              <Input readOnly value={rawToken ?? ''} className="font-mono text-xs" />
              <Button type="button" variant="outline" size="icon" onClick={copyToken}>
                {copied ? <Check className="h-4 w-4 text-green-500" /> : <Copy className="h-4 w-4" />}
              </Button>
            </div>
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

      {/* Assign Role */}
      <Dialog open={roleOpen} onOpenChange={setRoleOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Assign Role</DialogTitle>
            <DialogDescription>System service accounts can only hold the super_admin role.</DialogDescription>
          </DialogHeader>
          <div className="space-y-4">
            <div className="space-y-2">
              <Label>Role</Label>
              <Select onValueChange={handleAssignRole} disabled={roleSaving}>
                <SelectTrigger><SelectValue placeholder="Select a role" /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="super_admin">super_admin</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setRoleOpen(false)}>Cancel</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Remove Role */}
      <AlertDialog open={!!removeRoleTarget} onOpenChange={v => !v && setRemoveRoleTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove role "{removeRoleTarget?.role}"?</AlertDialogTitle>
            <AlertDialogDescription>This will revoke system-level access for this service account.</AlertDialogDescription>
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
            <AlertDialogDescription>All PATs will be revoked and system access removed. This cannot be undone.</AlertDialogDescription>
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
