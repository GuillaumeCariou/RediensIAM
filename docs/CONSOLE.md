# The admin console

What each page is for, and the order things have to happen in.

The console is served at `/console/` on the admin host — never on the public one, where the ingress
denies it along with the whole management API. It is a React application talking to the same routes
documented in [API.md](API.md), through the browser SDK this repository ships. It holds no
privileges of its own: everything it can do, it can do because Keto says the signed-in
administrator may.

---

## Three scopes, one tree

The console is not one application with a permission filter over it. It has three scopes, and which
one you are in decides both the navigation and the API prefix every page calls.

| Scope | Who has it | What it governs |
|---|---|---|
| **System** | `super_admin` | The deployment: organisations, the administrators of the deployment itself, every project and user across all tenants, the audit log in full, metrics, health |
| **Organisation** | `org_admin` | One organisation: its projects, its user lists, its own administrators, its SMTP, its webhooks, its audit log |
| **Project** | `project_admin` | One project: its users, its roles, its service accounts, its login page and authentication policy |

The three scopes are drawn as **one tree**, not three sidebars: the deployment at the root, its
tenants under it, each tenant's projects under that, and every level's destinations as its children.
The level is a *place* — you are on a node, and its children are what that node has. Expanding a
tenant is what fetches its projects, so a deployment with fifty tenants does not make fifty requests
to draw a sidebar. The top bar carries a breadcrumb that names where you are and clicks back up.

A token carrying no management role at all does not reach the console — it is refused with an
explanation rather than an empty screen, because a redirect loop was the alternative.

⚠ **In practice every console operator is a super-admin today.** Sign-in admits only accounts in the
immovable system user list, and membership of that list *is* deployment-wide administration. The
organisation and project scopes below are built, routed and tested, and no account can currently
hold one on its own. Which of the two rules gives is an open decision; `tests/e2e/PLAN.md` §12
carries the matrix, written and skipped, so the question stays asked.

---

## First run, in order

A fresh deployment has one account and nothing else. The order below is not a suggestion: each step
is the prerequisite of the next.

1. **Sign in as the bootstrap administrator.** The installer printed the address and wrote the
   password into `deploy/rediensiam/values.secret.yaml`. This first sign-in needs no second factor —
   see *Multi-factor* below for why, and why the next one will.
2. **Create an organisation** — System → Organisations → New. An organisation is the tenant
   boundary: every user list, project and audit record below it is scoped to it, and row-level
   security in the database enforces that independently of the application.
3. **Create a user list** inside it. A user list is a population of end users. It exists separately
   from projects because two projects can share one population — a customer portal and a mobile app
   with the same accounts — and because an account belongs to a list, not to an application.
4. **Create a project.** A project is an OAuth2 client: it has redirect URIs, a login page, an
   authentication policy. Give it its redirect URIs **and its post-logout redirect URIs** now —
   changing either afterwards means deleting and recreating the project, because the update route
   does not touch Hydra.
5. **Assign the user list to the project.** Until you do, the project has no population and nobody
   can sign in to it.
6. **Define roles** — Project → Role Definitions. Roles are names with a rank; the rank decides
   which of two grants wins. They are emitted into the access token qualified by their project, so
   two tenants' `admin` are not the same string to a resource server.
7. **Configure SMTP** if you want verification mails, password resets or invitations to be
   deliverable. Until then those flows exist but nothing arrives.

---

## The pages

### System scope

| Page | What you do there |
|---|---|
| **Dashboard** | Counts across the deployment, and the sign-in activity of the last 24 hours from the audit log |
| **Organisations** | Create, suspend, delete. Suspending revokes every live session of the tenant — including its own administrators', who cannot sign back in — so it asks for confirmation first, the way Delete does. Unsuspending is immediate: it takes nothing away |
| **Admins** | Who administers the deployment. Adding someone here grants `super_admin`, which is the most privileged grant there is and is audited as such |
| **Users** | Every user across every tenant. Search, inspect, unlock, disable, reset |
| **Projects** | Every project across every tenant |
| **User Lists** | Every population, and their members |
| **Service Accounts** | Machine identities at deployment level, and their API keys |
| **Audit Log** | Every recorded action, hash-chained. Exportable. **Verify integrity** recalcule la chaîne : « intacte » et « entièrement vérifiée » sont deux réponses différentes — une chaîne intacte mais invérifiable (lignes antérieures à sa mise sous clé, ou écrites sous une clé retirée) ne prouve rien, et la page les distingue |
| **Metrics** | Counts and sign-in outcomes over time |
| **Email** | Deployment-wide SMTP, used by any organisation that has not set its own |
| **Health** | Whether the database, the cache, Hydra and Keto are answering |
| **OAuth2 Clients** | Hydra's own client registry. Les clients frappés par la console pour chaque projet (`client_`) et chaque compte de service (`sa_`) y figurent et sont marqués comme tels : en supprimer un laisse un projet enregistré sans client, et plus personne ne s'y connecte |
| **Grant reconciliation** | Les divergences entre les tuples Keto et la base. Un tuple sans ligne est un privilège vivant dont personne ne sait qui l'a accordé ; une ligne sans tuple n'autorise rien mais sert encore les scopes au consentement. La réparation révoque et supprime, elle ne crée jamais de tuple |

### Organisation scope

