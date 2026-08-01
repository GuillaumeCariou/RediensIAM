import { useEffect, useState, useCallback } from 'react';
import { Shield, UserPlus, Trash2, Pencil } from 'lucide-react';
import {
  listOrgAdmins, assignOrgAdmin, removeOrgAdmin,
  listOrgListManagers, assignOrgListManager, removeOrgListManager,
  adminGetUser, adminUpdateUser, orgGetUser, orgUpdateUser,
  listProjects,
} from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';
import EditUserDialog from '@/components/EditUserDialog';
import type { UserEditFields } from '@/components/EditUserDialog';
import { IamChip, IamDialog } from '@/components/iam';

interface OrgRole {
  id: string; user_id: string; user_email: string; user_name: string;
  role: string; scope_id: string | null; scope_name: string | null;
  granted_at: string; active?: boolean; last_login_at?: string | null;
}
interface Project { id: string; name: string; }

const BLANK_FORM: UserEditFields = { email: '', username: '', display_name: '', phone: '', active: true, email_verified: false, clear_lock: false, new_password: '' };

export default function OrgAdminsPage() {
  const { orgId, isSystemCtx } = useOrgContext();

  const [roles, setRoles] = useState<OrgRole[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);

  const [assignOpen, setAssignOpen] = useState(false);
  const [assignForm, setAssignForm] = useState({ user_id: '', role: 'org_admin', scope_id: '' });
  const [assignSaving, setAssignSaving] = useState(false);

  const [editTarget, setEditTarget] = useState<{ id: string; label: string } | null>(null);
  const [editForm, setEditForm] = useState<UserEditFields>(BLANK_FORM);
  const [editLoading, setEditLoading] = useState(false);
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState('');

  const [removeTarget, setRemoveTarget] = useState<{ id: string; label: string } | null>(null);

  const load = useCallback(async () => {
    if (!orgId) return;
    setLoading(true);
    try {
      const [admins, projs] = await Promise.all([
        isSystemCtx
          ? listOrgAdmins(orgId).then(r => r.admins ?? r ?? [])
          : listOrgListManagers().then(r => r.admins ?? r ?? []),
        listProjects(orgId).then(r => r.projects ?? r ?? []),
      ]);
      setRoles(admins);
      setProjects(projs);
    } finally { setLoading(false); }
  }, [orgId, isSystemCtx]);

  useEffect(() => { load(); }, [load]);

  const handleAssign = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setAssignSaving(true);
    try {
      if (isSystemCtx) await assignOrgAdmin(orgId, assignForm.user_id, assignForm.role, assignForm.scope_id || undefined);
      else await assignOrgListManager({ user_id: assignForm.user_id, role: assignForm.role, scope_id: assignForm.scope_id || undefined });
      setAssignOpen(false);
      setAssignForm({ user_id: '', role: 'org_admin', scope_id: '' });
      load();
    } finally { setAssignSaving(false); }
  };

  const openEdit = async (userId: string, label: string) => {
    setEditTarget({ id: userId, label });
    setEditError(''); setEditLoading(true);
    try {
      const u = isSystemCtx ? await adminGetUser(userId) : await orgGetUser(userId);
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
      const body = {
        email: editForm.email, username: editForm.username,
        display_name: editForm.display_name, phone: editForm.phone,
        active: editForm.active, email_verified: editForm.email_verified,
        clear_lock: editForm.clear_lock, new_password: editForm.new_password || undefined,
      };
      if (isSystemCtx) await adminUpdateUser(editTarget.id, body);
      else await orgUpdateUser(editTarget.id, body);
      setEditTarget(null);
      await load();
    } catch { setEditError('Failed to save changes.'); }
    finally { setEditSaving(false); }
  };

  const handleRemove = async () => {
    if (!removeTarget) return;
    if (isSystemCtx) await removeOrgAdmin(orgId, removeTarget.id);
    else await removeOrgListManager(removeTarget.id);
    setRemoveTarget(null);
    load();
  };

  return (
    <div>
      <PageHeader
        title="Organisation Admins"
        description="Manage who administers this organisation and its projects"
        action={orgId ? <button className="iam-btn iam-btn-primary" onClick={() => setAssignOpen(true)}><UserPlus className="h-4 w-4" />Assign Role</button> : undefined}
      />

      <div className="p-6">
        <div className="rounded-xl border bg-card overflow-hidden">
          <table className="iam-tbl">
            <thead>
              <tr>
                <th>User</th>
                <th>Role</th>
                <th>Scope</th>
                <th>Status</th>
                <th>Granted</th>
                <th className="w-20" />
              </tr>
            </thead>
            <tbody>
              {(() => {
                if (loading) return (
                  Array.from({ length: 3 }, (_, i) => `sk-row-${i}`).map(rowId => (
                    <tr key={rowId}>
                      {Array.from({ length: 6 }, (_, j) => `sk-cell-${j}`).map(cellId => <td key={cellId}><div className="iam-skeleton h-4 w-full" /></td>)}
                    </tr>
                  ))
                );
                if (roles.length === 0) return (
                  <tr>
                    <td className="text-center text-muted-foreground py-12" colSpan={6}>
                      <Shield className="h-8 w-8 mx-auto mb-2 opacity-40" />No admins assigned yet.
                    </td>
                  </tr>
                );
                return roles.map(r => (
                  <tr key={r.id}>
                    <td>
                      <p className="font-medium text-sm">{r.user_name}</p>
                      <p className="text-xs text-muted-foreground">{r.user_email}</p>
                    </td>
                    <td>
                      <IamChip tone={r.role === 'org_admin' ? 'accent' : 'default'}>{r.role}</IamChip>
                    </td>
                    <td className="text-sm text-muted-foreground">
                      {r.scope_name ?? (r.scope_id ? `${r.scope_id.slice(0, 8)}…` : 'Entire org')}
                    </td>
                    <td>
                      {r.active === undefined
                        ? <span className="text-muted-foreground text-xs">—</span>
                        : <IamChip tone={r.active ? 'success' : 'default'}>{r.active ? 'Active' : 'Disabled'}</IamChip>
                      }
                    </td>
                    <td className="text-sm text-muted-foreground">{fmtDate(r.granted_at)}</td>
                    <td>
                      <div className="flex items-center justify-end gap-1">
                        <button className="iam-btn iam-btn-ghost iam-btn-icon" onClick={() => openEdit(r.user_id, r.user_name)}>
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button className="iam-btn iam-btn-ghost iam-btn-icon text-destructive hover:text-destructive hover:bg-[var(--danger-soft)]" onClick={() => setRemoveTarget({ id: r.id, label: `${r.user_name} — ${r.role}` })}>
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  </tr>
                ));
              })()}
            </tbody>
          </table>
        </div>
      </div>

      <IamDialog open={assignOpen} onClose={() => setAssignOpen(false)}
      title="Assign Admin Role"
      desc="Grant a user administrative access to this organisation."
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={() => setAssignOpen(false)}>Cancel</button>
              <button className="iam-btn iam-btn-primary" type="submit" form="orgadminspage-form" disabled={assignSaving}>{assignSaving ? 'Assigning…' : 'Assign'}</button></>}
    >
