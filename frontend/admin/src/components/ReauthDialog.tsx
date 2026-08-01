import { useState } from 'react';
import ReauthDialogView from './ReauthDialogView';
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
export type Pending = {
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

  /**
   * Sends one user-typed proof. The three catch branches are not interchangeable: only a fresh
   * reauth demand means the proof itself was wrong. The final `else` is the case where the proof
   * worked and the mutation failed for its own reasons — close the prompt and let the page report
   * it, rather than telling the user their password was wrong.
   */
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
        setPending(null);
        pending.settle(e);
      }
    } finally {
      setBusy(false);
    }
  };

  const dialog = (
    <ReauthDialogView
      pending={pending}
      error={error}
      busy={busy}
      onSubmit={submit}
      onCancel={() => { pending?.settle(); setPending(null); setError(''); }}
    />
  );

  return { guard, dialog };
}
