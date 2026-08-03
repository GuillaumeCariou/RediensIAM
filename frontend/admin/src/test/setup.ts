import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

/**
 * Setup for the `browser` project only.
 *
 * This file used to shim `HTMLDialogElement`'s show/showModal/close and Escape-to-close, because
 * jsdom ships the element with a reflected `open` property and none of its methods — a component
 * that calls showModal() could not even be rendered. Chromium has all of it for real, so the shim
 * is gone, and with it `isModal()`: tests now ask the browser directly with `:modal`. Keeping a
 * shim here would overwrite the exact platform behaviour these tests exist to exercise.
 */
afterEach(cleanup);
