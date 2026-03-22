import { useState } from 'react';
import { Search, UserX, LogOut, CheckCircle, XCircle } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Skeleton } from '@/components/ui/skeleton';
import { searchUsers, disableUser, enableUser, forceLogoutUser } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface User {
  id: string;
  email: string;
  username: string;
  discriminator: string;
  display_name: string | null;
  active: boolean;
  last_login_at: string | null;
  org_name: string;
  user_list_name: string;
}

export default function SystemUsers() {
  const [users, setUsers] = useState<User[]>([]);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState('');
  const [searched, setSearched] = useState(false);

  const doSearch = async () => {
    if (!query.trim()) return;
    setLoading(true);
    setSearched(true);
    try {
      const res = await searchUsers(query);
      setUsers(res.users ?? res ?? []);
    } catch { setUsers([]); } finally { setLoading(false); }
  };

  const handleDisable = async (user: User) => {
    await (user.active ? disableUser(user.id) : enableUser(user.id));
    setUsers(prev => prev.map(u => u.id === user.id ? { ...u, active: !u.active } : u));
  };

  const handleLogout = async (id: string) => {
    await forceLogoutUser(id);
  };

  return (
    <div>
      <PageHeader title="Global User Search" description="Search and manage users across all organisations" />
      <div className="p-6 space-y-4">
        <div className="flex gap-2 max-w-md">
          <Input
            placeholder="Search by email, username…"
            value={query}
            onChange={e => setQuery(e.target.value)}
            onKeyDown={e => e.key === 'Enter' && doSearch()}
          />
          <Button onClick={doSearch} disabled={loading}>
            <Search className="h-4 w-4" />Search
          </Button>
        </div>

        {(loading || searched) && (
          <div className="rounded-xl border bg-card overflow-hidden">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>User</TableHead>
                  <TableHead>Organisation</TableHead>
                  <TableHead>User List</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Last Login</TableHead>
                  <TableHead>Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {loading
                  ? Array.from({ length: 3 }).map((_, i) => (
                      <TableRow key={i}>
                        {Array.from({ length: 6 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}
                      </TableRow>
                    ))
                  : users.length === 0
                  ? (
                      <TableRow>
                        <TableCell colSpan={6} className="text-center text-muted-foreground py-8">No users found</TableCell>
                      </TableRow>
                    )
                  : users.map(user => (
                      <TableRow key={user.id}>
                        <TableCell>
                          <div>
                            <p className="font-medium">{user.display_name ?? user.username}</p>
                            <p className="text-xs text-muted-foreground">{user.email}</p>
                            <p className="text-xs text-muted-foreground font-mono">{user.username}#{user.discriminator}</p>
                          </div>
                        </TableCell>
                        <TableCell className="text-sm">{user.org_name}</TableCell>
                        <TableCell className="text-sm text-muted-foreground">{user.user_list_name}</TableCell>
                        <TableCell>
                          {user.active
                            ? <Badge variant="success"><CheckCircle className="h-3 w-3 mr-1" />Active</Badge>
                            : <Badge variant="destructive"><XCircle className="h-3 w-3 mr-1" />Disabled</Badge>
                          }
                        </TableCell>
                        <TableCell className="text-sm text-muted-foreground">{fmtDate(user.last_login_at)}</TableCell>
                        <TableCell>
                          <div className="flex gap-2">
                            <Button size="sm" variant="outline" onClick={() => handleDisable(user)}>
                              <UserX className="h-3 w-3" />
                              {user.active ? 'Disable' : 'Enable'}
                            </Button>
                            <Button size="sm" variant="outline" onClick={() => handleLogout(user.id)}>
                              <LogOut className="h-3 w-3" />Logout
                            </Button>
                          </div>
                        </TableCell>
                      </TableRow>
                    ))
                }
              </TableBody>
            </Table>
          </div>
        )}
      </div>
    </div>
  );
}
