import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes } from 'react-router';
import OrgWebhooks from './OrgWebhooks';
import { fmtDate } from '@/lib/utils';

/**
 * Two things here are not cosmetic.
 *
 * `/org/webhooks` is scoped by the caller's own token and has no admin-scope equivalent, so from
 * the system console this page would list and edit the SIGNED-IN admin's webhooks while the URL
 * named someone else's organisation. It refuses instead.
 *
 * And the signing secret is shown exactly once. A webhook created without one leaves the operator
 * unable to verify signatures with no second chance to read it, so that case is surfaced loudly
 * rather than closing the dialog on a success that is not one.
 */

const api = vi.hoisted(() => ({
  listWebhooks: vi.fn(), createWebhook: vi.fn(), updateWebhook: vi.fn(),
  deleteWebhook: vi.fn(), testWebhook: vi.fn(), listWebhookDeliveries: vi.fn(),
  rotateWebhookSecret: vi.fn(), getWebhook: vi.fn(),
}));
vi.mock('@/api', () => api);

const auth = vi.hoisted(() => ({ orgId: 'o1', projectId: '' }));
vi.mock('@/context/AuthContext', () => ({ useAuth: () => auth }));

const WEBHOOKS = [
  {
    id: 'w1', url: 'https://hooks.acme.test/iam', active: true,
    events: ['user.created', 'user.updated', 'user.deleted', 'role.assigned'],
    last_delivery_status: 200, created_at: '2026-01-02T00:00:00Z',
  },
  {
    id: 'w2', url: 'https://hooks.acme.test/audit', active: false,
    events: ['session.revoked'], last_delivery_status: 500, created_at: '2026-01-02T00:00:00Z',
  },
  {
    id: 'w3', url: 'https://hooks.acme.test/new', active: true,
    events: ['project.updated'], last_delivery_status: null, created_at: '2026-01-02T00:00:00Z',
  },
];

const DELIVERIES = [
  { id: 'd1', event: 'user.created', status_code: 200, attempt_count: 1, delivered_at: '2026-03-04T05:06:07Z', payload: '{"id":"u1"}' },
  { id: 'd2', event: 'user.deleted', status_code: 500, attempt_count: 3, delivered_at: null, payload: 'not json' },
  { id: 'd3', event: 'role.assigned', status_code: null, attempt_count: 0, delivered_at: null, payload: null },
];

beforeEach(() => {
  vi.clearAllMocks();
  auth.orgId = 'o1';
  api.listWebhooks.mockResolvedValue(WEBHOOKS);
  api.createWebhook.mockResolvedValue({ secret: 'whsec_abcdef' });
  api.testWebhook.mockResolvedValue({});
  api.listWebhookDeliveries.mockResolvedValue(DELIVERIES);
  api.getWebhook.mockResolvedValue({ ...WEBHOOKS[0], recent_deliveries: DELIVERIES.slice(0, 2) });
});

function show(path = '/org/webhooks', pattern = '/org/webhooks') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes><Route path={pattern} element={<OrgWebhooks />} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

const SYSTEM = ['/system/organisations/o9/webhooks', '/system/organisations/:id/webhooks'] as const;
const rowFor = (url: string) => screen.getByRole('row', { name: new RegExp(url) });
const openMenu = (user: Awaited<ReturnType<typeof show>>, url: string) =>
  user.click([...rowFor(url).querySelectorAll('button')].at(-1)!);

describe('from the system console', () => {
  it('refuses rather than editing the signed-in admin\'s own webhooks', async () => {
    show(...SYSTEM);

    expect(await screen.findByText('Not available from the system console')).toBeInTheDocument();
    expect(api.listWebhooks).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: /Add Webhook/ })).not.toBeInTheDocument();
  });
});

/**
 * Le tableau tronque : trois événements sur N, une URL coupée à 240px. `GET /org/webhooks/{id}`
 * rend les deux entiers plus les dix dernières livraisons, et n'avait aucun appelant.
 */
