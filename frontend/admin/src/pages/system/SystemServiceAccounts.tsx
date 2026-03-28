import { useEffect, useState, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { Bot, Plus, Trash2, MoreHorizontal } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Skeleton } from '@/components/ui/skeleton';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { listServiceAccounts, createServiceAccount, deleteServiceAccount, listUserLists } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface ServiceAccount {
  id: string;
  name: string;
  description: string | null;
  active: boolean;
  last_used_at: string | null;
  created_at: string;
}

export default function SystemServiceAccounts() {
  const navigate = useNavigate();
  const [accounts, setAccounts] = useState<ServiceAccount[]>([]);
  const [systemListId, setSystemListId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const [createOpen, setCreateOpen] = useState(false);
  const [newSa, setNewSa] = useState({ name: '', description: '' });
  const [createSaving, setCreateSaving] = useState(false);

  const [deleteTarget, setDeleteTarget] = useState<ServiceAccount | null>(null);

  const load = useCallback(() => {
    setLoading(true);
    Promise.all([
      listServiceAccounts().then((res: (ServiceAccount & { is_system: boolean })[]) =>
        setAccounts((res ?? []).filter(sa => sa.is_system))
      ),
      listUserLists().then((res: { id: string; org_id: string | null; immovable: boolean }[]) => {
        const syslist = (res ?? []).find(l => l.org_id == null && l.immovable);
        if (syslist) setSystemListId(syslist.id);
      }),
    ]).catch(console.error).finally(() => setLoading(false));
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!systemListId) return;
    setCreateSaving(true);
    try {
      await createServiceAccount({ name: newSa.name, description: newSa.description || undefined, user_list_id: systemListId });
      setCreateOpen(false);
      setNewSa({ name: '', description: '' });
      load();
    } finally { setCreateSaving(false); }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteServiceAccount(deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader title="System Service Accounts" description="Service accounts with system-level access" />
      <div className="p-6">
        <div className="rounded-xl border bg-card overflow-hidden">
          <div className="flex items-center justify-between px-4 py-3 border-b">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">Service Accounts</h2>
            <Button size="sm" onClick={() => setCreateOpen(true)} disabled={!systemListId}>
              <Plus className="h-4 w-4" />New Service Account
            </Button>
          </div>
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
                ? Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : accounts.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={5} className="text-center text-muted-foreground py-12">
                        <Bot className="h-8 w-8 mx-auto mb-2 opacity-40" />No system service accounts yet
                      </TableCell>
                    </TableRow>
                  )
                : accounts.map(sa => (
                    <TableRow key={sa.id} className="cursor-pointer" onClick={() => navigate(`/system/service-accounts/${sa.id}`)}>
                      <TableCell>
                        <p className="font-medium">{sa.name}</p>
                        {sa.description && <p className="text-xs text-muted-foreground">{sa.description}</p>}
                      </TableCell>
                      <TableCell>
                        <Badge variant={sa.active ? 'success' : 'secondary'}>{sa.active ? 'Active' : 'Inactive'}</Badge>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDateShort(sa.last_used_at)}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDateShort(sa.created_at)}</TableCell>
                      <TableCell onClick={e => e.stopPropagation()}>
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent>
                            <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setDeleteTarget(sa)}>
                              <Trash2 className="h-4 w-4" />Delete
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
        <DialogContent>
          <DialogHeader>
            <DialogTitle>New System Service Account</DialogTitle>
            <DialogDescription>Create a service account with system-level access.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleCreate} className="space-y-4">
            <div className="space-y-2">
              <Label>Name</Label>
              <Input value={newSa.name} onChange={e => setNewSa(s => ({ ...s, name: e.target.value }))} required placeholder="ci-deploy-bot" />
            </div>
            <div className="space-y-2">
              <Label>Description</Label>
              <Input value={newSa.description} onChange={e => setNewSa(s => ({ ...s, description: e.target.value }))} placeholder="Used by CI pipeline" />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={createSaving}>{createSaving ? 'Creating…' : 'Create'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!deleteTarget} onOpenChange={v => !v && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete "{deleteTarget?.name}"?</AlertDialogTitle>
            <AlertDialogDescription>
              This will revoke all PATs and remove system access. This cannot be undone.
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
