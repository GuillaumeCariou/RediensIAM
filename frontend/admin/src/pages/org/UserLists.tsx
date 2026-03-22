import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Plus, Users } from 'lucide-react';
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

interface UserList { id: string; name: string; immovable: boolean; user_count: number; created_at: string; }

export default function UserLists() {
  const navigate = useNavigate();
  const { orgId, userListBase } = useOrgContext();
  const [lists, setLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [listForm, setListForm] = useState({ name: '' });
  const [saving, setSaving] = useState(false);

  const load = () => {
    setLoading(true);
    listUserLists(orgId).then(r => setLists(r.user_lists ?? r ?? [])).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [orgId]);

  const handleCreateList = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      await createUserList({ name: listForm.name, org_id: orgId });
      setCreateOpen(false);
      setListForm({ name: '' });
      load();
    } finally { setSaving(false); }
  };

  return (
    <div>
      <PageHeader
        title="User Lists"
        description="Reusable pools of users that can be assigned to projects"
        action={
          orgId
            ? <Button onClick={() => setCreateOpen(true)}><Plus className="h-4 w-4" />New User List</Button>
            : undefined
        }
      />
      <div className="p-6 space-y-4">
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Users</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 4 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: 3 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : lists.length === 0
                ? (
                    <TableRow>
                      <TableCell colSpan={3} className="text-center text-muted-foreground py-12">
                        No user lists yet
                      </TableCell>
                    </TableRow>
                  )
                : lists.map(list => (
                    <TableRow
                      key={list.id}
                      className="cursor-pointer hover:bg-muted/50"
                      onClick={() => navigate(`${userListBase}/${list.id}`)}
                    >
                      <TableCell className="font-medium">{list.name}</TableCell>
                      <TableCell>
                        {list.immovable ? <Badge variant="secondary">Immovable</Badge> : <Badge variant="outline">Movable</Badge>}
                      </TableCell>
                      <TableCell>
                        <div className="flex items-center gap-1">
                          <Users className="h-3 w-3 text-muted-foreground" />
                          <span className="text-sm">{list.user_count}</span>
                        </div>
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
          <DialogHeader><DialogTitle>Create User List</DialogTitle><DialogDescription>A movable pool of users you can assign to projects.</DialogDescription></DialogHeader>
          <form onSubmit={handleCreateList} className="space-y-4">
            <div className="space-y-2"><Label>Name</Label><Input value={listForm.name} onChange={e => setListForm({ name: e.target.value })} required placeholder="Team Alpha" /></div>
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
