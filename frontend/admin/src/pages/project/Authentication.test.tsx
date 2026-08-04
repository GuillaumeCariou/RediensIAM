import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { MemoryRouter } from 'react-router';
import Authentication from './Authentication';
import * as api from '@/api';

/**
 * The project's whole login configuration on one page. Four things here are security-relevant and
 * each has a test that names why:
 *
 *  - a failed load must not render the form. Every field starts at a useState default, so saving
 *    after a failed read PATCHes MFA off and the IP allowlist empty over the tenant's real
 *    configuration, and reports success.
 *  - an OAuth client secret the server already holds must not be overwritten with "" when an
 *    unrelated setting is saved.
 *  - no secret may reach the preview URL, which lands in history, logs and Referer headers.
 *  - the logo must not be an SVG, by upload or by data: URL: it renders via <img> on the login
 *    page and can carry script.
 */

// `{ spy: true }` plutôt qu'une fabrique. En mode navigateur, Vitest exécute de l'ESM natif, dont
// l'espace de noms est SCELLÉ : une fabrique REMPLACE le module, et tout export que l'arbre rendu
// y cherche sans le trouver — `getMfaStatus`, via `MfaReminder` — échoue à se lier. Vitest le
// journalisait 39 fois par exécution en SyntaxError non gérée, pendant que chaque assertion
// passait : la forme même d'une suite qui a cessé de vérifier ce qu'elle annonce.
//
// L'option documentée pour ce cas garde TOUS les exports et les enveloppe en espions ; on ne
// remplace donc plus le module, on l'instrumente. `beforeEach` donne ensuite une implémentation à
// chacun de ceux que cette page appelle, sans quoi l'espion laisserait passer le vrai appel.
vi.mock('@/api', { spy: true });

const auth = vi.hoisted(() => ({ orgId: '', projectId: 'p1' }));
// Only `useAuth` is replaced: the module also exports the context object and the role constants,
// and a partial mock breaks whatever imports them.
vi.mock('@/context/AuthContext', async orig => ({
  ...(await orig<typeof import('@/context/AuthContext')>()),
  useAuth: () => auth,
}));

const PROJECT = {
  login_theme: {
    primary_color: '#ff0000', logo_url: '', font_family: 'Roboto', border_radius: '12',
    custom_css: '.card { color: red }', hydra_local_login: true,
    providers: [
      { id: 'google', type: 'google', label: 'Continue with Google', client_id: 'g-id', client_secret: null, enabled: true },
      { id: 'ab12cd34', type: 'oidc', label: 'Continue with SSO', client_id: 'o-id', client_secret: '', issuer_url: 'https://idp.test', enabled: true },
    ],
  },
  allow_self_registration: true,
  require_mfa: false,
  check_breached_passwords: true,
  email_verification_enabled: true,
  sms_verification_enabled: false,
  allowed_email_domains: ['acme.test', 'acme.io'],
  email_from_name: 'Acme Portal',
  default_role_id: 'r1',
  min_password_length: 12,
  password_require_uppercase: true,
  password_require_lowercase: false,
  password_require_digit: true,
  password_require_special: false,
  ip_allowlist: ['10.0.0.0/8'],
  allowed_scopes: ['openid', 'offline', 'read:orders'],
};

const ROLES = [{ id: 'r2', name: 'viewer', rank: 100 }, { id: 'r1', name: 'admin', rank: 1 }];
const SAML = [{
  id: 'i1', entity_id: 'https://saml-idp.test', metadata_url: 'https://saml-idp.test/meta',
  email_attribute_name: 'email', display_name_attribute_name: 'cn',
  jit_provisioning: true, active: true,
}];

beforeEach(() => {
  // `clearAllMocks` forgets the CALLS, not the implementations: a `mockRejectedValue` set by one
  // test stays in place for every test after it. The three reads below were already given a fresh
  // default here, which is why their failure cases don't leak; the two writes were not, so the
  // "save is refused" and "provider creation fails" tests left a rejected promise behind that
  // later tests triggered and nobody awaited — 8 unhandled rejections per run, and Vitest warns
  // that a suite in that state can report false passes.
  vi.clearAllMocks();
  auth.projectId = 'p1';
  vi.mocked(api.getProjectInfo).mockResolvedValue(PROJECT);
  vi.mocked(api.listRoles).mockResolvedValue({ roles: ROLES });
  vi.mocked(api.listSamlProviders).mockResolvedValue({ providers: SAML });
  vi.mocked(api.updateProject).mockResolvedValue({});
  vi.mocked(api.createSamlProvider).mockResolvedValue({});
  // Sous `{ spy: true }` un export sans implémentation laisse passer le VRAI appel : la suppression
  // partait au réseau et le test échouait. L'espion doit être armé pour chaque fonction appelée.
  vi.mocked(api.deleteSamlProvider).mockResolvedValue({});
});

function show() {
  const user = userEvent.setup();
  render(<MemoryRouter><Authentication /></MemoryRouter>);
  return user;
}

const loaded = () => screen.findByRole('button', { name: 'Save Changes' });
const tab = async (user: Awaited<ReturnType<typeof show>>, name: string) =>
  user.click(screen.getByRole('button', { name }));
/**
 * The header's save button, by position rather than by label: it reads "Saved!" for two seconds
 * after a save, and a test that saves twice would otherwise not find it the second time.
 */
const save = (user: Awaited<ReturnType<typeof show>>) =>
  user.click(document.querySelector<HTMLButtonElement>('.iam-page-header .iam-btn-primary')!);
