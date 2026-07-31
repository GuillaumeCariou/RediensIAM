# Audit trail — read this before trusting anything in here

These are **point-in-time records of what was done**, not a description of the system as it
stands. Each was written at the end of the step it describes, and several were overtaken within
hours by the step that followed.

## What is authoritative

| Question | Read |
|---|---|
| What protects what, and what is still open | [`../docs/SECURITY.md`](../docs/SECURITY.md) |
| What breaks on upgrade | [`../CHANGELOG.md`](../CHANGELOG.md) |
| How the system is built | [`../docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md) |
| What the routes are | [`../docs/API.md`](../docs/API.md) |
| Whether a control is actually live | `./deploy/verify-deployment.sh --dev` |

**The code is the authority.** Where a report here and the source disagree, the source wins.

## Why that warning is not boilerplate

Writing `docs/SECURITY.md` meant reading the code against these reports, and that turned up
**eight places where a report contradicted the source** — recorded in `26-documentation.md`. Two
reports overstated a fix that a later penetration test then broke: `04` and `08` both described
the MFA guard as covering every factor mutation, and `11` found that every federated user could
still have factors changed with a bearer token alone.

The failure mode is consistent and worth naming, because it will recur: a report describes what
its author *intended and believed*, verified by reading. Reading is how P-02 survived two steps,
how a `verify-deployment.sh` check passed for weeks without ever running, and how a detection
script printed "No hits" while every one of its queries was failing.

**A claim in here is worth exactly as much as the command output quoted beside it.**

## Reports moved out

Five were moved to `~/Desktop/rediensiam-audit-perime/` because they assert things that are no
longer true, and leaving them here invited someone to act on them:

| File | Why |
|---|---|
| `14-finding-ledger.md` | It claims a *current* status for every finding, and its closing list names 23 as open — S-1, S-3, S-5, S-8, S-10, P-05, P-06, R-30, R-31, T-04, T-07a–d and M7 have all shipped since. A ledger that is out of date is worse than no ledger. |
| `17-structural-debt.md` | Understates S-1: says the compile break is "one line away". It landed, and `GetManagementLevel` has since been deleted outright. |
| `19-api-surface.md` | §7.3 claims unknown namespaces fail closed. True for a tenant caller, false for a deployment-scoped one — closed in `75e9576`. |
| `20-sdk-audience.md` | Documents an audience-binding path through `grant_access_token_audience` that nothing in `src/` sets. |
| `23-cache-hardening.md` | Its summary says Dragonfly TLS is "done and live"; its own residuals section admits dev-only. Production values were set later and remain untested against a real cluster. |

They were moved rather than deleted: the trail matters for the SOC 2 work in `12`, and a wrong
report is itself evidence of how the work went.

## Reading order, if you want the story

`01` scan → `02` STRIDE threat model → `03` architecture → `04`–`10` remediation →
`11`/`11b` penetration test and its fixes → `12` compliance → `13` monitoring →
`15`–`29` the second pass.

The most useful single document is `11-pentest-results.md`, because it is the only one that set
out to break the others.
