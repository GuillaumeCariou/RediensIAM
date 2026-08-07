import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import OAuth2Clients from './OAuth2Clients';
import { ApiError } from '@/auth';

/**
 * The four Hydra client routes had no caller at all, so the whole surface is new. Two properties
 * are worth more than the rest: a refusal is on the screen rather than in the devtools, and the
 * delete confirmation says what a deleted client actually costs — the application using it stops
 * being able to sign anyone in.
 *
 * The mock factory REPLACES `@/api`, so every export this page imports has to be here, used or
 * not, or the file fails to link before a single assertion runs.
 */

const api = vi.hoisted(() => ({
  listHydraClients: vi.fn(),
  createHydraClient: vi.fn(),
  getHydraClient: vi.fn(),
  deleteHydraClient: vi.fn(),
}));
vi.mock('@/api', () => api);

const client = (over: Record<string, unknown> = {}) => ({
  client_id: 'billing-portal',
  client_name: 'Billing portal',
  grant_types: ['authorization_code', 'refresh_token'],
  redirect_uris: ['https://billing.test/callback'],
  post_logout_redirect_uris: ['https://billing.test/'],
  scope: 'openid profile offline_access',
  token_endpoint_auth_method: 'none',
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  api.listHydraClients.mockResolvedValue([client()]);
  api.getHydraClient.mockResolvedValue(client());
  api.createHydraClient.mockResolvedValue(client());
  api.deleteHydraClient.mockResolvedValue(undefined);
});

function show() {
  const user = userEvent.setup();
  render(<OAuth2Clients />);
  return user;
}

describe('the list', () => {
  it('shows the id, the name, the grants and where the client may send the browser', async () => {
    show();

    expect(await screen.findByText('billing-portal')).toBeInTheDocument();
    expect(screen.getByText('Billing portal')).toBeInTheDocument();
    expect(screen.getByText('authorization_code')).toBeInTheDocument();
    expect(screen.getByText('https://billing.test/callback')).toBeInTheDocument();
  });

  it('says plainly when Hydra holds none', async () => {
    api.listHydraClients.mockResolvedValue([]);
    show();

    expect(await screen.findByText('No OAuth2 clients')).toBeInTheDocument();
  });

  it('treats a null body as none', async () => {
    api.listHydraClients.mockResolvedValue(null);
    show();

    expect(await screen.findByText('No OAuth2 clients')).toBeInTheDocument();
  });

  it('names the failure when the registry cannot be read', async () => {
    api.listHydraClients.mockRejectedValue(new Error('502'));
    show();

    expect(await screen.findByText(/Could not read the OAuth2 clients/)).toBeInTheDocument();
  });

  /**
   * The list is Hydra's own, so it also contains the clients the console mints for a project and
   * for a service account. Those are not integrations an operator registered here, and deleting
   * one takes a tenant's sign-in down with it.
   */
  it.each([
    ['client_p1', 'project'],
    ['sa_deploy', 'service account'],
  ])('marks %s as belonging to a %s', async (id, kind) => {
    api.listHydraClients.mockResolvedValue([client({ client_id: id })]);
    show();

    expect(await screen.findByText(kind)).toBeInTheDocument();
  });
});

describe('the detail', () => {
  it('reads the client back and shows what the row has no room for', async () => {
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Details' }));

    await waitFor(() => expect(api.getHydraClient).toHaveBeenCalledWith('billing-portal'));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('openid profile offline_access')).toBeInTheDocument();
    expect(within(dialog).getByText('none')).toBeInTheDocument();
  });

  it('says so when the client is gone', async () => {
    api.getHydraClient.mockRejectedValue(new Error('404'));
    const user = show();
    await user.click(await screen.findByRole('button', { name: 'Details' }));

    expect(await screen.findByText(/may have just been deleted/)).toBeInTheDocument();
  });
});

