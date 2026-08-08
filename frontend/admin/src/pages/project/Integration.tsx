import { useEffect, useState } from 'react';
import { useProjectContext } from '@/hooks/useOrgContext';
import { IamChip } from '@/components/iam';
import { getProjectInfo, listRoles, adminListRoles } from '@/api';
import { ApiError, getIssuerUrl } from '@/auth';
import PageHeader from '@/components/layout/PageHeader';

/**
 * The contract between this deployment and the application plugged into it.
 *
 * Read-only on purpose. Every value here is already writable somewhere else — the redirect URIs and
 * the scopes on Settings, the login surface on Authentication — and a second editor for the same
 * field is a second place for the two to disagree. What no other page does is put the whole
 * contract in one place, spelled the way the integrator's code will actually see it.
 *
 * Nothing on this page is invented. A row exists only when a route answers for it; the sections the
 * mock-up carried that no route can fill (audiences, token lifetimes, per-claim toggles, secret
 * rotation) are absent rather than faked, because a configuration screen that shows a setting
 * nobody can save is worse than no screen.
 */

interface ProjectInfo {
  id: string;
  name: string;
  slug: string;
  active: boolean;
  hydra_client_id: string | null;
  redirect_uris?: string[];
  post_logout_redirect_uris?: string[];
  allowed_scopes?: string[];
}

interface Role { id: string; name: string }

/**
 * What every project's OAuth2 client is registered with, fixed at creation
 * (`OrgController.CreateProject` / `SystemAdminController.AdminCreateProject`). There is no route
 * that reads it back per project and none that changes it, so it is stated as the invariant it is.
 */
const BASE_SCOPES = ['openid', 'profile', 'offline_access'];
const GRANT_TYPES = ['authorization_code', 'refresh_token'];

function apiErrorMessage(e: unknown, fallback: string): string {
  const body = e instanceof ApiError ? (e.body as { error?: string; detail?: string } | null) : null;
  return body?.detail ?? body?.error ?? fallback;
}

/**
 * The origins Hydra is given for this client, derived the way the server derives them
 * (`ClientOriginsService.CorsOriginsFor`): http(s) only — a native scheme such as `myapp://cb` is a
 * redirect target and never an origin — and rebuilt from the parsed URL rather than sliced out of
 * the string, so nothing a tenant typed can reach a header intact.
 */
function corsOriginsFor(uris: readonly string[]): string[] {
  const origins = new Set<string>();
  for (const uri of uris) {
    try {
      const url = new URL(uri);
      if (url.protocol === 'http:' || url.protocol === 'https:') origins.add(url.origin);
    } catch { /* not an absolute URL: contributes no origin, exactly as on the server */ }
  }
  return [...origins].sort((a, b) => a.localeCompare(b));
}

function CopyButton({ text, label }: Readonly<{ text: string; label: string }>) {
  const [copied, setCopied] = useState(false);
  return (
    <button type="button" className="iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm" aria-label={label}
      onClick={() => { navigator.clipboard.writeText(text); setCopied(true); }}>
      {copied
        ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="var(--success)" strokeWidth="2"><polyline points="20 6 9 17 4 12"/></svg>
        : <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>}
    </button>
  );
}

const CODE_BOX: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 8, justifyContent: 'space-between',
  background: 'var(--surface-2)', border: '1px solid var(--border)', borderRadius: 6,
  padding: '6px 6px 6px 12px', fontSize: 12, minHeight: 34,
};

const PRE_BOX: React.CSSProperties = {
  margin: 0, background: 'var(--surface-2)', border: '1px solid var(--border)',
  borderRadius: 6, padding: 14, fontSize: 12, lineHeight: 1.7, overflowX: 'auto',
};

function CodeValue({ value, label }: Readonly<{ value: string; label: string }>) {
  return (
    <div style={CODE_BOX}>
      <span className="iam-mono" style={{ overflowWrap: 'anywhere' }}>{value}</span>
      <CopyButton text={value} label={`Copy ${label}`} />
    </div>
  );
}