describe('the detail view', () => {
  const openDetail = async (user: Awaited<ReturnType<typeof show>>) => {
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');
    await user.click(screen.getByRole('button', { name: 'View details' }));
  };

  it('reads the one webhook, not the whole list again', async () => {
    const user = show();

    await openDetail(user);

    await vi.waitFor(() => expect(api.getWebhook).toHaveBeenCalledWith('w1'));
    expect(api.listWebhooks).toHaveBeenCalledTimes(1);
  });

  it('shows every event, including the ones the row hid', async () => {
    const user = show();

    await openDetail(user);

    const dialog = await screen.findByText('Webhook Details');
    const panel = dialog.closest('dialog')!;
    expect(within(panel).getByText('role.assigned')).toBeInTheDocument();
    expect(within(panel).getByText('Events (4)')).toBeInTheDocument();
  });

  it('lists the recent deliveries the route returns', async () => {
    const user = show();

    await openDetail(user);

    const panel = (await screen.findByText('Webhook Details')).closest('dialog')!;
    // Once as a subscribed event, once as a delivery of it.
    expect(within(panel).getAllByText('user.created')).toHaveLength(2);
    expect(within(panel).getByText('200')).toBeInTheDocument();
    expect(within(panel).getByText('500')).toBeInTheDocument();
  });

  it('says so when there is nothing to show yet', async () => {
    api.getWebhook.mockResolvedValue({ ...WEBHOOKS[2], recent_deliveries: [] });
    const user = show();

    await openDetail(user);

    expect(await screen.findByText('No deliveries yet.')).toBeInTheDocument();
  });

  it('shows the refusal rather than an empty dialog', async () => {
    api.getWebhook.mockRejectedValue(new Error('403'));
    const user = show();

    await openDetail(user);

    expect(await screen.findByText('Could not read this webhook.')).toBeInTheDocument();
  });
});

describe('the table', () => {
  it('lists each endpoint with its status and creation date', async () => {
    show();

    expect(await screen.findByText('https://hooks.acme.test/iam')).toBeInTheDocument();
    expect(rowFor('/iam')).toHaveTextContent('200');
    expect(rowFor('/audit')).toHaveTextContent('500');
    expect(rowFor('/iam')).toHaveTextContent(fmtDate('2026-01-02T00:00:00Z'));
  });

  it('shows the first few events and counts the rest', async () => {
    show();

    await screen.findByText('https://hooks.acme.test/iam');
    expect(rowFor('/iam')).toHaveTextContent('user.created');
    expect(rowFor('/iam')).toHaveTextContent('+1');
    expect(rowFor('/iam')).not.toHaveTextContent('role.assigned');
  });

  it('shows an em dash for an endpoint nothing has been delivered to yet', async () => {
    show();

    await screen.findByText('https://hooks.acme.test/new');
    expect(rowFor('/new')).toHaveTextContent('—');
  });

  it('shows placeholders while loading', () => {
    api.listWebhooks.mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.queryByText('No webhooks configured')).not.toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('says there are none', async () => {
    api.listWebhooks.mockResolvedValue([]);
    show();

    expect(await screen.findByText('No webhooks configured')).toBeInTheDocument();
  });

  it('treats a body that is not a list as none', async () => {
    api.listWebhooks.mockResolvedValue({ webhooks: WEBHOOKS });
    show();

    expect(await screen.findByText('No webhooks configured')).toBeInTheDocument();
  });

  it('survives a listing that fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listWebhooks.mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('No webhooks configured')).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('flips an endpoint on and off', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');

    await user.click(rowFor('/iam').querySelector('input')!);

    await vi.waitFor(() => expect(api.updateWebhook).toHaveBeenCalledWith('w1', { active: false }));
    expect(rowFor('/iam').querySelector('input')).not.toBeChecked();
  });
});

