/**
 * Names that survive a suite run against a deployment that keeps its data.
 *
 * Every organisation, project and user these tests create stays behind — this is a dev
 * deployment, not a transaction — so a fixed name collides with the previous run and the failure
 * reads as "creation refused" rather than "you have run this before". A prefix makes the leftovers
 * identifiable, and a suffix makes each run distinct.
 */
const RUN = Date.now().toString(36);
let counter = 0;

export const PREFIX = 'e2e';

export function uniqueName(what: string): string {
  counter += 1;
  return `${PREFIX}-${what}-${RUN}${counter}`;
}

export function uniqueSlug(what: string): string {
  return uniqueName(what).toLowerCase().replaceAll(/[^a-z0-9-]/g, '-');
}

export function uniqueEmail(what: string): string {
  return `${uniqueSlug(what)}@e2e.test`;
}