describe('registering one', () => {
  async function fillForm(user: Awaited<ReturnType<typeof show>>) {
    await user.click(await screen.findByRole('button', { name: 'New Client' }));
    await user.fill(screen.getByLabelText('Name'), 'Billing portal');
    await user.fill(screen.getByLabelText('Client ID (optional)'), 'billing-portal');
    await user.fill(screen.getByLabelText('Redirect URIs — one per line'),
      'https://billing.test/callback\nhttps://billing.test/cb2');
  }

  it('sends one array per line and drops the empty ones', async () => {
    const user = show();
    await fillForm(user);
    await user.click(screen.getByRole('button', { name: 'Create client' }));

    await waitFor(() => expect(api.createHydraClient).toHaveBeenCalledWith({
      client_name: 'Billing portal',
      grant_types: ['authorization_code', 'refresh_token'],
      redirect_uris: ['https://billing.test/callback', 'https://billing.test/cb2'],
      post_logout_redirect_uris: [],
      scope: undefined,
      client_id: 'billing-portal',
    }));
    expect(api.listHydraClients).toHaveBeenCalledTimes(2);
  });

  it('sends the grants the operator actually ticked', async () => {
    const user = show();
    await fillForm(user);
    await user.click(screen.getByRole('checkbox', { name: 'refresh_token' }));
    await user.click(screen.getByRole('checkbox', { name: 'client_credentials' }));
    await user.click(screen.getByRole('button', { name: 'Create client' }));

    await waitFor(() => expect(api.createHydraClient).toHaveBeenCalledWith(
      expect.objectContaining({ grant_types: ['authorization_code', 'client_credentials'] }),
    ));
  });

  /** The refusal that used to exist only in the devtools: a reserved or taken id. */
  it('shows the reason the server refused, in words', async () => {
    api.createHydraClient.mockRejectedValue(new ApiError(409, { error: 'client_id_taken' }));
    const user = show();
    await fillForm(user);
    await user.click(screen.getByRole('button', { name: 'Create client' }));

    expect(await screen.findByText('A client already uses that id.')).toBeInTheDocument();
    expect(api.listHydraClients).toHaveBeenCalledTimes(1);
  });

  it('explains what a client id may contain when the server rejects it', async () => {
    api.createHydraClient.mockRejectedValue(new ApiError(400, { error: 'invalid_client_id' }));
    const user = show();
    await fillForm(user);
    await user.click(screen.getByRole('button', { name: 'Create client' }));

    expect(await screen.findByText(/reserved by the backend/)).toBeInTheDocument();
  });

  it('does not swallow a failure with no error code', async () => {
    api.createHydraClient.mockRejectedValue(new Error('boom'));
    const user = show();
    await fillForm(user);
    await user.click(screen.getByRole('button', { name: 'Create client' }));

    expect(await screen.findByText('Could not create the client.')).toBeInTheDocument();
  });
});

describe('deleting one', () => {
  const openConfirm = async (user: Awaited<ReturnType<typeof show>>) =>
    user.click(await screen.findByRole('button', { name: 'Delete' }));

  it('asks first, and says the application stops being able to sign anyone in', async () => {
    const user = show();
    await openConfirm(user);

    expect(await screen.findByText(/stops being able to sign anyone in/)).toBeInTheDocument();
    expect(screen.getByText(/Delete client "billing-portal"\?/)).toBeInTheDocument();
    expect(api.deleteHydraClient).not.toHaveBeenCalled();
  });

  it('warns harder when the client belongs to a project the console manages', async () => {
    api.listHydraClients.mockResolvedValue([client({ client_id: 'client_p1' })]);
    const user = show();
    await openConfirm(user);

    expect(await screen.findByText(/leaves that project registered with no/)).toBeInTheDocument();
  });

  it('deletes on confirmation and reloads', async () => {
    const user = show();
    await openConfirm(user);
    await user.click(screen.getByRole('button', { name: 'Delete client' }));

    await waitFor(() => expect(api.deleteHydraClient).toHaveBeenCalledWith('billing-portal'));
    expect(api.listHydraClients).toHaveBeenCalledTimes(2);
  });

  it('does nothing on cancel', async () => {
    const user = show();
    await openConfirm(user);
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    expect(api.deleteHydraClient).not.toHaveBeenCalled();
  });

  it('shows a refused delete rather than closing on it', async () => {
    api.deleteHydraClient.mockRejectedValue(new ApiError(500, { detail: 'Hydra is unreachable' }));
    const user = show();
    await openConfirm(user);
    await user.click(screen.getByRole('button', { name: 'Delete client' }));

    expect(await screen.findByText('Hydra is unreachable')).toBeInTheDocument();
  });
});
