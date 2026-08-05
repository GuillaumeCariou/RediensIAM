# Configuration reference

Every environment variable RediensIAM reads, what it controls, and what happens when it is unset.

ASP.NET maps a configuration key to an environment variable by replacing `:` with `__`, so
`Security:SsoSessionMinutes` is `Security__SsoSessionMinutes`. A few keys are already flat
(`IAM_PUBLIC_PORT`) and are written here exactly as an operator sets them.

**The chart names 23 of these.** Everything else is reached through `rediensiam.app.extraEnv`,
which passes arbitrary name/value pairs into the pod:

```yaml
rediensiam:
  app:
    extraEnv:
      Security__MaxLoginAttempts: "3"
      Audit__RetentionDays: "730"
```

Non-secret values only — anything secret-bearing belongs in `rediensiam.secrets`, which renders
into a Secret rather than into the pod spec where `kubectl describe` prints it.

---

## Where a value actually comes from

Configuration is layered, and one layer surprises people. `AddInstanceConfiguration()` reads a row
from the `instances` table and adds it **last**, so for the keys that row carries, the database
answers rather than the environment.

That row is created on first boot from the environment, and every boot afterwards re-applies the
environment over it: a variable you set reaches the process, and a variable you leave unset keeps
whatever the row holds. Until 0.5.0 the re-apply was conditional on `RECONFIGURE_FROM_ENV`, which
nothing set — so roughly twenty settings were frozen at whatever the first install happened to
write, and changing one in the chart did nothing at all.

Three groups never come from that row, deliberately:

- **Trust anchors** — `Hydra__AdminUrl`, `Keto__ReadUrl`, `Keto__WriteUrl`, `App__TrustedProxies`.
  They decide who the process believes: where tokens are introspected, where authorisation
  resolves, whose `X-Forwarded-For` counts. The row is written with the same Postgres credentials
  Hydra and Keto hold, and a process must not learn who to trust from data it can itself write.
- **Topology** — `App__PublicUrl`, `App__AdminSpaOrigin`, `App__Domain`. They decide which `Host`
  headers are accepted and which origin may receive an authorization code. Whoever deploys decides
  that, not a row from months ago.
- **Secrets** — never stored in the row at all.

---

## Application and topology

| Variable | Default | Chart value | What it controls |
|---|---|---|---|
| `App__PublicUrl` | `http://localhost` | `rediensiam.publicUrl` | The origin end users reach: the OIDC issuer handed to the SPA, the WebAuthn origin, invitation links, the CSP `connect-src` issuer. A non-HTTPS value in Production logs an error and continues |
| `App__AdminSpaOrigin` | falls back to `App__PublicUrl` | `rediensiam.adminUrl` | The origin the console is served from: the CORS policy, and the `redirect_uri` and post-logout URI registered with Hydra. Keep it on the issuer's registrable domain, or the SSO session cookie will not cross |
| `App__Domain` | **none — required** | derived from `publicUrl` | The WebAuthn relying-party ID. Startup throws `App:Domain configuration is required` without it |
| `App__TrustedProxies` | RFC 1918 outside Production; **required in Production** | `rediensiam.app.trustedProxies` | CSV of CIDRs whose `X-Forwarded-*` headers are honoured. Every IP-based control depends on it — rate limits, admin allowlists, the source address in the audit log. An empty value in Production refuses to start; unparsable entries are dropped with a warning, and if none parse it throws |
| `IAM_PUBLIC_PORT` | `5000` | `rediensiam.service.public.port` | Kestrel's public listener |
| `IAM_ADMIN_PORT` | `5001` | `rediensiam.service.admin.port` | Kestrel's admin listener. Swagger and `/metrics` answer only on connections arriving here |
| `AllowedHosts` | `*` | set to `*` by the chart | Host-header filtering. The literal `*` is replaced at startup by the hosts of the three topology URLs above, so the wildcard never actually ships |
| `INSTANCE_ID` | `default` | not set | Which `instances` row this process uses |
| `RECONFIGURE_FROM_ENV` | `false` | not set | Whether re-applying the environment counts as a deliberate reconfiguration and bumps `ConfigVersion`. Since 0.5.0 the environment is re-applied either way |

---

## Security

