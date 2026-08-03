import { useState } from 'react';
import { IamDialog } from '@/components/iam';
import type { MfaReauth } from '@/auth';
import type { Pending } from './ReauthDialog';

export default function ReauthDialog({ pending, error, busy, onSubmit, onCancel }: Readonly<{
  pending: Pending | null;
  error: string;
  busy: boolean;
  onSubmit: (proof: MfaReauth) => Promise<void>;
  onCancel: () => void;
}>) {
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');

  const canPassword = pending?.methods.includes('current_password') ?? false;
  const canTotp     = pending?.methods.includes('totp_code') ?? false;
  /**
   * Locked out by the rate limiter — a further attempt only extends the block, so the form is
   * disabled rather than left inviting. Tied by prefix to the 429 message set in `submit`; change
   * one and you must change the other.
   */
  const blocked = error.startsWith('Too many');

  const handleSubmit = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    const proof: MfaReauth = code ? { totp_code: code } : { current_password: password };
    setPassword('');
    setCode('');
    await onSubmit(proof);
  };

  return (
    <IamDialog
      open={pending !== null}
      onClose={onCancel}
      title="Confirm it's you"
      desc="This change affects an existing second factor, so it needs proof you still hold one."
      footer={
        <>
          <button className="iam-btn iam-btn-secondary" type="button" onClick={onCancel}>Cancel</button>
          {/* Outside the <form>, so it submits by id — the native way to keep the footer separate. */}
          <button className="iam-btn iam-btn-primary" type="submit" form="reauth-form"
            disabled={busy || blocked || (!password && code.length !== 6)}>
            {busy ? 'Verifying…' : 'Confirm'}
          </button>
        </>
      }
    >
      <form id="reauth-form" onSubmit={handleSubmit}>
        <div className="space-y-4">
            {error && <div className="iam-alert iam-alert-danger text-sm py-2 px-3">{error}</div>}
            {canPassword && (
              <div className="space-y-2">
                <label className="iam-label" htmlFor="reauth-password">Current password</label>
                <input className="iam-input" id="reauth-password" type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} disabled={busy || blocked || code.length > 0} />
              </div>
            )}
            {canPassword && canTotp && (
              <p className="text-xs text-muted-foreground">or</p>
            )}
            {canTotp && (
              <div className="space-y-2">
                <label className="iam-label" htmlFor="reauth-totp">Authenticator code</label>
                <input className="iam-input font-mono w-32 text-center text-lg tracking-widest" id="reauth-totp" inputMode="numeric" autoComplete="one-time-code" placeholder="000000" maxLength={6} value={code} onChange={e => setCode(e.target.value.replaceAll(/\D/g, '').slice(0, 6))} disabled={busy || blocked || password.length > 0} />
              </div>
            )}
        </div>
      </form>
    </IamDialog>
  );
}
