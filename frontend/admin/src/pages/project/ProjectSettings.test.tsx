import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import ProjectSettings from './ProjectSettings';
import { ApiError } from '@/auth';

/**
 * The load-failure branch is the important one. Every field here starts at a useState default, so
 * rendering the form after a failed read and then saving PATCHes those defaults over the tenant's
 * real configuration — and reports success. Refusing to render is the fix, and it has to stay.
 */

const api = vi.hoisted(() => ({
  getProjectInfo: vi.fn(), updateProject: vi.fn(), deleteProject: vi.fn(),
  getProjectScopes: vi.fn(), updateProjectScopes: vi.fn(),
  adminGetProjectScopes: vi.fn(), adminUpdateProjectScopes: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: '', projectId: 'p1', isSuperAdmin: false }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const PROJECT = {
  id: 'p1', name: 'Customer Portal', slug: 'portal', active: true,
  require_role_to_login: false, hydra_client_id: 'client-abc',
  redirect_uris: ['https://a.test/cb', 'https://b.test/cb'],
  post_logout_redirect_uris: ['https://a.test/'],
};

const SCOPES = { built_in: ['openid', 'profile', 'offline_access'], custom_scopes: ['read:orders'] };

beforeEach(() => {
  vi.clearAllMocks();
  auth.projectId = 'p1';
  auth.isSuperAdmin = false;
  api.getProjectInfo.mockResolvedValue(PROJECT);
  api.getProjectScopes.mockResolvedValue(SCOPES);
  api.adminGetProjectScopes.mockResolvedValue(SCOPES);
  api.updateProjectScopes.mockImplementation(async (_id: string, scopes: string[]) => ({ custom_scopes: scopes }));
  api.adminUpdateProjectScopes.mockImplementation(async (_id: string, scopes: string[]) => ({ custom_scopes: scopes }));
});

function Here() {
  return <output data-testid="here">{useLocation().pathname}</output>;
}

function show() {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={['/project/settings']}>
      <Routes><Route path="/project/settings" element={<ProjectSettings />} /></Routes>
      <Here />
    </MemoryRouter>,
  );
  return user;
}

const nameField = () => screen.getByLabelText('Project Name');

describe('the form', () => {
  it('loads the project into the fields', async () => {
    show();

    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));
    expect(screen.getByText('client-abc')).toBeInTheDocument();
    expect(screen.getByText('portal')).toBeInTheDocument();
  });

  it('shows placeholders while loading, and no Save to press', () => {
    api.getProjectInfo.mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.queryByLabelText('Project Name')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save Changes' })).not.toBeInTheDocument();
  });

  it('asks for nothing when no project is in scope', () => {
    auth.projectId = '';
    show();

    expect(api.getProjectInfo).not.toHaveBeenCalled();
  });
});

describe('saving', () => {
  it('sends the three editable fields together', async () => {
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));

    await user.fill(nameField(), 'Client Portal');
    await user.click(screen.getAllByRole('checkbox')[1]);
    await user.click(screen.getByRole('button', { name: 'Save Changes' }));

    await vi.waitFor(() => expect(api.updateProject).toHaveBeenCalledWith('p1', {
      name: 'Client Portal', active: true, require_role_to_login: true,
      // Unchanged here, but sent: this route round-trips what it read, and omitting them would
      // clear a project's redirect URIs every time someone renamed it.
      redirect_uris: ['https://a.test/cb', 'https://b.test/cb'],
      post_logout_redirect_uris: ['https://a.test/'],
    }));
  });

  it('can deactivate the project, which stops every login into it', async () => {
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));

    await user.click(screen.getAllByRole('checkbox')[0]);
    await user.click(screen.getByRole('button', { name: 'Save Changes' }));

    await vi.waitFor(() => expect(api.updateProject).toHaveBeenCalledWith('p1',
      expect.objectContaining({ active: false })));
  });

  it('confirms, then takes the confirmation back down', async () => {
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));

    await user.click(screen.getByRole('button', { name: 'Save Changes' }));

    expect(await screen.findByRole('button', { name: 'Saved!' })).toBeInTheDocument();
    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Save Changes' })).toBeInTheDocument(),
      { timeout: 5000 });
  }, 10_000);

  it('re-enables the button when the save is refused', async () => {
    api.updateProject.mockRejectedValue(new Error('500'));
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));

    await user.click(screen.getByRole('button', { name: 'Save Changes' }));

    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Save Changes' })).toBeEnabled());
    expect(await screen.findByText('Could not save. Nothing was changed.')).toBeInTheDocument();
  });
});

