import { IamDialog } from '@/components/iam';
export type UserEditFields = {
  email: string;
  username: string;
  display_name: string;
  phone: string;
  active: boolean;
  email_verified: boolean;
  clear_lock: boolean;
  new_password: string;
};

interface Props {
  open: boolean;
  targetLabel: string;
  form: UserEditFields;
  loading: boolean;
  saving: boolean;
  error: string;
  onChange: <K extends keyof UserEditFields>(field: K, value: UserEditFields[K]) => void;
  onSubmit: (e: React.SyntheticEvent<HTMLFormElement>) => void;
  onClose: () => void;
  extra?: React.ReactNode;
}

export default function EditUserDialog({ open, targetLabel, form, loading, saving, error, onChange, onSubmit, onClose, extra }: Readonly<Props>) {
  return (
    <IamDialog open={open} onClose={() => onClose()}
      title={<>Edit {targetLabel}</>}
      desc="Update this account's information. Leave password blank to keep it unchanged."
      footer={<><button className="iam-btn iam-btn-secondary" type="button" onClick={onClose}>Cancel</button>
                <button className="iam-btn iam-btn-primary" type="submit" form="edituserdialog-form" disabled={saving}>{saving ? 'Saving…' : 'Save changes'}</button></>}
    >
{loading
          ? <div className="space-y-3 py-2">{Array.from({ length: 5 }, (_, i) => `sk-${i}`).map(id => <div className="iam-skeleton h-8 w-full" key={id} />)}</div>
          : (
            <form id="edituserdialog-form" onSubmit={onSubmit} className="space-y-4">
              {error && <p className="text-sm text-destructive">{error}</p>}
              <div className="grid grid-cols-2 gap-3">
                <div className="space-y-2">
                  <label className="iam-label">Email</label>
                  <input className="iam-input" type="email" value={form.email} onChange={e => onChange('email', e.target.value)} required />
                </div>
                <div className="space-y-2">
                  <label className="iam-label">Username</label>
                  <input className="iam-input" value={form.username} onChange={e => onChange('username', e.target.value)} required />
                </div>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div className="space-y-2">
                  <label className="iam-label">Display name</label>
                  <input className="iam-input" value={form.display_name} onChange={e => onChange('display_name', e.target.value)} placeholder="Optional" />
                </div>
                <div className="space-y-2">
                  <label className="iam-label">Phone</label>
                  <input className="iam-input" value={form.phone} onChange={e => onChange('phone', e.target.value)} placeholder="Optional" />
                </div>
              </div>
              <div className="space-y-2">
                <label className="iam-label">New password</label>
                <input className="iam-input" type="password" autoComplete="new-password" value={form.new_password} onChange={e => onChange('new_password', e.target.value)} placeholder="Leave blank to keep current" minLength={8} />
              </div>
              <div className="flex flex-col gap-3 pt-1">
                <div className="flex items-center justify-between"><label className="iam-label">Active</label><input type="checkbox" className="iam-switch" checked={form.active} onChange={e => (v => onChange('active', v))(e.target.checked)} /></div>
                <div className="flex items-center justify-between"><label className="iam-label">Email verified</label><input type="checkbox" className="iam-switch" checked={form.email_verified} onChange={e => (v => onChange('email_verified', v))(e.target.checked)} /></div>
                <div className="flex items-center justify-between"><label className="iam-label">Clear account lock</label><input type="checkbox" className="iam-switch" checked={form.clear_lock} onChange={e => (v => onChange('clear_lock', v))(e.target.checked)} /></div>
              </div>
              {extra}
              
            </form>
          )
        }
    </IamDialog>
  );
}
