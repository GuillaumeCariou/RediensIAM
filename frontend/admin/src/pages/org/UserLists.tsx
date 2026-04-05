import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Plus, Users, List } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { Skeleton } from '@/components/ui/skeleton';
import { listUserLists, createUserList } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';

interface UserList {
  id: string;
  name: string;
  org_id: string | null;
  org_name: string | null;
  immovable: boolean;
  user_count?: number;
  created_at: string;
}

export default function UserLists() {
  const navigate = useNavigate();
  const location = useLocation();
  const { orgId, isSystemCtx, userListBase } = useOrgContext();

  // System global route (/system/userlists) has no org param — orgId comes from token (null for super_admin)
  const isGlobal = !orgId;
  const navigateBase = isGlobal ? '/system/userlists' : userListBase;

  const [lists, setLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [listForm, setListForm] = useState({ name: '' });
  const [saving, setSaving] = useState(false);

  const load = () => {
    setLoading(true);
    listUserLists(orgId ?? undefined)
      .then(r => setLists(r.user_lists ?? r ?? []))
      .catch(console.error)
      .finally(() => setLoading(false));
  };
  useEffect(load, [orgId]);

  const filtered = isGlobal
    ? lists.filter(l =>
        l.name.toLowerCase().includes(search.toLowerCase()) ||
        (l.org_name?.toLowerCase().includes(search.toLowerCase()) ?? false)
      )
    : lists;

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      await createUserList({ name: listForm.name, org_id: orgId });
      setCreateOpen(false);
      setListForm({ name: '' });
      load();
    } finally { setSaving(false); }
  };

  const colSpan = isGlobal ? 3 : 3;

  return (
    <div>
      <PageHeader
        title="User Lists"
        description={isGlobal ? 'All user lists across the system' : 'Reusable pools of users that can be assigned to projects'}
        action={
          !isGlobal
            ? <Button onClick={() => setCreateOpen(true)}><Plus className="h-4 w-4" />New User List</Button>
            : undefined
        }
      />
      <div className="p-6 space-y-4">
        {isGlobal && (
          <Input
            placeholder="Search by name or organisation…"
            value={search}
            onChange={e => setSearch(e.target.value)}
            className="max-w-sm"
          />
        )}
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                {isGlobal
                  ? <TableHead>Organisation</TableHead>
                  : <TableHead>Users</TableHead>
                }
                <TableHead>Type</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 4 }).map((_, i) => (
                    <TableRow key={i}>
                      {Array.from({ length: colSpan }).map((__, j) => (
                        <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>
                      ))}
                    </TableRow>
                  ))
                : filtered.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={colSpan} className="text-center text-muted-foreground py-12">
                        <List className="h-8 w-8 mx-auto mb-2 opacity-40" />
                        {isGlobal ? 'No user lists found' : 'No user lists yet'}
                      </TableCell>
                    </TableRow>
                  )
                : filtered.map(list => (
                    <TableRow
                      key={list.id}
                      className="cursor-pointer hover:bg-muted/50"
                      onClick={() => navigate(`${navigateBase}/${list.id}`)}
                    >
                      <TableCell className="font-medium">{list.name}</TableCell>
                      {isGlobal
                        ? <TableCell className="text-sm text-muted-foreground">{list.org_name ?? 'System (root)'}</TableCell>
                        : (
                            <TableCell>
                              <div className="flex items-center gap-1">
                                <Users className="h-3 w-3 text-muted-foreground" />
                                <span className="text-sm">{list.user_count ?? '—'}</span>
                              </div>
                            </TableCell>
                          )
                      }
                      <TableCell>
                        {list.immovable
                          ? <Badge variant="secondary">Immovable</Badge>
                          : <Badge variant="outline">Movable</Badge>
                        }
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
            <DialogTitle>Create User List</DialogTitle>
            <DialogDescription>A movable pool of users you can assign to projects.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleCreate} className="space-y-4">
            <div className="space-y-2">
              <Label>Name</Label>
              <Input
                value={listForm.name}
                onChange={e => setListForm({ name: e.target.value })}
                required
                placeholder="Team Alpha"
              />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={saving}>{saving ? 'Creating…' : 'Create'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  );
}
