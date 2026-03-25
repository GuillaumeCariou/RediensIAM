import { useEffect, useState } from 'react';
import { Trash2, Key, MoreHorizontal, Plus } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from '@/components/ui/dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { listHydraClients, createHydraClient, deleteHydraClient } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface HydraClient {
  client_id: string;
  client_name: string;
  grant_types: string[];
  redirect_uris: string[];
  metadata: Record<string, string>;
  created_at: string;
}

const GRANT_TYPES = ['authorization_code', 'client_credentials', 'refresh_token'];
const DEFAULT_FORM = { client_name: '', redirect_uris: '', grant_types: ['authorization_code', 'refresh_token'], scope: 'openid profile offline_access' };

export default function HydraClients() {
  const [clients, setClients] = useState<HydraClient[]>([]);
  const [loading, setLoading] = useState(true);
  const [deleteTarget, setDeleteTarget] = useState<string | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const [form, setForm] = useState(DEFAULT_FORM);
  const [saving, setSaving] = useState(false);

  const load = () => {
    setLoading(true);
    listHydraClients().then(res => setClients(Array.isArray(res) ? res : (res?.clients ?? []))).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteHydraClient(deleteTarget);
    setDeleteTarget(null);
    load();
  };

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      await createHydraClient({
        client_name: form.client_name,
        grant_types: form.grant_types,
        redirect_uris: form.redirect_uris.split('\n').map(s => s.trim()).filter(Boolean),
        scope: form.scope,
      });
      setCreateOpen(false);
      setForm(DEFAULT_FORM);
      load();
    } finally { setSaving(false); }
  };

  const toggleGrantType = (gt: string) =>
    setForm(f => ({ ...f, grant_types: f.grant_types.includes(gt) ? f.grant_types.filter(g => g !== gt) : [...f.grant_types, gt] }));

  return (
    <div>
      <PageHeader title="Hydra OAuth2 Clients" description="All registered OAuth2 clients in Ory Hydra" />
      <div className="p-6 space-y-4">
        <div className="flex justify-end">
          <Button onClick={() => setCreateOpen(true)}><Plus className="h-4 w-4" />New Client</Button>
        </div>
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Client ID</TableHead>
                <TableHead>Name</TableHead>
                <TableHead>Grant Types</TableHead>
                <TableHead>Redirect URIs</TableHead>
                <TableHead>Project</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 5 }).map((_, i) => (
                    <TableRow key={i}>
                      {Array.from({ length: 6 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}
                    </TableRow>
                  ))
                : clients.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={6} className="text-center text-muted-foreground py-12">
                        <Key className="h-8 w-8 mx-auto mb-2 opacity-40" />
                        No Hydra clients registered
                      </TableCell>
                    </TableRow>
                  )
                : clients.map(c => (
                    <TableRow key={c.client_id}>
                      <TableCell className="font-mono text-xs">{c.client_id}</TableCell>
                      <TableCell className="font-medium">{c.client_name}</TableCell>
                      <TableCell>
                        <div className="flex gap-1 flex-wrap">
                          {c.grant_types?.map(g => <Badge key={g} variant="secondary" className="text-xs">{g}</Badge>)}
                        </div>
                      </TableCell>
                      <TableCell>
                        <div className="space-y-0.5">
                          {c.redirect_uris?.slice(0, 2).map((u, i) => (
                            <p key={i} className="text-xs text-muted-foreground truncate max-w-[200px]">{u}</p>
                          ))}
                          {(c.redirect_uris?.length ?? 0) > 2 && (
                            <p className="text-xs text-muted-foreground">+{c.redirect_uris.length - 2} more</p>
                          )}
                        </div>
                      </TableCell>
                      <TableCell>
                        {c.metadata?.project_id
                          ? <Badge variant="default" className="text-xs font-mono">{c.metadata.project_id.slice(0, 8)}…</Badge>
                          : c.client_id === 'client_admin_system'
                          ? <Badge variant="secondary">System</Badge>
                          : '—'
                        }
                      </TableCell>
                      <TableCell>
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent>
                            <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setDeleteTarget(c.client_id)}>
                              <Trash2 className="h-4 w-4" />Delete Client
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
      </div>

      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent className="max-w-md">
          <DialogHeader><DialogTitle>New OAuth2 Client</DialogTitle></DialogHeader>
          <form onSubmit={handleCreate} className="space-y-4">
            <div className="space-y-2">
              <Label>Client Name</Label>
              <Input value={form.client_name} onChange={e => setForm(f => ({ ...f, client_name: e.target.value }))} required autoFocus placeholder="My Application" />
            </div>
            <div className="space-y-2">
              <Label>Grant Types</Label>
              <div className="flex flex-wrap gap-2">
                {GRANT_TYPES.map(gt => (
                  <button key={gt} type="button" onClick={() => toggleGrantType(gt)}
                    className={`px-2 py-1 rounded text-xs border transition-colors ${form.grant_types.includes(gt) ? 'bg-primary text-primary-foreground border-primary' : 'border-border text-muted-foreground'}`}>
                    {gt}
                  </button>
                ))}
              </div>
            </div>
            {form.grant_types.includes('authorization_code') && (
              <div className="space-y-2">
                <Label>Redirect URIs <span className="text-muted-foreground text-xs">(one per line)</span></Label>
                <textarea className="w-full min-h-[80px] rounded-md border border-input bg-background px-3 py-2 text-sm" value={form.redirect_uris} onChange={e => setForm(f => ({ ...f, redirect_uris: e.target.value }))} placeholder="https://myapp.example.com/callback" />
              </div>
            )}
            <div className="space-y-2">
              <Label>Scope</Label>
              <Input value={form.scope} onChange={e => setForm(f => ({ ...f, scope: e.target.value }))} />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving || form.grant_types.length === 0}>{saving ? 'Creating…' : 'Create'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!deleteTarget} onOpenChange={v => !v && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete Hydra Client?</AlertDialogTitle>
            <AlertDialogDescription>
              Client <code className="font-mono">{deleteTarget}</code> will be permanently deleted from Hydra. Any applications using this client will stop working.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleDelete} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
