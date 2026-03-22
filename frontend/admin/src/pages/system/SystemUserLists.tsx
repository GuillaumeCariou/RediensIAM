import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { List } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Skeleton } from '@/components/ui/skeleton';
import { listUserLists } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface UserList {
  id: string;
  name: string;
  org_id: string | null;
  org_name: string | null;
  immovable: boolean;
  created_at: string;
}

export default function SystemUserLists() {
  const navigate = useNavigate();
  const [lists, setLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');

  useEffect(() => {
    listUserLists().then(res => setLists(res.user_lists ?? res ?? [])).catch(console.error).finally(() => setLoading(false));
  }, []);

  const filtered = lists.filter(l =>
    l.name.toLowerCase().includes(search.toLowerCase()) ||
    (l.org_name?.toLowerCase().includes(search.toLowerCase()) ?? false)
  );

  return (
    <div>
      <PageHeader title="User Lists" description="All user lists across the system" />
      <div className="p-6 space-y-4">
        <Input placeholder="Search by name or organisation…" value={search} onChange={e => setSearch(e.target.value)} className="max-w-sm" />
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Organisation</TableHead>
                <TableHead>Type</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 5 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: 3 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : filtered.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={3} className="text-center text-muted-foreground py-12">
                        <List className="h-8 w-8 mx-auto mb-2 opacity-40" />No user lists found
                      </TableCell>
                    </TableRow>
                  )
                : filtered.map(list => (
                    <TableRow
                      key={list.id}
                      className="cursor-pointer hover:bg-muted/50"
                      onClick={() => navigate(`/system/userlists/${list.id}`)}
                    >
                      <TableCell className="font-medium">{list.name}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">
                        {list.org_name ?? 'System (root)'}
                      </TableCell>
                      <TableCell>
                        {list.immovable ? <Badge variant="secondary">Immovable</Badge> : <Badge variant="outline">Movable</Badge>}
                      </TableCell>
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
