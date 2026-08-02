import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import EditUserDialog, { type UserEditFields } from './EditUserDialog';

const FORM: UserEditFields = {
  email: 'ada@acme.test', username: 'ada', display_name: 'Ada', phone: '+33600000000',
  active: true, email_verified: false, clear_lock: false, new_password: '',
};

function show(props: Partial<React.ComponentProps<typeof EditUserDialog>> = {}) {
  const h = { onChange: vi.fn(), onSubmit: vi.fn(e => e.preventDefault()), onClose: vi.fn() };
  render(
    <EditUserDialog open targetLabel="ada@acme.test" form={FORM} loading={false} saving={false} error=""
      {...h} {...props} />,
  );
  return { ...h, user: userEvent.setup() };
}

describe('the form', () => {
  it('names the account and loads its current values', () => {
    show();

    expect(screen.getByText(/Edit ada@acme.test/)).toBeInTheDocument();
    expect(screen.getByLabelText('Email')).toHaveValue('ada@acme.test');
    expect(screen.getByLabelText('Username')).toHaveValue('ada');
    expect(screen.getByLabelText('Display name')).toHaveValue('Ada');
    expect(screen.getByLabelText('Phone')).toHaveValue('+33600000000');
    expect(screen.getByLabelText('Active')).toBeChecked();
    expect(screen.getByLabelText('Email verified')).not.toBeChecked();
  });

  it('leaves the password blank, which is what keeps the current one', () => {
    show();

    const pw = screen.getByLabelText('New password');
    expect(pw).toHaveValue('');
    expect(pw).toHaveAttribute('placeholder', 'Leave blank to keep current');
    // The browser must not offer to fill the operator's own saved password into someone else's
    // account, and the minimum has to be refused before the request goes out.
    expect(pw).toHaveAttribute('autocomplete', 'new-password');
    expect(pw).toHaveAttribute('minlength', '8');
  });

  it('requires the two identifiers that cannot be blank', () => {
    show();
    expect(screen.getByLabelText('Email')).toBeRequired();
    expect(screen.getByLabelText('Username')).toBeRequired();
  });

  it('gives every instance its own field ids', async () => {
    // More than one of these can be mounted at a time; shared ids would make every <label for>
    // point at the first dialog's input.
    render(
      <>
        <EditUserDialog open targetLabel="a" form={FORM} loading={false} saving={false} error=""
          onChange={vi.fn()} onSubmit={vi.fn()} onClose={vi.fn()} />
        <EditUserDialog open targetLabel="b" form={FORM} loading={false} saving={false} error=""
          onChange={vi.fn()} onSubmit={vi.fn()} onClose={vi.fn()} />
      </>,
    );

    const ids = screen.getAllByLabelText('Email').map(e => e.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('shows placeholders instead of a half-filled form while loading', () => {
    show({ loading: true });

    expect(document.querySelectorAll('.iam-skeleton')).toHaveLength(5);
    expect(screen.queryByLabelText('Email')).not.toBeInTheDocument();
  });

  it('shows the caller\'s error, and nothing when there is none', () => {
    show({ error: 'That email is already taken.' });
    expect(screen.getByText('That email is already taken.')).toBeInTheDocument();
  });

  it('renders whatever extra controls the page adds', () => {
    show({ extra: <p>role picker</p> });
    expect(screen.getByText('role picker')).toBeInTheDocument();
  });
});

describe('editing', () => {
  it.each([
    ['Email', 'email'],
    ['Username', 'username'],
    ['Display name', 'display_name'],
    ['Phone', 'phone'],
    ['New password', 'new_password'],
  ])('reports a change to %s as a change to %s', async (label, field) => {
    const { user, onChange } = show();

    await user.fill(screen.getByLabelText(label), 'x');

    expect(onChange).toHaveBeenLastCalledWith(field, 'x');
  });

  it.each([
    ['Active', 'active', false],
    ['Email verified', 'email_verified', true],
    ['Clear account lock', 'clear_lock', true],
  ])('reports the %s switch as a boolean, not a string', async (label, field, expected) => {
    const { user, onChange } = show();

    await user.click(screen.getByLabelText(label));

    expect(onChange).toHaveBeenLastCalledWith(field, expected);
  });
});

describe('saving and cancelling', () => {
  it('submits the form from the footer button, which sits outside it', async () => {
    const { user, onSubmit } = show();

    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(onSubmit).toHaveBeenCalledOnce();
  });

  it('says it is saving and refuses a second submit meanwhile', () => {
    show({ saving: true });
    expect(screen.getByRole('button', { name: 'Saving…' })).toBeDisabled();
  });

  it('cancels without submitting', async () => {
    const { user, onClose, onSubmit } = show();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(onClose).toHaveBeenCalledOnce();
    expect(onSubmit).not.toHaveBeenCalled();
  });
});

describe('dismissing it with Escape', () => {
  it('tells the page, so the state behind it is cleared too', async () => {
    const { user, onClose } = show();

    await user.keyboard('{Escape}');

    await vi.waitFor(() => expect(onClose).toHaveBeenCalled());
  });
});