The same shapes, narrowed to one tenant: **Projects**, **User Lists**, **Admins**,
**Service Accounts**, **Audit Log**, **Email**, **Webhooks**, **Settings**.

Two are worth calling out:

- **Email** — an organisation's own SMTP relay. Set it when a tenant wants its mail to come from
  its own domain. It overrides the deployment's.
- **Webhooks** — where this organisation's events are delivered. Deliveries are signed; the secret
  is shown once, at creation, and never again — et **Rotate secret** le refrappe, ce que le message
  d'erreur de création demandait déjà sans qu'aucun bouton ne sache le faire. Le secret courant
  cesse d'être valide immédiatement : les receveurs qui vérifient les signatures rejettent les
  livraisons jusqu'à l'installation du nouveau.

Deux réglages système méritent aussi d'être signalés, dans **Settings** au niveau déploiement :
**Key rotation** montre ce qui reste à rechiffrer sous la clé active et le fait en une passe — un
balayage partiel est dit comme tel, parce que retirer une clé encore nécessaire perd les valeurs
chiffrées sous elle.

### Project scope

| Page | What you do there |
|---|---|
| **Users** | Who may sign in to this project, and with which roles. Un `project_admin` y voit son propre panneau, servi par les routes de portée projet — celui de l'administrateur d'organisation lit une route gardée plus haut et ne lui rendrait que des 403. On y crée un membre, on révoque ses sessions, et **Cleanup** propose d'abord un aperçu avant de supprimer quoi que ce soit |
| **Audit Log** | Les actions enregistrées pour ce projet seul |
| **Role Definitions** | The roles this project emits, and their ranks. Un rôle se modifie (description, rang) ; **son nom, non** — Keto écrit `role:{nom}` pour chaque porteur, et renommer laisserait ces tuples orphelins |
| **Service Accounts** | Machine identities scoped to this project |
| **Authentication** | The login page and the policy behind it — see below |
| **Settings** | Name, slug, redirect URIs, deletion |

**Authentication** is the densest page in the console. It carries:

- the **login page theme** — colours, logo, fonts — with a live preview of the real page, framed
  from the login SPA's own `/preview` route rather than reimplemented;
- **social providers** — the client id and secret for each. A stored secret is never sent back to
  the browser: the page shows that one exists, and leaving the field blank keeps it;
- the **password policy** — length, character classes, and whether breached passwords are refused;
- **`require_mfa`** — whether this project's users must hold a second factor. Turning it *off* on a
  project whose users have enrolled is refused once, with the count of who it would affect, and
  needs an explicit confirmation on the retry;
- the **IP allowlist** — CIDRs. An entry that does not parse is refused at save time, because an
  allowlist nobody matches is a tenant outage rather than a saved setting;
- **allowed scopes** and **allowed email domains**;
- les **fournisseurs SAML** — création, modification et suppression, dans la portée de l'appelant :
  un administrateur d'organisation configure le SAML de son propre projet, ce que la console ne
  savait pas faire.

Les **scopes OAuth2** du projet s'éditent dans **Settings**. `openid`, `profile` et
`offline_access` sont implicites et ne se retirent pas ; les autres sont remplacés en bloc, et un
nom que le serveur refuse est recopié tel qu'il l'a nommé.

### Your own account

**Account** — display name, password, and multi-factor: authenticator app, phone, passkeys, backup
codes. Every change to a factor requires proving you still hold one; a stolen session must not be
able to swap the second factor. The dialog offers exactly the methods the server says you have, so
a passwordless account is never asked for a password.

---

## Multi-factor, and the first administrator

The first administrator of a deployment signs in without a second factor. Every administrator after
that must enrol one.

That is not a setting, and it used to be. `Security:RequireAdminMfa` had to be off for the first
ten minutes of a deployment's life — enrolling a factor needs SMTP or SMS, and configuring either
needs the console — and on for the rest of it. A setting whose correct value changes by itself is
not a setting; forgotten at `false` it leaves a `super_admin` on a password forever. The rule is now
derived from the deployment's own state and closes itself at the first enrolment.

While an administrator has no factor, a banner says so on every page. It has no dismiss button, on
purpose: it is the only thing standing between that one account and a password on its own.

---

## Things the console will not let you do

- **Reach it on the public host.** `/console` and `/admin` are both denied there by the ingress. The
  console is reachable on the admin host only.
- **Change a project's redirect URIs after creation.** The route exists but does not reach Hydra, so
  the change would appear to work and would not. Delete and recreate.
- **See a service account's key twice.** It is shown once, at creation.
- **Silence the MFA reminder.**
- **Create a user list at deployment level.** `/system/userlists` is an index across every tenant,
  not a place to make one: a list belongs to an organisation. Create it inside the tenant.

---

## When something does not load

- **A page renders its shell and nothing else** — the API call behind it failed. The two editors
  that write their whole form back (Authentication, Settings) refuse to render at all in that case
  rather than let you save defaults over a tenant's real configuration.
- **You are asked for your password again on refresh** — the SSO session has expired. Its length is
  `Security__SsoSessionMinutes`, eight hours by default; zero disables it entirely and asks every
  time. See [CONFIGURATION.md](CONFIGURATION.md).
- **A whole scope is missing from the sidebar** — the signed-in account does not hold that role.
  Roles come from Keto and are re-checked on every privileged request, not read from the token.