const body = () => vi.mocked(api.updateProject).mock.calls.at(-1)![1] as Record<string, unknown>;

describe('loading', () => {
  it('shows placeholders, and nothing to save, until the project has answered', () => {
    vi.mocked(api.getProjectInfo).mockReturnValue(new Promise(() => {}));
    show();

    expect(screen.queryByRole('button', { name: 'Save Changes' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Appearance' })).not.toBeInTheDocument();
  });

  it('refuses to render the form at all when the read fails', async () => {
    // Otherwise the next Save writes the useState defaults over the real configuration.
    vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.mocked(api.getProjectInfo).mockRejectedValue(new Error('500'));
    show();

    expect(await screen.findByText('This configuration could not be loaded, so it is not safe to edit.'))
      .toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save Changes' })).not.toBeInTheDocument();
    vi.restoreAllMocks();
  });

  it('retries by re-reading, not by reloading the page', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.mocked(api.getProjectInfo).mockRejectedValueOnce(new Error('500')).mockResolvedValue(PROJECT);
    const user = show();
    await screen.findByRole('button', { name: 'Retry' });

    await user.click(screen.getByRole('button', { name: 'Retry' }));

    await loaded();
    expect(api.getProjectInfo).toHaveBeenCalledTimes(2);
    vi.restoreAllMocks();
  });

  it('still renders when the SAML endpoint is unavailable', async () => {
    // SAML is optional; losing it must not take the whole page down.
    vi.mocked(api.listSamlProviders).mockRejectedValue(new Error('501'));
    show();

    await loaded();
    expect(screen.getByRole('button', { name: 'Appearance' })).toBeInTheDocument();
  });

  it('asks for nothing when no project is in scope', () => {
    auth.projectId = '';
    show();

    expect(api.getProjectInfo).not.toHaveBeenCalled();
  });
});

describe('the appearance tab', () => {
  it('loads the stored colours, font and radius', async () => {
    show();
    await loaded();

    expect(screen.getByLabelText('Primary')).toHaveValue('#ff0000');
    expect(screen.getByLabelText('Font Family')).toHaveValue('Roboto');
    expect(screen.getByLabelText(/Border Radius — 12px/)).toHaveValue('12');
  });

  it('edits a colour from either the swatch or the hex field', async () => {
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText('Primary'), '#00ff00');

    expect(screen.getByLabelText('Primary colour picker')).toHaveValue('#00ff00');
  });

  it('offers a free-text font once "Custom" is chosen, and keeps it', async () => {
    const user = show();
    await loaded();

    await user.selectOptions(screen.getByLabelText('Font Family'), 'Custom');
    const custom = screen.getByPlaceholderText("e.g. 'Nunito', sans-serif");
    await user.fill(custom, 'Nunito');

    await save(user);
    expect((body()['login_theme'] as Record<string, unknown>)['font_family']).toBe('Nunito');
  });

  it('recognises a stored font that is not one of the presets as a custom one', async () => {
    vi.mocked(api.getProjectInfo).mockResolvedValue({
      ...PROJECT, login_theme: { ...PROJECT.login_theme, font_family: 'Comic Sans' },
    });
    show();
    await loaded();

    expect(screen.getByLabelText('Font Family')).toHaveValue('Custom');
    expect(screen.getByPlaceholderText("e.g. 'Nunito', sans-serif")).toHaveValue('Comic Sans');
  });

  it('falls back to the defaults for a project with no stored theme', async () => {
    vi.mocked(api.getProjectInfo).mockResolvedValue({ ...PROJECT, login_theme: null });
    show();
    await loaded();

    expect(screen.getByLabelText('Primary')).toHaveValue('#1a56db');
  });
});

describe('the logo', () => {
  const png = () => new File([new Uint8Array([137, 80, 78, 71])], 'logo.png', { type: 'image/png' });
  const svg = () => new File(['<svg onload="alert(1)"/>'], 'logo.svg', { type: 'image/svg+xml' });
  const fileInput = () => document.querySelector<HTMLInputElement>('input[type="file"]')!;

  it('accepts a raster upload', async () => {
    const user = show();
    await loaded();

    await user.upload(fileInput(), png());

    expect(await screen.findByAltText('Logo')).toBeInTheDocument();
  });

  it('refuses an SVG upload, which can carry script onto the login page', async () => {
    const user = show();
    await loaded();

    await user.upload(fileInput(), svg());

    expect(await screen.findByRole('alert'))
      .toHaveTextContent('Logo must be a raster image (PNG, JPEG, GIF, WebP, AVIF). SVG is not allowed.');
    expect(screen.queryByAltText('Logo')).not.toBeInTheDocument();
  });

  it('refuses an upload over the size cap', async () => {
    const big = new File([new Uint8Array(300 * 1024)], 'big.png', { type: 'image/png' });
    const user = show();
    await loaded();

    await user.upload(fileInput(), big);

    expect(await screen.findByRole('alert')).toHaveTextContent('Image must be under 256 KB.');
  });

  it('accepts an https URL', async () => {
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText('Logo URL'), 'https://cdn.test/logo.png');
    await save(user);

    expect((body()['login_theme'] as Record<string, unknown>)['logo_url']).toBe('https://cdn.test/logo.png');
  });

  it('refuses an http URL, which is mixed content on the login page', async () => {
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText('Logo URL'), 'http://cdn.test/logo.png');

    expect(await screen.findByRole('alert')).toHaveTextContent('URL must use https://.');
  });

  it('applies the same allowlist to a pasted data: URL', async () => {
    // Otherwise the URL field is a straight bypass of the upload checks.
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText('Logo URL'), 'data:image/svg+xml,%3Csvg/%3E');

    expect(await screen.findByRole('alert'))
      .toHaveTextContent('data: URL must reference a raster image (PNG, JPEG, GIF, WebP, AVIF).');
  });

  it('applies the same size cap to a pasted data: URL', async () => {
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText('Logo URL'), `data:image/png;base64,${'A'.repeat(400 * 1024)}`);

    expect(await screen.findByRole('alert')).toHaveTextContent('Embedded image must be under 256 KB.');
  });

  it('accepts a raster data: URL', async () => {
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText('Logo URL'), 'data:image/png;base64,AAAA');

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(await screen.findByAltText('Logo')).toBeInTheDocument();
  });

  it('lets the logo be cleared', async () => {
    vi.mocked(api.getProjectInfo).mockResolvedValue({
      ...PROJECT, login_theme: { ...PROJECT.login_theme, logo_url: 'https://cdn.test/logo.png' },
    });
    const user = show();
    await loaded();
    await screen.findByAltText('Logo');

    await user.click(screen.getByAltText('Logo').nextElementSibling as HTMLButtonElement);

    expect(screen.queryByAltText('Logo')).not.toBeInTheDocument();
  });

  it('keeps the URL field empty for an embedded logo, which would not fit in it', async () => {
    vi.mocked(api.getProjectInfo).mockResolvedValue({
      ...PROJECT, login_theme: { ...PROJECT.login_theme, logo_url: 'data:image/png;base64,AAAA' },
    });
    show();
    await loaded();

    expect(screen.getByLabelText('Logo URL')).toHaveValue('');
  });
});