<form id="orgadminspage-form" onSubmit={handleAssign} className="space-y-4">
            <div className="space-y-2"><label className="iam-label">User ID</label><input className="iam-input" value={assignForm.user_id} onChange={e => setAssignForm(f => ({ ...f, user_id: e.target.value }))} required placeholder="User UUID" /></div>
            <div className="space-y-2">
              <label className="iam-label">Role</label>
              <select className="iam-select" value={assignForm.role} onChange={e => (v => setAssignForm(f => ({ ...f, role: v, scope_id: '' })))(e.target.value)}>
<option value="org_admin">Org Admin</option>
                  <option value="project_admin">Project Admin</option>
</select>
            </div>
            {assignForm.role === 'project_admin' && (
              <div className="space-y-2">
                <label className="iam-label">Project scope</label>
                <select className="iam-select" value={assignForm.scope_id} onChange={e => (v => setAssignForm(f => ({ ...f, scope_id: v })))(e.target.value)}>
                  <option value="" disabled>Select a project…</option>
{projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
</select>
              </div>
            )}
            
          </form>
    </IamDialog>

      <EditUserDialog
        open={!!editTarget}
        targetLabel={editTarget?.label ?? ''}
        form={editForm}
        loading={editLoading}
        saving={editSaving}
        error={editError}
        onChange={(field, value) => setEditForm(f => ({ ...f, [field]: value }))}
        onSubmit={handleEdit}
        onClose={() => setEditTarget(null)}
      />

      <IamDialog open={!!removeTarget} onClose={() => (v => !v && setRemoveTarget(null))(false)}
      title={<>Remove {removeTarget?.label}?</>}
      desc="This will revoke this management role from the user."
      footer={<><button type="button" onClick={() => (v => !v && setRemoveTarget(null))(false)} className="iam-btn iam-btn-secondary">Cancel</button><button className="iam-btn iam-btn-danger" onClick={handleRemove}>Remove</button></>}
    >

    </IamDialog>
    </div>
  );
}
