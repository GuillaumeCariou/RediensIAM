import { useEffect, useState } from 'react';
import { Plus, Trash2, Bot, Key, MoreHorizontal, Copy, Check } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { listOrgServiceAccounts, createServiceAccount, deleteServiceAccount, generatePat, listPats, revokePat, listUserLists } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';

interface SA { id: string; name: string; description: string | null; active: boolean; last_used_at: string | null; created_at: string; }
interface Pat { id: string; name: string; expires_at: string | null; last_used_at: string | null; created_at: string; }
interface UserList { id: string; name: string; }

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  const copy = () => { navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 2000); };
  return (
    <Button variant="ghost" size="icon" onClick={copy} className="h-6 w-6">
      {copied ? <Check className="h-3 w-3 text-green-600" /> : <Copy className="h-3 w-3" />}
    </Button>
  );
}

export default function OrgServiceAccounts() {
  const { orgId } = useOrgContext();
  const [accounts, setAccounts] = useState<SA[]>([]);
  const [userLists, setUserLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<SA | null>(null);
  const [patSa, setPatSa] = useState<SA | null>(null);
  const [pats, setPats] = useState<Pat[]>([]);
  const [newPat, setNewPat] = useState<string | null>(null);
  const [genPatOpen, setGenPatOpen] = useState<SA | null>(null);
  const [form, setForm] = useState({ name: '', description: '', user_list_id: '' });
  const [patForm, setPatForm] = useState({ name: '', expires_at: '' });
  const [saving, setSaving] = useState(false);

  const load = () => {
    if (!orgId) { setLoading(false); return; }
    setLoading(true);
    Promise.all([
      listOrgServiceAccounts(orgId).then(r => setAccounts(r.service_accounts ?? r ?? [])),
      listUserLists(orgId).then(r => setUserLists(r.user_lists ?? r ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [orgId]);

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      await createServiceAccount({ name: form.name, description: form.description || undefined, user_list_id: form.user_list_id });
      setCreateOpen(false);
      setForm({ name: '', description: '', user_list_id: '' });
      load();
    } finally { setSaving(false); }
  };

  const openPats = async (sa: SA) => {
    setPatSa(sa);
    const res = await listPats(sa.id);
    setPats(res.pats ?? res ?? []);
  };

  const handleGenPat = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!genPatOpen) return;
    setSaving(true);
    try {
      const res = await generatePat(genPatOpen.id, { name: patForm.name, expires_at: patForm.expires_at || undefined });
      setNewPat(res.token);
      setPatForm({ name: '', expires_at: '' });
      setGenPatOpen(null);
      if (patSa?.id === genPatOpen.id) openPats(genPatOpen);
    } finally { setSaving(false); }
  };

  const handleRevokePat = async (patId: string) => {
    if (!patSa) return;
    await revokePat(patSa.id, patId);
    setPats(p => p.filter(x => x.id !== patId));
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteServiceAccount(deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Service Accounts"
        description="Non-human identities for automation and integrations"
        action={orgId ? <Button onClick={() => setCreateOpen(true)}><Plus className="h-4 w-4" />New Service Account</Button> : undefined}
      />
      <div className="p-6 space-y-4">
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Last Used</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 3 }).map((_, i) => <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>)
                : accounts.length === 0
                ? <TableRow><TableCell colSpan={5} className="text-center text-muted-foreground py-12"><Bot className="h-8 w-8 mx-auto mb-2 opacity-40" />No service accounts</TableCell></TableRow>
                : accounts.map(sa => (
                    <TableRow key={sa.id}>
                      <TableCell>
                        <p className="font-medium">{sa.name}</p>
                        {sa.description && <p className="text-xs text-muted-foreground">{sa.description}</p>}
                      </TableCell>
                      <TableCell>
                        <Badge variant={sa.active ? 'success' : 'secondary'}>{sa.active ? 'Active' : 'Inactive'}</Badge>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(sa.last_used_at)}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(sa.created_at)}</TableCell>
                      <TableCell>
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild><Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button></DropdownMenuTrigger>
                          <DropdownMenuContent>
                            <DropdownMenuItem onClick={() => openPats(sa)}><Key className="h-4 w-4" />View PATs</DropdownMenuItem>
                            <DropdownMenuItem onClick={() => setGenPatOpen(sa)}><Plus className="h-4 w-4" />Generate PAT</DropdownMenuItem>
                            <DropdownMenuSeparator />
                            <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setDeleteTarget(sa)}><Trash2 className="h-4 w-4" />Delete</DropdownMenuItem>
                          </DropdownMenuContent>
                        </DropdownMenu>
                      </TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>
      </div>

      {/* Create SA */}
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Create Service Account</DialogTitle></DialogHeader>
          <form onSubmit={handleCreate} className="space-y-4">
            <div className="space-y-2"><Label>Name</Label><Input value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} required placeholder="ci-deploy-bot" /></div>
            <div className="space-y-2"><Label>Description (optional)</Label><Input value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} /></div>
            <div className="space-y-2">
              <Label>User List</Label>
              <Select value={form.user_list_id} onValueChange={v => setForm(f => ({ ...f, user_list_id: v }))}>
                <SelectTrigger><SelectValue placeholder="Select list" /></SelectTrigger>
                <SelectContent>{userLists.map(l => <SelectItem key={l.id} value={l.id}>{l.name}</SelectItem>)}</SelectContent>
              </Select>
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Creating…' : 'Create'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* PAT list */}
      <Dialog open={!!patSa} onOpenChange={v => !v && setPatSa(null)}>
        <DialogContent className="max-w-2xl">
          <DialogHeader><DialogTitle>PATs — {patSa?.name}</DialogTitle><DialogDescription>Personal Access Tokens. Raw tokens are shown once at generation.</DialogDescription></DialogHeader>
          <div className="space-y-2 max-h-80 overflow-y-auto">
            {pats.length === 0
              ? <p className="text-sm text-muted-foreground py-4 text-center">No PATs yet.</p>
              : pats.map(p => (
                  <div key={p.id} className="flex items-center justify-between py-2 border-b last:border-0">
                    <div>
                      <p className="text-sm font-medium">{p.name}</p>
                      <p className="text-xs text-muted-foreground">Expires: {fmtDate(p.expires_at)} · Last used: {fmtDate(p.last_used_at)}</p>
                    </div>
                    <Button size="sm" variant="outline" className="text-destructive" onClick={() => handleRevokePat(p.id)}>Revoke</Button>
                  </div>
                ))
            }
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => { setPatSa(null); setGenPatOpen(patSa); }}>
              <Plus className="h-4 w-4" />Generate PAT
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Generate PAT */}
      <Dialog open={!!genPatOpen} onOpenChange={v => !v && setGenPatOpen(null)}>
        <DialogContent>
          <DialogHeader><DialogTitle>Generate PAT</DialogTitle><DialogDescription>The token will only be shown once.</DialogDescription></DialogHeader>
          <form onSubmit={handleGenPat} className="space-y-4">
            <div className="space-y-2"><Label>Token Name</Label><Input value={patForm.name} onChange={e => setPatForm(f => ({ ...f, name: e.target.value }))} required placeholder="ci-pipeline-token" /></div>
            <div className="space-y-2"><Label>Expires At (optional)</Label><Input type="datetime-local" value={patForm.expires_at} onChange={e => setPatForm(f => ({ ...f, expires_at: e.target.value }))} /></div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setGenPatOpen(null)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Generating…' : 'Generate'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* New PAT reveal */}
      <Dialog open={!!newPat} onOpenChange={v => !v && setNewPat(null)}>
        <DialogContent>
          <DialogHeader><DialogTitle>Your New PAT</DialogTitle><DialogDescription>Copy this token now — it will not be shown again.</DialogDescription></DialogHeader>
          <div className="flex items-center gap-2 p-3 bg-muted rounded-lg font-mono text-sm break-all">
            <span className="flex-1">{newPat}</span>
            {newPat && <CopyButton text={newPat} />}
          </div>
          <DialogFooter><Button onClick={() => setNewPat(null)}>Done</Button></DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!deleteTarget} onOpenChange={v => !v && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader><AlertDialogTitle>Delete {deleteTarget?.name}?</AlertDialogTitle><AlertDialogDescription>All PATs for this service account will also be revoked.</AlertDialogDescription></AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleDelete} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Delete</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
