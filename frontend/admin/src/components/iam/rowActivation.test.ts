import { describe, expect, it, vi } from 'vitest';
import type { KeyboardEvent } from 'react';
import { rowActivation } from './rowActivation';

/** A key event on the row itself unless `onChild` says it started in a control inside it. */
function keyEvent(key: string, onChild = false) {
  const row = { tag: 'tr' };
  return {
    key,
    currentTarget: row,
    target: onChild ? { tag: 'button' } : row,
    preventDefault: vi.fn(),
  } as unknown as KeyboardEvent<HTMLTableRowElement> & { preventDefault: ReturnType<typeof vi.fn> };
}

describe('rowActivation', () => {
  it('makes the row a focus stop, which a bare onClick does not', () => {
    expect(rowActivation(() => {}).tabIndex).toBe(0);
  });

  it('opens the row on click', () => {
    const activate = vi.fn();
    rowActivation(activate).onClick();
    expect(activate).toHaveBeenCalledOnce();
  });

  it.each(['Enter', ' '])('opens the row on %s', key => {
    const activate = vi.fn();
    const e = keyEvent(key);

    rowActivation(activate).onKeyDown(e);

    expect(activate).toHaveBeenCalledOnce();
    // Space would scroll the page out from under the row that just opened.
    expect(e.preventDefault).toHaveBeenCalledOnce();
  });

  it('ignores every other key, so typing still reaches the page', () => {
    const activate = vi.fn();
    const e = keyEvent('a');

    rowActivation(activate).onKeyDown(e);

    expect(activate).not.toHaveBeenCalled();
    expect(e.preventDefault).not.toHaveBeenCalled();
  });

  it('leaves a key pressed on a control inside the row to that control', () => {
    // Enter on the row's own delete button must delete, not open the row behind the button.
    const activate = vi.fn();
    const e = keyEvent('Enter', true);

    rowActivation(activate).onKeyDown(e);

    expect(activate).not.toHaveBeenCalled();
    expect(e.preventDefault).not.toHaveBeenCalled();
  });
});
