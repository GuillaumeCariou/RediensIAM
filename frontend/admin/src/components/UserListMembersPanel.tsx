import { useEffect, useState } from 'react';
import { UserPlus, Trash2, CheckCircle, XCircle, Plus, MoreHorizontal, LockOpen, Mail, Monitor } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Skeleton } from '@/components/ui/skeleton';
import { Alert, AlertDescription } from '@/components/ui/alert';
import {
  listSystemUserListMembers, listUserListMembers,
  addUserToList, removeSystemUserFromList, removeUserFromList,
  adminGetUser, adminUpdateUser,
  listProjectUsers, listRoles, assignRole, removeRole,
  resendInvite, unlockUser, getUserSessions, revokeAllUserSessions,
} from '@/api';
import { fmtDate } from '@/lib/utils';

interface Member {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
  invite_pending?: boolean; locked_until?: string | null;
}

interface Session {
  client_id: string; client_name?: string; granted_at?: string; expires_at?: string;
}

interface Role { id: string; name: string; }

interface Props {
  listId: string;
  title?: string;
  isSystemCtx?: boolean;
  projectId?: string;
  defaultRoleId?: string | null;
  onChanged?: () => void;
}

export default function UserListMembersPanel({
  listId, title = 'Members', isSystemCtx = false,
  projectId, defaultRoleId, onChanged,
}: Readonly<Props>) {
  const [members, setMembers] = useState<Member[]>([]);
  const [loading, setLoading] = useState(true);

  // ── Feedback banner ────────────────────────────────────────────
  const [actionMsg, setActionMsg] = useState<{ text: string; error?: boolean } | null>(null);

  // ── Role state ─────────────────────────────────────────────────
  const [memberRoles, setMemberRoles] = useState<Map<string, Role[]>>(new Map());
  const [availableRoles, setAvailableRoles] = useState<Role[]>([]);
  const [selectedRole, setSelectedRole] = useState('');
  const [roleSaving, setRoleSaving] = useState(false);

  // ── Add dialog ─────────────────────────────────────────────────
  const [addOpen, setAddOpen] = useState(false);
  const [addForm, setAddForm] = useState({ email: '', username: '', password: '', email_verified: false });
  const [addSaving, setAddSaving] = useState(false);

  // ── Remove dialog ──────────────────────────────────────────────
  const [removeTarget, setRemoveTarget] = useState<Member | null>(null);

  // ── Edit dialog ────────────────────────────────────────────────
  const [editTarget, setEditTarget] = useState<Member | null>(null);
  const [editForm, setEditForm] = useState({ email: '', username: '', display_name: '', phone: '', active: true, email_verified: false, clear_lock: false, new_password: '' });
  const [editLoading, setEditLoading] = useState(false);
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState('');

  // ── Sessions dialog ────────────────────────────────────────────
  const [sessionsUser, setSessionsUser] = useState<Member | null>(null);
  const [sessions, setSessions] = useState<Session[]>([]);
  const [sessionsLoading, setSessionsLoading] = useState(false);
  const [revokeAllLoading, setRevokeAllLoading] = useState(false);

  // ── Helpers ────────────────────────────────────────────────────
  const ctxListId = isSystemCtx ? null : listId;

  function flash(text: string, error = false) {
    setActionMsg({ text, error });
    setTimeout(() => setActionMsg(null), 3500);
  }

  const isLocked = (m: Member) =>
    !!m.locked_until && new Date(m.locked_until) > new Date();

  const loadMembers = async () => {
    const res = isSystemCtx
      ? await listSystemUserListMembers(listId)
      : await listUserListMembers(listId);
    setMembers(res.users ?? res ?? []);
  };

  const loadRoles = async () => {
    if (!projectId) return;
    const [usersRes, rolesRes] = await Promise.all([
      listProjectUsers(projectId),
      listRoles(projectId),
    ]);
    const projectUsers: { id: string; roles: Role[] }[] = usersRes.users ?? usersRes ?? [];
    const map = new Map<string, Role[]>();
    for (const u of projectUsers) map.set(u.id, u.roles ?? []);
    setMemberRoles(map);
    setAvailableRoles(rolesRes.roles ?? rolesRes ?? []);
  };

  const load = async () => {
    await Promise.all([loadMembers(), loadRoles()]);
  };

  useEffect(() => {
    setLoading(true);
    load().catch(console.error).finally(() => setLoading(false));
  }, [listId, projectId]);

  // ── Handlers ───────────────────────────────────────────────────

  const handleAdd = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setAddSaving(true);
    try {
      await addUserToList(listId, addForm);
      setAddOpen(false);
      setAddForm({ email: '', username: '', password: '', email_verified: false });
      await load();
      onChanged?.();
    } finally { setAddSaving(false); }
  };

  const handleRemove = async () => {
    if (!removeTarget) return;
    if (isSystemCtx) await removeSystemUserFromList(listId, removeTarget.id);
    else await removeUserFromList(listId, removeTarget.id);
    setMembers(m => m.filter(u => u.id !== removeTarget.id));
    setRemoveTarget(null);
    onChanged?.();
  };

  const openEdit = async (m: Member) => {
    setEditTarget(m); setEditError(''); setEditLoading(true);
    setSelectedRole('');
    try {
      const u = await adminGetUser(m.id);
      setEditForm({
        email: u.email ?? '', username: u.username ?? '',
        display_name: u.display_name ?? '', phone: u.phone ?? '',
        active: u.active ?? true, email_verified: u.email_verified ?? false,
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
      setEditTarget(null);
      await load();
      onChanged?.();
    } catch { setEditError('Failed to save changes.'); }
    finally { setEditSaving(false); }
  };

  const handleAssignRole = async () => {
    if (!editTarget || !selectedRole || !projectId) return;
    setRoleSaving(true);
    try {
      await assignRole(projectId, editTarget.id, selectedRole);
      setSelectedRole('');
      await loadRoles();
    } finally { setRoleSaving(false); }
  };

  const handleRemoveRole = async (userId: string, roleId: string) => {
    if (!projectId) return;
    await removeRole(projectId, userId, roleId);
    setMemberRoles(prev => {
      const next = new Map(prev);
      next.set(userId, (next.get(userId) ?? []).filter(r => r.id !== roleId));
      return next;
    });
  };

  const handleResendInvite = async (m: Member) => {
    try {
      const res = await resendInvite(listId, m.id);
      if (res.error === 'user_already_active') {
        flash('This user has already accepted their invitation.', true);
      } else {
        flash(`Invite resent to ${m.email}.`);
      }
    } catch { flash('Failed to resend invite.', true); }
  };

  const handleUnlock = async (m: Member) => {
    try {
      await unlockUser(ctxListId, m.id);
      flash('Account unlocked.');
      await loadMembers();
    } catch { flash('Failed to unlock account.', true); }
  };

  const openSessions = async (m: Member) => {
    setSessionsUser(m);
    setSessions([]);
    setSessionsLoading(true);
    try {
      const res = await getUserSessions(ctxListId, m.id);
      setSessions(res.sessions ?? res ?? []);
    } catch { setSessions([]); }
    finally { setSessionsLoading(false); }
  };

  const handleRevokeAllSessions = async () => {
    if (!sessionsUser) return;
    setRevokeAllLoading(true);
    try {
      await revokeAllUserSessions(ctxListId, sessionsUser.id);
      setSessions([]);
      flash('All sessions revoked.');
    } catch { flash('Failed to revoke sessions.', true); }
    finally { setRevokeAllLoading(false); }
  };

  const userRoles = (userId: string) => memberRoles.get(userId) ?? [];
  const unassignedRoles = (userId: string) =>
    availableRoles.filter(r => !userRoles(userId).some(ur => ur.id === r.id));

  return (
    <>
      {/* ── Feedback banner ── */}
      {actionMsg && (
        <Alert variant={actionMsg.error ? 'destructive' : 'default'} className="mb-3">
          <AlertDescription>{actionMsg.text}</AlertDescription>
        </Alert>
      )}

      <Card>
        <CardHeader className="pb-3 flex flex-row items-center justify-between">
          <CardTitle className="text-sm font-medium">{title}</CardTitle>
          <Button size="sm" onClick={() => setAddOpen(true)}>
            <UserPlus className="h-4 w-4" />Add User
          </Button>
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                <TableHead>Status</TableHead>
                {projectId && <TableHead>Roles</TableHead>}
                <TableHead>Last Login</TableHead>
                <TableHead className="w-12"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading
                ? Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={i}>{Array.from({ length: projectId ? 5 : 4 }).map((__, j) => <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>)}</TableRow>
                  ))
                : members.length === 0
                ? <TableRow><TableCell colSpan={projectId ? 5 : 4} className="text-center text-muted-foreground py-8">No members yet</TableCell></TableRow>
                : members.map(m => (
                    <TableRow
                      key={m.id}
                      className="cursor-pointer hover:bg-muted/50"
                      onClick={() => openEdit(m)}
                    >
                      <TableCell>
                        <div className="flex items-center gap-2 flex-wrap">
                          <div>
                            <p className="font-medium text-sm">{m.display_name ?? m.username}#{m.discriminator}</p>
                            <p className="text-xs text-muted-foreground">{m.email}</p>
                          </div>
                          {m.invite_pending && (
                            <Badge variant="outline" className="text-amber-600 border-amber-400 text-[10px]">
                              Invite pending
                            </Badge>
                          )}
                          {isLocked(m) && (
                            <Badge variant="destructive" className="text-[10px]">
                              Locked
                            </Badge>
                          )}
                        </div>
                      </TableCell>
                      <TableCell>
                        {m.active
                          ? <Badge variant="success"><CheckCircle className="h-3 w-3 mr-1" />Active</Badge>
                          : <Badge variant="destructive"><XCircle className="h-3 w-3 mr-1" />Disabled</Badge>
                        }
                      </TableCell>
                      {projectId && (
                        <TableCell onClick={e => e.stopPropagation()}>
                          <div className="flex flex-wrap items-center gap-1">
                            {userRoles(m.id).map(r => (
                              <Badge key={r.id} variant="secondary" className="gap-1 pr-1">
                                {r.name}
                                {r.id === defaultRoleId && <span className="text-[10px] opacity-60 ml-0.5">default</span>}
                                <button
                                  onClick={() => handleRemoveRole(m.id, r.id)}
                                  className="ml-0.5 rounded-full hover:bg-muted-foreground/20 p-0.5"
                                >
                                  <Trash2 className="h-2.5 w-2.5" />
                                </button>
                              </Badge>
                            ))}
                            {userRoles(m.id).length === 0 && (
                              <span className="text-xs text-muted-foreground">No roles</span>
                            )}
                          </div>
                        </TableCell>
                      )}
                      <TableCell className="text-sm text-muted-foreground">{fmtDate(m.last_login_at)}</TableCell>
                      <TableCell onClick={e => e.stopPropagation()}>
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button size="sm" variant="ghost">
                              <MoreHorizontal className="h-4 w-4" />
                            </Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent align="end">
                            <DropdownMenuItem onClick={() => openEdit(m)}>
                              Edit
                            </DropdownMenuItem>
                            <DropdownMenuItem onClick={() => openSessions(m)}>
                              <Monitor className="h-4 w-4 mr-2" />View sessions
                            </DropdownMenuItem>
                            {m.invite_pending && (
                              <DropdownMenuItem onClick={() => handleResendInvite(m)}>
                                <Mail className="h-4 w-4 mr-2" />Resend invite
                              </DropdownMenuItem>
                            )}
                            {isLocked(m) && (
                              <DropdownMenuItem onClick={() => handleUnlock(m)} className="text-amber-600">
                                <LockOpen className="h-4 w-4 mr-2" />Unlock account
                              </DropdownMenuItem>
                            )}
                            <DropdownMenuSeparator />
                            <DropdownMenuItem
                              className="text-destructive focus:text-destructive"
                              onClick={() => setRemoveTarget(m)}
                            >
                              <Trash2 className="h-4 w-4 mr-2" />Remove
                            </DropdownMenuItem>
                          </DropdownMenuContent>
                        </DropdownMenu>
                      </TableCell>
                    </TableRow>
                  ))
              }
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      {/* ── Add dialog ── */}
      <Dialog open={addOpen} onOpenChange={setAddOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add User</DialogTitle>
            <DialogDescription>Create a new user account in this list.</DialogDescription>
          </DialogHeader>
          <form onSubmit={handleAdd} className="space-y-4">
            <div className="space-y-2"><Label>Email</Label><Input type="email" value={addForm.email} onChange={e => setAddForm(f => ({ ...f, email: e.target.value }))} required autoFocus /></div>
            <div className="space-y-2"><Label>Username</Label><Input value={addForm.username} onChange={e => setAddForm(f => ({ ...f, username: e.target.value }))} required /></div>
            <div className="space-y-2"><Label>Password</Label><Input type="password" autoComplete="new-password" value={addForm.password} onChange={e => setAddForm(f => ({ ...f, password: e.target.value }))} required minLength={8} /></div>
            <div className="flex items-center justify-between">
              <Label>Email verified</Label>
              <Switch checked={addForm.email_verified} onCheckedChange={v => setAddForm(f => ({ ...f, email_verified: v }))} />
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setAddOpen(false)}>Cancel</Button>
              <Button type="submit" disabled={addSaving}>{addSaving ? 'Adding…' : 'Add User'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

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

                {/* ── Roles section ── */}
                {projectId && editTarget && (
                  <div className="space-y-2 pt-1 border-t">
                    <Label>Project Roles</Label>
                    <div className="flex flex-wrap gap-1 min-h-6">
                      {userRoles(editTarget.id).map(r => (
                        <Badge key={r.id} variant="secondary" className="gap-1 pr-1">
                          {r.name}
                          {r.id === defaultRoleId && <span className="text-[10px] opacity-60 ml-0.5">default</span>}
                          <button
                            type="button"
                            onClick={() => handleRemoveRole(editTarget.id, r.id)}
                            className="ml-0.5 rounded-full hover:bg-muted-foreground/20 p-0.5"
                          >
                            <Trash2 className="h-2.5 w-2.5" />
                          </button>
                        </Badge>
                      ))}
                      {userRoles(editTarget.id).length === 0 && (
                        <span className="text-xs text-muted-foreground">No roles assigned</span>
                      )}
                    </div>
                    {unassignedRoles(editTarget.id).length > 0 && (
                      <div className="flex gap-2">
                        <Select value={selectedRole} onValueChange={setSelectedRole}>
                          <SelectTrigger className="flex-1">
                            <SelectValue placeholder="Add a role…" />
                          </SelectTrigger>
                          <SelectContent>
                            {unassignedRoles(editTarget.id).map(r => (
                              <SelectItem key={r.id} value={r.id}>
                                {r.name}
                                {r.id === defaultRoleId && <span className="text-muted-foreground ml-1 text-xs">(default)</span>}
                              </SelectItem>
                            ))}
                          </SelectContent>
                        </Select>
                        <Button type="button" size="sm" disabled={!selectedRole || roleSaving} onClick={handleAssignRole}>
                          <Plus className="h-3 w-3" />{roleSaving ? '…' : 'Add'}
                        </Button>
                      </div>
                    )}
                  </div>
                )}

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
                  <TableRow>
                    <TableHead>App</TableHead>
                    <TableHead>Granted</TableHead>
                  </TableRow>
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

      {/* ── Remove confirmation ── */}
      <AlertDialog open={!!removeTarget} onOpenChange={v => !v && setRemoveTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove {removeTarget?.email}?</AlertDialogTitle>
            <AlertDialogDescription>This will permanently delete the user account.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleRemove} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">Remove</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
