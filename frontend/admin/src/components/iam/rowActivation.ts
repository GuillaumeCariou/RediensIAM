import type { KeyboardEvent } from 'react';

/**
 * Props for a table row whose whole surface is the click target.
 *
 * A bare `<tr onClick>` is reachable with a mouse and by nothing else: no tabindex, no key
 * handler, so a keyboard user tabs past every row. Spreading this gives the row focus and the two
 * keys a control is expected to answer to.
 *
 * No `role="button"` on purpose — that would take the row out of the table's row/cell semantics,
 * which is a worse trade than an unlabelled focus stop. Action buttons inside the row still need
 * their own `onClick={e => e.stopPropagation()}` cell wrapper.
 */
export function rowActivation(activate: () => void) {
  return {
    tabIndex: 0,
    style: { cursor: 'pointer' },
    onClick: activate,
    onKeyDown: (e: KeyboardEvent<HTMLTableRowElement>) => {
      if (e.key !== 'Enter' && e.key !== ' ') return;
      if (e.target !== e.currentTarget) return;   // a control inside the row handles its own keys
      e.preventDefault();                          // Space would scroll the page
      activate();
    },
  };
}