describe('the providers tab', () => {
  const providers = async (user: Awaited<ReturnType<typeof show>>) => {
    await loaded();
    await tab(user, 'Providers');
  };

  it('shows a stored built-in as enabled, with its client id', async () => {
    const user = show();
    await providers(user);

    expect(screen.getByLabelText('Google')).toBeChecked();
    // Scoped to the Google card: the custom OIDC provider below has a Client ID field too.
    expect(within(screen.getByText('Google').closest('.iam-card')!)
      .getByLabelText('Client ID')).toHaveValue('g-id');
  });

  it('enables one that was never configured, without touching the others', async () => {
    const user = show();
    await providers(user);

    await user.click(screen.getByLabelText('GitHub'));
    await save(user);

    const saved = (body()['login_theme'] as { providers: { id: string; enabled: boolean }[] }).providers;
    expect(saved.find(p => p.id === 'github')).toMatchObject({ enabled: true, client_id: '' });
    expect(saved.find(p => p.id === 'google')).toMatchObject({ enabled: true });
  });

  it('turns a configured one off without forgetting its client id', async () => {
    const user = show();
    await providers(user);

    await user.click(screen.getByLabelText('Google'));
    await save(user);

    const saved = (body()['login_theme'] as { providers: { id: string; enabled: boolean; client_id: string }[] }).providers;
    expect(saved.find(p => p.id === 'google')).toMatchObject({ enabled: false, client_id: 'g-id' });
  });

  it('keeps a stored secret when the field was left alone', async () => {
    // Sending "" here would silently break every sign-in through that provider.
    const user = show();
    await providers(user);

    // Scoped to Google: every expanded provider has a Client Secret field of its own.
    expect(within(screen.getByText('Google').closest('.iam-card')!).getByLabelText('Client Secret'))
      .toHaveAttribute('placeholder', '••••••••• (saved — enter new to replace)');
    await save(user);

    const google = (body()['login_theme'] as { providers: Record<string, unknown>[] })
      .providers.find(p => p['id'] === 'google')!;
    expect('client_secret' in google).toBe(false);
    expect('client_secret_saved' in google).toBe(false);
  });

  it('sends a secret the admin retyped', async () => {
    const user = show();
    await providers(user);

    await user.fill(
      within(screen.getByText('Google').closest('.iam-card')!).getByLabelText('Client Secret'),
      'new-secret');
    await save(user);

    const google = (body()['login_theme'] as { providers: Record<string, unknown>[] })
      .providers.find(p => p['id'] === 'google')!;
    expect(google['client_secret']).toBe('new-secret');
  });

  it('adds and removes a custom OIDC provider', async () => {
    const user = show();
    await providers(user);
    expect(screen.getByDisplayValue('https://idp.test')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Add Provider' }));
    await save(user);
    let saved = (body()['login_theme'] as { providers: { type: string }[] }).providers;
    expect(saved.filter(p => p.type === 'oidc')).toHaveLength(2);

    // The remove button sits in that provider's header row, beside its enable switch.
    const card = screen.getByDisplayValue('https://idp.test').closest('.iam-card')!;
    await user.click(card.querySelector<HTMLButtonElement>('button')!);
    await save(user);
    saved = (body()['login_theme'] as { providers: { type: string }[] }).providers;
    expect(saved.filter(p => p.type === 'oidc')).toHaveLength(1);
  });

  it('edits a custom provider\'s label, client id and issuer', async () => {
    const user = show();
    await providers(user);

    await user.fill(screen.getByDisplayValue('https://idp.test'), 'https://sso.test');
    await save(user);

    const oidc = (body()['login_theme'] as { providers: Record<string, unknown>[] })
      .providers.find(p => p['type'] === 'oidc')!;
    expect(oidc['issuer_url']).toBe('https://sso.test');
  });

  it('can turn the built-in password form off', async () => {
    const user = show();
    await providers(user);

    await user.click(screen.getByLabelText('Password login'));
    await save(user);

    expect((body()['login_theme'] as Record<string, unknown>)['hydra_local_login']).toBe(false);
  });
});

describe('the SAML providers', () => {
  const providers = async (user: Awaited<ReturnType<typeof show>>) => {
    await loaded();
    await tab(user, 'Providers');
  };

  it('lists the ones already registered, and the URL to hand the IdP', async () => {
    const user = show();
    await providers(user);

    expect(screen.getByText('https://saml-idp.test')).toBeInTheDocument();
    expect(screen.getByText(`${globalThis.location.origin}/admin/projects/p1/saml/metadata`))
      .toBeInTheDocument();
  });

  it('accepts a bare array as well as an envelope', async () => {
    vi.mocked(api.listSamlProviders).mockResolvedValue(SAML);
    const user = show();
    await providers(user);

    expect(screen.getByText('https://saml-idp.test')).toBeInTheDocument();
  });

  it('adds one, defaulting the email attribute rather than sending a blank', async () => {
    vi.mocked(api.createSamlProvider).mockResolvedValue({ ...SAML[0], id: 'i2', entity_id: 'https://idp2.test' });
    const user = show();
    await providers(user);
    await user.click(screen.getByRole('button', { name: 'Add IdP' }));

    await user.fill(screen.getByLabelText(/Entity ID/), 'https://idp2.test');
    await user.fill(screen.getByLabelText(/Email attribute/), '');
    await user.click(document.querySelector<HTMLButtonElement>('button[form="add-saml-form"]')!);

    await vi.waitFor(() => expect(api.createSamlProvider).toHaveBeenCalledWith('p1', {
      entity_id: 'https://idp2.test', metadata_url: undefined,
      email_attribute_name: 'email', display_name_attribute_name: undefined,
      jit_provisioning: true,
    }));
    expect(await screen.findByText('https://idp2.test')).toBeInTheDocument();
  });

  it('sends the optional fields when they were filled in', async () => {
    vi.mocked(api.createSamlProvider).mockResolvedValue({ ...SAML[0], id: 'i2' });
    const user = show();
    await providers(user);
    await user.click(screen.getByRole('button', { name: 'Add IdP' }));

    await user.fill(screen.getByLabelText(/Entity ID/), 'https://idp2.test');
    await user.fill(screen.getByLabelText(/Metadata URL/), 'https://idp2.test/meta');
    await user.fill(screen.getByLabelText(/Name attribute/), 'displayName');
    await user.click(document.querySelector<HTMLButtonElement>('button[form="add-saml-form"]')!);

    await vi.waitFor(() => expect(api.createSamlProvider).toHaveBeenCalledWith('p1',
      expect.objectContaining({
        metadata_url: 'https://idp2.test/meta', display_name_attribute_name: 'displayName',
      })));
  });

  it('never sends `active`, which the create endpoint ignores', async () => {
    // Sending it looks like it works and silently does nothing.
    vi.mocked(api.createSamlProvider).mockResolvedValue({ ...SAML[0], id: 'i2' });
    const user = show();
    await providers(user);
    await user.click(screen.getByRole('button', { name: 'Add IdP' }));
    await user.fill(screen.getByLabelText(/Entity ID/), 'https://idp2.test');

    await user.click(document.querySelector<HTMLButtonElement>('button[form="add-saml-form"]')!);

    await vi.waitFor(() => expect(api.createSamlProvider).toHaveBeenCalled());
    expect(vi.mocked(api.createSamlProvider).mock.calls[0][1]).not.toHaveProperty('active');
  });

  it('reports what the server refused', async () => {
    vi.mocked(api.createSamlProvider).mockResolvedValue({ error: 'duplicate', error_description: 'Already registered.' });
    const user = show();
    await providers(user);
    await user.click(screen.getByRole('button', { name: 'Add IdP' }));
    await user.fill(screen.getByLabelText(/Entity ID/), 'https://saml-idp.test');

    await user.click(document.querySelector<HTMLButtonElement>('button[form="add-saml-form"]')!);

    expect(await screen.findByText('Already registered.')).toBeInTheDocument();
  });

  it('falls back to a generic message when it gave none', async () => {
    vi.mocked(api.createSamlProvider).mockResolvedValue({ error: 'duplicate' });
    const user = show();
    await providers(user);
    await user.click(screen.getByRole('button', { name: 'Add IdP' }));
    await user.fill(screen.getByLabelText(/Entity ID/), 'https://saml-idp.test');

    await user.click(document.querySelector<HTMLButtonElement>('button[form="add-saml-form"]')!);

    expect(await screen.findByText('Failed to add provider.')).toBeInTheDocument();
  });

  it('reports a request that failed outright', async () => {
    vi.mocked(api.createSamlProvider).mockRejectedValue(new Error('network'));
    const user = show();
    await providers(user);
    await user.click(screen.getByRole('button', { name: 'Add IdP' }));
    await user.fill(screen.getByLabelText(/Entity ID/), 'https://idp2.test');

    await user.click(document.querySelector<HTMLButtonElement>('button[form="add-saml-form"]')!);

    expect(await screen.findByText('Something went wrong.')).toBeInTheDocument();
  });

  it('deletes one and drops it from the list', async () => {
    const user = show();
    await providers(user);

    await user.click([...screen.getByText('https://saml-idp.test').closest('div[style]')!
      .parentElement!.querySelectorAll<HTMLButtonElement>('button')].at(-1)!);

    await vi.waitFor(() => expect(api.deleteSamlProvider).toHaveBeenCalledWith('p1', 'i1'));
    await vi.waitFor(() => expect(screen.queryByText('https://saml-idp.test')).toBeNull());
  });

  it('requires an entity id, and does nothing on cancel', async () => {
    const user = show();
    await providers(user);
    await user.click(screen.getByRole('button', { name: 'Add IdP' }));

    expect(screen.getByLabelText(/Entity ID/)).toBeRequired();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(api.createSamlProvider).not.toHaveBeenCalled();
  });
});

describe('the custom scopes', () => {
  const providers = async (user: Awaited<ReturnType<typeof show>>) => {
    await loaded();
    await tab(user, 'Providers');
  };
  const scopeBox = () => screen.getByPlaceholderText('read:orders');
  const addScope = (user: Awaited<ReturnType<typeof show>>) =>
    user.click(screen.getByRole('button', { name: 'Add' }));

  it('shows the two standing scopes beside the project\'s own', async () => {
    const user = show();
    await providers(user);

    // They are shown as fixed chips, and are never part of what the operator edits.
    expect(screen.getByText('read:orders')).toBeInTheDocument();
    expect(screen.getAllByText('openid').length).toBeGreaterThan(0);
  });

  it('adds one and sends it alongside the two standing scopes', async () => {
    const user = show();
    await providers(user);

    await user.fill(scopeBox(), 'write:orders');
    await addScope(user);
    await save(user);

    expect(body()['allowed_scopes']).toEqual(['openid', 'offline', 'read:orders', 'write:orders']);
  });

  it('lower-cases and strips illegal characters as the operator types', async () => {
    const user = show();
    await providers(user);

    await user.fill(scopeBox(), 'Write Orders!');

    expect(scopeBox()).toHaveValue('writeorders');
  });

  it('refuses a shape it cannot fix by stripping', async () => {
    const user = show();
    await providers(user);

    await user.fill(scopeBox(), '1scope');
    await addScope(user);

    expect(screen.getByText(/Scope must be lowercase/)).toBeInTheDocument();
  });

  it('adds one on Enter, which is what anyone typing will press', async () => {
    const user = show();
    await providers(user);

    await user.fill(scopeBox(), 'write:orders');
    await user.keyboard('{Enter}');

    expect(scopeBox()).toHaveValue('');
    expect(screen.getByText('write:orders')).toBeInTheDocument();
  });

  it.each([
    ['one it already has', 'read:orders'],
    ['a standing scope', 'openid'],
  ])('refuses %s as a duplicate', async (_n, dup) => {
    const user = show();
    await providers(user);

    await user.fill(scopeBox(), dup);
    await addScope(user);

    expect(screen.getByText('Scope already exists.')).toBeInTheDocument();
  });

  it('ignores an empty box', async () => {
    const user = show();
    await providers(user);

    await addScope(user);

    expect(screen.queryByText(/Scope must be lowercase/)).not.toBeInTheDocument();
  });

  it('removes one', async () => {
    const user = show();
    await providers(user);

    await user.click(within(screen.getByText('read:orders').closest('span')!).getByRole('button'));
    await save(user);

    expect(body()['allowed_scopes']).toEqual(['openid', 'offline']);
  });
});

describe('the registration tab', () => {
  const registration = async (user: Awaited<ReturnType<typeof show>>) => {
    await loaded();
    await tab(user, 'Registration');
  };

  it('loads the stored switches and domain list', async () => {
    const user = show();
    await registration(user);

    expect(screen.getByLabelText('Allow self-registration')).toBeChecked();
    expect(screen.getByLabelText('Require MFA')).not.toBeChecked();
    expect(screen.getByDisplayValue('acme.test, acme.io')).toBeInTheDocument();
  });

  it('splits the domain list, dropping blanks and stray spaces', async () => {
    const user = show();
    await registration(user);

    await user.fill(screen.getByDisplayValue('acme.test, acme.io'), ' a.test , , b.test ');
    await save(user);

    expect(body()['allowed_email_domains']).toEqual(['a.test', 'b.test']);
  });

  it('sends an empty list, not a list holding one empty string, for "any domain"', async () => {
    const user = show();
    await registration(user);

    await user.fill(screen.getByDisplayValue('acme.test, acme.io'), '');
    await save(user);

    expect(body()['allowed_email_domains']).toEqual([]);
  });

  it('carries the switches through', async () => {
    const user = show();
    await registration(user);

    await user.click(screen.getByLabelText('Require MFA'));
    await user.click(screen.getByLabelText('Allow self-registration'));
    await save(user);

    expect(body()['require_mfa']).toBe(true);
    expect(body()['allow_self_registration']).toBe(false);
  });
});

describe('the password policy, on the registration tab', () => {
  const security = async (user: Awaited<ReturnType<typeof show>>) => {
    await loaded();
    await tab(user, 'Registration');
  };

  it('loads the stored password policy', async () => {
    const user = show();
    await security(user);

    expect(screen.getByLabelText('Minimum length')).toHaveValue(12);
    expect(screen.getByLabelText(/Require uppercase/)).toBeChecked();
    expect(screen.getByLabelText(/Require lowercase/)).not.toBeChecked();
  });

  it('carries every rule through', async () => {
    const user = show();
    await security(user);

    await user.click(screen.getByLabelText(/Require lowercase/));
    await user.click(screen.getByLabelText(/Require special character/));
    await save(user);

    expect(body()['password_require_lowercase']).toBe(true);
    expect(body()['password_require_special']).toBe(true);
    expect(body()['password_require_digit']).toBe(true);
  });

  it('carries the breached-password check', async () => {
    const user = show();
    await security(user);

    await user.click(screen.getByLabelText('Reject breached passwords'));
    await save(user);

    expect(body()['check_breached_passwords']).toBe(false);
  });

  it('sets the default role, and clears it with a flag rather than a null', async () => {
    const user = show();
    await security(user);

    expect(screen.getByLabelText('Default role')).toHaveValue('r1');
    await user.selectOptions(screen.getByLabelText('Default role'), '__none__');
    await save(user);

    expect(body()['clear_default_role']).toBe(true);
    expect(body()['default_role_id']).toBeUndefined();
  });

  it('offers the roles strongest first', async () => {
    const user = show();
    await security(user);

    const options = [...screen.getByLabelText('Default role').querySelectorAll('option')]
      .map(o => o.textContent);
    expect(options).toEqual(['No default role', 'admin (rank 1)', 'viewer (rank 100)']);
  });

  it('accepts a bare role array as well as an envelope', async () => {
    vi.mocked(api.listRoles).mockResolvedValue(ROLES);
    const user = show();
    await security(user);

    expect(screen.getByLabelText('Default role')).toHaveValue('r1');
  });
});

describe('the IP allowlist', () => {
  const security = async (user: Awaited<ReturnType<typeof show>>) => {
    await loaded();
    await tab(user, 'Security');
  };
  const box = () => screen.getByPlaceholderText(/10\.0\.0\.0\/8/);

  it('loads the stored ranges one per line', async () => {
    const user = show();
    await security(user);

    expect(box()).toHaveValue('10.0.0.0/8');
  });

  it.each([
    ['a bare IPv4 address', '192.168.1.1'],
    ['an IPv4 range', '192.168.0.0/24'],
    ['an IPv6 range', '2001:db8::/32'],
  ])('accepts %s', async (_n, cidr) => {
    const user = show();
    await security(user);

    await user.fill(box(), cidr);
    await save(user);

    expect(body()['ip_allowlist']).toEqual([cidr]);
  });

  it('refuses to save anything at all when one line is malformed', async () => {
    // A partial save would leave the tenant locked to whatever survived.
    const user = show();
    await security(user);

    await user.fill(box(), '10.0.0.0/8\nnot-an-ip');
    await save(user);

    expect(screen.getByText('Invalid CIDR: not-an-ip')).toBeInTheDocument();
    expect(api.updateProject).not.toHaveBeenCalled();
  });

  it('drops blank lines and stray spaces', async () => {
    const user = show();
    await security(user);

    await user.fill(box(), ' 10.0.0.0/8 \n\n 192.168.1.0/24 ');
    await save(user);

    expect(body()['ip_allowlist']).toEqual(['10.0.0.0/8', '192.168.1.0/24']);
  });

  it('clears the complaint once the lines are valid', async () => {
    const user = show();
    await security(user);
    await user.fill(box(), 'nope');
    await save(user);
    expect(screen.getByText('Invalid CIDR: nope')).toBeInTheDocument();

    await user.fill(box(), '10.0.0.0/8');
    await save(user);

    expect(screen.queryByText(/Invalid CIDR/)).not.toBeInTheDocument();
  });
});

describe('the verification tab', () => {
  const verification = async (user: Awaited<ReturnType<typeof show>>) => {
    await loaded();
    await tab(user, 'Verification');
  };

  it('loads the stored switches and sender name', async () => {
    const user = show();
    await verification(user);

    expect(screen.getByLabelText('Email verification')).toBeChecked();
    expect(screen.getByLabelText('SMS verification')).not.toBeChecked();
    expect(screen.getByLabelText('From name')).toHaveValue('Acme Portal');
  });

  it('carries them through', async () => {
    const user = show();
    await verification(user);

    await user.click(screen.getByLabelText('SMS verification'));
    await save(user);

    expect(body()['sms_verification_enabled']).toBe(true);
  });

  it('clears the sender name with a flag rather than an empty string', async () => {
    // An empty string is a sender name; the flag is how the org default is restored.
    const user = show();
    await verification(user);

    await user.fill(screen.getByLabelText('From name'), '');
    await save(user);

    expect(body()['clear_email_from_name']).toBe(true);
    expect(body()['email_from_name']).toBeUndefined();
  });

  it('sends the name when there is one', async () => {
    const user = show();
    await verification(user);

    await user.fill(screen.getByLabelText('From name'), 'Acme Dev');
    await save(user);

    expect(body()['email_from_name']).toBe('Acme Dev');
    expect(body()['clear_email_from_name']).toBeUndefined();
  });
});

describe('the custom CSS tab', () => {
  it('loads and saves the stylesheet', async () => {
    const user = show();
    await loaded();
    await tab(user, 'Custom CSS');

    const box = screen.getByDisplayValue('.card { color: red }');
    await user.fill(box, '.card { color: blue }');
    await save(user);

    expect((body()['login_theme'] as Record<string, unknown>)['custom_css']).toBe('.card { color: blue }');
  });
});

describe('the preview', () => {
  const cfg = () => {
    const src = document.querySelector('iframe')!.getAttribute('src')!;
    return JSON.parse(atob(new URL(src, globalThis.location.origin).searchParams.get('cfg')!));
  };

  it('carries the theme and the rules the login page needs to render', async () => {
    show();
    await loaded();

    expect(cfg()).toMatchObject({
      mode: 'login', dark: false,
      theme: expect.objectContaining({ primary_color: '#ff0000' }),
      allow_self_registration: true, min_password_length: 12,
    });
  });

  it('never carries a provider secret — that URL reaches history, logs and Referer', async () => {
    const user = show();
    await loaded();
    await tab(user, 'Providers');
    await user.fill(
      within(screen.getByText('Google').closest('.iam-card')!).getByLabelText('Client Secret'),
      'super-secret');

    const providers = cfg().theme.providers as Record<string, unknown>[];
    expect(providers.some(p => 'client_secret' in p || 'client_secret_saved' in p)).toBe(false);
    expect(document.querySelector('iframe')!.getAttribute('src')).not.toContain(btoa('super-secret'));
  });

  it('switches between the pages it can show', async () => {
    const user = show();
    await loaded();

    await user.click(screen.getByRole('button', { name: 'register' }));

    await vi.waitFor(() => expect(cfg().mode).toBe('register'));
  });

  it('switches the preview to dark without touching the console\'s own theme', async () => {
    const user = show();
    await loaded();

    await user.click(screen.getByTitle('Toggle dark/light preview'));

    await vi.waitFor(() => expect(cfg().dark).toBe(true));
  });
});

describe('saving', () => {
  it('confirms, then takes the confirmation back down', async () => {
    const user = show();
    await loaded();

    await save(user);

    expect(await screen.findByRole('button', { name: 'Saved!' })).toBeInTheDocument();
    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Save Changes' })).toBeInTheDocument(),
      { timeout: 5000 });
  }, 10_000);

  it('re-enables the button when the save is refused', async () => {
    vi.mocked(api.updateProject).mockRejectedValue(new Error('500'));
    const user = show();
    await loaded();

    await save(user);

    await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Save Changes' })).toBeEnabled());
    // Re-enabling alone told the operator nothing: the fields still showed the edits the server had
    // just refused, so this page in particular could be walked away from believing MFA was now on.
    expect(await screen.findByText('Could not save. None of these settings were changed.')).toBeInTheDocument();
  });
});