describe('when the project cannot be read', () => {
  beforeEach(() => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.getProjectInfo.mockRejectedValue(new Error('500'));
  });

  it('refuses to show the form at all', async () => {
    show();

    expect(await screen.findByText('This configuration could not be loaded, so it is not safe to edit.'))
      .toBeInTheDocument();
    expect(screen.queryByLabelText('Project Name')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save Changes' })).not.toBeInTheDocument();
  });

  it('offers a retry rather than a save, and shows the form once it succeeds', async () => {
    const user = show();
    await screen.findByRole('button', { name: 'Retry' });
    api.getProjectInfo.mockResolvedValue(PROJECT);

    await user.click(screen.getByRole('button', { name: 'Retry' }));

    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));
    expect(api.getProjectInfo).toHaveBeenCalledTimes(2);
  });
});

describe('deleting the project', () => {
  it('names it and warns what goes with it, before doing anything', async () => {
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));

    await user.click(screen.getByRole('button', { name: /^Delete$/ }));

    expect(await screen.findByText('Delete "Customer Portal"?')).toBeInTheDocument();
    // Once in the danger-zone copy, once in the dialog that confirms it.
    expect(screen.getAllByText(/Hydra OAuth2 client/)).toHaveLength(2);
    expect(api.deleteProject).not.toHaveBeenCalled();
  });

  it('deletes and leaves for the project list', async () => {
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));
    await user.click(screen.getByRole('button', { name: /^Delete$/ }));

    await user.click(await screen.findByRole('button', { name: 'Delete Project' }));

    await vi.waitFor(() => expect(api.deleteProject).toHaveBeenCalledWith('p1'));
    await vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe('/org/projects'));
  });

  it('re-enables the button when the delete is refused', async () => {
    api.deleteProject.mockRejectedValue(new Error('409'));
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));
    await user.click(screen.getByRole('button', { name: /^Delete$/ }));

    await user.click(await screen.findByRole('button', { name: 'Delete Project' }));

    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Delete Project' })).toBeEnabled());
    // Inside the dialog, which stays open and covers the page: an alert in the body would be shown
    // to nobody.
    expect(await screen.findByText('Could not delete this project. It still exists.')).toBeInTheDocument();
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));
    await user.click(screen.getByRole('button', { name: /^Delete$/ }));
    await screen.findByText('Delete "Customer Portal"?');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.deleteProject).not.toHaveBeenCalled();
  });
});


describe('dismissing the delete confirmation with Escape', () => {
  it('closes it without deleting', async () => {
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));
    await user.click(screen.getByRole('button', { name: /^Delete$/ }));
    await screen.findByText('Delete "Customer Portal"?');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('Delete "Customer Portal"?')).toBeNull());
    expect(api.deleteProject).not.toHaveBeenCalled();
  });
});

/**
 * A project's redirect URIs could be set at creation and never again. They live in Hydra rather
 * than in the database, and no route wrote them after the fact — so nothing in the product could
 * add a second front, fix a typo in a callback, or withdraw one.
 *
 * It matters more now that CSP and CORS are derived from those same URIs: deriving them is
 * worthless if the list they derive from cannot be edited.
 */
describe('a project\'s redirect URIs', () => {
  it('renders what the project has registered', async () => {
    show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));

    expect(screen.getByLabelText(/Redirect URIs/)).toHaveValue('https://a.test/cb\nhttps://b.test/cb');
    expect(screen.getByLabelText(/Post-logout redirect URIs/)).toHaveValue('https://a.test/');
  });

  it('saves an added one, one per line', async () => {
    const user = show();
    await vi.waitFor(() => expect(nameField()).toHaveValue('Customer Portal'));

    await user.fill(screen.getByLabelText(/Redirect URIs/),
      'https://a.test/cb\nhttps://b.test/cb\nhttps://c.test/cb');
    await user.click(screen.getByRole('button', { name: 'Save Changes' }));

    await vi.waitFor(() => expect(api.updateProject).toHaveBeenCalledWith('p1',
      expect.objectContaining({
        redirect_uris: ['https://a.test/cb', 'https://b.test/cb', 'https://c.test/cb'],
        post_logout_redirect_uris: ['https://a.test/'],
      })));
  });
});

