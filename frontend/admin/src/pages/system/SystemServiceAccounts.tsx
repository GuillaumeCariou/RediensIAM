import { useEffect, useState } from 'react';
import { Bot, CheckCircle, XCircle } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Skeleton } from '@/components/ui/skeleton';
import { listServiceAccounts } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface ServiceAccount {
  id: string;
  name: string;
  description: string | null;
  active: boolean;
  last_used_at: string | null;
  created_at: string;
  org_name: string | null;
  user_list_name: string | null;
}

export default function SystemServiceAccounts() {
  const [accounts, setAccounts] = useState<ServiceAccount[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    listServiceAccounts()
      .then(res => setAccounts(res.service_accounts ?? res ?? []))
      .catch(console.error)
      .finally(() => setLoading(false));
  }, []);

  return (
    <div>
      <PageHeader title="Service Accounts" description="All service accounts across the system" />
      <div className="p-6">
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Organisation</TableHead>
                <TableHead>User List</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Last Used</TableHead>
                <TableHead>Created</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 5 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: 6 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : accounts.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={6} className="text-center text-muted-foreground py-12">
                        <Bot className="h-8 w-8 mx-auto mb-2 opacity-40" />No service accounts found
                      </TableCell>
                    </TableRow>
                  )
                : accounts.map(sa => (
                    <TableRow key={sa.id}>
                      <TableCell>
                        <p className="font-medium">{sa.name}</p>
                        {sa.description && <p className="text-xs text-muted-foreground">{sa.description}</p>}
                      </TableCell>
                      <TableCell className="text-sm">{sa.org_name ?? 'System'}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">{sa.user_list_name ?? '—'}</TableCell>
                      <TableCell>
                        {sa.active
                          ? <Badge variant="success"><CheckCircle className="h-3 w-3 mr-1" />Active</Badge>
                          : <Badge variant="destructive"><XCircle className="h-3 w-3 mr-1" />Inactive</Badge>
                        }
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(sa.last_used_at)}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(sa.created_at)}</TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>
      </div>
    </div>
  );
}
