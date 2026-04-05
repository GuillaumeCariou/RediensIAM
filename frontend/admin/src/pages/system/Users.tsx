import { useState } from 'react';
import { Search, CheckCircle, XCircle, MoreHorizontal, LockOpen, Monitor } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Switch } from '@/components/ui/switch';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { Alert, AlertDescription } from '@/components/ui/alert';
import {
  searchUsers, adminGetUser, adminUpdateUser,
  unlockUser, getUserSessions, revokeAllUserSessions,
} from '@/api';
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
  locked_until?: string | null;
}

interface Session {
  client_id: string; client_name?: string; granted_at?: string;
}

export default function SystemUsers() {
  const [users, setUsers] = useState<User[]>([]);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState('');
  const [searched, setSearched] = useState(false);
  const [actionMsg, setActionMsg] = useState<{ text: string; error?: boolean } | null>(null);

  // ── Edit dialog ────────────────────────────────────────────────
  const [editTarget, setEditTarget] = useState<User | null>(null);
  const [editForm, setEditForm] = useState({ email: '', username: '', display_name: '', phone: '', active: true, email_verified: false, clear_lock: false, new_password: '' });
  const [editLoading, setEditLoading] = useState(false);
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState('');

  // ── Sessions dialog ────────────────────────────────────────────
  const [sessionsUser, setSessionsUser] = useState<User | null>(null);
  const [sessions, setSessions] = useState<Session[]>([]);
  const [sessionsLoading, setSessionsLoading] = useState(false);
  const [revokeAllLoading, setRevokeAllLoading] = useState(false);

  function flash(text: string, error = false) {
    setActionMsg({ text, error });
    setTimeout(() => setActionMsg(null), 3500);
  }

  const isLocked = (u: User) => !!u.locked_until && new Date(u.locked_until) > new Date();

  const doSearch = async () => {
    if (!query.trim()) return;
    setLoading(true);
    setSearched(true);
    try {
      const res = await searchUsers(query);
      setUsers(res.users ?? res ?? []);
    } catch { setUsers([]); } finally { setLoading(false); }
  };

  const openEdit = async (u: User) => {
    setEditTarget(u); setEditError(''); setEditLoading(true);
    try {
      const data = await adminGetUser(u.id);
      setEditForm({
        email: data.email ?? '', username: data.username ?? '',
        display_name: data.display_name ?? '', phone: data.phone ?? '',
        active: data.active ?? true, email_verified: data.email_verified ?? false,
        clear_lock: false, new_password: '',
      });
    } catch { setEditError('Failed to load user details.'); }
    finally { setEditLoading(false); }
  };

  const handleEdit = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!editTarget) return;
    setEditSaving(true); setEditError('');
    try {
      await adminUpdateUser(editTarget.id, {
        email: editForm.email, username: editForm.username,
        display_name: editForm.display_name, phone: editForm.phone,
        active: editForm.active, email_verified: editForm.email_verified,
        clear_lock: editForm.clear_lock,
        new_password: editForm.new_password || undefined,
      });
      setUsers(prev => prev.map(u => u.id === editTarget.id
        ? { ...u, active: editForm.active, display_name: editForm.display_name || null }
        : u
      ));
      setEditTarget(null);
    } catch { setEditError('Failed to save changes.'); }
    finally { setEditSaving(false); }
  };

  const handleUnlock = async (u: User) => {
    try {
      await unlockUser(null, u.id);
      setUsers(prev => prev.map(x => x.id === u.id ? { ...x, locked_until: null } : x));
      flash('Account unlocked.');
    } catch { flash('Failed to unlock account.', true); }
  };

  const openSessions = async (u: User) => {
    setSessionsUser(u); setSessions([]); setSessionsLoading(true);
    try {
      const res = await getUserSessions(null, u.id);
      setSessions(res.sessions ?? res ?? []);
    } catch { setSessions([]); }
    finally { setSessionsLoading(false); }
  };

  const handleRevokeAllSessions = async () => {
    if (!sessionsUser) return;
    setRevokeAllLoading(true);
    try {
      await revokeAllUserSessions(null, sessionsUser.id);
      setSessions([]);
      flash('All sessions revoked.');
    } catch { flash('Failed to revoke sessions.', true); }
    finally { setRevokeAllLoading(false); }
  };

  return (
    <div>
      <PageHeader title="Global User Search" description="Search and manage users across all organisations" />
      <div className="p-6 space-y-4">
        {actionMsg && (
          <Alert variant={actionMsg.error ? 'destructive' : 'default'}>
            <AlertDescription>{actionMsg.text}</AlertDescription>
          </Alert>
        )}

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
                  <TableHead className="w-12"></TableHead>
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
                      <TableRow key={user.id} className="cursor-pointer hover:bg-muted/50" onClick={() => openEdit(user)}>
                        <TableCell>
                          <div className="flex items-center gap-2 flex-wrap">
                            <div>
                              <p className="font-medium">{user.display_name ?? user.username}</p>
                              <p className="text-xs text-muted-foreground">{user.email}</p>
                              <p className="text-xs text-muted-foreground font-mono">{user.username}#{user.discriminator}</p>
                            </div>
                            {isLocked(user) && <Badge variant="destructive" className="text-[10px]">Locked</Badge>}
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
                        <TableCell onClick={e => e.stopPropagation()}>
                          <DropdownMenu>
                            <DropdownMenuTrigger asChild>
                              <Button size="sm" variant="ghost"><MoreHorizontal className="h-4 w-4" /></Button>
                            </DropdownMenuTrigger>
                            <DropdownMenuContent align="end">
                              <DropdownMenuItem onClick={() => openEdit(user)}>Edit</DropdownMenuItem>
                              <DropdownMenuItem onClick={() => openSessions(user)}>
                                <Monitor className="h-4 w-4 mr-2" />View sessions
                              </DropdownMenuItem>
                              {isLocked(user) && (
                                <DropdownMenuItem onClick={() => handleUnlock(user)} className="text-amber-600">
                                  <LockOpen className="h-4 w-4 mr-2" />Unlock account
                                </DropdownMenuItem>
                              )}
                            </DropdownMenuContent>
                          </DropdownMenu>
                        </TableCell>
                      </TableRow>
                    ))
                }
              </TableBody>
            </Table>
          </div>
        )}
      </div>

      {/* ── Edit dialog ── */}
      <Dialog open={!!editTarget} onOpenChange={v => { if (!v) setEditTarget(null); }}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Edit {editTarget?.username}#{editTarget?.discriminator}</DialogTitle>
            <DialogDescription>Update this account's information. Leave password blank to keep it unchanged.</DialogDescription>
          </DialogHeader>
          {editLoading
            ? <div className="space-y-3 py-2">{Array.from({ length: 5 }).map((_, i) => <Skeleton key={i} className="h-8 w-full" />)}</div>
            : (
              <form onSubmit={handleEdit} className="space-y-4">
                {editError && <p className="text-sm text-destructive">{editError}</p>}
                <div className="grid grid-cols-2 gap-3">
                  <div className="space-y-2"><Label>Email</Label><Input type="email" value={editForm.email} onChange={e => setEditForm(f => ({ ...f, email: e.target.value }))} required /></div>
                  <div className="space-y-2"><Label>Username</Label><Input value={editForm.username} onChange={e => setEditForm(f => ({ ...f, username: e.target.value }))} required /></div>
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div className="space-y-2"><Label>Display name</Label><Input value={editForm.display_name} onChange={e => setEditForm(f => ({ ...f, display_name: e.target.value }))} placeholder="Optional" /></div>
                  <div className="space-y-2"><Label>Phone</Label><Input value={editForm.phone} onChange={e => setEditForm(f => ({ ...f, phone: e.target.value }))} placeholder="Optional" /></div>
                </div>
                <div className="space-y-2">
                  <Label>New password</Label>
                  <Input type="password" autoComplete="new-password" value={editForm.new_password} onChange={e => setEditForm(f => ({ ...f, new_password: e.target.value }))} placeholder="Leave blank to keep current" minLength={8} />
                </div>
                <div className="flex flex-col gap-3 pt-1">
                  <div className="flex items-center justify-between"><Label>Active</Label><Switch checked={editForm.active} onCheckedChange={v => setEditForm(f => ({ ...f, active: v }))} /></div>
                  <div className="flex items-center justify-between"><Label>Email verified</Label><Switch checked={editForm.email_verified} onCheckedChange={v => setEditForm(f => ({ ...f, email_verified: v }))} /></div>
                  <div className="flex items-center justify-between"><Label>Clear account lock</Label><Switch checked={editForm.clear_lock} onCheckedChange={v => setEditForm(f => ({ ...f, clear_lock: v }))} /></div>
                </div>
                <DialogFooter>
                  <Button type="button" variant="outline" onClick={() => setEditTarget(null)}>Cancel</Button>
                  <Button type="submit" disabled={editSaving}>{editSaving ? 'Saving…' : 'Save changes'}</Button>
                </DialogFooter>
              </form>
            )
          }
        </DialogContent>
      </Dialog>

      {/* ── Sessions dialog ── */}
      <Dialog open={!!sessionsUser} onOpenChange={v => { if (!v) setSessionsUser(null); }}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>Active sessions — {sessionsUser?.email}</DialogTitle>
            <DialogDescription>OAuth2 applications this user has granted access to.</DialogDescription>
          </DialogHeader>
          {sessionsLoading
            ? <div className="space-y-2 py-2">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-8 w-full" />)}</div>
            : sessions.length === 0
            ? <p className="text-sm text-muted-foreground py-4 text-center">No active sessions.</p>
            : (
              <Table>
                <TableHeader>
                  <TableRow><TableHead>App</TableHead><TableHead>Granted</TableHead></TableRow>
                </TableHeader>
                <TableBody>
                  {sessions.map(s => (
                    <TableRow key={s.client_id}>
                      <TableCell className="text-sm font-medium">{s.client_name ?? s.client_id}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(s.granted_at ?? null)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )
          }
          <DialogFooter>
            <Button variant="outline" onClick={() => setSessionsUser(null)}>Close</Button>
            <Button
              variant="destructive"
              disabled={revokeAllLoading || sessions.length === 0}
              onClick={handleRevokeAllSessions}
            >
              {revokeAllLoading ? 'Revoking…' : 'Revoke all sessions'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
