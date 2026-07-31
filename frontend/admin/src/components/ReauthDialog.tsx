import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { ApiError, reauthMethods } from '@/auth';
import type { MfaReauth } from '@/auth';

/**
 * Re-authentication prompt for the MFA mutations (R-24).
 *
 * The flow is optimistic: run the mutation with no proof, and only prompt if the backend asks.
 * That keeps first enrolment (which needs no proof) a single step, and means the prompt offers
 * exactly the methods the server said this account has rather than the ones we guessed.
 *
 * Two things the backend does that the UI has to respect:
 *  - a failed proof charges a rate limiter, and enough of them lock the account out;
 *  - a TOTP code that verifies is burned by the anti-replay cache and can never be sent again.
 * So nothing here retries by itself and the input is cleared after every attempt — the next
 * attempt is always a fresh, user-typed proof.
 */
type Pending = {
  methods: string[];
  run: (proof: MfaReauth) => Promise<unknown>;
  /** Settles the caller's `await guard(...)`. Cancelling is not an error the page should report. */
  settle: (failure?: unknown) => void;
};

export function useReauth() {
  const [pending, setPending] = useState<Pending | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  /**
   * Runs `action` and, if the backend demands re-authentication, opens the prompt and re-runs it
   * with the proof the user supplies. Any other failure propagates to the caller unchanged, so a
   * failed mutation is never reported as a failed password.
   *
   * The returned promise covers the whole flow, prompt included — it settles when the mutation
   * finally succeeds, fails for a reason the prompt cannot fix, or the user cancels. Resolving it
   * early (when the prompt opens) would leave the caller's `catch` out of scope by the time the
   * mutation runs for real, and a mutation that failed after a good proof would be reported to
   * nobody.
   */
  const guard = async (action: (proof?: MfaReauth) => Promise<unknown>) => {
    try {
      await action();
    } catch (e) {
      const methods = reauthMethods(e);
      if (!methods) throw e;
      setError('');
      await new Promise<void>((resolve, reject) => {
        setPending({ methods, run: action, settle: failure => failure ? reject(failure) : resolve() });
      });
    }
  };

  const submit = async (proof: MfaReauth) => {
    if (!pending) return;
    setBusy(true);
    try {
      await pending.run(proof);
      setPending(null);
      pending.settle();
    } catch (e) {
      if (reauthMethods(e)) {
        setError(proof.totp_code
          ? 'That code did not verify. Wait for your app to show the next code — a code can only be used once.'
          : 'That password is not correct.');
      } else if (e instanceof ApiError && e.status === 429) {
        setError('Too many failed attempts. Wait a few minutes before trying again.');
      } else {
        // The proof worked; the mutation itself failed. Close and let the page report it.
        setPending(null);
        pending.settle(e);
      }
    } finally {
      setBusy(false);
    }
  };

  const dialog = (
    <ReauthDialog
      pending={pending}
      error={error}
      busy={busy}
      onSubmit={submit}
      onCancel={() => { pending?.settle(); setPending(null); setError(''); }}
    />
  );

  return { guard, dialog };
}

function ReauthDialog({ pending, error, busy, onSubmit, onCancel }: Readonly<{
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
  // Locked out by the rate limiter — a further attempt only extends the block.
  const blocked = error.startsWith('Too many');

  const handleSubmit = async (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    const proof: MfaReauth = code ? { totp_code: code } : { current_password: password };
    setPassword('');
    setCode('');
    await onSubmit(proof);
  };

  return (
    <Dialog open={pending !== null} onOpenChange={open => { if (!open) onCancel(); }}>
      <DialogContent>
        <form onSubmit={handleSubmit}>
          <DialogHeader>
            <DialogTitle>Confirm it&apos;s you</DialogTitle>
            <DialogDescription>
              This change affects an existing second factor, so it needs proof you still hold one.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-4">
            {error && <Alert variant="destructive" className="text-sm py-2 px-3">{error}</Alert>}
            {canPassword && (
              <div className="space-y-2">
                <Label htmlFor="reauth-password">Current password</Label>
                <Input
                  id="reauth-password" type="password" autoComplete="current-password"
                  value={password} onChange={e => setPassword(e.target.value)}
                  disabled={busy || blocked || code.length > 0}
                />
              </div>
            )}
            {canPassword && canTotp && (
              <p className="text-xs text-muted-foreground">or</p>
            )}
            {canTotp && (
              <div className="space-y-2">
                <Label htmlFor="reauth-totp">Authenticator code</Label>
                <Input
                  id="reauth-totp" inputMode="numeric" autoComplete="one-time-code"
                  placeholder="000000" maxLength={6}
                  className="font-mono w-32 text-center text-lg tracking-widest"
                  value={code}
                  onChange={e => setCode(e.target.value.replaceAll(/\D/g, '').slice(0, 6))}
                  disabled={busy || blocked || password.length > 0}
                />
              </div>
            )}
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onCancel}>Cancel</Button>
            <Button type="submit" disabled={busy || blocked || (!password && code.length !== 6)}>
              {busy ? 'Verifying…' : 'Confirm'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