describe('the rest of the appearance controls', () => {
  it.each(['Background', 'Card surface', 'Text'])('edits the %s colour', async label => {
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText(label), '#123456');

    expect(screen.getByLabelText(`${label} colour picker`)).toHaveValue('#123456');
  });

  it('edits a colour from the swatch as well as the hex field', async () => {
    const user = show();
    await loaded();

    await user.fill(screen.getByLabelText('Primary colour picker'), '#abcdef');
    await save(user);

    expect((body()['login_theme'] as Record<string, unknown>)['primary_color']).toBe('#abcdef');
  });

  it('edits the corner radius', async () => {
    const user = show();
    await loaded();

    const slider = screen.getByLabelText(/Border Radius/);
    slider.focus();
    await user.keyboard('{ArrowRight}');
    await save(user);

    expect((body()['login_theme'] as Record<string, unknown>)['border_radius']).toBe('13');
  });
});

describe('the logo drop zone', () => {
  const dropZone = () => screen.getAllByRole('button', { name: /Drag & drop or browse/ })[0];

  it('highlights while a file is over it, and stops when it leaves', async () => {
    show();
    await loaded();
    const zone = dropZone();

    zone.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(zone.getAttribute('style')).toContain('--ia-accent'));

    zone.dispatchEvent(new DragEvent('dragleave', { bubbles: true }));
    await vi.waitFor(() => expect(zone.getAttribute('style')).not.toContain('--ia-accent'));
  });

  it('accepts a file dropped onto it', async () => {
    show();
    await loaded();
    const file = new File([new Uint8Array([137, 80, 78, 71])], 'logo.png', { type: 'image/png' });
    const dt = new DataTransfer();
    dt.items.add(file);

    dropZone().dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));

    expect(await screen.findByAltText('Logo')).toBeInTheDocument();
  });

  it('opens the file picker when the zone itself is clicked', async () => {
    const user = show();
    await loaded();
    const click = vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(() => {});

    await user.click(dropZone());

    expect(click).toHaveBeenCalled();
    click.mockRestore();
  });

  it('ignores a drop carrying no file', async () => {
    show();
    await loaded();

    dropZone().dispatchEvent(new DragEvent('drop', {
      bubbles: true, cancelable: true, dataTransfer: new DataTransfer(),
    }));

    expect(screen.queryByAltText('Logo')).not.toBeInTheDocument();
  });
});

