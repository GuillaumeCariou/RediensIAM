/**
 * The dev fixture: a deployment with known contents, created through the public management API.
 *
 * Every object here exists because a test needs it to *already be there*. A spec can create its
 * own organisation — several do — but it cannot create a suspended one, a tenant with two
 * projects, or a user list with fifty members without spending its whole runtime on setup and
 * asserting nothing about the page under test.
 *
 * Two rules this file keeps:
 *
 *   - **Idempotent.** Seeding twice leaves the same objects. A failed run is recovered with
 *     `--seed-only`, not a reinstall, and the names below are fixed rather than run-unique — which
 *     is the whole point: a test may assert `SEED.orgs.acme.name` is on screen.
 *   - **Through the API, never the database.** A row written past the controller is a row whose
 *     Keto tuple, Hydra client and audit entry do not exist, and the first test to touch it fails
 *     for a reason that has nothing to do with the test.
 *
 * Run by deploy/dev-fixture.sh. Reads the bootstrap administrator the same way global-setup.ts
 * does, from deploy/rediensiam/values.secret.yaml unless the environment overrides it.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// The admin ingress serves a certificate cert-manager signed for itself. Process-scoped, and this
// process talks to nothing but the deployment under test.
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SECRETS = path.resolve(HERE, '../../deploy/rediensiam/values.secret.yaml');

const APP_URL = process.env.TEST_APP_URL ?? 'http://iam.localhost';
const CONSOLE_URL = process.env.TEST_CONSOLE_URL ?? 'https://admin.iam.localhost';

/** RFC 7636 test vector: the verifier whose S256 challenge is the constant below. */
const VERIFIER = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk';
const CHALLENGE = 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM';

/**
 * The password every fixture user shares.
 *
 * A literal on purpose and not a secret: these accounts exist only on a dev deployment that
 * `dev-fixture.sh` destroys and rebuilds, and a spec asserting a wrong password is refused has to
 * know the right one. Overridable so a suite pointed at a deployment with a password policy of its
 * own can satisfy it.
 */
const FIXTURE_PASSWORD = process.env.SEED_USER_PASSWORD ?? 'Fixture-Passw0rd!';   // NOSONAR — dev fixture, see above

/**
 * What the deployment holds after seeding. Exported so specs import the names rather than
 * repeating string literals that drift from this file.
 */
export const SEED = {
  orgs: {
    acme:      { name: 'Acme Corporation', slug: 'acme' },
    globex:    { name: 'Globex Industries', slug: 'globex' },
    suspended: { name: 'Initech (suspended)', slug: 'initech' },
  },
  projects: {
    acmePortal:  { org: 'acme',   name: 'Customer Portal', slug: 'portal' },
    acmeInternal:{ org: 'acme',   name: 'Internal Tools',  slug: 'internal' },
    globexApp:   { org: 'globex', name: 'Globex App',      slug: 'app' },
  },
  userLists: {
    acmeStaff:    { org: 'acme',   name: 'Acme Staff' },
    acmeCustomers:{ org: 'acme',   name: 'Acme Customers' },
    globexStaff:  { org: 'globex', name: 'Globex Staff' },
  },
  /** Ordinary members of a tenant. They sign in to a project; the console is not for them. */
  users: {
    acmeAdmin:  { list: 'acmeStaff', email: 'admin@acme.test',  password: FIXTURE_PASSWORD, org: 'acme' },
    acmeUser:   { list: 'acmeStaff', email: 'user@acme.test',   password: FIXTURE_PASSWORD, org: 'acme' },
    acmeLocked: { list: 'acmeStaff', email: 'locked@acme.test', password: FIXTURE_PASSWORD, org: 'acme' },
  },

  /**
   * Console operators.
   *
   * In the SYSTEM list, and that is not a detail: `AdminLogin` admits only accounts whose user list
   * is the immovable one with no organisation. A tenant's administrator is therefore a deployment
   * account holding an `OrgRole` over that tenant — the role scopes what they see, membership of
   * the tenant's own list would not let them in at all. PLAN §12 is a matrix of refusals, and a
   * refusal cannot be asserted without an identity that is genuinely refused.
   */
  operators: {
    acmeOrgAdmin:     { email: 'acme-admin@console.test',   password: FIXTURE_PASSWORD, org: 'acme',   role: 'org_admin' },
    acmeProjectAdmin: { email: 'acme-project@console.test', password: FIXTURE_PASSWORD, org: 'acme',   role: 'project_admin', project: 'acmePortal' },
    globexOrgAdmin:   { email: 'globex-admin@console.test', password: FIXTURE_PASSWORD, org: 'globex', role: 'org_admin' },
  },
  serviceAccounts: {
    deploymentBot: { level: 'deployment', name: 'deployment-bot' },
    acmeBot:       { list: 'acmeStaff',   name: 'acme-ci-bot' },
  },
};

// ── Plumbing ────────────────────────────────────────────────────────────────

