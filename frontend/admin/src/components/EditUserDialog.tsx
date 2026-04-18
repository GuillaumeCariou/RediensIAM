import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Skeleton } from '@/components/ui/skeleton';

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
    <Dialog open={open} onOpenChange={v => { if (!v) onClose(); }}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Edit {targetLabel}</DialogTitle>
          <DialogDescription>Update this account's information. Leave password blank to keep it unchanged.</DialogDescription>
        </DialogHeader>
        {loading
          ? <div className="space-y-3 py-2">{Array.from({ length: 5 }, (_, i) => `sk-${i}`).map(id => <Skeleton key={id} className="h-8 w-full" />)}</div>
          : (
            <form onSubmit={onSubmit} className="space-y-4">
              {error && <p className="text-sm text-destructive">{error}</p>}
              <div className="grid grid-cols-2 gap-3">
                <div className="space-y-2">
                  <Label>Email</Label>
                  <Input type="email" value={form.email} onChange={e => onChange('email', e.target.value)} required />
                </div>
                <div className="space-y-2">
                  <Label>Username</Label>
                  <Input value={form.username} onChange={e => onChange('username', e.target.value)} required />
                </div>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div className="space-y-2">
                  <Label>Display name</Label>
                  <Input value={form.display_name} onChange={e => onChange('display_name', e.target.value)} placeholder="Optional" />
                </div>
                <div className="space-y-2">
                  <Label>Phone</Label>
                  <Input value={form.phone} onChange={e => onChange('phone', e.target.value)} placeholder="Optional" />
                </div>
              </div>
              <div className="space-y-2">
                <Label>New password</Label>
                <Input type="password" autoComplete="new-password" value={form.new_password} onChange={e => onChange('new_password', e.target.value)} placeholder="Leave blank to keep current" minLength={8} />
              </div>
              <div className="flex flex-col gap-3 pt-1">
                <div className="flex items-center justify-between"><Label>Active</Label><Switch checked={form.active} onCheckedChange={v => onChange('active', v)} /></div>
                <div className="flex items-center justify-between"><Label>Email verified</Label><Switch checked={form.email_verified} onCheckedChange={v => onChange('email_verified', v)} /></div>
                <div className="flex items-center justify-between"><Label>Clear account lock</Label><Switch checked={form.clear_lock} onCheckedChange={v => onChange('clear_lock', v)} /></div>
              </div>
              {extra}
              <DialogFooter>
                <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
                <Button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save changes'}</Button>
              </DialogFooter>
            </form>
          )
        }
      </DialogContent>
    </Dialog>
  );
}
