# RediensIAM

Multi-tenant Identity & Access Management built on Ory Hydra + Keto, ASP.NET Core 10, and React.

- **Login SPA** — user-facing login, registration, MFA, password reset
- **Admin SPA** — super-admin, org-admin and project-manager console
- **Backend API** — ASP.NET Core 10, one process, two listeners: public `:5000` / admin `:5001`
- **Ory Hydra** — OAuth2/OIDC token issuance and consent
- **Ory Keto** — the authorisation store, re-checked live on every privileged request
- **PostgreSQL + Dragonfly** — durable state and ephemeral shared state

---

## Documentation

| Read this | For |
|---|---|
| [CHANGELOG.md](CHANGELOG.md) | **what breaks when you upgrade.** 0.2.0 changes the wire contract in four places and deploy order is load-bearing — read this first |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | how the system is put together and where authority lives |
| [docs/DIAGRAMS.md](docs/DIAGRAMS.md) | the same thing drawn — deployment topology, request pipeline, authorisation decision, OIDC and introspection sequences, data model and RLS coverage, key material, audit chain |
| [docs/SECURITY.md](docs/SECURITY.md) | what protects what, and what is deliberately still open — **read before trusting it with anything** |
| [docs/API.md](docs/API.md) | all 184 routes: method, path, required authority, where each is reachable |
| [docs/INTEGRATION.md](docs/INTEGRATION.md) | plugging an application in — includes four breaking wire-contract changes in this release |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | bare cluster to working IdP, plus the day-2 runbooks |
| [docs/TESTING.md](docs/TESTING.md) | running the suites, and what has no tests |
| [sdk/README.md](sdk/README.md) | which SDK, and why |
| `.security-hardening/` | the audit trail, finding by finding |

---

## Prerequisites

| Tool | Version |
|------|---------|
| Docker | 20+ |
| k3s (or any Kubernetes) | 1.28+ |
| kubectl | matching the cluster |
| Helm | 3.12+ |
| .NET SDK | 10.0 |
| Node.js | 20+ |

The cluster also needs a default StorageClass, an IngressClass, Traefik's `Middleware` CRD and
cert-manager. `./deploy/preflight.sh --dev` checks every one and names the fix; it can install
cert-manager with `--install-cert-manager`.

---

## Deployment

**Full guide: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).** Short version below.

### Development

```bash
./deploy/setup.sh --dev
```

One command, no manual steps, **nothing to fill in first**. It runs preflight, builds both SPAs and
the image, pushes to a loopback-only registry, deploys the chart pinned to the image digest, runs
`verify-deployment.sh`, and prints the bootstrap credentials.

Do not hand-write `values.secret.yaml`. Every credential — the four Postgres role passwords, the
cache password, the encryption root, the Argon2 pepper, the Hydra system secret and the bootstrap
admin password — is generated per machine into `deploy/rediensiam/values.secret.yaml` (mode 600,
gitignored). Earlier versions of this README shipped a template full of `CHANGE_ME_…` placeholders;
every copy of the repository knew those values, and `deploy.sh` now refuses to deploy them to
production.

What you get:

```
Login          http://iam.localhost/login
Admin console  http://localhost:30501/admin/
OIDC discovery http://iam.localhost/.well-known/openid-configuration
```

Clean slate: `./deploy/reset-dev.sh` (lists exactly what it destroys, then asks).

### Production

```bash
./deploy/setup.sh --prod --plan     # interview only — writes the answers, deploys nothing
./deploy/setup.sh --prod
```

The interview asks for the things nothing can default — public and admin hostnames, TLS issuer,
CloudNativePG or the built-in StatefulSet, where the off-node backup copy goes — and fails rather
than guessing. [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) covers what each answer costs you.

**Production has never been deployed from this branch.** Every prod path is template-verified and
preflight-verified; none has been run against a real production cluster.

#### Encrypt Kubernetes secrets at rest

The chart cannot do this for you — it is a flag on the cluster, not on the release. Without it
every Secret this deployment creates, including the database passwords, the Argon2 pepper and the
encryption root, sits in plaintext in the k3s datastore, readable by anyone who can read the file.

```bash
# On the k3s server node, as root
sudo sed -i 's|^ExecStart=.*k3s server|& --secrets-encryption|' /etc/systemd/system/k3s.service
sudo systemctl daemon-reload && sudo systemctl restart k3s

sudo k3s secrets-encrypt status          # expect: Encryption Status: Enabled
sudo k3s secrets-encrypt reencrypt       # rewrites Secrets that predate the flag
```

