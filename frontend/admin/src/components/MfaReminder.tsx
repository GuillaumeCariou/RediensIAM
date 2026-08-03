import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { ShieldAlert } from 'lucide-react';
import { getMfaStatus, listWebAuthnCredentials } from '@/api';

/**
 * Standing reminder for an admin with no second factor.
 *
 * The first administrator of a deployment signs in without one, because a first launch has to be
 * able to reach this console in order to configure the SMTP and SMS providers that make a factor
 * deliverable — gating that login on enrolment locks the operator out of the fix. Every
 * administrator after the first is sent through enrolment instead, so this banner is what stands
 * between exactly one account and a password on its own: not blocked on the first page, told on
 * every page.
 *
 * It stays until a factor exists. There is no dismiss button on purpose — a reminder you can
 * silence is one you silence on day one and never see again.
 */
export default function MfaReminder() {
  const [hasFactor, setHasFactor] = useState<boolean | null>(null);

  useEffect(() => {
    Promise.all([getMfaStatus(), listWebAuthnCredentials().catch(() => [])])
      .then(([mfa, creds]: [{ totp_enabled: boolean; phone_verified: boolean }, unknown]) =>
        setHasFactor(mfa.totp_enabled || mfa.phone_verified || (Array.isArray(creds) && creds.length > 0)))
      // A reminder that cannot read the status says nothing rather than crying wolf.
      .catch(() => setHasFactor(true));
  }, []);

  if (hasFactor !== false) return null;

  return (
    <div className="iam-alert iam-alert-warn" style={{ margin: '12px 24px 0', alignItems: 'center' }}>
      <ShieldAlert className="h-4 w-4" style={{ flex: 'none' }} />
      <span style={{ flex: 1 }}>
        This account has no second factor. An authenticator app needs no SMTP or SMS provider and
        takes a minute to set up.
      </span>
      <Link className="iam-btn iam-btn-secondary iam-btn-sm" to="/account">Set up MFA</Link>
    </div>
  );
}
