import { useEffect, useState } from 'react';
import { Trash2, Key, MoreHorizontal } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { listHydraClients, deleteHydraClient } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface HydraClient {
  client_id: string;
  client_name: string;
  grant_types: string[];
  redirect_uris: string[];
  metadata: Record<string, string>;
  created_at: string;
}

export default function HydraClients() {
  const [clients, setClients] = useState<HydraClient[]>([]);
  const [loading, setLoading] = useState(true);
  const [deleteTarget, setDeleteTarget] = useState<string | null>(null);

  const load = () => {
    setLoading(true);
    listHydraClients().then(res => setClients(res.clients ?? res ?? [])).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await deleteHydraClient(deleteTarget);
    setDeleteTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader title="Hydra OAuth2 Clients" description="All registered OAuth2 clients in Ory Hydra" />
      <div className="p-6 space-y-4">
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