function credentials() {
  if (process.env.TEST_SUPER_ADMIN_EMAIL && process.env.TEST_SUPER_ADMIN_PASSWORD) {
    return { email: process.env.TEST_SUPER_ADMIN_EMAIL, password: process.env.TEST_SUPER_ADMIN_PASSWORD };
  }
  const text = fs.readFileSync(SECRETS, 'utf8');
  // Read exactly the way global-setup.ts does — one quote character, either kind. The generator
  // quotes the password with whichever quote does not appear inside it, so a reader that only
  // knows `"` hands the sign-in a password wrapped in two literal apostrophes and gets a 401 that
  // reads as "the deployment was reinstalled" rather than "this regex is wrong".
  const read = (key) => {
    const m = new RegExp(String.raw`^\s*${key}:\s*(.+)$`, 'm').exec(text);
    return m ? m[1].trim().replaceAll(/^['"]|['"]$/g, '') : null;
  };
  const email = read('bootstrapEmail');
  const password = read('bootstrapPassword');
  if (!email || !password) {
    throw new Error(`No bootstrap administrator in ${SECRETS}. Set TEST_SUPER_ADMIN_EMAIL / _PASSWORD.`);
  }
  return { email, password };
}

/**
 * Signs in and exchanges the code for an access token.
 *
 * The whole flow, rather than a shortcut, because the shortcut does not exist: the management API
 * takes a bearer token minted by Hydra, and Hydra mints one only at the end of an authorization
 * code exchange. A seeder that wrote rows directly would also be a seeder that never proved the
 * deployment can issue a token at all — which is the first thing worth knowing before running a
 * suite against it.
 */
export async function accessToken() {
  const { email, password } = credentials();
  const jar = new Map();
  const cookieHeader = () => [...jar].map(([k, v]) => `${k}=${v}`).join('; ');
  const remember = (res) => {
    for (const raw of res.headers.getSetCookie?.() ?? []) {
      const [pair] = raw.split(';');
      const i = pair.indexOf('=');
      if (i > 0) jar.set(pair.slice(0, i).trim(), pair.slice(i + 1).trim());
    }
  };

  const authorize = new URL(`${APP_URL}/oauth2/auth`);
  authorize.search = new URLSearchParams({
    client_id: 'client_admin_system',
    response_type: 'code',
    scope: 'openid offline',
    redirect_uri: `${CONSOLE_URL}/console/callback`,
    // Eight characters minimum: Hydra refuses a shorter one as too weak, and answers with a
    // redirect carrying error=invalid_state rather than a login challenge.
    state: 'seedfixture01',
    code_challenge: CHALLENGE,
    code_challenge_method: 'S256',
  }).toString();

  let res = await fetch(authorize, { redirect: 'manual' });
  remember(res);
  let location = res.headers.get('location') ?? '';
  const loginChallenge = new URL(location, APP_URL).searchParams.get('login_challenge');
  if (!loginChallenge) throw new Error('No login_challenge — is Hydra reachable?');

  res = await fetch(`${APP_URL}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', cookie: cookieHeader() },
    body: JSON.stringify({ email, password, login_challenge: loginChallenge }),
  });
  remember(res);
  if (!res.ok) throw new Error(`Sign-in refused (${res.status}): check the bootstrap credentials.`);
  const { redirect_to: afterLogin } = await res.json();

  // Follow Hydra's redirects — login accept → consent → code — carrying the cookies it sets.
  //
  // Every hop is a plain GET, consent included: `/auth/consent` is a redirect target for a browser,
  // so it reads the challenge, decides, accepts against Hydra's admin API and answers 302. There is
  // no form and no POST to grant — asking for one gets a 405 from a route that only allows GET.
  location = afterLogin;
  for (let hop = 0; hop < 12 && location; hop++) {
    if (location.startsWith(`${CONSOLE_URL}/console/callback`)) break;
    res = await fetch(new URL(location, APP_URL), { redirect: 'manual', headers: { cookie: cookieHeader() } });
    remember(res);
    location = res.headers.get('location') ?? '';
  }

  const code = new URL(location, CONSOLE_URL).searchParams.get('code');
  if (!code) throw new Error(`The flow ended without an authorization code (last hop: ${location}).`);

  const token = await fetch(`${APP_URL}/oauth2/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'authorization_code',
      code,
      redirect_uri: `${CONSOLE_URL}/console/callback`,
      client_id: 'client_admin_system',
      code_verifier: VERIFIER,
    }),
  });
  if (!token.ok) throw new Error(`Token exchange failed (${token.status}): ${await token.text()}`);
  return (await token.json()).access_token;
}

let TOKEN = '';
/**
 * One management API call, on the admin host.
 *
 * Not APP_URL: an Ingress named `rediensiam-public-admin-deny` refuses the whole management surface
 * on the public host, and does it at the router — so the answer is a bare `403 Forbidden` from
 * Traefik with no JSON body, arriving before the token has been looked at. That refusal is a
 * deliberate control (PLAN §12) and asserted by a test of its own; the seed simply has to knock on
 * the right door.
 */