| Variable | Default | Bounds | What it controls |
|---|---|---|---|
| `Security__TotpSecretEncryptionKey` | **none — required** | exactly 64 hex characters | The HKDF root every at-rest key derives from: TOTP secrets, webhook secrets, SMTP passwords, the DataProtection key ring, the audit hash chain, device fingerprints. An all-zero value refuses to start in Production |
| `Security__EncryptionKeys` | falls back to the single root above | `id:hex,id:hex,…`, first is active | Multi-root ring for rotating that key without re-encrypting everything at once. Throws on a duplicate id, a bad separator or a key that is not 64 hex |
| `Security__Argon2Pepper` | `""` (no pepper) | 64 hex when set | Server-side pepper mixed into every password hash. An attacker with the database alone cannot test guesses without it |
| `Security__Argon2Peppers` | falls back to the single pepper | `id:hex,…`, first is active | Pepper ring. Rotation is per-login — a hash cannot be re-derived — so the old pepper stays listed until the last user has signed in |
| `Security__SsoSessionMinutes` | `480` (8 h) | 0–10080 | How long one sign-in lasts before Hydra asks for the password again. **Zero disables SSO entirely**, which is what this deployment did by accident before 0.5.0 |
| `Security__MaxLoginAttempts` | `5` | 1–10 | Failed attempts before lockout. The per-IP counter is deliberately never cleared by a success |
| `Security__LockoutMinutes` | `15` | 1–1440 | How long a lockout lasts, and the window the attempt counter lives in |
| `Security__OtpTtlSeconds` | `300` | — | Lifetime of an emailed or texted one-time code |
| `Security__MaxSmsPerWindow` | `3` | — | SMS codes allowed per window |
| `Security__SmsWindowMinutes` | `10` | — | Length of that window |
| `Security__ArgonTimeCost` | `3` | floor 2 | Argon2id iterations. The floor is the OWASP minimum |
| `Security__ArgonMemoryCost` | `65536` KiB | floor 19456 | Argon2id memory. Drives the pod's memory limit — raising it without raising the limit gets the pod killed |
| `Security__ArgonParallelism` | `4` | 1–16 | Argon2id lanes |
| `Security__PatPrefix` | `rediens_pat_` | — | Literal prefix on personal-access tokens, so a leaked one is recognisable in a log |
| `Security__MaxPatLifetimeDays` | `365` | 1–730 | Ceiling on a requested token lifetime |
| `Security__NewDeviceCacheDays` | `90` | — | How long a device is remembered before signing in from it notifies the user again |
| `Security__PatCacheTtlMinutes` | `5` | 0–15 | Ceiling on how long a revoked personal-access token keeps working. `0` disables the cache and makes revocation immediate. Bounds the freshness of the token's *role set* only — liveness (account active, organisation not suspended, token unexpired) is re-checked on **every** hit |
| `Security__ManagementClientIds` | `client_admin_system` | CSV | Which OAuth2 clients may call the management API at all. An audience boundary, checked before any role is |

---

## Database

| Variable | Default | What it controls |
|---|---|---|
| `ConnectionStrings__Default` | **none — required** | The application's PostgreSQL DSN. Startup refuses `No Reset On Close=true` and `Multiplexing=true`: both break the per-connection tenant scope that row-level security reads, which would leak across tenants rather than fail |
| `Database__MigrateOnStartup` | `true` | Whether EF migrations run at boot. `false` starts anyway and logs how many are pending; `true` retries twelve times before giving up and aborting |
| `ConnectionStrings__DefaultConnection` | localhost | **Design-time only** — `dotnet ef migrations`. Never read by the running application, and note the different name |

---

## Hydra and Keto

| Variable | Default | What it controls |
|---|---|---|
| `Hydra__AdminUrl` | `http://rediensiam-hydra-admin:4445` | Login and consent decisions, OAuth2 client management, session revocation |
| `Hydra__PublicUrl` | `http://rediensiam-hydra-public:4444` | Token, introspection and JWKS endpoints as reached from inside the cluster |
| `Keto__ReadUrl` | `http://rediensiam-keto-read:4466` | Permission checks, re-evaluated live on every privileged request |
| `Keto__WriteUrl` | `http://rediensiam-keto-write:4467` | Creating and deleting role grants |

All four are trust anchors: they come from the environment only, never from the database.

---

## Mail

| Variable | Default | What it controls |
|---|---|---|
| `Smtp__Host` | none | Deployment-wide relay. A per-organisation SMTP configuration overrides it |
| `Smtp__Port` | `587` | |
| `Smtp__StartTls` | `true` | |
| `Smtp__Username` | none | |
| `Smtp__Password` | none | Rendered into a Secret, never into the pod spec |
| `Smtp__FromName` | `RediensIAM` | Display name on outbound mail |
| `Smtp__FromAddress` | `noreply@localhost` | Envelope sender |

---

## Everything else

| Variable | Default | What it controls |
|---|---|---|
| `IAM_BOOTSTRAP_EMAIL` | none | The super-admin created on first boot. Bootstrap runs only when both this and the password are set |
| `IAM_BOOTSTRAP_PASSWORD` | none | Its password. Remove it from the environment once the account exists — the application says so in its own log line |
| `Audit__RetentionDays` | `365` | How long audit rows are kept. Clamped to 90–3650: the floor exists because this drives an unconditional delete |
| `Invitations__ExpiryHours` | `72` | How long an emailed invitation stays valid |
| `Webhooks__TimeoutSeconds` | `10` | Per-attempt timeout on outbound webhook delivery |
| `Export__RateLimitMinutes` | `1` | Minimum interval between data exports |
| `Social__GithubUserApiUrl` | `https://api.github.com/user` | Override for GitHub Enterprise |
| `Social__GithubEmailsApiUrl` | `https://api.github.com/user/emails` | Override for GitHub Enterprise |
| `Logging__LogLevel__Default` | `Information` | Standard ASP.NET logging |
| `ASPNETCORE_ENVIRONMENT` | `Production` in the image | Gates three behaviours: the trusted-proxies requirement, the all-zero-key refusal, and the non-HTTPS warnings |

---

## Declared and read by nothing

`IAM_ADMIN_PATH` is still declared in `appsettings.json` and still written to an `instances`
column, and no code consults either. It never controlled where the console is served: that is the
compile-time constant `Roles.ConsoleBasePath`. The chart stopped setting it in 0.4.0. It is left in
place because removing the column is a migration, not a deletion.