/**
 * Les scopes OAuth2 d'un projet n'étaient éditables nulle part : les quatre routes existaient, la
 * console n'en appelait aucune. Les trois scopes implicites ne sont pas retirables, et le nom est
 * validé serveur — le refus est affiché tel qu'il vient, il n'est pas redeviné ici.
 */
describe('a project\'s OAuth2 scopes', () => {
  /** Le contexte système : /system/organisations/:oid/projects/:pid/settings. */
  function showSystem() {
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={['/system/organisations/o1/projects/p1/settings']}>
        <Routes>
          <Route path="/system/organisations/:oid/projects/:pid/settings" element={<ProjectSettings />} />
        </Routes>
      </MemoryRouter>,
    );
    return user;
  }

  const newScopeField = () => screen.getByLabelText('New scope');

  it('shows the implicit three apart, with nothing to remove them with', async () => {
    show();

    expect(await screen.findByText('read:orders')).toBeInTheDocument();
    for (const s of SCOPES.built_in) expect(screen.getByText(s)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove read:orders' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove openid' })).toBeNull();
  });

  it('reads the org route for an org admin', async () => {
    show();

    await vi.waitFor(() => expect(api.getProjectScopes).toHaveBeenCalledWith('p1'));
    expect(api.adminGetProjectScopes).not.toHaveBeenCalled();
  });

  it('reads the system route in the system context', async () => {
    showSystem();

    await vi.waitFor(() => expect(api.adminGetProjectScopes).toHaveBeenCalledWith('p1'));
    expect(api.getProjectScopes).not.toHaveBeenCalled();
  });

  it('writes through the system route when a super admin edits from the org context', async () => {
    // Sinon un super-admin, dont le jeton ne nomme aucune organisation, prend un 404 sur sa propre
    // requête : la route d'organisation filtre sur l'organisation du jeton.
    auth.isSuperAdmin = true;
    const user = show();
    await screen.findByText('read:orders');

    await user.fill(newScopeField(), 'write:orders');
    await user.click(screen.getByRole('button', { name: 'Add scope' }));

    await vi.waitFor(() => expect(api.adminUpdateProjectScopes)
      .toHaveBeenCalledWith('p1', ['read:orders', 'write:orders']));
    expect(api.updateProjectScopes).not.toHaveBeenCalled();
  });

  it('adds one, sending the whole list', async () => {
    const user = show();
    await screen.findByText('read:orders');

    await user.fill(newScopeField(), 'write:orders');
    await user.click(screen.getByRole('button', { name: 'Add scope' }));

    await vi.waitFor(() => expect(api.updateProjectScopes)
      .toHaveBeenCalledWith('p1', ['read:orders', 'write:orders']));
    expect(await screen.findByText('write:orders')).toBeInTheDocument();
    expect(newScopeField()).toHaveValue('');
  });

  it('removes one by sending the list without it', async () => {
    const user = show();
    await screen.findByText('read:orders');

    await user.click(screen.getByRole('button', { name: 'Remove read:orders' }));

    await vi.waitFor(() => expect(api.updateProjectScopes).toHaveBeenCalledWith('p1', []));
    expect(await screen.findByText('No custom scopes.')).toBeInTheDocument();
  });

  it('repeats the server refusal, naming what it refused', async () => {
    api.updateProjectScopes.mockRejectedValue(
      new ApiError(400, { error: 'invalid_scope_names', invalid: ['read orders'] }));
    const user = show();
    await screen.findByText('read:orders');

    await user.fill(newScopeField(), 'read orders');
    await user.click(screen.getByRole('button', { name: 'Add scope' }));

    expect(await screen.findByText(/Refused: read orders/)).toBeInTheDocument();
    // Le champ garde la saisie : elle est à corriger, pas à retaper.
    expect(newScopeField()).toHaveValue('read orders');
  });

  it('shows any other refusal rather than swallowing it', async () => {
    api.updateProjectScopes.mockRejectedValue(new Error('500'));
    const user = show();
    await screen.findByText('read:orders');

    await user.click(screen.getByRole('button', { name: 'Remove read:orders' }));

    expect(await screen.findByText('Could not remove that scope.')).toBeInTheDocument();
    expect(screen.getByText('read:orders')).toBeInTheDocument();
  });

  it('says so when the scopes cannot be read at all', async () => {
    api.getProjectScopes.mockRejectedValue(new ApiError(403, { error: 'forbidden' }));
    show();

    expect(await screen.findByText('forbidden')).toBeInTheDocument();
  });
});
