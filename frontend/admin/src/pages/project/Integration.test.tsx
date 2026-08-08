import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import Integration from './Integration';
import { ApiError } from '@/auth';

/**
 * The page an integrator reads before writing a line of code, so the assertions here are about
 * what it *claims*, not about layout.
 *
 * The load-bearing one is the role shape. A project role reaches the token qualified —
 * `{project_id}/{name}` (`Roles.ProjectRoleClaim`) — because two tenants both naming a role `admin`
 * would otherwise be the same string in every consumer's claims. A page that showed the bare name
 * would send every integrator to write `roles.contains("admin")`, which matches nobody.
 *
 * The second is the refusal. This page exists because a rejected promise with no catch left a
 * skeleton on screen and the 403 in devtools.
 */

// The factory REPLACES the module: every export the page imports has to be here.
const api = vi.hoisted(() => ({
  getProjectInfo: vi.fn(),
  listRoles: vi.fn(),
  adminListRoles: vi.fn(),
}));
vi.mock('@/api', () => api);

// The issuer comes from /console/config, which no test serves. Everything under Endpoints is built
// from it, so it is part of the fixture rather than an invented location.origin.
vi.mock('@/auth', async orig => ({
  ...(await orig<typeof import('@/auth')>()),
  getIssuerUrl: () => 'https://iam.example.test',
}));

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: 'p1' }));
vi.mock('@/context/AuthContext', async orig => ({
  ...(await orig<typeof import('@/context/AuthContext')>()),
  useAuth: () => auth,
}));

const PROJECT = {
  id: 'p1',
  name: 'Yandee Portal',
  slug: 'yandee-portal',
  active: true,
  hydra_client_id: 'client_p1',
  redirect_uris: ['https://portal.example.test/callback', 'myapp://cb'],
  post_logout_redirect_uris: ['https://portal.example.test/'],
  allowed_scopes: ['read:orders'],
};

const ROLES = [
  { id: 'r1', name: 'admin' },
  { id: 'r2', name: 'viewer' },
];

beforeEach(() => {
  vi.clearAllMocks();
  auth.projectId = 'p1';
  api.getProjectInfo.mockResolvedValue(PROJECT);
  api.listRoles.mockResolvedValue(ROLES);
  api.adminListRoles.mockResolvedValue(ROLES);
});

function show(path = '/project/integration', pattern = '/project/integration') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<Integration />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

/** The same screen opened by a super-admin browsing into the tenant. */
const showSystem = () =>
  show('/system/organisations/o9/projects/p9/integration',
    '/system/organisations/:oid/projects/:pid/integration');

const clipboard = () => {
  const writeText = vi.fn();
  vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue({ writeText } as unknown as Clipboard);
  return writeText;
};

