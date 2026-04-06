import { useState, useEffect } from 'react';
import { verifyTotp, verifyBackupCode, verifySmsOtp, sendSmsOtp, getWebAuthnOptions, verifyWebAuthn } from '../api';

type MfaMode = 'totp' | 'backup' | 'sms' | 'webauthn';

export default function MfaChallenge() {
  const initialMfaType = (sessionStorage.getItem('mfa_type') ?? 'totp') as MfaMode;
  const phoneHint = sessionStorage.getItem('mfa_phone_hint') ?? '';

  const [mode, setMode]       = useState<MfaMode>(initialMfaType);
  const [code, setCode]       = useState('');
  const [error, setError]     = useState('');
  const [loading, setLoading] = useState(false);
  const [resent, setResent]   = useState(false);

  useEffect(() => {
    if (mode === 'webauthn') handleWebAuthn();
  }, [mode]);

  async function handleWebAuthn() {
    setLoading(true);
    setError('');
    try {
      const options = await getWebAuthnOptions();
      if (options.error) { setError('Failed to get passkey options.'); return; }

      // Decode base64url challenge and allowCredentials
      options.challenge = base64urlToBuffer(options.challenge);
      if (options.allowCredentials) {
        options.allowCredentials = options.allowCredentials.map((c: { id: string }) => ({
          ...c, id: base64urlToBuffer(c.id)
        }));
      }

      const assertion = await navigator.credentials.get({ publicKey: options }) as PublicKeyCredential;
      if (!assertion) { setError('No credential returned.'); return; }

      const response = assertion.response as AuthenticatorAssertionResponse;
      const body = {
        id:    assertion.id,
        rawId: bufferToBase64url(assertion.rawId),
        type:  assertion.type,
        response: {
          authenticatorData: bufferToBase64url(response.authenticatorData),
          clientDataJSON:    bufferToBase64url(response.clientDataJSON),
          signature:         bufferToBase64url(response.signature),
          userHandle:        response.userHandle ? bufferToBase64url(response.userHandle) : null,
        }
      };

      const res = await verifyWebAuthn(body);
      if (res.error) { setError('Passkey verification failed. Try again.'); return; }
      if (res.redirect_to) {
        sessionStorage.removeItem('mfa_type');
        sessionStorage.removeItem('mfa_phone_hint');
        globalThis.location.href = res.redirect_to;
      }
    } catch (e: unknown) {
      if (e instanceof Error && e.name === 'NotAllowedError') {
        setError('Passkey prompt was cancelled or timed out.');
      } else {
        setError('Something went wrong. Try a different method.');
      }
    } finally {
      setLoading(false);
    }
  }

  async function handleSubmit(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      let res;
      if (mode === 'totp')   res = await verifyTotp(code);
      else if (mode === 'sms') res = await verifySmsOtp(code);
      else                   res = await verifyBackupCode(code);

      if (res.error) {
        setError(mode === 'backup'
          ? 'Invalid backup code. Check the code and try again.'
          : 'Invalid or expired code. Try again.');
        return;
      }
      if (res.redirect_to) {
        sessionStorage.removeItem('mfa_type');
        sessionStorage.removeItem('mfa_phone_hint');
        globalThis.location.href = res.redirect_to;
      }
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  async function handleResend() {
    setResent(false);
    await sendSmsOtp();
    setResent(true);
  }

  function switchMode(next: MfaMode) {
    setMode(next);
    setCode('');
    setError('');
    setResent(false);
  }

  // ── WebAuthn mode ────────────────────────────────────────────────
  if (mode === 'webauthn') {
    return (
      <div className="card">
        <h1 className="card-title">Passkey sign-in</h1>
        <p className="card-subtitle">Use your device passkey or security key to continue.</p>
        {error && <div className="alert alert-error">{error}</div>}
        <button className="btn" type="button" disabled={loading} onClick={handleWebAuthn}>
          {loading ? 'Waiting for passkey…' : 'Use passkey'}
        </button>
        <div className="mt-4 text-center" style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
          <button className="link" type="button" onClick={() => switchMode('backup')}>Use a backup code instead</button>
        </div>
      </div>
    );
  }

  return (
    <div className="card">
      {mode === 'totp' && (
        <>
          <h1 className="card-title">Two-factor auth</h1>
          <p className="card-subtitle">Enter the 6-digit code from your authenticator app</p>
        </>
      )}
      {mode === 'sms' && (
        <>
          <h1 className="card-title">SMS verification</h1>
          <p className="card-subtitle">
            Code sent to {phoneHint || 'your phone'}
          </p>
        </>
      )}
      {mode === 'backup' && (
        <>
          <h1 className="card-title">Use a backup code</h1>
          <p className="card-subtitle">Enter one of your 8-character backup codes</p>
        </>
      )}

      {error && <div className="alert alert-error">{error}</div>}
      {resent && <div className="alert alert-success">Code resent!</div>}

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          {mode === 'backup' ? (
            <input
              className="input" type="text" autoFocus required
              placeholder="XXXXXXXX" maxLength={8} autoComplete="off"
              value={code} onChange={e => setCode(e.target.value.toUpperCase().replaceAll(/[^A-Z0-9]/g, ''))}
            />
          ) : (
            <input
              className="otp-input" type="text" inputMode="numeric"
              pattern="\d{6}" maxLength={6} autoFocus required
              placeholder="000000"
              value={code} onChange={e => setCode(e.target.value.replaceAll(/\D/g, ''))}
            />
          )}
        </div>
        <button
          className="btn" type="submit"
          disabled={loading || (mode === 'backup' ? code.length !== 8 : code.length !== 6)}
        >
          {loading ? 'Verifying…' : 'Verify'}
        </button>
      </form>

      <div className="mt-4 text-center" style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
        {mode === 'sms' && (
          <button className="link" type="button" onClick={handleResend}>Resend code</button>
        )}
        {mode !== 'backup' && (
          <button className="link" type="button" onClick={() => switchMode('backup')}>
            Use a backup code instead
          </button>
        )}
        {mode === 'backup' && initialMfaType !== 'backup' && (
          <button className="link" type="button" onClick={() => switchMode(initialMfaType)}>
            Back to {(() => {
              if (initialMfaType === 'sms') return 'SMS code';
              if (initialMfaType === 'webauthn') return 'passkey';
              return 'authenticator app';
            })()}
          </button>
        )}
        {initialMfaType === 'webauthn' && (
          <button className="link" type="button" onClick={() => switchMode('webauthn')}>Use passkey instead</button>
        )}
      </div>
    </div>
  );
}

// ── WebAuthn buffer helpers ──────────────────────────────────────────────────
function base64urlToBuffer(b64: string): ArrayBuffer {
  const bin = atob(b64.replaceAll('-', '+').replaceAll('_', '/'));
  const buf = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) buf[i] = bin.codePointAt(i)!;
  return buf.buffer;
}

function bufferToBase64url(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let str = '';
  for (const b of bytes) str += String.fromCodePoint(b);
  return btoa(str).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}
