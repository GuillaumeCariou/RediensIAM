import { useState } from 'react';
import { Search, CheckCircle, XCircle, MoreHorizontal, LockOpen, Monitor } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { searchUsers, adminGetUser, adminUpdateUser, unlockUser, getUserSessions, revokeAllUserSessions } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';
import EditUserDialog from '@/components/EditUserDialog';
import type { UserEditFields } from '@/components/EditUserDialog';
import SessionsDialog from '@/components/SessionsDialog';
import type { OAuthSession } from '@/components/SessionsDialog';

interface User {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
  org_name: string; user_list_name: string; org_id: string | null;
  locked_until?: string | null;
}

const BLANK_FORM: UserEditFields = { email: '', username: '', display_name: '', phone: '', active: true, email_verified: false, clear_lock: false, new_password: '' };

export default function SystemUsers() {
  const [users, setUsers] = useState<User[]>([]);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState('');
  const [searched, setSearched] = useState(false);
  const [actionMsg, setActionMsg] = useState<{ text: string; error?: boolean } | null>(null);

  const [editTarget, setEditTarget] = useState<User | null>(null);
  const [editForm, setEditForm] = useState<UserEditFields>(BLANK_FORM);
  const [editLoading, setEditLoading] = useState(false);
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState('');

  const [sessionsUser, setSessionsUser] = useState<User | null>(null);
  const [sessions, setSessions] = useState<OAuthSession[]>([]);
  const [sessionsLoading, setSessionsLoading] = useState(false);
  const [revokeAllLoading, setRevokeAllLoading] = useState(false);

  function flash(text: string, error = false) {
    setActionMsg({ text, error });
    setTimeout(() => setActionMsg(null), 3500);
  }

  const isLocked = (u: User) => !!u.locked_until && new Date(u.locked_until) > new Date();

  const doSearch = async () => {
    if (!query.trim()) return;
    setLoading(true); setSearched(true);
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
        clear_lock: editForm.clear_lock, new_password: editForm.new_password || undefined,
      });
      setUsers(prev => prev.map(u => u.id === editTarget.id
        ? { ...u, active: editForm.active, display_name: editForm.display_name || null } : u));
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
          <Input placeholder="Search by email, username…" value={query} onChange={e => setQuery(e.target.value)} onKeyDown={e => e.key === 'Enter' && doSearch()} />
          <Button onClick={doSearch} disabled={loading}><Search className="h-4 w-4" />Search</Button>
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
                {(() => {
                  if (loading) return (
                    Array.from({ length: 3 }, (_, i) => `sk-row-${i}`).map(rowId => (
                      <TableRow key={rowId}>
                        {Array.from({ length: 6 }, (_, j) => `sk-cell-${j}`).map(cellId => <TableCell key={cellId}><Skeleton className="h-4 w-full" /></TableCell>)}
                      </TableRow>
                    ))
                  );
                  if (users.length === 0) return (
                    <TableRow><TableCell colSpan={6} className="text-center text-muted-foreground py-8">No users found</TableCell></TableRow>
                  );
                  return users.map(user => (
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
                  ));
                })()}
              </TableBody>
            </Table>
          </div>
        )}
      </div>

      <EditUserDialog
        open={!!editTarget}
        targetLabel={editTarget ? `${editTarget.username}#${editTarget.discriminator}` : ''}
        form={editForm}
        loading={editLoading}
        saving={editSaving}
        error={editError}
        onChange={(field, value) => setEditForm(f => ({ ...f, [field]: value }))}
        onSubmit={handleEdit}
        onClose={() => setEditTarget(null)}
      />

      <SessionsDialog
        userEmail={sessionsUser?.email ?? null}
        sessions={sessions}
        loading={sessionsLoading}
        revokeAllLoading={revokeAllLoading}
        onClose={() => { setSessionsUser(null); setSessions([]); }}
        onRevokeAll={handleRevokeAllSessions}
      />
    </div>
  );
}
