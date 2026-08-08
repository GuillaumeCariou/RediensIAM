import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from 'vitest/browser';
import { OrgSuspendDialog, OrgDeleteDialog, ORG_DELETE_CASCADE, ORG_SUSPEND_EFFECT } from './OrgLifecycle';

/**
 * Trois pages offraient ces deux actions avec trois textes indépendants, et l'écart n'était pas
 * cosmétique : la liste disait « toutes les données associées » — donc rien —, la vue d'ensemble
 * omettait les comptes contenus dans les listes, les grants Keto et la chaîne d'audit, et elle
 * suspendait sans rien demander du tout.
 *
 * Ce fichier tient le contenu, pas la mise en page : ce qui doit être dit à quelqu'un sur le point
 * de détruire un locataire.
 */

describe('what the delete confirmation must name', () => {
  it.each([
    ['les projets et leur client OAuth2', /every project and its OAuth2 client/],
    ['les user lists', /every user list/],
    ['les comptes CONTENUS dans ces listes', /every account in those lists/],
    ['les grants d\'administration dans Keto', /every admin grant in Keto/],
    ['la chaîne d\'audit du locataire', /audit chain/],
    ['que les sessions tombent au passage', /Sessions are revoked as it goes/],
    ['que rien n\'est réversible', /cannot be undone/],
  ])('nomme %s', (_what, pattern) => {
    expect(ORG_DELETE_CASCADE).toMatch(pattern);
  });

  it('le dit à l\'écran, avec le nom du locataire', async () => {
    render(<OrgDeleteDialog open name="Acme" onClose={() => {}} onConfirm={() => {}} />);

    expect(await screen.findByText(/Delete .Acme.\?/)).toBeInTheDocument();
    expect(screen.getByText(ORG_DELETE_CASCADE)).toBeInTheDocument();
  });

  it('ne détruit rien tant que personne n\'a confirmé', async () => {
    const onConfirm = vi.fn();
    render(<OrgDeleteDialog open name="Acme" onClose={() => {}} onConfirm={onConfirm} />);

    expect(onConfirm).not.toHaveBeenCalled();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Delete for good' }));

    expect(onConfirm).toHaveBeenCalledOnce();
  });
});

describe('what the suspend confirmation must say', () => {
  /**
   * La moitié qui manquait le plus souvent : les administrateurs du locataire sont déconnectés eux
   * aussi et ne peuvent plus revenir. C'est ce qui distingue une suspension d'une pause.
   */
  it('dit que les administrateurs du locataire y passent aussi', () => {
    expect(ORG_SUSPEND_EFFECT).toMatch(/its own administrators included/);
  });

  it('dit aussi que rien n\'est supprimé — l\'autre moitié compte autant', () => {
    expect(ORG_SUSPEND_EFFECT).toMatch(/Nothing is deleted/);
  });

  it('devient la boîte du retour en arrière quand le locataire est déjà suspendu', async () => {
    render(<OrgSuspendDialog open name="Acme" suspended onClose={() => {}} onConfirm={() => {}} />);

    expect(await screen.findByText(/Unsuspend .Acme.\?/)).toBeInTheDocument();
    // Les sessions révoquées ne reviennent pas : le dire évite d'attendre un retour qui n'aura pas lieu.
    expect(screen.getByText(/stay revoked/)).toBeInTheDocument();
  });
});
