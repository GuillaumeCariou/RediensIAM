import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import SystemEmail from './SystemEmail';
import { fmtDateShort } from '@/lib/utils';

/**
 * A read-only map of where every organisation's mail actually goes. What it must not do is imply
 * a relay that is not there: an organisation with no SMTP of its own falls back to the global one,
 * and the table has to show the global values as the fallback rather than blanks that read as
 * "no mail configured".
 */

const api = vi.hoisted(() => ({ getEmailOverview: vi.fn() }));
vi.mock('@/api', () => api);

const GLOBAL = {
  configured: true, host: 'smtp.global.test', port: 587, start_tls: true,
  from_address: 'noreply@global.test', from_name: 'RediensIAM',
};

const ORGS = [
  {
    id: 'o1', name: 'Acme', slug: 'acme', smtp_configured: true,
    smtp_host: 'smtp.acme.test', smtp_port: 465,
    smtp_from_address: 'noreply@acme.test', smtp_from_name: 'Acme',
    smtp_updated_at: '2026-03-04T05:06:07Z',
    project_overrides: [{ id: 'p1', name: 'Portal', email_from_name: 'Acme Portal' }],
  },
  {
    id: 'o2', name: 'Globex', slug: 'globex', smtp_configured: false,
    project_overrides: [],
  },
];

beforeEach(() => {
  vi.clearAllMocks();
  api.getEmailOverview.mockResolvedValue({ global_smtp: GLOBAL, orgs: ORGS });
});

function Here() {
  return <output data-testid="here">{useLocation().pathname}</output>;
}

function show() {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={['/system/email']}>
      <Routes><Route path="*" element={<><SystemEmail /><Here /></>} /></Routes>
    </MemoryRouter>,
  );
  return user;
}

/** Router navigation is a state update, so the address is asserted once it has settled. */
const arrivedAt = (path: string) =>
  vi.waitFor(() => expect(screen.getByTestId('here').textContent).toBe(path));
/** The counter card carrying `label`. */
const stat = (label: string) => {
  const card = [...document.querySelectorAll<HTMLElement>('.iam-stat')]
    .find(c => c.querySelector('.iam-stat-label')?.textContent === label);
  if (!card) throw new Error(`no counter card labelled ${label}`);
  return card;
};
/** The table row for `name` — the name also appears in the summary above the table. */
const orgRow = (name: string) => vi.waitFor(() => {
  const row = [...document.querySelectorAll('tbody tr')]
    .find(tr => tr.querySelector('td')?.textContent?.startsWith(name));
  if (!row) throw new Error(`no row for ${name}`);
  return row as HTMLTableRowElement;
});

describe('the summary', () => {
  it('counts the organisations with their own relay, out of all of them', async () => {
    show();

    const card = await vi.waitFor(() => stat('Custom SMTP'));
    expect(card).toHaveTextContent('1 / 2');
    expect(card).toHaveTextContent('organisations with own relay');
  });

  it('counts the projects overriding the sender name across every organisation', async () => {
    show();

    const card = await vi.waitFor(() => stat('From-name overrides'));
    expect(card.querySelector('.iam-stat-value')).toHaveTextContent('1');
  });
});

describe('the global relay', () => {
  it('shows its settings when one is configured', async () => {
    show();

    expect(await screen.findByText('Active')).toBeInTheDocument();
    expect(screen.getAllByText('smtp.global.test:587')).not.toHaveLength(0);
    expect(screen.getByText('STARTTLS')).toBeInTheDocument();
    // Once as the relay's own setting, and again as the fallback on the organisation without one.
    expect(screen.getAllByText('noreply@global.test')).toHaveLength(2);
  });

  it('says implicit TLS rather than claiming STARTTLS on a 465 relay', async () => {
    api.getEmailOverview.mockResolvedValue({
      global_smtp: { ...GLOBAL, start_tls: false }, orgs: [],
    });
    show();

    expect(await screen.findByText('None / SSL')).toBeInTheDocument();
  });

  it('says nothing is set, and shows no settings, when none is', async () => {
    api.getEmailOverview.mockResolvedValue({ global_smtp: { configured: false }, orgs: [] });
    show();

    expect(await screen.findByText('Not set')).toBeInTheDocument();
    expect(screen.getByText('Not configured')).toBeInTheDocument();
    expect(screen.queryByText('STARTTLS')).not.toBeInTheDocument();
  });
});

describe('the adoption table', () => {
  it('shows an organisation with its own relay', async () => {
    show();

    expect(await orgRow('Acme')).toBeInTheDocument();
    expect(screen.getByText('smtp.acme.test:465')).toBeInTheDocument();
    expect(screen.getByText('noreply@acme.test')).toBeInTheDocument();
    expect(screen.getByText(fmtDateShort('2026-03-04T05:06:07Z'))).toBeInTheDocument();
  });

  it('shows the global values as the fallback for one without', async () => {
    // Blanks here would read as "this organisation sends no mail", which is the opposite of true.
    show();

    const row = await orgRow('Globex');
    expect(row).toHaveTextContent('Global');
    expect(row).toHaveTextContent('noreply@global.test');
    expect(row).toHaveTextContent('RediensIAM');
    expect(row).toHaveTextContent('—');
  });

  it('falls back to an em dash when there is no global relay either', async () => {
    api.getEmailOverview.mockResolvedValue({
      global_smtp: { configured: false }, orgs: [ORGS[1]],
    });
    show();

    expect(await orgRow('Globex')).toHaveTextContent('—');
  });

  it('lists the projects that override the sender name, with the name in the tooltip', async () => {
    show();

    expect(await screen.findByRole('button', { name: 'Portal' }))
      .toHaveAttribute('title', 'From name: "Acme Portal"');
  });

  it('opens the organisation\'s projects from an override chip', async () => {
    const user = show();

    await user.click(await screen.findByRole('button', { name: 'Portal' }));

    await arrivedAt('/system/organisations/o1/projects');
  });

  it('opens the organisation\'s own email page from the row', async () => {
    const user = show();
    const row = await orgRow('Acme');

    // The last button in the row is the arrow into that organisation's own email page;
    // the ones before it are the project-override chips.
    await user.click([...row.querySelectorAll('button')].at(-1)!);

    await arrivedAt('/system/organisations/o1/email');
  });

  it('says there are no organisations rather than showing an empty table', async () => {
    api.getEmailOverview.mockResolvedValue({ global_smtp: GLOBAL, orgs: [] });
    show();

    expect(await screen.findByText('No organisations yet.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});

describe('while loading and when it fails', () => {
  it('shows placeholders, and no counts that would read as real', () => {
    api.getEmailOverview.mockReturnValue(new Promise(() => {}));
    show();

    expect(document.querySelectorAll('.iam-stat')).toHaveLength(3);
    expect(screen.queryByText('Global SMTP')).not.toBeInTheDocument();
  });

  it('reports the failure rather than an empty platform', async () => {
    api.getEmailOverview.mockRejectedValue(new Error('overview unavailable'));
    show();

    expect(await screen.findByText('overview unavailable')).toBeInTheDocument();
  });

  it('has something to say even when the error carries no message', async () => {
    api.getEmailOverview.mockRejectedValue({});
    show();

    expect(await screen.findByText('Failed to load email overview')).toBeInTheDocument();
  });

  it('treats an empty body as a failure, not as a platform with nothing in it', async () => {
    api.getEmailOverview.mockResolvedValue(null);
    show();

    expect(await screen.findByText('No data returned')).toBeInTheDocument();
  });
});
