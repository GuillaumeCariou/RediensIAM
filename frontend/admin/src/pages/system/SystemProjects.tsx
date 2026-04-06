import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { FolderKanban } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { adminListAllProjects } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface Project {
  id: string; name: string; slug: string; active: boolean;
  org_id: string; org_name: string; hydra_client_id: string | null; created_at: string;
}

export default function SystemProjects() {
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');

  useEffect(() => {
    adminListAllProjects()
      .then(r => setProjects(r.projects ?? r ?? []))
      .catch(console.error)
      .finally(() => setLoading(false));
  }, []);

  const filtered = projects.filter(p =>
    p.name.toLowerCase().includes(search.toLowerCase()) ||
    p.org_name.toLowerCase().includes(search.toLowerCase()) ||
    p.slug.toLowerCase().includes(search.toLowerCase())
  );

  return (
    <div>
      <PageHeader title="All Projects" description="Every project across all organisations" />
      <div className="p-6 space-y-4">
        <Input
          placeholder="Search by name, org, or slug…"
          value={search}
          onChange={e => setSearch(e.target.value)}
          className="max-w-sm"
        />
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Project</TableHead>
                <TableHead>Organisation</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Created</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(() => {
                if (loading) return (
                  Array.from({ length: 6 }, (_, i) => `sk-row-${i}`).map(rowId => (
                      <TableRow key={rowId}>
                        {Array.from({ length: 4 }, (_, j) => `sk-cell-${j}`).map(cellId => (
                          <TableCell key={cellId}><Skeleton className="h-4 w-full" /></TableCell>
                          ))}
                      </TableRow>
                    ))
                );
                if (filtered.length === 0) return (
                  (
                      <TableRow>
                        <TableCell colSpan={4} className="text-center text-muted-foreground py-12">
                          <FolderKanban className="h-8 w-8 mx-auto mb-2 opacity-40" />
                          {search ? 'No projects match your search' : 'No projects yet'}
                        </TableCell>
                      </TableRow>
                    )
                );
                return (
                  filtered.map(p => (
                      <TableRow key={p.id}>
                        <TableCell>
                          <Link
                            to={`/system/organisations/${p.org_id}/projects/${p.id}`}
                            className="block group"
                          >
                            <p className="font-medium group-hover:text-primary transition-colors">{p.name}</p>
                            <p className="text-xs text-muted-foreground font-mono">/{p.slug}</p>
                          </Link>
                        </TableCell>
                        <TableCell>
                          <Link
                            to={`/system/organisations/${p.org_id}`}
                            className="text-sm hover:underline text-muted-foreground hover:text-foreground"
                          >
                            {p.org_name}
                          </Link>
                        </TableCell>
                        <TableCell>
                          {p.active
                            ? <Badge variant="success">Active</Badge>
                            : <Badge variant="secondary">Inactive</Badge>
                          }
                        </TableCell>
                        <TableCell className="text-sm text-muted-foreground">
                          {fmtDateShort(p.created_at)}
                        </TableCell>
                      </TableRow>
                    ))
                );
              })()}
            </TableBody>
          </Table>
        </div>
      </div>
    </div>
  );
}