Fifteen minutes and a restart of the API server. Existing Secrets stay in plaintext until the
`reencrypt` — enabling the flag alone protects only what is written afterwards, which is the part
that is easy to miss.

### The stages, individually

```bash
./deploy/preflight.sh --dev          # can this host and cluster run the chart?
./deploy/deploy.sh --dev             # build and deploy only
./deploy/verify-deployment.sh --dev  # are the security controls live in the cluster?
NAMESPACE=rediensiam ./deploy/setup.sh --prod   # install into its own namespace
```

---

## Configuration

### Where settings live

Most runtime configuration is read from a single-row **`instances`** table in Postgres, not from the
environment. On first boot the row is seeded from environment variables; subsequent boots read the
row and ignore the environment. Design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#configuration-model--zitadel-style).

Trust anchors are deliberately excluded from that layer — `Hydra:*Url`, `Keto:*Url` and
`App:TrustedProxies` are env-only, so a database write cannot redirect the deployment's
authorisation store.

⚠ **The chart has no generic environment passthrough.** `templates/deployment.yaml` sets a fixed
list of variables; there is no `rediensiam.env` map. `INSTANCE_ID` and `RECONFIGURE_FROM_ENV` are
read by the application but **cannot be set through the chart as it ships**. Reconfiguring a running
instance means editing `templates/deployment.yaml` or writing the `instances` row directly.

### `values.yaml` — the keys you will actually set

`deploy/rediensiam/values.yaml` is heavily commented and is the source of truth; each key there
explains why it has the default it has. This table covers the ones an operator touches. It is
**not** exhaustive, and it deliberately no longer tries to be — the previous version of this table
listed an `env.*` structure the chart has not had for three revisions.

Everything is under the top-level `rediensiam:` key unless noted.

#### App and networking

| Key | Default | Notes |
|---|---|---|
| `app.adminPath` | `/admin` | path prefix for the admin SPA |
| `app.trustedProxies` | `10.42.0.0/16,10.43.0.0/16` | **The app refuses to start on an empty value.** CSV of CIDRs whose `X-Forwarded-*` headers are honoured. Silently trusting RFC1918 would let any in-cluster pod spoof `X-Forwarded-For` and bypass every IP-based control. The default is the k3s pod and service CIDR |
| `image.digest` | `""` | set by `deploy.sh` from `docker push`. When set it replaces `image.tag`, so a restart re-runs the exact bytes deployed |
| `image.pullPolicy` | `IfNotPresent` | only safe *because* of the digest pin |
| `replicaCount` | `1` | |
| `service.public.port` / `service.admin.port` | `5000` / `5001` | |
| `service.admin.type` | `ClusterIP` | `values.dev.yaml` opts into `NodePort` (`30501`) for local development |
| `ingress.className` | `traefik` | |
| `ingress.public.host` | `""` | set per environment |
| `ingress.public.tls.enabled` | `false` | on in prod; off in dev because `iam.localhost` cannot be certified |
| `ingress.public.adminOnlyPaths` | `[/admin, /org, /project, /service-accounts]` | denied on the public hostname by an unconditional Traefik `ipAllowList`. `/api` is deliberately absent — see [docs/API.md](docs/API.md) |
| `ingress.public.rateLimit` / `.maxBodyBytes` | 50/s burst 100 · 1 MiB | ingress-layer |
| `ingress.admin.clusterIssuer` | `selfsigned` | a known defect — see [docs/SECURITY.md](docs/SECURITY.md) |
| `networkPolicy.defaultDenyScope` | `namespace` | set to `release` if you share the namespace with anything that has no policy of its own |

#### Secrets — generated, not hand-written

| Key | Notes |
|---|---|
| `secrets.encryptionKey` | **Required.** 64 hex chars (`openssl rand -hex 32`). Not base64. Every at-rest subkey is HKDF-derived from it |
| `secrets.encryptionKeys` | Key **ring** for rotation: `"id:hex,id:hex"`, active key first. Supersedes `encryptionKey`. Never reuse an id. Runbook: `.security-hardening/16-key-rotation.md §7` |
| `secrets.databaseUrl` | **Required.** Npgsql connection string, user `iam_app` |
| `secrets.cacheUrl` | **Required.** Must carry `ssl=true` exactly when `dragonfly.local.tls.enabled` is on — the chart fails the render if they disagree |
| `secrets.smtpPassword`, `secrets.bootstrapEmail`, `secrets.bootstrapPassword` | |
| `security.argon2Pepper` | Optional hex pepper |
| `security.argon2Peppers` | Pepper **ring**: `"id:hex,id:hex"`, active first. No sweep is possible — accounts re-pepper on next login |

