import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import SystemAdmins from './SystemAdmins';

/**
 * The platform's super admins live in one particular user list: the one with no organisation that
 * is marked immovable. Picking any other list here would show — and let an operator edit — the
 * wrong set of accounts, so the identification is the whole page.
 */

const api = vi.hoisted(() => ({ listUserLists: vi.fn() }));
vi.mock('@/api', () => api);

const panel = vi.hoisted(() => ({ props: {} as Record<string, unknown> }));
vi.mock('@/components/UserListMembersPanel', () => ({
  default: (props: Record<string, unknown>) => {
    panel.props = props;
    return <p>members of {String(props['listId'])}</p>;
  },
}));

const SYSTEM_LIST = { id: 'sys', org_id: null, immovable: true };
const TENANT_LIST = { id: 'l1', org_id: 'o1', immovable: true };
/** An organisation-less list that is not the system one — a deleted tenant leaves these behind. */
const ORPHAN_LIST = { id: 'l2', org_id: null, immovable: false };

beforeEach(() => {
  vi.clearAllMocks();
  panel.props = {};
  api.listUserLists.mockResolvedValue({ user_lists: [TENANT_LIST, ORPHAN_LIST, SYSTEM_LIST] });
});

describe('finding the system list', () => {
  it('picks the immovable list belonging to no organisation', async () => {
    render(<SystemAdmins />);

    expect(await screen.findByText('members of sys')).toBeInTheDocument();
    expect(panel.props['isSystemCtx']).toBe(true);
  });

  it('accepts a bare array as well as an envelope', async () => {
    api.listUserLists.mockResolvedValue([TENANT_LIST, SYSTEM_LIST]);
    render(<SystemAdmins />);

    expect(await screen.findByText('members of sys')).toBeInTheDocument();
  });

  it('shows placeholder rows while it looks', () => {
    api.listUserLists.mockReturnValue(new Promise(() => {}));
    const { container } = render(<SystemAdmins />);

    expect(container.querySelectorAll('.iam-card > div')).toHaveLength(3);
    expect(screen.queryByText(/^members of/)).not.toBeInTheDocument();
  });
});

describe('when the list cannot be identified', () => {
  it('says so rather than showing the members of some other list', async () => {
    api.listUserLists.mockResolvedValue({ user_lists: [TENANT_LIST, ORPHAN_LIST] });
    render(<SystemAdmins />);

    expect(await screen.findByText('System user list not found.')).toBeInTheDocument();
    expect(screen.queryByText(/^members of/)).not.toBeInTheDocument();
  });

  it('says so when the lists cannot be read at all', async () => {
    api.listUserLists.mockRejectedValue(new Error('500'));
    render(<SystemAdmins />);

    expect(await screen.findByText('Failed to load system admin list.')).toBeInTheDocument();
  });
});
