import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import ProjectSettings from './ProjectSettings';

/**
 * The load-failure branch is the important one. Every field here starts at a useState default, so
 * rendering the form after a failed read and then saving PATCHes those defaults over the tenant's
 * real configuration — and reports success. Refusing to render is the fix, and it has to stay.
 */

const api = vi.hoisted(() => ({
  getProjectInfo: vi.fn(), updateProject: vi.fn(), deleteProject: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: '', projectId: 'p1' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const PROJECT = {
  id: 'p1', name: 'Customer Portal', slug: 'portal', active: true,
  require_role_to_login: false, hydra_client_id: 'client-abc',
};

beforeEach(() => {
  vi.clearAllMocks();
  auth.projectId = 'p1';
  api.getProjectInfo.mockResolvedValue(PROJECT);
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
