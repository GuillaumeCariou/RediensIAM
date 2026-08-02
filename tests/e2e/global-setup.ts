import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { APP_URL, CONSOLE_URL } from './playwright.config';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SECRETS = path.resolve(HERE, '../../deploy/rediensiam/values.secret.yaml');

export interface Credentials { email: string; password: string }

/**
 * The bootstrap administrator.
 *
 * Environment first, so a suite can be pointed at any deployment. Otherwise the dev secrets file
 * the installer wrote, which is the whole reason a developer does not have to configure anything
 * before running these: `./deploy/setup.sh --dev` produced both the deployment and this account.
 */
export function credentials(): Credentials {
  const email    = process.env.TEST_SUPER_ADMIN_EMAIL;
  const password = process.env.TEST_SUPER_ADMIN_PASSWORD;
  if (email && password) return { email, password };

  if (!fs.existsSync(SECRETS)) {
    throw new Error(
      `No credentials. Set TEST_SUPER_ADMIN_EMAIL and TEST_SUPER_ADMIN_PASSWORD, or install a dev\n` +
      `deployment with ./deploy/setup.sh --dev, which writes them to ${SECRETS}.`,
    );
  }
  const text = fs.readFileSync(SECRETS, 'utf8');
  const read = (key: string) => {
    const m = new RegExp(`^\\s*${key}:\\s*(.+)$`, 'm').exec(text);
    // Values are quoted with whichever quote does not appear in the generated password.
    return m ? m[1].trim().replace(/^['"]|['"]$/g, '') : null;
  };
  const fromFile = { email: read('bootstrapEmail'), password: read('bootstrapPassword') };
  if (!fromFile.email || !fromFile.password) {
    throw new Error(`bootstrapEmail / bootstrapPassword not found in ${SECRETS}`);
  }
  return fromFile as Credentials;
}

/**
 * Fails the run before a single test does, with the reason.
 *
 * A suite that needs a deployment and finds none produces dozens of timeouts that all say
 * "waiting for locator" and none of which say "nothing is running".
 */
export default async function globalSetup() {
  for (const [name, url] of [['app', `${APP_URL}/health`], ['console', `${CONSOLE_URL}/console/config`]] as const) {
    let res: Response;
    try {
      res = await fetch(url);
    } catch (e) {
      throw new Error(
        `The ${name} is not reachable at ${url} (${(e as Error).message}).\n` +
        `These tests need a running deployment: ./deploy/setup.sh --dev`,
      );
    }
    if (!res.ok) throw new Error(`${url} answered ${res.status}; expected a healthy deployment.`);
  }

  // Prove the credentials before a single test uses them. A stale .env — this suite shipped with
  // one dated six months before the deployment it was pointed at — otherwise turns into a dozen
  // timeouts whose page all read "Invalid email or password", none of which name the file.
  const { email, password } = credentials();
  const authorize = new URL(`${APP_URL}/oauth2/auth`);
  authorize.search = new URLSearchParams({
    client_id: 'client_admin_system',
    response_type: 'code',
    scope: 'openid offline',
    redirect_uri: `${CONSOLE_URL}/console/callback`,
    state: 'globalsetupprobe01',
    code_challenge: 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',   // RFC 7636 test vector
    code_challenge_method: 'S256',
  }).toString();

  const redirect = await fetch(authorize, { redirect: 'manual' });
  const challenge = new URL(redirect.headers.get('location') ?? '', APP_URL).searchParams.get('login_challenge');
  if (!challenge) throw new Error(`${APP_URL} did not hand out a login challenge; is Hydra reachable?`);

  const probe = await fetch(`${APP_URL}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password, login_challenge: challenge }),
  });
  if (probe.status === 429) {
    // The per-IP failure budget is five attempts and is deliberately never cleared by a success,
    // so a run that failed to authenticate spends it for the next fifteen minutes. Say so, rather
    // than blaming the credentials it never got to check.
    throw new Error(
      `${APP_URL} is rate-limiting this address: five failed sign-ins inside the lockout window.\n` +
      `Wait it out (Security:LockoutMinutes, 15 by default) or, on a dev deployment, restart the\n` +
      `cache that holds the counters: kubectl delete pod -l app=rediensiam-dragonfly`,
    );
  }
  if (!probe.ok) {
    const source = process.env.TEST_SUPER_ADMIN_EMAIL
      ? 'TEST_SUPER_ADMIN_EMAIL / _PASSWORD (environment, possibly from tests/e2e/.env)'
      : SECRETS;
    throw new Error(
      `The administrator ${email} cannot sign in (${probe.status}). Credentials came from ${source}.\n` +
      `If the deployment was reinstalled, delete tests/e2e/.env so the installer's own secrets file is used.`,
    );
  }

  // eslint-disable-next-line no-console
  console.log(`\n  e2e → app ${APP_URL} · console ${CONSOLE_URL} · admin ${email}\n`);
}