describe('loading', () => {
  it('reads the project and its roles, in the caller’s own scope', async () => {
    show();

    await screen.findByText('client_p1');
    expect(api.getProjectInfo).toHaveBeenCalledWith('p1');
    expect(api.listRoles).toHaveBeenCalledWith('p1');
    expect(api.adminListRoles).not.toHaveBeenCalled();
  });

  it('uses the super-admin role route when browsing into a tenant', async () => {
    showSystem();

    await screen.findByText('client_p1');
    expect(api.adminListRoles).toHaveBeenCalledWith('p9');
    expect(api.listRoles).not.toHaveBeenCalled();
  });

  it('shows the identifiers an application is configured with', async () => {
    show();

    await screen.findByText('client_p1');
    expect(screen.getByText('Yandee Portal')).toBeInTheDocument();
    expect(screen.getByText('yandee-portal')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
  });

  it('names the flow the client is actually registered with', async () => {
    show();

    await screen.findByText('authorization_code');
    expect(screen.getByText('refresh_token')).toBeInTheDocument();
    expect(screen.getByText(/token_endpoint_auth_method/)).toBeInTheDocument();
    // No secret is displayed, because a public client has none to display.
    expect(screen.queryByText(/client_secret/)).not.toBeInTheDocument();
  });

  it('lists the redirect targets and the built-in and custom scopes', async () => {
    show();

    await screen.findByText('https://portal.example.test/callback');
    expect(screen.getByText('https://portal.example.test/')).toBeInTheDocument();
    expect(screen.getByText('openid')).toBeInTheDocument();
    expect(screen.getByText('offline_access')).toBeInTheDocument();
    expect(screen.getByText('read:orders')).toBeInTheDocument();
  });

  /** CorsOriginsFor: http(s) only, and rebuilt from the parsed URL — myapp://cb is no origin. */
  it('derives the allowed origins the way the server does', async () => {
    show();

    await screen.findByText('https://portal.example.test');
    expect(screen.queryByText('myapp://cb', { selector: '.iam-chip' })).not.toBeInTheDocument();
  });

  it('builds the endpoints from the issuer, not from the console’s own origin', async () => {
    show();

    await screen.findByText('https://iam.example.test/.well-known/openid-configuration');
    expect(screen.getByText('https://iam.example.test/oauth2/token')).toBeInTheDocument();
    expect(screen.getByText('https://iam.example.test/api/introspect')).toBeInTheDocument();
  });
});

describe('the token preview', () => {
  it('shows every role qualified by the project, never bare', async () => {
    show();

    const preview = await screen.findByText(/"ext"/);
    expect(preview.textContent).toContain('"p1/admin"');
    expect(preview.textContent).toContain('"p1/viewer"');
    expect(preview.textContent).not.toMatch(/"admin"/);
  });

  it('warns that a bare comparison fails closed', async () => {
    show();

    await screen.findByText(/"ext"/);
    expect(screen.getByText(/roles\.contains\("admin"\)/)).toBeInTheDocument();
    expect(screen.getByText(/HasProjectRole\(projectId, "admin"\)/)).toBeInTheDocument();
  });

  it('says so when the project defines no roles at all', async () => {
    api.listRoles.mockResolvedValue([]);
    show();

    await screen.findByText(/defines no roles yet/);
    const preview = screen.getByText(/"ext"/);
    expect(preview.textContent).toContain('"roles":      []');
  });
});

describe('empty states', () => {
  it('says a project with no redirect URI cannot complete a sign-in', async () => {
    api.getProjectInfo.mockResolvedValue({ ...PROJECT, redirect_uris: [], post_logout_redirect_uris: [] });
    show();

    await screen.findByText(/None registered — sign-in cannot complete/);
    expect(screen.getByText(/No origin is allowed to call Hydra/)).toBeInTheDocument();
  });

  it('says a project with no custom scope has none', async () => {
    api.getProjectInfo.mockResolvedValue({ ...PROJECT, allowed_scopes: [] });
    show();

    await screen.findByText('No custom scopes.');
  });

  it('asks for a project when none is in scope', async () => {
    auth.projectId = '';
    show();

    await screen.findByText('No project selected');
    expect(api.getProjectInfo).not.toHaveBeenCalled();
  });
});

describe('a refusal from the API', () => {
  it('is shown, rather than left in devtools behind a skeleton', async () => {
    api.getProjectInfo.mockRejectedValue(new ApiError(403, { error: 'forbidden' }));
    show();

    await screen.findByText('forbidden');
    expect(screen.queryByText('client_p1')).not.toBeInTheDocument();
  });

  it('prefers the server’s own detail when it sends one', async () => {
    api.listRoles.mockRejectedValue(new ApiError(500, { detail: 'Hydra is unreachable.' }));
    show();

    await screen.findByText('Hydra is unreachable.');
  });

  it('falls back to a sentence of its own when the body says nothing', async () => {
    api.getProjectInfo.mockRejectedValue(new Error('boom'));
    show();

    await screen.findByText(/Could not load this project’s integration details\./);
  });
});

describe('interactions', () => {
  it('copies an identifier', async () => {
    const writeText = clipboard();
    const user = show();

    await screen.findByText('client_p1');
    await user.click(screen.getByRole('button', { name: 'Copy client ID' }));

    expect(writeText).toHaveBeenCalledWith('client_p1');
  });

  it('copies the whole configuration as a .env', async () => {
    const writeText = clipboard();
    const user = show();

    await screen.findByText('client_p1');
    await user.click(screen.getByRole('button', { name: 'Copy as .env' }));

    const env = writeText.mock.calls[0][0] as string;
    expect(env).toContain('REDIENSIAM_ISSUER=https://iam.example.test');
    expect(env).toContain('REDIENSIAM_CLIENT_ID=client_p1');
    expect(env).toContain('REDIENSIAM_PROJECT_ID=p1');
    expect(env).toContain('REDIENSIAM_REDIRECT_URI=https://portal.example.test/callback');
    expect(env).toContain('REDIENSIAM_SCOPE=openid profile offline_access read:orders');
  });

  it('switches the introspection example between curl and the C# SDK', async () => {
    const user = show();

    const example = await screen.findByTestId('introspect-example');
    expect(example.textContent).toContain('curl -X POST https://iam.example.test/api/introspect');
    expect(example.textContent).toContain('project_id=p1');

    await user.click(screen.getByRole('button', { name: 'C#' }));

    expect(screen.getByTestId('introspect-example').textContent).toContain('o.ProjectId           = "p1";');
    expect(screen.getByTestId('introspect-example').textContent).toContain('HasProjectRole("p1", "admin")');

    await user.click(screen.getByRole('button', { name: 'curl' }));

    expect(screen.getByTestId('introspect-example').textContent).toContain('curl -X POST');
  });
});
