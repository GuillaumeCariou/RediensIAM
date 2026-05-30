import { useState } from 'react';
import { IamChip, IamAvatar } from '@/components/iam';
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

function MoreMenu({ onEdit, onSessions, onUnlock, locked }: Readonly<{
  onEdit: () => void; onSessions: () => void; onUnlock: () => void; locked: boolean;
}>) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ position: 'relative' }}>
      <button className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm"
        onClick={e => { e.stopPropagation(); setOpen(o => !o); }}>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/></svg>
      </button>
      {open && (
        <>
          <div role="none" style={{ position: 'fixed', inset: 0, zIndex: 49 }} onClick={() => setOpen(false)} onKeyDown={(e) => { if (e.key === 'Escape') setOpen(false); }} />
          <div style={{ position: 'absolute', right: 0, top: '100%', zIndex: 50, background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8, boxShadow: 'var(--shadow-md)', minWidth: 140, padding: 4 }}>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }}
              onClick={() => { setOpen(false); onEdit(); }}>Edit</button>
            <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13 }}
              onClick={() => { setOpen(false); onSessions(); }}>View sessions</button>
            {locked && (
              <button className="iam-btn iam-btn-ghost" style={{ width: '100%', justifyContent: 'flex-start', padding: '6px 10px', fontSize: 13, color: 'var(--warn)' }}
                onClick={() => { setOpen(false); onUnlock(); }}>Unlock account</button>
            )}
          </div>
        </>
      )}
    </div>
  );
}

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
      <div className="iam-page">
        {actionMsg && (
          <div style={{
            padding: '8px 14px', borderRadius: 8, marginBottom: 14, fontSize: 13,
            background: actionMsg.error ? 'var(--danger-soft)' : 'var(--success-soft)',
            color: actionMsg.error ? 'var(--danger)' : 'var(--success)',
            border: `1px solid ${actionMsg.error ? 'oklch(from var(--danger) l c h / 0.3)' : 'oklch(from var(--success) l c h / 0.3)'}`,
          }}>
            {actionMsg.text}
          </div>
        )}

        <div style={{ display: 'flex', gap: 8, maxWidth: 420, marginBottom: 14 }}>
          <div style={{ position: 'relative', flex: 1 }}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
              style={{ position: 'absolute', left: 10, top: '50%', transform: 'translateY(-50%)', color: 'var(--fg-subtle)', pointerEvents: 'none' }}>
              <circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>
            </svg>
            <input
              className="iam-input"
              style={{ paddingLeft: 30 }}
              placeholder="Search by email, username…"
              value={query}
              onChange={e => setQuery(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && doSearch()}
            />
          </div>
          <button className="iam-btn iam-btn-primary iam-btn-sm" onClick={doSearch} disabled={loading}>
            Search
          </button>
        </div>

        {(loading || searched) && (
          <div className="iam-card">
            <table className="iam-tbl">
              <thead>
                <tr>
                  <th>User</th>
                  <th>Organisation</th>
                  <th>User List</th>
                  <th>Status</th>
                  <th>Last Login</th>
                  <th style={{ width: 36 }}></th>
                </tr>
              </thead>
              <tbody>
                {(() => {
                  if (loading) return Array.from({ length: 3 }, (_, i) => (
                    <tr key={i}>
                      {Array.from({ length: 6 }, (_, j) => (
                        <td key={j}><div style={{ height: 14, background: 'var(--surface-2)', borderRadius: 4, width: '70%' }} /></td>
                      ))}
                    </tr>
                  ));
                  if (users.length === 0) return (
                    <tr><td colSpan={6}>
                      <div className="iam-empty">
                        <div className="iam-empty-title">No users found</div>
                      </div>
                    </td></tr>
                  );
                  return users.map(user => (
                    <tr key={user.id} style={{ cursor: 'pointer' }} onClick={() => openEdit(user)}>
                      <td>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                          <IamAvatar name={user.display_name ?? user.username} size="sm" />
                          <div>
                            <div style={{ fontWeight: 500, display: 'flex', alignItems: 'center', gap: 6 }}>
                              {user.display_name ?? user.username}
                              {isLocked(user) && <IamChip tone="danger">Locked</IamChip>}
                            </div>
                            <div style={{ fontSize: 11, color: 'var(--fg-muted)' }}>{user.email}</div>
                            <div className="iam-mono" style={{ fontSize: 10, color: 'var(--fg-subtle)' }}>{user.username}#{user.discriminator}</div>
                          </div>
                        </div>
                      </td>
                      <td style={{ fontSize: 13 }}>{user.org_name}</td>
                      <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{user.user_list_name}</td>
                      <td>
                        {user.active
                          ? <IamChip tone="success">Active</IamChip>
                          : <IamChip tone="danger">Disabled</IamChip>}
                      </td>
                      <td style={{ fontSize: 12, color: 'var(--fg-muted)' }}>{fmtDate(user.last_login_at)}</td>
                      <td onClick={e => e.stopPropagation()}>
                        <MoreMenu
                          onEdit={() => openEdit(user)}
                          onSessions={() => openSessions(user)}
                          onUnlock={() => handleUnlock(user)}
                          locked={isLocked(user)}
                        />
                      </td>
                    </tr>
                  ));
                })()}
              </tbody>
            </table>
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
