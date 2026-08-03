# Security audit log

What was audited, in what order, and where the outcome lives now. The step-by-step reports this
summarises were removed from the working tree in the 2026-08-01 cleanup; every one of them is in
git history at `385b6ca` under `.security-hardening/`, e.g.:

```sh
git show 385b6ca:.security-hardening/11-pentest-results.md
```

## What is authoritative

| Question | Read |
|---|---|
| What protects what, and what is still open | [`docs/SECURITY.md`](docs/SECURITY.md) |
| What breaks on upgrade | [`CHANGELOG.md`](CHANGELOG.md) |
| How the system is built | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) |
| What the routes are | [`docs/API.md`](docs/API.md) |
| Whether a control is actually live | `./deploy/verify-deployment.sh --dev` |

**The code is the authority.** Where a report and the source disagree, the source wins. The
reports were written at the end of the step they describe and several were overtaken within
hours by the step that followed — that is why they are history rather than documentation.

## The failure mode worth remembering

Writing `docs/SECURITY.md` meant reading the code against those reports, and that turned up
**eight places where a report contradicted the source**. Two overstated a fix that a later
penetration test then broke: steps 4 and 8 both described the MFA guard as covering every factor
mutation, and step 11 found that every federated user could still have factors changed with a
bearer token alone. The same shape recurs: a report describes what its author *intended and
believed*, verified by reading. Reading is how P-02 survived two steps, how a
`verify-deployment.sh` check passed for weeks without ever running, and how a detection script
printed "No hits" while every one of its queries was failing.

**A claim is worth exactly as much as the command output quoted beside it.**

## The steps

| # | Subject |
|---|---|
| 01 | Vulnerability scan |
| 02 | Threat model — STRIDE, attack trees, risk matrix |
| 03 | Architecture review |
| 04 | Critical fixes |
| 05 | Backend hardening |
| 06 | Frontend hardening |
| 07 | Mobile hardening — skipped, no target |
| 08 | Authentication and authorisation enhancement |
| 09 | Infrastructure hardening |
| 10 | Secrets management |
| 11 / 11b | Penetration test of steps 4–10, and its remediation |
| 12 | Compliance assessment (OWASP ASVS L2, CIS, SOC 2 Type II, PCI-DSS, HIPAA) |
| 13 | Security monitoring and SIEM |
| 15a–c | Residuals: backend, SDK/frontend, infrastructure and data layer |
| 16 | Key rotation (S-10) |
| 18 | CloudNativePG, transport encryption (R-15), row-level security phase 2 (S-5) |
| 21 | The application half of RLS and cache TLS |
| 22 | Setup scripts: bare cluster to working IdP |
| 24 / 25 | SonarQube reliability and maintainability — SDK/frontend, then backend |
| 26 / 37 | Documentation against the code, twice |
| 27 | Release 0.2.0 |
| 28 | Frontend test suites |
| 29 | Enabling RLS on a running system; cache TLS in production values |
| 30 | Architecture diagrams |
| 31 | Keying the audit chain; reconciling the grant dual write |
| 32 | Making the login path tenant-scoped |
| 33 | The production profile, actually deployed |
| 34 / 35 / 35b | Dead code sweep; comment sweeps including `tests/e2e` |
| 36 | SAML `Destination` validation, `MigrateOnStartup`, dead configuration keys |
| 38 | SAML tenant scope, the `/api/authorize` fail-open, health-endpoint exception text, SAML ordering |
| 39 | Rust SDK trust store; npm advisories in both SPAs |

Five reports (`14`, `17`, `19`, `20`, `23`) were pulled from the tree before this cleanup because
they asserted things that had stopped being true — a finding ledger listing 23 open items that
had all shipped, a structural-debt note understating a break that had already landed, an API
surface claim closed in `75e9576`, an audience-binding path nothing in `src/` sets, and a cache
report whose summary and its own residuals section disagreed.

The single most useful report is step 11: it is the only one that set out to break the others.
