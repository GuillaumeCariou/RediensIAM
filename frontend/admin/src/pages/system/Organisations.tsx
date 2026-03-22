import { useEffect, useState } from 'react';
import { Plus, MoreHorizontal, Building2, CheckCircle, XCircle, Trash2, PauseCircle, PlayCircle } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { listOrgs, createOrg, suspendOrg, unsuspendOrg, deleteOrg } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface Org {
  id: string;
  name: string;
  slug: string;
  active: boolean;
  suspended_at: string | null;
  created_at: string;
  metadata: Record<string, string>;
}

export default function Organisations() {
  const navigate = useNavigate();
  const [orgs, setOrgs] = useState<Org[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<Org | null>(null);
  const [form, setForm] = useState({ name: '', slug: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const load = () => {
    setLoading(true);
    listOrgs().then(setOrgs).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const filtered = orgs.filter(o =>
    o.name.toLowerCase().includes(search.toLowerCase()) ||
    o.slug.toLowerCase().includes(search.toLowerCase())
  );

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError('');
    try {
      await createOrg(form);
      setCreateOpen(false);
      setForm({ name: '', slug: '' });
      load();
    } catch {
      setError('Failed to create organisation.');
    } finally { setSaving(false); }
  };

  const handleSuspend = async (org: Org) => {
    await (org.suspended_at ? unsuspendOrg(org.id) : suspendOrg(org.id));
    load();
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteOrg(deleteTarget.id);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Organisations"
        description="Manage all tenant organisations in the system"
        action={<Button onClick={() => setCreateOpen(true)}><Plus className="h-4 w-4" />New Organisation</Button>}
      />
      <div className="p-6 space-y-4">
        <Input
          placeholder="Search by name or slug…"
          value={search}
          onChange={e => setSearch(e.target.value)}
          className="max-w-sm"
        />

        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Slug</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 5 }).map((_, i) => (
                    <TableRow key={i}>
                      {Array.from({ length: 5 }).map((__, j) => (
                        <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>
                      ))}
                    </TableRow>
                  ))
                : filtered.length === 0
                ? (
                    <TableRow>
                      <TableCell className="text-center text-muted-foreground py-12" colSpan={5}>
                        <Building2 className="h-8 w-8 mx-auto mb-2 opacity-40" />
                        No organisations found
                      </TableCell>
                    </TableRow>
                  )
                : filtered.map(org => (
                    <TableRow
                      key={org.id}
                      className="cursor-pointer hover:bg-muted/50"
                      onClick={() => navigate(`/system/organisations/${org.id}`)}
                    >
                      <TableCell className="font-medium">{org.name}</TableCell>
                      <TableCell className="font-mono text-sm text-muted-foreground">{org.slug}</TableCell>
                      <TableCell>
                        {org.suspended_at
                          ? <Badge variant="destructive"><XCircle className="h-3 w-3 mr-1" />Suspended</Badge>
                          : org.active
                          ? <Badge variant="success"><CheckCircle className="h-3 w-3 mr-1" />Active</Badge>
                          : <Badge variant="secondary">Inactive</Badge>
                        }
                      </TableCell>
                      <TableCell className="text-muted-foreground text-sm">{fmtDateShort(org.created_at)}</TableCell>
                      <TableCell onClick={e => e.stopPropagation()}>
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button variant="ghost" size="icon"><MoreHorizontal className="h-4 w-4" /></Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent>
                            <DropdownMenuItem onClick={() => handleSuspend(org)}>
                              {org.suspended_at
                                ? <><PlayCircle className="h-4 w-4" />Unsuspend</>
                                : <><PauseCircle className="h-4 w-4" />Suspend</>
                              }
                            </DropdownMenuItem>
                            <DropdownMenuSeparator />
                            <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => setDeleteTarget(org)}>
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

      {/* Create dialog */}
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create Organisation</DialogTitle>
            <DialogDescription>A new tenant with its own projects and user lists.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleCreate} className="space-y-4">
            {error && <p className="text-sm text-destructive">{error}</p>}
            <div className="space-y-2">
              <Label htmlFor="name">Name</Label>
              <Input id="name" value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} required placeholder="Acme Corp" />
            </div>
            <div className="space-y-2">
              <Label htmlFor="slug">Slug</Label>
              <Input id="slug" value={form.slug} onChange={e => setForm(f => ({ ...f, slug: e.target.value.toLowerCase().replace(/\s+/g, '-') }))} required placeholder="acme-corp" pattern="[a-z0-9][a-z0-9-]*" />
              <p className="text-xs text-muted-foreground">Lowercase letters, numbers and hyphens only.</p>
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Creating…' : 'Create'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      {/* Delete confirm */}
      <AlertDialog open={!!deleteTarget} onOpenChange={v => !v && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete {deleteTarget?.name}?</AlertDialogTitle>
            <AlertDialogDescription>
              This will permanently delete the organisation and all associated data. This action cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleDelete} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">
              Delete permanently
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