function Field({ label, hint, children }: Readonly<{ label: string; hint?: string; children: React.ReactNode }>) {
  return (
    <div>
      <div className="iam-label" style={{ display: 'flex', alignItems: 'center', gap: 6 }}>{label}</div>
      {hint && <p className="iam-help" style={{ marginTop: 0, marginBottom: 5 }}>{hint}</p>}
      {children}
    </div>
  );
}

function Card({ title, aside, children }: Readonly<{ title: string; aside?: React.ReactNode; children: React.ReactNode }>) {
  return (
    <section className="iam-card">
      <div style={{
        padding: '14px 18px', borderBottom: '1px solid var(--border)', display: 'flex',
        alignItems: 'center', justifyContent: 'space-between', gap: 12,
      }}>
        <h2 style={{ fontSize: 13, fontWeight: 600, margin: 0 }}>{title}</h2>
        {aside}
      </div>
      <div className="iam-card-pad" style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>{children}</div>
    </section>
  );
}

function UriList({ uris, empty }: Readonly<{ uris: string[]; empty: string }>) {
  if (uris.length === 0) return <p style={{ fontSize: 12, color: 'var(--fg-muted)', fontStyle: 'italic' }}>{empty}</p>;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      {uris.map(u => <CodeValue key={u} value={u} label={u} />)}
    </div>
  );
}