describe('adding an endpoint', () => {
  const openAdd = async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await user.click(screen.getByRole('button', { name: /Add Webhook/ }));
    return user;
  };

  it('refuses a URL that is not HTTPS, before sending anything', async () => {
    // The payload carries user data and the signature only protects it in transit.
    const user = await openAdd();

    await user.fill(screen.getByLabelText('URL'), 'http://hooks.acme.test/iam');
    await user.click(screen.getByRole('checkbox', { name: 'user.created' }));
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));

    expect(await screen.findByText('URL must use HTTPS.')).toBeInTheDocument();
    expect(api.createWebhook).not.toHaveBeenCalled();
  });

  it('refuses an endpoint subscribed to nothing', async () => {
    const user = await openAdd();

    await user.fill(screen.getByLabelText('URL'), 'https://hooks.acme.test/iam');
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));

    expect(await screen.findByText('Select at least one event.')).toBeInTheDocument();
    expect(api.createWebhook).not.toHaveBeenCalled();
  });

  it('creates it and shows the signing secret exactly once', async () => {
    const user = await openAdd();

    await user.fill(screen.getByLabelText('URL'), 'https://hooks.acme.test/iam');
    await user.click(screen.getByRole('checkbox', { name: 'user.created' }));
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));

    await vi.waitFor(() => expect(api.createWebhook)
      .toHaveBeenCalledWith({ url: 'https://hooks.acme.test/iam', events: ['user.created'] }));
    expect(await screen.findByText('whsec_abcdef')).toBeInTheDocument();
    expect(screen.getByText(/won't be shown again/)).toBeInTheDocument();
  });

  it('copies the secret', async () => {
    const writeText = vi.fn(async () => {});
    vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue({ writeText } as unknown as Clipboard);
    const user = await openAdd();
    await user.fill(screen.getByLabelText('URL'), 'https://hooks.acme.test/iam');
    await user.click(screen.getByRole('checkbox', { name: 'user.created' }));
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));
    await screen.findByText('whsec_abcdef');

    await user.click(screen.getByRole('button', { name: 'Copy' }));

    expect(writeText).toHaveBeenCalledWith('whsec_abcdef');
    vi.restoreAllMocks();
  });

  it('forgets the secret once acknowledged', async () => {
    const user = await openAdd();
    await user.fill(screen.getByLabelText('URL'), 'https://hooks.acme.test/iam');
    await user.click(screen.getByRole('checkbox', { name: 'user.created' }));
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));
    await screen.findByText('whsec_abcdef');

    await user.click(screen.getByRole('button', { name: "I've saved it" }));

    expect(screen.queryByText('whsec_abcdef')).not.toBeInTheDocument();
  });

  it('says so loudly when no secret came back, instead of closing on a false success', async () => {
    api.createWebhook.mockResolvedValue({});
    const user = await openAdd();

    await user.fill(screen.getByLabelText('URL'), 'https://hooks.acme.test/iam');
    await user.click(screen.getByRole('checkbox', { name: 'user.created' }));
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));

    expect(await screen.findByText(/did not return a signing secret/)).toBeInTheDocument();
    expect(screen.getByLabelText('URL')).toBeInTheDocument();
  });

  it('reports what the server refused, in its own words when it gave any', async () => {
    api.createWebhook.mockResolvedValue({ error: 'url_not_allowed', error_description: 'That host is private.' });
    const user = await openAdd();

    await user.fill(screen.getByLabelText('URL'), 'https://hooks.acme.test/iam');
    await user.click(screen.getByRole('checkbox', { name: 'user.created' }));
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));

    expect(await screen.findByText('That host is private.')).toBeInTheDocument();
  });

  it('falls back to a generic message when it gave none', async () => {
    api.createWebhook.mockResolvedValue({ error: 'url_not_allowed' });
    const user = await openAdd();

    await user.fill(screen.getByLabelText('URL'), 'https://hooks.acme.test/iam');
    await user.click(screen.getByRole('checkbox', { name: 'user.created' }));
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));

    expect(await screen.findByText('Failed to create webhook.')).toBeInTheDocument();
  });

  it('clears the last error when the dialog is reopened', async () => {
    const user = await openAdd();
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));
    await screen.findByText('URL must use HTTPS.');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await user.click(screen.getByRole('button', { name: /Add Webhook/ }));

    expect(screen.queryByText('URL must use HTTPS.')).not.toBeInTheDocument();
  });
});

