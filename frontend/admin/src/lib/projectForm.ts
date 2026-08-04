/**
 * What a project form holds, and how it becomes a request body — in one place.
 *
 * There were two project forms: the organisation's own Projects page and the system OrgDetail page,
 * each parsing redirect URIs by hand. They drifted, as two copies of a rule do. OrgDetail kept
 * single `<input>` fields, so a project created from the system scope could hold exactly one
 * redirect URI and one post-logout URI, while the same project created one screen away could hold
 * any number. Nothing announced the difference; the second URI was simply impossible to enter.
 *
 * A page may lay the fields out however it likes — that is the part allowed to differ. No page
 * decides again what a redirect URI list is.
 */

export interface ProjectFormState {
  name: string;
  slug: string;
  redirect_uris: string;
  post_logout_redirect_uris: string;
  require_role_to_login: boolean;
}

export const emptyProjectForm: ProjectFormState = {
  name: '',
  slug: '',
  redirect_uris: '',
  post_logout_redirect_uris: '',
  require_role_to_login: false,
};

/**
 * One URI per line. Blank lines are dropped rather than sent: Hydra refuses a client whose
 * `redirect_uris` contains an empty string, and a trailing newline is the most ordinary thing a
 * textarea produces.
 */
export function parseUriLines(value: string): string[] {
  return value.split('\n').map(s => s.trim()).filter(Boolean);
}

/** Joins a list back into the textarea form, for editing what a project already has. */
export function formatUriLines(uris: readonly string[] | undefined): string {
  return (uris ?? []).join('\n');
}

/** A slug as the API will accept it — the pattern attribute below is the same rule, stated twice. */
export function slugify(value: string): string {
  return value.toLowerCase().replaceAll(/\s+/g, '-');
}

/** The request body both create paths send. */
export function toProjectPayload(form: ProjectFormState) {
  return {
    name: form.name,
    slug: form.slug,
    redirect_uris: parseUriLines(form.redirect_uris),
    post_logout_redirect_uris: parseUriLines(form.post_logout_redirect_uris),
    require_role_to_login: form.require_role_to_login,
  };
}