export default function Integration() {
  const { projectId, isSystemCtx, projectBase } = useProjectContext();
  const [project, setProject] = useState<ProjectInfo | null>(null);
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(Boolean(projectId));
  const [error, setError] = useState('');
  const [example, setExample] = useState<'curl' | 'csharp'>('curl');

  // No setState in the effect body: `loading` starts true when there is a project to read, so the
  // first paint is already the skeleton (react-hooks/set-state-in-effect).
  useEffect(() => {
    if (!projectId) return;
    Promise.all([
      getProjectInfo(projectId),
      (isSystemCtx ? adminListRoles : listRoles)(projectId),
    ])
      .then(([p, r]: [ProjectInfo, { roles?: Role[] } & Role[]]) => {
        setProject(p);
        setRoles(r?.roles ?? (Array.isArray(r) ? r : []));
        setError('');
      })
      // The defect this page was written after: a rejected promise with no catch left the skeleton
      // on screen forever and put the refusal in devtools, where no operator looks.
      .catch(e => {
        setProject(null);
        setRoles([]);
        setError(apiErrorMessage(e, 'Could not load this project’s integration details.'));
      })
      .finally(() => setLoading(false));
  }, [projectId, isSystemCtx]);

  const issuer = getIssuerUrl()?.replace(/\/$/, '') ?? null;
  const clientId = project?.hydra_client_id ?? (project ? `client_${project.id}` : '');
  const redirectUris = project?.redirect_uris ?? [];
  const logoutUris = project?.post_logout_redirect_uris ?? [];
  const customScopes = project?.allowed_scopes ?? [];
  const scopeString = [...BASE_SCOPES, ...customScopes].join(' ');

  // The whole point of the page, in one string. A bare "admin" fails closed; the token carries
  // `{project_id}/admin` (Roles.ProjectRoleClaim), so that is what is shown.
  const qualifiedRoles = roles.map(r => `${project?.id ?? ''}/${r.name}`);

  const dotenv = [
    `REDIENSIAM_ISSUER=${issuer ?? ''}`,
    `REDIENSIAM_CLIENT_ID=${clientId}`,
    `REDIENSIAM_PROJECT_ID=${project?.id ?? ''}`,
    `REDIENSIAM_REDIRECT_URI=${redirectUris[0] ?? ''}`,
    `REDIENSIAM_SCOPE=${scopeString}`,
  ].join('\n');

  const accessToken = [
    '{',
    `  "iss": "${issuer ?? '<issuer>'}",`,
    '  "sub": "3ab9c1d2-…",                    // the user id',
    `  "client_id": "${clientId}",`,
    `  "scp": [${[...BASE_SCOPES, ...customScopes].map(s => `"${s}"`).join(', ')}],`,
    '  "ext": {',
    '    "org_id":     "7c1f2a9b-…",',
    `    "project_id": "${project?.id ?? ''}",`,
    '    "user_id":    "3ab9c1d2-…",',
    `    "roles":      [${qualifiedRoles.map(r => `"${r}"`).join(', ')}]`,
    '  }',
    '}',
  ].join('\n');

  const idToken = ['{', '  "email": "marie@example.com",', '  "org_id": "7c1f2a9b-…",', `  "project_id": "${project?.id ?? ''}"`, '}'].join('\n');

  const curlExample = [
    `curl -X POST ${issuer ?? '<issuer>'}/api/introspect \\`,
    '  -H "Authorization: Bearer $REDIENSIAM_PAT" \\',
    '  --data-urlencode "token=$ACCESS_TOKEN" \\',
    `  --data-urlencode "project_id=${project?.id ?? ''}"`,
  ].join('\n');

  const csharpExample = [
    'builder.Services.AddRediensIam(o =>',
    '{',
    `    o.BaseUrl             = "${issuer ?? '<issuer>'}";`,
    '    o.ServiceAccountToken = builder.Configuration["RediensIAM:Token"]!;',
    `    o.ProjectId           = "${project?.id ?? ''}";`,
    '});',
    '',
    '// Qualified, always. HasRole matches management roles only.',
    `if (info.HasProjectRole("${project?.id ?? ''}", "${roles[0]?.name ?? 'admin'}")) { … }`,
  ].join('\n');

  const endpoints: ReadonlyArray<readonly [string, string]> = issuer ? [
    ['Issuer', `${issuer}/`],
    ['Discovery', `${issuer}/.well-known/openid-configuration`],
    ['Authorize', `${issuer}/oauth2/auth`],
    ['Token', `${issuer}/oauth2/token`],
    ['Userinfo', `${issuer}/userinfo`],
    ['JWKS', `${issuer}/.well-known/jwks.json`],
    ['End session', `${issuer}/oauth2/sessions/logout`],
    ['Introspection', `${issuer}/api/introspect`],
  ] : [];

  if (!projectId) {
    return (
      <div>
        <PageHeader title="Integration" />
        <div className="iam-page">
          <div className="iam-empty">
            <div className="iam-empty-title">No project selected</div>
            <div className="iam-empty-desc">Open a project to see what its applications must be configured with.</div>
          </div>
        </div>
      </div>
    );
  }

  if (loading) {
    return (
      <div>
        <PageHeader title="Integration" />
        <div className="iam-page" style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          {[0, 1, 2].map(i => (
            <div key={i} className="iam-card iam-card-pad">
              <div style={{ height: 14, width: '30%', background: 'var(--surface-2)', borderRadius: 4, marginBottom: 12 }} />
              <div style={{ height: 34, background: 'var(--surface-2)', borderRadius: 6 }} />
            </div>
          ))}
        </div>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title="Integration"
        description="The whole contract between this deployment and the applications that sign in through it — identifiers, endpoints, and the shape of the token they receive."
        actions={project ? [
          <button key="env" className="iam-btn iam-btn-secondary iam-btn-sm" onClick={() => navigator.clipboard.writeText(dotenv)}>
            Copy as .env
          </button>,
        ] : []}
      />

      <div className="iam-page" style={{ maxWidth: 1080, display: 'flex', flexDirection: 'column', gap: 16 }}>
        {error && <div className="iam-alert iam-alert-danger">{error}</div>}

        {project && (
          <>
            <Card title="Identity">
              <Field label="Client ID" hint="Derived from the project id at creation. It cannot be changed: every deployed application is configured with it.">
                <CodeValue value={clientId} label="client ID" />
              </Field>
              <Field label="Project ID" hint="The primary key — and what qualifies every role name in the token.">
                <CodeValue value={project.id} label="project ID" />
              </Field>
              <div style={{ display: 'flex', gap: 24, flexWrap: 'wrap' }}>
                <Field label="Display name"><span style={{ fontSize: 13 }}>{project.name}</span></Field>
                <Field label="Slug"><span className="iam-mono" style={{ fontSize: 12.5 }}>{project.slug}</span></Field>
                <Field label="Status">
                  {project.active
                    ? <IamChip tone="success">Active</IamChip>
                    : <IamChip tone="danger">Inactive — every sign-in is refused</IamChip>}
                </Field>
              </div>
            </Card>

            <Card title="Client type and flow" aside={<span style={{ fontSize: 11.5, color: 'var(--fg-subtle)' }}>Fixed at project creation — there is no route that changes it</span>}>
              <p style={{ margin: 0, fontSize: 13, lineHeight: 1.6, color: 'var(--fg-muted)' }}>
                A project's client is a <strong>public PKCE client</strong>: <span className="iam-mono">token_endpoint_auth_method</span> is{' '}
                <span className="iam-mono">none</span>. There is no client secret — nothing to store, nothing to leak, and nothing to rotate.
                A confidential client is a different object, created through <span className="iam-mono">POST /admin/hydra/clients</span>, and it
                carries no project metadata.
              </p>
              <Field label="Grant types">
                <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                  {GRANT_TYPES.map(g => <IamChip key={g} mono>{g}</IamChip>)}
                </div>
              </Field>
              <Field label="Response types">
                <div style={{ display: 'flex', gap: 6 }}><IamChip mono>code</IamChip></div>
              </Field>
            </Card>

            <Card title="Redirects and origins" aside={<span style={{ fontSize: 11.5, color: 'var(--fg-subtle)' }}>Edit them on Settings</span>}>
              <Field label="Sign-in returns to">
                <UriList uris={redirectUris} empty="None registered — sign-in cannot complete until one is added on Settings." />
              </Field>
              <Field label="Sign-out may return to">
                <UriList uris={logoutUris} empty="None registered. A sign-out naming any target is refused." />
              </Field>
              <Field
                label="Allowed origins"
                hint="Rebuilt from the two lists above at every write, never edited by hand. Only http(s) contributes an origin — a native scheme such as myapp://cb is a valid redirect target and never an origin."
              >
                {(() => {
                  const origins = corsOriginsFor([...redirectUris, ...logoutUris]);
                  if (origins.length === 0) return <p style={{ fontSize: 12, color: 'var(--fg-muted)', fontStyle: 'italic' }}>No origin is allowed to call Hydra for this project.</p>;
                  return (
                    <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                      {origins.map(o => <IamChip key={o} mono>{o}</IamChip>)}
                    </div>
                  );
                })()}
              </Field>
              <p style={{ margin: 0 }}>
                <a className="iam-btn iam-btn-secondary iam-btn-sm" href={`${projectBase}/settings`}>Open Settings</a>
              </p>
            </Card>

            <Card title="Scopes this client may request">
              <Field label="Always granted">
                <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                  {BASE_SCOPES.map(s => <IamChip key={s} mono>{s}</IamChip>)}
                </div>
              </Field>
              <Field label="Added by this project">
                {customScopes.length === 0
                  ? <p style={{ fontSize: 12, color: 'var(--fg-muted)', fontStyle: 'italic' }}>No custom scopes.</p>
                  : (
                    <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                      {customScopes.map(s => <IamChip key={s} tone="accent" mono>{s}</IamChip>)}
                    </div>
                  )}
              </Field>
              <p className="iam-help" style={{ margin: 0 }}>
                <span className="iam-mono">email</span> is deliberately not in the list: the address travels in the id_token whether or not a
                scope asks for it. RediensIAM does not interpret a custom scope — it puts it in <span className="iam-mono">scp</span> and your
                resource server decides. A scope a client asks for that is not listed here is dropped silently: the token simply comes back narrower.
              </p>
            </Card>

            <Card title="Endpoints" aside={<span style={{ fontSize: 11.5, color: 'var(--fg-subtle)' }}>From the deployment's own configuration, not from this project</span>}>
              {endpoints.length === 0
                ? <p style={{ fontSize: 12, color: 'var(--fg-muted)', fontStyle: 'italic' }}>The issuer is not known yet. These are read from /console/config, and the console will not invent them.</p>
                : (
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: '14px 20px' }}>
                    {endpoints.map(([label, url]) => (
                      <Field key={label} label={label}><CodeValue value={url} label={label} /></Field>
                    ))}
                  </div>
                )}
            </Card>

            <Card title="The token your application will receive" aside={<IamChip tone="warn">roles are qualified by project</IamChip>}>
              <pre className="iam-mono" style={PRE_BOX}>{accessToken}</pre>
              <div className="iam-alert iam-alert-warn">
                <span>
                  <span className="iam-mono">roles.contains(&quot;{roles[0]?.name ?? 'admin'}&quot;)</span> fails closed. Compare{' '}
                  <span className="iam-mono">projectId + &quot;/&quot; + &quot;{roles[0]?.name ?? 'admin'}&quot;</span>, or call{' '}
                  <span className="iam-mono">HasProjectRole(projectId, &quot;{roles[0]?.name ?? 'admin'}&quot;)</span>. In .NET,{' '}
                  <span className="iam-mono">[Authorize(Roles = &quot;{roles[0]?.name ?? 'admin'}&quot;)]</span> matches nobody — intended, not a bug.
                </span>
              </div>
              {roles.length === 0 && (
                <p style={{ fontSize: 12, color: 'var(--fg-muted)' }}>
                  This project defines no roles yet, so <span className="iam-mono">ext.roles</span> arrives empty for every user.
                </p>
              )}
              <Field label="And in the id_token" hint="Hydra merges these at the top level, not under ext.">
                <pre className="iam-mono" style={PRE_BOX}>{idToken}</pre>
              </Field>
            </Card>

            <Card
              title="Validating tokens from your backend"
              aside={
                <div style={{ display: 'flex', gap: 6 }}>
                  <button className={`iam-btn iam-btn-sm ${example === 'curl' ? 'iam-btn-primary' : 'iam-btn-secondary'}`}
                    aria-pressed={example === 'curl'} onClick={() => setExample('curl')}>curl</button>
                  <button className={`iam-btn iam-btn-sm ${example === 'csharp' ? 'iam-btn-primary' : 'iam-btn-secondary'}`}
                    aria-pressed={example === 'csharp'} onClick={() => setExample('csharp')}>C#</button>
                </div>
              }
            >
              <p style={{ margin: 0, fontSize: 13, lineHeight: 1.6, color: 'var(--fg-muted)' }}>
                A resource server introspects through a <strong>service account</strong> of this project, authenticating with its PAT.
                Local JWKS verification proves the token was issued; it cannot see a role revoked, an account deactivated or a token
                revoked since. <span className="iam-mono">project_id</span> is mandatory and names the tenant your service serves — omit it and
                every call answers <span className="iam-mono">400 project_id_required</span>.
              </p>
              <pre className="iam-mono" style={PRE_BOX} data-testid="introspect-example">{example === 'curl' ? curlExample : csharpExample}</pre>
              <p className="iam-help" style={{ margin: 0 }}>
                An active reply carries <span className="iam-mono">sub</span>, <span className="iam-mono">user_id</span>,{' '}
                <span className="iam-mono">org_id</span>, <span className="iam-mono">project_id</span>, <span className="iam-mono">roles</span>,{' '}
                <span className="iam-mono">client_id</span> and <span className="iam-mono">is_service_account</span>. An unusable token answers{' '}
                <span className="iam-mono">{'{"active": false}'}</span> with a 200 — never an error status, so a caller cannot tell malformed from
                revoked from expired.
              </p>
              <p style={{ margin: 0 }}>
                <a className="iam-btn iam-btn-secondary iam-btn-sm" href={`${projectBase}/service-accounts`}>Manage service accounts</a>
              </p>
            </Card>
          </>
        )}
      </div>
    </div>
  );
}
