import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

afterEach(cleanup);

// jsdom 29 ships HTMLDialogElement with a reflected `open` property and none of its methods,
// so a component that calls showModal() cannot be rendered at all without this.
//
// The shim covers exactly the part of the platform contract CommandPalette depends on:
// show() vs showModal() are distinguishable, close() fires a `close` event, and Escape closes
// the top modal. It deliberately does NOT emulate focus containment or inertness — jsdom has
// no layout or top-layer, so real Tab containment can only be verified in a browser. What the
// tests below assert is the thing that regressed: that the palette asks for a *modal* dialog
// rather than the non-modal one that let Tab walk into the page behind.
const modals = new Set<HTMLDialogElement>();

/** True when the element is currently open via showModal() rather than show(). */
export function isModal(el: HTMLDialogElement): boolean {
  return modals.has(el);
}

const proto = globalThis.HTMLDialogElement?.prototype;
if (proto && !proto.showModal) {
  proto.show = function show(this: HTMLDialogElement) {
    this.open = true;
  };
  proto.showModal = function showModal(this: HTMLDialogElement) {
    this.open = true;
    modals.add(this);
  };
  proto.close = function close(this: HTMLDialogElement, returnValue?: string) {
    if (!this.open) return;
    if (returnValue !== undefined) this.returnValue = returnValue;
    this.open = false;
    modals.delete(this);
    this.dispatchEvent(new Event('close'));
  };
  // Browsers close the topmost open modal on Escape; that is what gives a native <dialog>
  // its Escape-to-close for free, and the palette relies on it instead of its own handler.
  document.addEventListener('keydown', e => {
    if (e.key !== 'Escape') return;
    const top = [...modals].at(-1);
    if (!top) return;
    if (top.dispatchEvent(new Event('cancel', { cancelable: true }))) top.close();
  });
}

afterEach(() => modals.clear());
