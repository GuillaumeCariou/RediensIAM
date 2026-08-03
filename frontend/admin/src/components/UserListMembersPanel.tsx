import { rowActivation } from './iam/rowActivation';
import { useCallback, useEffect, useId, useState } from 'react';
import { UserPlus, Trash2, Plus, MoreHorizontal } from 'lucide-react';
import {
  listSystemUserListMembers, listUserListMembers,
  addUserToList, removeSystemUserFromList, removeUserFromList,
  adminGetUser, adminUpdateUser,
  listProjectUsers, listRoles, assignRole, removeRole,
  resendInvite, unlockUser, getUserSessions, revokeAllUserSessions,
} from '@/api';
import { fmtDate } from '@/lib/utils';
import EditUserDialog from '@/components/EditUserDialog';
import type { UserEditFields } from '@/components/EditUserDialog';
import SessionsDialog from '@/components/SessionsDialog';
import type { OAuthSession } from '@/components/SessionsDialog';
import { IamChip, IamDialog, IamMenu } from '@/components/iam';

interface Member {
  id: string; email: string; username: string; discriminator: string;
  display_name: string | null; active: boolean; last_login_at: string | null;
  invite_pending?: boolean; locked_until?: string | null;
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

const BLANK_FORM: UserEditFields = { email: '', username: '', display_name: '', phone: '', active: true, email_verified: false, clear_lock: false, new_password: '' };

function MemberStatusBadge({ member, isLocked }: Readonly<{ member: Member; isLocked: (m: Member) => boolean }>) {
  if (member.invite_pending) return <IamChip tone="default">Invite pending</IamChip>;
  if (isLocked(member)) return <IamChip tone="danger">Locked</IamChip>;
  if (member.active) return <IamChip tone="accent">Active</IamChip>;
  return <IamChip tone="danger">Inactive</IamChip>;
}

export default function UserListMembersPanel({
  listId, title = 'Members', isSystemCtx = false,
  projectId, defaultRoleId, onChanged,
}: Readonly<Props>) {
  // A page can show more than one panel (an org list beside a project list), so the ids that tie
  // a <label for> to its field have to be per-instance rather than constants.
  const uid = useId();
  const [members, setMembers] = useState<Member[]>([]);
  const [loading, setLoading] = useState(true);
  const [actionMsg, setActionMsg] = useState<{ text: string; error?: boolean } | null>(null);

  const [memberRoles, setMemberRoles] = useState<Map<string, Role[]>>(new Map());
  const [availableRoles, setAvailableRoles] = useState<Role[]>([]);
  const [selectedRole, setSelectedRole] = useState('');
  const [roleSaving, setRoleSaving] = useState(false);

  const [addOpen, setAddOpen] = useState(false);
  const [addForm, setAddForm] = useState({ email: '', username: '', password: '', email_verified: false });
  const [addSaving, setAddSaving] = useState(false);

  const [removeTarget, setRemoveTarget] = useState<Member | null>(null);

  const [editTarget, setEditTarget] = useState<Member | null>(null);
  const [editForm, setEditForm] = useState<UserEditFields>(BLANK_FORM);
  const [editLoading, setEditLoading] = useState(false);
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState('');

  const [sessionsUser, setSessionsUser] = useState<Member | null>(null);
  const [sessions, setSessions] = useState<OAuthSession[]>([]);
  const [sessionsLoading, setSessionsLoading] = useState(false);
  const [revokeAllLoading, setRevokeAllLoading] = useState(false);

  const ctxListId = isSystemCtx ? null : listId;

  function flash(text: string, error = false) {
    setActionMsg({ text, error });
    setTimeout(() => setActionMsg(null), 3500);
  }

  const isLocked = (m: Member) => !!m.locked_until && new Date(m.locked_until) > new Date();

  const loadMembers = useCallback(async () => {
    const res = isSystemCtx ? await listSystemUserListMembers(listId) : await listUserListMembers(listId);
    setMembers(res.users ?? res ?? []);
  }, [isSystemCtx, listId]);

  const loadRoles = useCallback(async () => {
    if (!projectId) return;
    const [usersRes, rolesRes] = await Promise.all([listProjectUsers(projectId), listRoles(projectId)]);
    const projectUsers: { id: string; roles: Role[] }[] = usersRes.users ?? usersRes ?? [];
    const map = new Map<string, Role[]>();
    for (const u of projectUsers) map.set(u.id, u.roles ?? []);
    setMemberRoles(map);
    setAvailableRoles(rolesRes.roles ?? rolesRes ?? []);
  }, [projectId]);

  // Both loaders are memoised on what they actually read, so this one can depend on them by
  // identity rather than on a suppression asserting what they read.
  const load = useCallback(async () => { await Promise.all([loadMembers(), loadRoles()]); },
    [loadMembers, loadRoles]);

  useEffect(() => {
    setLoading(true);
    load().catch(console.error).finally(() => setLoading(false));
  }, [load]);

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
    setEditTarget(m); setEditError(''); setEditLoading(true); setSelectedRole('');
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
        clear_lock: editForm.clear_lock, new_password: editForm.new_password || undefined,
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
    try { await assignRole(projectId, editTarget.id, selectedRole); setSelectedRole(''); await loadRoles(); }
    finally { setRoleSaving(false); }
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
      if (res.error === 'user_already_active') flash('This user has already accepted their invitation.', true);
      else flash(`Invite resent to ${m.email}.`);
    } catch { flash('Failed to resend invite.', true); }
  };

  const handleUnlock = async (m: Member) => {
    try { await unlockUser(ctxListId, m.id); flash('Account unlocked.'); await loadMembers(); }
    catch { flash('Failed to unlock account.', true); }
  };

  const openSessions = async (m: Member) => {
    setSessionsUser(m); setSessions([]); setSessionsLoading(true);
    try { const res = await getUserSessions(ctxListId, m.id); setSessions(res.sessions ?? res ?? []); }
    catch { setSessions([]); }
    finally { setSessionsLoading(false); }
  };

  const handleRevokeAllSessions = async () => {
    if (!sessionsUser) return;
    setRevokeAllLoading(true);
    try { await revokeAllUserSessions(ctxListId, sessionsUser.id); setSessions([]); flash('All sessions revoked.'); }
    catch { flash('Failed to revoke sessions.', true); }
    finally { setRevokeAllLoading(false); }
  };

  const userRoles = (userId: string) => memberRoles.get(userId) ?? [];
  const unassignedRoles = (userId: string) => availableRoles.filter(r => !userRoles(userId).some(ur => ur.id === r.id));

  return (
    <>
      {actionMsg && (
        <div className={actionMsg.error ? 'iam-alert iam-alert-danger mb-3' : 'iam-alert mb-3'}>
          <div>{actionMsg.text}</div>
        </div>
      )}

      <div className="iam-card">
        <div className="iam-card-pad pb-0 pb-3 flex flex-row items-center justify-between">
          <h3 className="text-sm font-semibold text-sm font-medium">{title}</h3>
          <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={() => setAddOpen(true)}>
            <UserPlus className="h-4 w-4" />Add User
          </button>
        </div>
        <div className="iam-card-pad p-0">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>User</th>
                <th>Status</th>
                {projectId && <th>Roles</th>}
                <th>Last Login</th>
                <th className="w-12"></th>
              </tr>
            </thead>
            <tbody>
              {loading
                ? Array.from({ length: 3 }, (_, i) => (
                    <tr key={i}>
                      <td colSpan={projectId ? 5 : 4}><div className="iam-skeleton h-6 w-full" /></td>
                    </tr>
                  ))
                : members.map(m => (
                    <tr key={m.id} {...rowActivation(() => openEdit(m))}>
                      <td>
                        <p className="text-sm font-medium">{m.username}#{m.discriminator}</p>
                        <p className="text-xs text-muted-foreground">{m.email}</p>
                      </td>
                      <td><MemberStatusBadge member={m} isLocked={isLocked} /></td>
                      {projectId && (
                        <td>
                          <div className="flex flex-wrap gap-1">
                            {userRoles(m.id).map(r => <IamChip tone="default" key={r.id}>{r.name}</IamChip>)}
                          </div>
                        </td>
                      )}
                      <td className="text-sm text-muted-foreground">{fmtDate(m.last_login_at)}</td>
                      <td onClick={e => e.stopPropagation()}>
                        <IamMenu trigger={<MoreHorizontal className="h-4 w-4" />}>
<button type="button" className="iam-menu-item" onClick={() => openEdit(m)}>Edit</button>
                            <button type="button" className="iam-menu-item" onClick={() => openSessions(m)}>View sessions</button>
                            {m.invite_pending && <button type="button" className="iam-menu-item" onClick={() => handleResendInvite(m)}>Resend invite</button>}
                            {isLocked(m) && <button type="button" className="iam-menu-item" onClick={() => handleUnlock(m)}>Unlock account</button>}
                            <div className="iam-menu-sep" />
                            <button type="button" className="iam-menu-item iam-menu-item-danger" onClick={() => setRemoveTarget(m)}>Remove</button>
</IamMenu>
                      </td>
                    </tr>
                  ))
              }
            </tbody>
          </table>
        </div>
      </div>

      {/* ── Add dialog ── */}
      <IamDialog open={addOpen} onClose={() => setAddOpen(false)}
      title="Add User"
      desc="Create a new user account in this list."
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setAddOpen(false)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="userlistmemberspanel-form" disabled={addSaving}>{addSaving ? 'Adding…' : 'Add User'}</button></>}
    >