async function api(method, path, body) {
  const res = await fetch(`${CONSOLE_URL}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${TOKEN}`,
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  if (res.status === 409) return { conflict: true };          // already seeded
  if (!res.ok) throw new Error(`${method} ${path} → ${res.status}: ${await res.text()}`);
  return res.status === 204 ? {} : res.json();
}

/** Creates unless something with that name is already there — this is what makes seeding idempotent. */
async function ensure(label, find, create) {
  const existing = await find();
  if (existing) { console.log(`  = ${label}`); return existing; }
  const made = await create();
  console.log(`  + ${label}`);
  return made;
}

// ── The fixture ─────────────────────────────────────────────────────────────

async function main() {
  console.log(`Seeding ${APP_URL} …`);
  TOKEN = await accessToken();

  const orgs = {};
  for (const [key, spec] of Object.entries(SEED.orgs)) {
    orgs[key] = await ensure(`org ${spec.name}`,
      async () => (await api('GET', '/admin/organizations')).find(o => o.slug === spec.slug),
      () => api('POST', '/admin/organizations', { name: spec.name, slug: spec.slug }));
  }

  const projects = {};
  for (const [key, spec] of Object.entries(SEED.projects)) {
    const org = orgs[spec.org];
    projects[key] = await ensure(`project ${spec.name}`,
      async () => (await api('GET', `/admin/organizations/${org.id}/projects`)).find(p => p.slug === spec.slug),
      () => api('POST', `/admin/organizations/${org.id}/projects`, {
        name: spec.name, slug: spec.slug,
        redirect_uris: [`${CONSOLE_URL}/callback`],
      }));
  }

  const lists = {};
  for (const [key, spec] of Object.entries(SEED.userLists)) {
    const org = orgs[spec.org];
    lists[key] = await ensure(`user list ${spec.name}`,
      async () => (await api('GET', '/admin/userlists')).find(l => l.name === spec.name),
      () => api('POST', '/admin/userlists', { name: spec.name, org_id: org.id }));
  }

  const users = {};
  for (const [key, spec] of Object.entries(SEED.users)) {
    const list = lists[spec.list];
    users[key] = await ensure(`user ${spec.email}`,
      async () => (await api('GET', `/admin/userlists/${list.id}/users`)).find(u => u.email === spec.email),
      // org_id so the token these accounts get names their own tenant. Without it the organisation
      // would come from the project, which is the historical behaviour and the wrong answer on a
      // list several tenants could share.
      () => api('POST', `/admin/userlists/${list.id}/users`, {
        email: spec.email, password: spec.password, username: spec.email.split('@')[0],
        org_id: orgs[spec.org].id,
      }));
  }

  const systemList = (await api('GET', '/admin/userlists')).find(l => l.org_id == null && l.immovable);

  // The console operators, in the system list, then their grants. The grants come last because a
  // project_admin is scoped to a project that has to exist — and they go through the same API a
  // console operator uses, so the Keto tuple behind each one is written by the code under test
  // rather than around it.
  for (const spec of Object.values(SEED.operators)) {
    const operator = await ensure(`operator ${spec.email}`,
      async () => (await api('GET', `/admin/userlists/${systemList.id}/users`)).find(u => u.email === spec.email),
      () => api('POST', `/admin/userlists/${systemList.id}/users`, {
        email: spec.email, password: spec.password, username: spec.email.split('@')[0],
      }));

    const org = orgs[spec.org];
    const scopeId = spec.project ? projects[spec.project].id : null;
    const held = (await api('GET', `/admin/organizations/${org.id}/admins`))
      .some(a => a.user_id === operator.id && a.role === spec.role);
    if (held) { console.log(`  = ${spec.role} ${spec.email}`); continue; }
    await api('POST', `/admin/organizations/${org.id}/admins`, {
      user_id: operator.id, role: spec.role, scope_id: scopeId,
    });
    console.log(`  + ${spec.role} ${spec.email}`);
  }

  for (const spec of Object.values(SEED.serviceAccounts)) {
    const listId = spec.level === 'deployment' ? systemList.id : lists[spec.list].id;
    await ensure(`service account ${spec.name}`,
      async () => (await api('GET', '/service-accounts')).find(sa => sa.name === spec.name),
      () => api('POST', '/service-accounts', { name: spec.name, user_list_id: listId }));
  }

  // Suspended last: a suspended organisation refuses further writes into itself, so seeding its
  // projects and lists first is not a preference but an ordering constraint.
  const initech = orgs.suspended;
  const state = (await api('GET', `/admin/organizations/${initech.id}`));
  if (state.active === false) {
    console.log('  = Initech already suspended');
  } else {
    await api('POST', `/admin/organizations/${initech.id}/suspend`);
    console.log('  + suspended Initech');
  }

  console.log('\nFixture ready.');
}

// Only when run as a script. Specs import `SEED` from this file — that is the whole point of the
// names living here — and an unguarded call would make `import { SEED }` re-seed the deployment
// once per spec file, from inside the test process, before any test had started.
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    await main();
  } catch (e) {
    console.error(`\nSeed failed: ${e.message}`);
    process.exit(1);
  }
}