#### PostgreSQL

| Key | Default | Notes |
|---|---|---|
| `postgres.local.enabled` | `true` | `false` swaps in an external CloudNativePG cluster |
| `postgres.local.password` | `""` | the bootstrap SUPERUSER `iam`. Used by **nothing** at runtime — initdb's owner and the break-glass account. Do not put it in a DSN |
| `postgres.local.roles.{app,hydra,keto,backup}Password` | `""` | the four least-privilege roles. `deploy.sh` generates all four. **Created only on a first-ever start** — an existing installation needs the migration in `.security-hardening/15c-infra-residuals.md` |
| `postgres.local.tls.enabled` | `false` | **on in both shipped environments.** Needs cert-manager |
| `postgres.local.tls.requireSsl` | `false` | **on in both shipped environments.** Rewrites `pg_hba.conf` to `hostssl`, so the *server* refuses cleartext. Takes effect only at initdb |
| `postgres.rls.enabled` | `false` | Row-level security. **Off everywhere.** Fail-closed policies — enabling it before verifying the application half on a live connection is a total outage. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#turning-rls-on) |
| `postgres.external.podSelector` | `cnpg.io/cluster: rediensiam-db` | which pods the NetworkPolicies should target in CNPG mode |
| `backup.enabled` / `.schedule` / `.retainCopies` | `true` · `0 3 * * *` · `14` | nightly `pg_dumpall` to a PVC **on the same node as the data**. Copy it off-node yourself |

#### Cache

| Key | Default | Notes |
|---|---|---|
| `dragonfly.local.enabled` | `true` | |
| `dragonfly.local.password` | `""` | required when TLS is on |
| `dragonfly.local.tls.enabled` | `false` | on in dev, **off in prod**. A hard cutover — `--tls` makes Dragonfly stop answering cleartext, so `cacheUrl` must gain `ssl=true` in the same `helm upgrade` |

#### Hydra and Keto

`hydra.*` and `keto.*` under `rediensiam:` select local subchart or external URLs. The subcharts
themselves are configured at the **top level**, outside `rediensiam:`:

| Key | Notes |
|---|---|
| `hydra.hydra.config.secrets.system` | **Required**, ≥32 chars. A list — prepend to rotate, never replace |
| `hydra.hydra.config.dsn` | **Required.** User `iam_hydra`, database `hydra` |
| `hydra.hydra.config.ttl.access_token` | `15m` — the residual window after a revocation |
| `hydra.hydra.config.ttl.refresh_token` | `168h` — the outer bound on a stolen, unrotated chain |
| `hydra.maester.enabled` | `false`, deliberately: it holds a ClusterRole granting cluster-wide `create` on Secrets for a reconciliation loop with nothing to reconcile |
| `keto.keto.config.dsn` | **Required.** User `iam_keto`, database `keto` |

---

## Tests

```bash
dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true
```

**1345 tests** against real Postgres and Dragonfly containers (Testcontainers) with WireMock Hydra
and Keto stubs. The `-p:SonarQubeTargetsImported=true` flag suppresses a user-global MSBuild hook
that a stale `.sonarqube/` directory at the repository root arms — pass it whenever you run `dotnet`
from the repository root.

`Tests/Regression/` holds one suite per audit finding: 16 files, 241 executed tests, each written to
fail against the pre-fix build.

Everything else — the SDK suites, `verify-deployment.sh`, the detection rules and their self-test,
the Playwright E2E suite, and **the fact that neither SPA has a single test** — is in
[docs/TESTING.md](docs/TESTING.md).

---

## Static analysis

```bash
bash sonar-scan.sh
```

Publishes a single SonarQube project, `RediensIAM`, covering the backend and both SPAs. The backend
also references `SecurityCodeScan.VS2019` and `SonarAnalyzer.CSharp` directly, so most C# issues
surface at build time.

Neither analyser models cross-tenant authorisation. **A clean quality gate is not evidence of tenant
isolation.**
