// Aliased: the plain name is the DOM's own KeyboardEvent, which the global listener below uses.
import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from 'react';

interface IamMenuProps {
  /** Rendered inside the trigger button. */
  trigger: ReactNode;
  triggerClassName?: string;
  triggerLabel?: string;
  children: ReactNode;
}

/**
 * Dropdown menu. Closes on Escape and on any pointer press outside the anchor — `pointerdown`
 * rather than `click`, so a press that lands on another control closes this menu before that
 * control's own handler runs, instead of leaving two menus open.
 *
 * Items are plain `<button className="iam-menu-item">` at the call site; nothing here wraps them.
 */
export default function IamMenu({ trigger, triggerClassName = 'iam-btn iam-btn-ghost iam-btn-icon iam-btn-sm', triggerLabel, children }: Readonly<IamMenuProps>) {
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLDivElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  /** The items, in DOM order, skipping the ones that cannot be activated. */
  const itemsOf = (root: HTMLElement | null) =>
    [...(root?.querySelectorAll<HTMLElement>('.iam-menu-item') ?? [])].filter(el => !el.hasAttribute('disabled'));

  /**
   * Closing puts focus back on the trigger. Only for the two dismissals that come from inside the
   * menu — Escape and activating an item — because those leave focus on an element about to be
   * unmounted, and without this it would fall to <body> and the keyboard user would lose their
   * place. A press outside deliberately does not, since focus belongs wherever the user pressed.
   */
  const closeAndRestore = () => {
    setOpen(false);
    triggerRef.current?.focus();
  };

  useEffect(() => {
    if (!open) return;
    // Opening moves focus to the first item — the menu-button convention, and what makes the
    // arrow keys below have something to move from.
    itemsOf(menu.current)[0]?.focus();
    const onDown = (e: PointerEvent) => {
      if (!anchor.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') closeAndRestore(); };
    globalThis.addEventListener('pointerdown', onDown);
    globalThis.addEventListener('keydown', onKey);
    return () => {
      globalThis.removeEventListener('pointerdown', onDown);
      globalThis.removeEventListener('keydown', onKey);
    };
  }, [open]);

  /** Up/Down/Home/End walk the items, wrapping at both ends — the menu-widget convention. */
  const onMenuKeyDown = (e: ReactKeyboardEvent<HTMLDivElement>) => {
    const items = itemsOf(menu.current);
    if (items.length === 0) return;
    const at = items.indexOf(document.activeElement as HTMLElement);
    let next;
    if (e.key === 'ArrowDown')    next = at + 1 >= items.length ? 0 : at + 1;
    else if (e.key === 'ArrowUp') next = at <= 0 ? items.length - 1 : at - 1;
    else if (e.key === 'Home')    next = 0;
    else if (e.key === 'End')     next = items.length - 1;
    else return;
    e.preventDefault();   // the arrows would scroll the page behind the menu
    items[next].focus();
  };

  return (
    <div className="iam-menu-anchor" ref={anchor}>
      <button
        type="button"
        ref={triggerRef}
        className={triggerClassName}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={triggerLabel}
        onClick={() => setOpen(o => !o)}
      >
        {trigger}
      </button>
      {open && (
        // Click anywhere in the menu closes it: every item here runs one action and dismisses.
        // A keyboard activation of an item fires that same click, so this covers both pointers and
        // keys — but a `role="menu"` still has to be focusable and driven by the arrow keys, which
        // is what `tabIndex` and `onKeyDown` below are for. `-1` and not `0`: the items are real
        // buttons and already in the tab order, so a tab stop on the container would be a stop at
        // nothing. Deliberately no Enter/Space branch: a <button> fires its click on keyup for
        // Space, so closing on keydown would unmount the item before its own handler ever ran.
        <div
          className="iam-menu"
          role="menu"
          ref={menu}
          tabIndex={-1}
          onClick={closeAndRestore}
          onKeyDown={onMenuKeyDown}
        >
          {children}
        </div>
      )}
    </div>
  );
}