describe('the rest of the provider fields', () => {
  const providers = async (user: Awaited<ReturnType<typeof show>>) => {
    await loaded();
    await tab(user, 'Providers');
  };

  it('edits a built-in provider\'s button label, client id and logo', async () => {
    const user = show();
    await providers(user);
    const card = within(screen.getByText('Google').closest('.iam-card')!);

    await user.fill(card.getByLabelText('Button Label'), 'Sign in with Google');
    await user.fill(card.getByLabelText('Client ID'), 'new-google-id');
    await user.fill(card.getByLabelText('Custom logo (optional) URL'), 'https://cdn.test/g.png');
    await save(user);

    const google = (body()['login_theme'] as { providers: Record<string, unknown>[] })
      .providers.find(p => p['id'] === 'google')!;
    expect(google).toMatchObject({
      label: 'Sign in with Google', client_id: 'new-google-id', logo_url: 'https://cdn.test/g.png',
    });
  });

  it('edits a custom provider\'s label, client id, secret and logo', async () => {
    const user = show();
    await providers(user);
    const card = within(screen.getByDisplayValue('https://idp.test').closest('.iam-card')!);

    await user.fill(card.getByLabelText('Button Label'), 'Continue with Okta');
    await user.fill(card.getByLabelText('Client ID'), 'okta-id');
    await user.fill(card.getByLabelText('Client Secret'), 'okta-secret');
    await user.fill(card.getByLabelText('Logo URL'), 'https://cdn.test/okta.png');
    await save(user);

    const oidc = (body()['login_theme'] as { providers: Record<string, unknown>[] })
      .providers.find(p => p['type'] === 'oidc')!;
    expect(oidc).toMatchObject({
      label: 'Continue with Okta', client_id: 'okta-id',
      client_secret: 'okta-secret', logo_url: 'https://cdn.test/okta.png',
    });
  });

  it('turns a custom provider off without removing it', async () => {
    const user = show();
    await providers(user);

    await user.click(screen.getByLabelText('Continue with SSO enabled'));
    await save(user);

    const oidc = (body()['login_theme'] as { providers: Record<string, unknown>[] })
      .providers.find(p => p['type'] === 'oidc')!;
    expect(oidc['enabled']).toBe(false);
  });

  it('shows a custom provider\'s logo once it has one', async () => {
    vi.mocked(api.getProjectInfo).mockResolvedValue({
      ...PROJECT,
      login_theme: {
        ...PROJECT.login_theme,
        providers: [{ ...PROJECT.login_theme.providers[1], logo_url: 'https://cdn.test/okta.png' }],
      },
    });
    const user = show();
    await providers(user);

    expect(screen.getByAltText('Continue with SSO')).toBeInTheDocument();
  });

  it('copies the SP metadata URL for the IdP', async () => {
    const writeText = vi.fn(async () => {});
    vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue({ writeText } as unknown as Clipboard);
    const user = show();
    await providers(user);

    await user.click(screen.getByText(`${globalThis.location.origin}/admin/projects/p1/saml/metadata`)
      .parentElement!.querySelector<HTMLButtonElement>('button')!);

    expect(writeText).toHaveBeenCalledWith(`${globalThis.location.origin}/admin/projects/p1/saml/metadata`);
    vi.restoreAllMocks();
  });
});

