import { useState } from 'react';
import { Search, UserX, LogOut, CheckCircle, XCircle, UserRoundCog, Copy, TriangleAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Skeleton } from '@/components/ui/skeleton';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from '@/components/ui/dialog';
import { Select, SelectTrigger, SelectValue, SelectContent, SelectItem } from '@/components/ui/select';
import { searchUsers, disableUser, enableUser, forceLogoutUser, listAdminOrgProjects, impersonateUser } from '@/api';
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
  org_id: string | null;
}

interface Project {
  id: string;
  name: string;
  slug: string;
  active: boolean;
}

interface ImpersonateResult {
  token: string;
  expires_in_minutes: number;
  user_id: string;
  project_id: string;
  warning: string;
}

export default function SystemUsers() {
  const [users, setUsers] = useState<User[]>([]);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState('');
  const [searched, setSearched] = useState(false);

  // Impersonation dialog state
  const [impUser, setImpUser] = useState<User | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);
  const [projectsLoading, setProjectsLoading] = useState(false);
  const [selectedProject, setSelectedProject] = useState('');
  const [impersonating, setImpersonating] = useState(false);
  const [impResult, setImpResult] = useState<ImpersonateResult | null>(null);
  const [copied, setCopied] = useState(false);

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

  const openImpersonateDialog = async (user: User) => {
    setImpUser(user);
    setSelectedProject('');
    setImpResult(null);
    setCopied(false);
    if (user.org_id) {
      setProjectsLoading(true);
      try {
        const res = await listAdminOrgProjects(user.org_id);
        setProjects(res ?? []);
      } catch { setProjects([]); } finally { setProjectsLoading(false); }
    } else {
      setProjects([]);
    }
  };

  const closeImpersonateDialog = () => {
    setImpUser(null);
    setImpResult(null);
    setProjects([]);
    setSelectedProject('');
    setCopied(false);
  };

  const handleImpersonate = async () => {
    if (!impUser || !selectedProject) return;
    setImpersonating(true);
    try {
      const res = await impersonateUser(impUser.id, selectedProject);
      setImpResult(res);
    } finally { setImpersonating(false); }
  };

  const handleCopy = () => {
    if (!impResult) return;
    navigator.clipboard.writeText(impResult.token);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
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
                            <Button size="sm" variant="outline" className="text-amber-600 border-amber-300 hover:bg-amber-50 dark:hover:bg-amber-950" onClick={() => openImpersonateDialog(user)}>
                              <UserRoundCog className="h-3 w-3" />Impersonate
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

      {/* Impersonation dialog — project picker */}
      <Dialog open={!!impUser && !impResult} onOpenChange={open => { if (!open) closeImpersonateDialog(); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Impersonate User</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 py-2">
            <div className="rounded-md border border-amber-300 bg-amber-50 dark:bg-amber-950 dark:border-amber-700 p-3 flex gap-2 text-sm text-amber-800 dark:text-amber-300">
              <TriangleAlert className="h-4 w-4 shrink-0 mt-0.5" />
              <span>You are about to impersonate <strong>{impUser?.email}</strong>. This action is audit-logged. The token is valid for 15 minutes.</span>
            </div>
            <div className="space-y-1">
              <p className="text-sm font-medium">Select project to impersonate into</p>
              {projectsLoading
                ? <Skeleton className="h-9 w-full" />
                : (
                  <Select value={selectedProject} onValueChange={setSelectedProject}>
                    <SelectTrigger>
                      <SelectValue placeholder="Choose a project…" />
                    </SelectTrigger>
                    <SelectContent>
                      {projects.length === 0
                        ? <SelectItem value="__none__" disabled>No projects available</SelectItem>
                        : projects.map(p => (
                          <SelectItem key={p.id} value={p.id}>{p.name}</SelectItem>
                        ))
                      }
                    </SelectContent>
                  </Select>
                )
              }
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={closeImpersonateDialog}>Cancel</Button>
            <Button
              variant="destructive"
              disabled={!selectedProject || selectedProject === '__none__' || impersonating}
              onClick={handleImpersonate}
            >
              {impersonating ? 'Generating…' : 'Generate token'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Impersonation result dialog — token reveal */}
      <Dialog open={!!impResult} onOpenChange={open => { if (!open) closeImpersonateDialog(); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Impersonation Token</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 py-2">
            <div className="rounded-md border border-destructive bg-destructive/10 p-3 flex gap-2 text-sm text-destructive">
              <TriangleAlert className="h-4 w-4 shrink-0 mt-0.5" />
              <span><strong>Warning:</strong> This token grants full access as the target user. It expires in {impResult?.expires_in_minutes ?? 15} minutes. Do not share it.</span>
            </div>
            <div className="space-y-1">
              <p className="text-xs text-muted-foreground font-medium uppercase tracking-wide">Token</p>
              <div className="flex gap-2 items-start">
                <code className="flex-1 break-all rounded bg-muted px-3 py-2 text-xs font-mono select-all">
                  {impResult?.token}
                </code>
                <Button size="sm" variant="outline" onClick={handleCopy} className="shrink-0">
                  <Copy className="h-3 w-3" />{copied ? 'Copied!' : 'Copy'}
                </Button>
              </div>
            </div>
            <p className="text-xs text-muted-foreground">{impResult?.warning}</p>
          </div>
          <DialogFooter>
            <Button onClick={closeImpersonateDialog}>Done</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