describe('choosing which events to subscribe to', () => {
  const openAdd = async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await user.click(screen.getByRole('button', { name: /Add Webhook/ }));
    return user;
  };
  const group = (label: string) => screen.getByRole('checkbox', { name: new RegExp(label, 'i') });

  it('ticks and unticks a whole group at once', async () => {
    const user = await openAdd();

    await user.click(group('Role events'));

    expect(screen.getByRole('checkbox', { name: 'role.assigned' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'role.revoked' })).toBeChecked();

    await user.click(group('Role events'));

    expect(screen.getByRole('checkbox', { name: 'role.assigned' })).not.toBeChecked();
  });

  it('marks a partly-chosen group as indeterminate, not as chosen', async () => {
    const user = await openAdd();

    await user.click(screen.getByRole('checkbox', { name: 'role.assigned' }));

    const g = group('Role events') as HTMLInputElement;
    expect(g.indeterminate).toBe(true);
    expect(g).not.toBeChecked();
  });

  it('marks it chosen once every event in it is', async () => {
    const user = await openAdd();

    await user.click(screen.getByRole('checkbox', { name: 'role.assigned' }));
    await user.click(screen.getByRole('checkbox', { name: 'role.revoked' }));

    const g = group('Role events') as HTMLInputElement;
    expect(g).toBeChecked();
    expect(g.indeterminate).toBe(false);
  });

  it('adds a group without dropping choices made elsewhere', async () => {
    const user = await openAdd();

    await user.click(screen.getByRole('checkbox', { name: 'session.revoked' }));
    await user.click(group('Role events'));
    await user.fill(screen.getByLabelText('URL'), 'https://hooks.acme.test/iam');
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));

    await vi.waitFor(() => expect(api.createWebhook).toHaveBeenCalledWith({
      url: 'https://hooks.acme.test/iam',
      events: ['session.revoked', 'role.assigned', 'role.revoked'],
    }));
  });
});

describe('the row menu', () => {
  it('sends a test payload and says so', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.click(screen.getByRole('button', { name: 'Test' }));

    await vi.waitFor(() => expect(api.testWebhook).toHaveBeenCalledWith('w1'));
    expect(await screen.findByText('Test payload sent.')).toBeInTheDocument();
  });

  it('reports a test the endpoint refused', async () => {
    api.testWebhook.mockResolvedValue({ error: 'connection_refused' });
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.click(screen.getByRole('button', { name: 'Test' }));

    expect(await screen.findByText('Test failed: connection_refused')).toBeInTheDocument();
  });

  it('takes the test result away again', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');
    await user.click(screen.getByRole('button', { name: 'Test' }));
    await screen.findByText('Test payload sent.');

    await vi.waitFor(() => expect(screen.queryByText('Test payload sent.')).toBeNull(), { timeout: 7000 });
  }, 12_000);

  it('deletes an endpoint and drops it from the table', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.click(screen.getByRole('button', { name: 'Delete' }));

    await vi.waitFor(() => expect(api.deleteWebhook).toHaveBeenCalledWith('w1'));
    await vi.waitFor(() => expect(screen.queryByText('https://hooks.acme.test/iam')).toBeNull());
  });

  it('closes on Escape', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('button', { name: 'Test' })).not.toBeInTheDocument();
  });

  it('closes when the operator clicks away', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.click(document.querySelector<HTMLElement>('[role="none"]')!);

    expect(screen.queryByRole('button', { name: 'Test' })).not.toBeInTheDocument();
  });
});

describe('the delivery log', () => {
  /** Scoped to the dialog: `user.created` is also an event chip on the webhook's own row. */
  const log = () => within(screen.getByRole('dialog', { name: /Delivery Log/ }));

  const openLog = async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');
    await user.click(screen.getByRole('button', { name: 'View deliveries' }));
    await screen.findByText('3 attempts');
    return user;
  };

  it('lists each attempt with its outcome', async () => {
    await openLog();

    expect(api.listWebhookDeliveries).toHaveBeenCalledWith('w1');
    expect(log().getByText('200')).toBeInTheDocument();
    expect(log().getByText('500')).toBeInTheDocument();
    expect(log().getByText('1 attempt')).toBeInTheDocument();
    expect(log().getByText('3 attempts')).toBeInTheDocument();
  });

  it('marks a delivery that has not been attempted yet as pending, not as failed', async () => {
    await openLog();

    expect(log().getByText('pending')).toBeInTheDocument();
    expect(log().getByText('0 attempts')).toBeInTheDocument();
  });

  it('pretty-prints a JSON payload when the row is opened', async () => {
    const user = await openLog();

    await user.click(log().getByText('user.created'));

    expect(await screen.findByText(/"id": "u1"/)).toBeInTheDocument();
  });

  it('shows a payload that is not JSON as it came', async () => {
    const user = await openLog();

    await user.click(log().getByText('user.deleted'));

    expect(await screen.findByText('not json')).toBeInTheDocument();
  });

  it('closes the row again on a second click', async () => {
    const user = await openLog();
    await user.click(log().getByText('user.created'));
    await screen.findByText(/"id": "u1"/);

    await user.click(log().getByText('user.created'));

    expect(screen.queryByText(/"id": "u1"/)).not.toBeInTheDocument();
  });

  it('has nothing to expand for a delivery with no payload', async () => {
    const user = await openLog();

    await user.click(log().getByText('role.assigned'));

    expect(document.querySelector('pre')).toBeNull();
  });

  it('shows placeholders while the log loads', async () => {
    api.listWebhookDeliveries.mockReturnValue(new Promise(() => {}));
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.click(screen.getByRole('button', { name: 'View deliveries' }));

    expect(screen.queryByText('No deliveries yet.')).not.toBeInTheDocument();
  });

  it('says there are none', async () => {
    api.listWebhookDeliveries.mockResolvedValue([]);
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.click(screen.getByRole('button', { name: 'View deliveries' }));

    expect(await screen.findByText('No deliveries yet.')).toBeInTheDocument();
  });

  it('treats a body that is not a list as none', async () => {
    api.listWebhookDeliveries.mockResolvedValue({ deliveries: DELIVERIES });
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.click(screen.getByRole('button', { name: 'View deliveries' }));

    expect(await screen.findByText('No deliveries yet.')).toBeInTheDocument();
  });

  it('survives a log that cannot be read', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    api.listWebhookDeliveries.mockRejectedValue(new Error('500'));
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.click(screen.getByRole('button', { name: 'View deliveries' }));

    expect(await screen.findByText('No deliveries yet.')).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('closes', async () => {
    const user = await openLog();

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(screen.queryByText('3 attempts')).not.toBeInTheDocument();
  });
});