<form id="userlistmemberspanel-form" onSubmit={handleAdd} className="space-y-4">
            <div className="space-y-2"><label className="iam-label" htmlFor={`${uid}-add-email`}>Email</label><input className="iam-input" id={`${uid}-add-email`} type="email" value={addForm.email} onChange={e => setAddForm(f => ({ ...f, email: e.target.value }))} required autoFocus /></div>
            <div className="space-y-2"><label className="iam-label" htmlFor={`${uid}-add-username`}>Username</label><input className="iam-input" id={`${uid}-add-username`} value={addForm.username} onChange={e => setAddForm(f => ({ ...f, username: e.target.value }))} required /></div>
            <div className="space-y-2"><label className="iam-label" htmlFor={`${uid}-add-password`}>Password</label><input className="iam-input" id={`${uid}-add-password`} type="password" autoComplete="new-password" value={addForm.password} onChange={e => setAddForm(f => ({ ...f, password: e.target.value }))} required minLength={8} /></div>
            <div className="flex items-center justify-between">
              <label className="iam-label" htmlFor={`${uid}-add-email-verified`}>Email verified</label>
              <input id={`${uid}-add-email-verified`} type="checkbox" className="iam-switch" checked={addForm.email_verified} onChange={e => (v => setAddForm(f => ({ ...f, email_verified: v })))(e.target.checked)} />
            </div>
            
          </form>
    </IamDialog>

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
        extra={projectId && editTarget ? (
          <div className="space-y-2 pt-1 border-t">
            {/* A caption for the whole block — the chips, their remove buttons and the picker
                below — not a label for any one control, so it is not a <label>. */}
            <p className="iam-label">Project Roles</p>
            <div className="flex flex-wrap gap-1 min-h-6">
              {userRoles(editTarget.id).map(r => (
                <IamChip className="gap-1 pr-1" tone="default" key={r.id}>
                  {r.name}
                  {r.id === defaultRoleId && <span className="text-[10px] opacity-60 ml-0.5">default</span>}
                  <button type="button" onClick={() => handleRemoveRole(editTarget.id, r.id)} className="ml-0.5 rounded-full hover:bg-[var(--surface-2)] p-0.5">
                    <Trash2 className="h-2.5 w-2.5" />
                  </button>
                </IamChip>
              ))}
              {userRoles(editTarget.id).length === 0 && <span className="text-xs text-muted-foreground">No roles assigned</span>}
            </div>
            {unassignedRoles(editTarget.id).length > 0 && (
              <div className="flex gap-2">
                <select className="iam-select flex-1" aria-label="Project role to add" value={selectedRole} onChange={e => setSelectedRole(e.target.value)}>
                  <option value="" disabled>Select a role…</option>
{unassignedRoles(editTarget.id).map(r => (
                      <option key={r.id} value={r.id}>
                        {r.name}
                        {r.id === defaultRoleId && <span className="text-muted-foreground ml-1 text-xs">(default)</span>}
                      </option>
                    ))}
</select>
                <button className="iam-btn iam-btn-primary iam-btn-sm" type="button" disabled={!selectedRole || roleSaving} onClick={handleAssignRole}>
                  <Plus className="h-3 w-3" />{roleSaving ? '…' : 'Add'}
                </button>
              </div>
            )}
          </div>
        ) : undefined}
      />

      <SessionsDialog
        userEmail={sessionsUser?.email ?? null}
        sessions={sessions}
        loading={sessionsLoading}
        revokeAllLoading={revokeAllLoading}
        onClose={() => { setSessionsUser(null); setSessions([]); }}
        onRevokeAll={handleRevokeAllSessions}
      />

      {/* ── Remove confirmation ── */}
      <IamDialog open={!!removeTarget} onClose={() => (v => !v && setRemoveTarget(null))(false)}
      title={<>Remove {removeTarget?.email}?</>}
      desc="This will permanently delete the user account."
      footer={<><button type="button" onClick={() => (v => !v && setRemoveTarget(null))(false)} className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-danger" onClick={handleRemove}>Remove</button></>}
    >

    </IamDialog>
    </>
  );
}