describe('the rest of the SAML form, and dismissing it', () => {
  const openSaml = async () => {
    const user = show();
    await loaded();
    await tab(user, 'Providers');
    await user.click(screen.getByRole('button', { name: 'Add IdP' }));
    return user;
  };

  it('can turn JIT provisioning off', async () => {
    vi.mocked(api.createSamlProvider).mockResolvedValue({ ...SAML[0], id: 'i2' });
    const user = await openSaml();

    await user.fill(screen.getByLabelText(/Entity ID/), 'https://idp2.test');
    await user.click(screen.getByLabelText('JIT provisioning'));
    await user.click(document.querySelector<HTMLButtonElement>('button[form="add-saml-form"]')!);

    await vi.waitFor(() => expect(api.createSamlProvider).toHaveBeenCalledWith('p1',
      expect.objectContaining({ jit_provisioning: false })));
  });

  it('has an Active switch that is never sent', async () => {
    vi.mocked(api.createSamlProvider).mockResolvedValue({ ...SAML[0], id: 'i2' });
    const user = await openSaml();

    await user.fill(screen.getByLabelText(/Entity ID/), 'https://idp2.test');
    await user.click(screen.getByLabelText('Active'));
    await user.click(document.querySelector<HTMLButtonElement>('button[form="add-saml-form"]')!);

    await vi.waitFor(() => expect(api.createSamlProvider).toHaveBeenCalled());
    expect(vi.mocked(api.createSamlProvider).mock.calls[0][1]).not.toHaveProperty('active');
  });

  it('closes on Escape, dropping the error it was showing', async () => {
    vi.mocked(api.createSamlProvider).mockResolvedValue({ error: 'duplicate' });
    const user = await openSaml();
    await user.fill(screen.getByLabelText(/Entity ID/), 'https://idp2.test');
    await user.click(document.querySelector<HTMLButtonElement>('button[form="add-saml-form"]')!);
    await screen.findByText('Failed to add provider.');

    await user.keyboard('{Escape}');
    await vi.waitFor(() => expect(screen.queryByLabelText(/Entity ID/)).toBeNull());
    await user.click(screen.getByRole('button', { name: 'Add IdP' }));

    expect(screen.queryByText('Failed to add provider.')).not.toBeInTheDocument();
  });
});

describe('the minimum password length', () => {
  it('clamps what the operator types to something a policy can mean', async () => {
    const user = show();
    await loaded();
    await tab(user, 'Registration');

    await user.fill(screen.getByLabelText('Minimum length'), '999');
    await save(user);
    expect(body()['min_password_length']).toBe(128);

    await user.fill(screen.getByLabelText('Minimum length'), '');
    await save(user);
    expect(body()['min_password_length']).toBe(0);
  });
});
