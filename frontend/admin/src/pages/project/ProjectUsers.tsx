import { useEffect, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { UserPlus, Trash2, CheckCircle, XCircle, LogOut } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Label } from '@/components/ui/label';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { listProjectUsers, listRoles, assignRole, removeRole, forceLogoutProjectUser } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface ProjectUser {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
  roles: { id: string; name: string }[];
}
interface Role { id: string; name: string; }

export default function ProjectUsers() {
  const { projectId } = useProjectContext();
  const [users, setUsers] = useState<ProjectUser[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');
  const [assignOpen, setAssignOpen] = useState<ProjectUser | null>(null);
  const [selectedRole, setSelectedRole] = useState('');
  const [saving, setSaving] = useState(false);

  const load = () => {
    if (!projectId) { setLoading(false); return; }
    setLoading(true);
    Promise.all([
      listProjectUsers(projectId).then(r => setUsers(r.users ?? r ?? [])),
      listRoles(projectId).then(r => setRoles(r.roles ?? r ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  };
  useEffect(load, [projectId]);

  const filtered = users.filter(u =>
    u.email.toLowerCase().includes(search.toLowerCase()) ||
    u.username.toLowerCase().includes(search.toLowerCase()) ||
    (u.display_name?.toLowerCase().includes(search.toLowerCase()) ?? false)
  );

  const handleAssign = async () => {
    if (!assignOpen || !selectedRole) return;
    setSaving(true);
    try {
      await assignRole(projectId, assignOpen.id, selectedRole);
      setAssignOpen(null);
      setSelectedRole('');
      load();
    } finally { setSaving(false); }
  };

  const handleRemoveRole = async (userId: string, roleId: string) => {
    await removeRole(projectId, userId, roleId);
    setUsers(prev => prev.map(u =>
      u.id === userId ? { ...u, roles: u.roles.filter(r => r.id !== roleId) } : u
    ));
  };

  const handleForceLogout = async (userId: string) => {
    await forceLogoutProjectUser(projectId, userId);
  };

  const availableRoles = (user: ProjectUser) =>
    roles.filter(r => !user.roles.some(ur => ur.id === r.id));

  return (
    <div>
      <PageHeader title="Project Users" description="Users and their role assignments in this project" />
      <div className="p-6 space-y-4">
        <Input
          placeholder="Search users…"
          value={search}
          onChange={e => setSearch(e.target.value)}
          className="max-w-sm"
        />
        <div className="rounded-xl border bg-card overflow-hidden">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Roles</TableHead>
                <TableHead>Last Login</TableHead>
                <TableHead>Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 5 }).map((_, i) => <TableRow key={i}>{Array.from({ length: 5 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>)
                : filtered.length === 0
                ? <TableRow><TableCell colSpan={5} className="text-center text-muted-foreground py-12">No users found</TableCell></TableRow>
                : filtered.map(user => (
                    <TableRow key={user.id}>
                      <TableCell>
                        <p className="font-medium text-sm">{user.display_name ?? user.username}#{user.discriminator}</p>
                        <p className="text-xs text-muted-foreground">{user.email}</p>
                      </TableCell>
                      <TableCell>
                        {user.active
                          ? <Badge variant="success"><CheckCircle className="h-3 w-3 mr-1" />Active</Badge>
                          : <Badge variant="destructive"><XCircle className="h-3 w-3 mr-1" />Disabled</Badge>
                        }
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-wrap gap-1">
                          {user.roles.length === 0
                            ? <span className="text-xs text-muted-foreground">No roles</span>
                            : user.roles.map(r => (
                                <Badge key={r.id} variant="secondary" className="gap-1 pr-1">
                                  {r.name}
                                  <button
                                    onClick={() => handleRemoveRole(user.id, r.id)}
                                    className="ml-0.5 rounded-full hover:bg-muted-foreground/20 p-0.5"
                                  >
                                    <Trash2 className="h-2.5 w-2.5" />
                                  </button>
                                </Badge>
                              ))
                          }
                        </div>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(user.last_login_at)}</TableCell>
                      <TableCell>
                        <div className="flex items-center gap-1">
                          {availableRoles(user).length > 0 && (
                            <Button
                              size="sm" variant="outline"
                              onClick={() => { setAssignOpen(user); setSelectedRole(''); }}
                            >
                              <UserPlus className="h-3 w-3" />Add Role
                            </Button>
                          )}
                          <Button
                            size="sm" variant="ghost"
                            className="text-destructive hover:text-destructive"
                            title="Force logout all sessions"
                            onClick={() => handleForceLogout(user.id)}
                          >
                            <LogOut className="h-3 w-3" />
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </div>
      </div>

      <Dialog open={!!assignOpen} onOpenChange={v => !v && setAssignOpen(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Assign Role</DialogTitle>
            <DialogDescription>Add a role to {assignOpen?.display_name ?? assignOpen?.username}.</DialogDescription>
          </DialogHeader>
          <div className="py-2 space-y-3">
            <div className="space-y-2">
              <Label>Role</Label>
              <Select value={selectedRole} onValueChange={setSelectedRole}>
                <SelectTrigger><SelectValue placeholder="Select a role" /></SelectTrigger>
                <SelectContent>
                  {assignOpen && availableRoles(assignOpen).map(r => (
                    <SelectItem key={r.id} value={r.id}>{r.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setAssignOpen(null)}>Cancel</Button>
            <Button onClick={handleAssign} disabled={saving || !selectedRole}>{saving ? 'Assigning…' : 'Assign'}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