describe('dismissing a dialog with Escape', () => {
  it('closes the add form without creating anything', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await user.click(screen.getByRole('button', { name: /Add Webhook/ }));

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByLabelText('URL')).toBeNull());
    expect(api.createWebhook).not.toHaveBeenCalled();
  });

  it('closes the secret dialog, which is the operator\'s only sight of it', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await user.click(screen.getByRole('button', { name: /Add Webhook/ }));
    await user.fill(screen.getByLabelText('URL'), 'https://hooks.acme.test/new');
    await user.click(screen.getByRole('checkbox', { name: 'user.created' }));
    await user.click(screen.getByRole('button', { name: 'Create Webhook' }));
    await screen.findByText('whsec_abcdef');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('whsec_abcdef')).toBeNull());
  });

  it('closes the delivery log', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');
    await user.click(screen.getByRole('button', { name: 'View deliveries' }));
    await screen.findByText('3 attempts');

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(screen.queryByText('3 attempts')).toBeNull());
  });
});


describe('rotating the signing secret', () => {
  it('confirms before rotating, naming the endpoint that will start failing', async () => {
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');

    await user.click(screen.getByRole('button', { name: 'Rotate secret' }));

    expect(await screen.findByText(/stops being valid immediately/)).toBeInTheDocument();
    expect(api.rotateWebhookSecret).not.toHaveBeenCalled();
  });

  it('shows the new secret once, since nothing can read it back', async () => {
    api.rotateWebhookSecret.mockResolvedValue({ secret: 's3cr3t-rotated' });
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');
    await user.click(screen.getByRole('button', { name: 'Rotate secret' }));

    await user.click(screen.getByRole('button', { name: 'Rotate' }));

    await vi.waitFor(() => expect(api.rotateWebhookSecret).toHaveBeenCalledWith('w1'));
    expect(await screen.findByText('s3cr3t-rotated')).toBeInTheDocument();
    expect(screen.getByText(/won't be shown again/)).toBeInTheDocument();
  });

  it('says so when the server rotated but returned nothing', async () => {
    api.rotateWebhookSecret.mockResolvedValue({ message: 'store_secret_shown_once' });
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');
    await user.click(screen.getByRole('button', { name: 'Rotate secret' }));

    await user.click(screen.getByRole('button', { name: 'Rotate' }));

    expect(await screen.findByText(/did not return it/)).toBeInTheDocument();
  });

  it('does not swallow a refusal', async () => {
    api.rotateWebhookSecret.mockRejectedValue(new Error('403'));
    const user = show();
    await screen.findByText('https://hooks.acme.test/iam');
    await openMenu(user, '/iam');
    await user.click(screen.getByRole('button', { name: 'Rotate secret' }));

    await user.click(screen.getByRole('button', { name: 'Rotate' }));

    expect(await screen.findByText('Failed to rotate the signing secret.')).toBeInTheDocument();
  });
});
