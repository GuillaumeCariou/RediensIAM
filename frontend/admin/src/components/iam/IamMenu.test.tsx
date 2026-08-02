import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import IamMenu from './IamMenu';

function open() {
  const onA = vi.fn();
  const user = userEvent.setup();
  render(
    <IamMenu trigger={<span>dots</span>} triggerLabel="Actions">
      <button type="button" className="iam-menu-item" onClick={onA}>Alpha</button>
      <button type="button" className="iam-menu-item" disabled>Nope</button>
      <button type="button" className="iam-menu-item">Beta</button>
    </IamMenu>,
  );
  return { user, onA, trigger: screen.getByRole('button', { name: 'Actions' }) };
}

const item = (name: string) => screen.getByRole('button', { name });

describe('IamMenu keyboard', () => {
  it('opens with the keyboard and lands on the first item', async () => {
    const { user, trigger } = open();
    trigger.focus();
    await user.keyboard('{Enter}');
    expect(item('Alpha')).toHaveFocus();
  });

  it('arrows walk enabled items only, and wrap', async () => {
    const { user, trigger } = open();
    trigger.focus();
    await user.keyboard('{Enter}');

    await user.keyboard('{ArrowDown}');
    expect(item('Beta')).toHaveFocus();        // skipped the disabled one
    await user.keyboard('{ArrowDown}');
    expect(item('Alpha')).toHaveFocus();       // wrapped
    await user.keyboard('{ArrowUp}');
    expect(item('Beta')).toHaveFocus();
    await user.keyboard('{Home}');
    expect(item('Alpha')).toHaveFocus();
    await user.keyboard('{End}');
    expect(item('Beta')).toHaveFocus();
  });

  it('Space on an item still runs it — the menu must not close before keyup', async () => {
    const { user, trigger, onA } = open();
    trigger.focus();
    await user.keyboard('{Enter}');
    await user.keyboard(' ');
    expect(onA).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });

  it('Enter on an item runs it and returns focus to the trigger', async () => {
    const { user, trigger, onA } = open();
    trigger.focus();
    await user.keyboard('{Enter}{Enter}');
    expect(onA).toHaveBeenCalledTimes(1);
    expect(trigger).toHaveFocus();
  });

  it('Escape closes and returns focus to the trigger', async () => {
    const { user, trigger } = open();
    trigger.focus();
    await user.keyboard('{Enter}');
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });

  it('the menu container is focusable, as its role requires', async () => {
    const { user, trigger } = open();
    await user.click(trigger);
    expect(screen.getByRole('menu')).toHaveAttribute('tabindex', '-1');
  });
});
