import { useState } from 'react';

import { verifyTotp } from '../api';

export default function MfaChallenge() {
  // const challenge = params.get('login_challenge');
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      const res = await verifyTotp(code);
      if (res.error) { setError('Invalid or expired code. Try again.'); return; }
      if (res.redirect_to) window.location.href = res.redirect_to;
    } catch {
      setError('Something went wrong. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="card">
      <h1 className="card-title">Two-factor auth</h1>
      <p className="card-subtitle">Enter the 6-digit code from your authenticator app</p>
      {error && <div className="alert alert-error">{error}</div>}
      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <input className="otp-input" type="text" inputMode="numeric" pattern="\d{6}" maxLength={6}
            value={code} onChange={e => setCode(e.target.value.replace(/\D/g, ''))}
            required autoFocus placeholder="000000" />
        </div>
        <button className="btn" type="submit" disabled={loading || code.length !== 6}>
          {loading ? 'Verifying…' : 'Verify'}
        </button>
      </form>
    </div>
  );
}
