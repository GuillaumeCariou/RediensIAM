#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Tests for the deploy layer itself.
#
#   ./deploy/tests.sh
#
# The scripts under deploy/ had no harness, and three of the worst faults this repo has shipped
# lived here: a deploy that uninstalled the release it was upgrading, a backup role with no read
# grant, and a self-test that could not authenticate. None of them failed loudly — the deploy
# printed "complete", the backup CronJob logged an error nobody read, and the self-test reported
# its assertions as mismatches rather than as an auth failure.
#
# Static checks only: nothing here touches a cluster. Exit 0 all pass · 1 at least one failure.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${DIR}/.." && pwd)"
PASS=0; FAIL=0

pass() { printf '  \033[32m OK \033[0m %s\n' "$1"; PASS=$((PASS + 1)); }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; [ -n "${2:-}" ] && printf '       %s\n' "$2"; FAIL=$((FAIL + 1)); }

echo "──── deploy layer ────────────────────────────────"

# ── the upgrade path must not tear the release down ──────────────────────────
# `helm rollback <rel> 0` errors on a single-revision release, so the `|| helm uninstall` beside it
# fired on every deploy once the release had been reinstalled once — taking the backup PVC and
# every retained dump with it. Upgrading in place is the whole point of `helm upgrade --install`.
if grep -qE 'helm +rollback +"?\$\{?[A-Za-z_]+\}?"? +0' "${DIR}/deploy.sh"; then
  fail "deploy.sh does not roll back to revision 0" \
       "rollback 0 means 'the previous revision' and errors when there is none"
else
  pass "deploy.sh does not roll back to revision 0"
fi

# Scoped to helm_deploy(), the function every deploy goes through. An uninstall elsewhere is fine
# when the operator asked for it — `--rotate-secrets` is dev-only and says what it discards.
if awk '/^helm_deploy\(\)/,/^}/' "${DIR}/deploy.sh" | grep -vE '^\s*#' | grep -qE 'helm +uninstall'; then
  fail "the upgrade path does not uninstall the release" \
       "$(awk '/^helm_deploy\(\)/,/^}/' "${DIR}/deploy.sh" | grep -vE '^\s*#' | grep -nE 'helm +uninstall' | head -2)"
else
  pass "the upgrade path does not uninstall the release"
fi

# ── a failed image build must stop the deploy ────────────────────────────────
# `set -e` does not fire for a non-final command in an && list, so `cd && docker build && docker
# push` let a failed build fall through to the digest step — which resolved the previous local
# image and deployed that. Three deploys reported success while shipping stale code.
if grep -qE 'docker build[^|]*&&' "${DIR}/deploy.sh"; then
  fail "a failed image build stops the deploy" \
       "docker build is chained with && — set -e will not fire for it"
else
  pass "a failed image build stops the deploy"
fi

# ── destructive commands honour ${RELEASE} ───────────────────────────────────
if grep -nE '^[^#]*(helm +uninstall|kubectl +delete)[^|]*[^{$]rediensiam' "${DIR}/deploy.sh" | grep -q .; then
  fail "no destructive command in deploy.sh names a release literally" \
       "$(grep -nE '^[^#]*(helm +uninstall|kubectl +delete)[^|]*[^{$]rediensiam' "${DIR}/deploy.sh" | head -3)"
else
  pass "no destructive command in deploy.sh names a release literally"
fi

# ── the backup role can actually read ────────────────────────────────────────
# Deleted once while every comment and doc citing it stayed in place, so pg_dumpall failed on the
# first table and all 13 detection rules — which authenticate as the same role — errored out.
if grep -q 'GRANT pg_read_all_data TO iam_backup' "${DIR}/rediensiam/templates/postgres.yaml"; then
  pass "the postgres init grants the backup role read access"
else
  fail "the postgres init grants the backup role read access" \
       "without it pg_dumpall reads nothing and every detection rule errors"
fi

# ── the detection self-test can authenticate ─────────────────────────────────
# T-04 replaced `trust` with scram-sha-256. audit-detections.sh was updated to read the password
# from the release secret; selftest.sh was not, so all six assertions failed as mismatches.
if grep -q 'PGPASSWORD' "${DIR}/monitoring/selftest.sh"; then
  pass "monitoring/selftest.sh supplies a password"
else
  fail "monitoring/selftest.sh supplies a password" \
       "every assertion currently fails as a mismatch rather than as an auth error"
fi

# ── reset-dev refuses to guess ───────────────────────────────────────────────
# The dev guard accepted an empty issuer, so a `helm get values` that failed for any reason read as
# permission to delete the release, its PVCs and the secrets file.
if grep -qE '\*localhost\*\|\*127\.0\.0\.1\*\|""' "${DIR}/reset-dev.sh"; then
  fail "reset-dev.sh treats an unreadable issuer as a refusal" \
       "an empty ISSUER currently matches the dev branch and authorises deletion"
else
  pass "reset-dev.sh treats an unreadable issuer as a refusal"
fi

# ── the shipped defaults are the safe ones ───────────────────────────────────
# The code default for RequireAdminMfa is false so a first launch can reach the console and
# configure a delivery channel. The chart is a different question: an operator who installs it
# without layering values.prod.yaml should not get a password-only super_admin.
if grep -qE '^\s*requireAdminMfa:\s*true' "${DIR}/rediensiam/values.yaml"; then
  pass "values.yaml requires admin MFA by default"
else
  fail "values.yaml requires admin MFA by default" \
       "only values.prod.yaml re-enables it, so any other install ships password-only super_admin"
fi

# The RLS layer is the backstop under ~200 hand-written tenant conjuncts. Shipping prod with it
# off means the one control that does not depend on remembering a WHERE clause never runs.
# Checked against the rendered chart rather than the file: the first attempt at this set the key
# under a second `postgres:` block, and YAML last-wins silently discarded it.
if [ "$(helm template rel "${DIR}/rediensiam" -f "${DIR}/rediensiam/values.prod.yaml" 2>/dev/null \
          | grep -c 'rls.sql')" -gt 0 ]; then
  pass "values.prod.yaml enables row-level security"
else
  fail "values.prod.yaml enables row-level security" \
       "the chart default is off and the rendered prod output contains no RLS job"
fi

# ── no values file declares the same key twice ───────────────────────────────
# YAML takes the last one and drops the first without a word. That is how an `rls.enabled: true`
# added to prod rendered nothing at all.
for f in "${DIR}"/rediensiam/values*.yaml; do
  dupes="$(grep -nE '^  [a-zA-Z][a-zA-Z0-9_]*:' "${f}" | sed 's/.*: *//; s/:$//' | awk '{print $1}' \
             | sort | uniq -d)"
  if [ -n "${dupes}" ]; then
    fail "no duplicate top-level key in $(basename "${f}")" "${dupes}"
  else
    pass "no duplicate top-level key in $(basename "${f}")"
  fi
done

# ── a PodDisruptionBudget that permits evicting the only pod ─────────────────
# maxUnavailable: 1 with replicaCount: 1 lets the drain take the last pod, so the object that looks
# like it prevents downtime prevents nothing — and every rollout is a full auth outage.
PDB_MAX="$(grep -E '^\s*maxUnavailable:' "${DIR}/rediensiam/templates/pdb.yaml" | head -1 | tr -dc '0-9')"
PROD_REPLICAS="$(grep -E '^\s*replicaCount:' "${DIR}/rediensiam/values.prod.yaml" | head -1 | tr -dc '0-9')"
if [ "${PDB_MAX:-1}" -ge 1 ] && [ "${PROD_REPLICAS:-1}" -lt 2 ]; then
  fail "the PDB can actually protect a pod in prod" \
       "maxUnavailable=${PDB_MAX:-1} with replicaCount=${PROD_REPLICAS:-1} permits evicting the only replica"
else
  pass "the PDB can actually protect a pod in prod"
fi

# ── preflight must validate what will actually be deployed ───────────────────
# It honours ENV_FILE and OVERRIDE_FILE everywhere except three greps that read values.yaml
# directly, so an override changing the ingress class or the trusted proxies was checked against
# the committed defaults instead — the check passes for a value the deploy will not use.
if grep -nE 'grep[^|]*"?\$\{?CHART\}?"?/values\.yaml' "${DIR}/preflight.sh" | grep -q .; then
  fail "preflight.sh validates the rendered values, not just values.yaml" \
       "$(grep -nE 'grep[^|]*values\.yaml' "${DIR}/preflight.sh" | head -3)"
else
  pass "preflight.sh validates the rendered values, not just values.yaml"
fi

# ── a tracked path with a backslash breaks the image build ───────────────────
# `src/bin\Debug` — one directory, literally named with a backslash, left by a Windows-style
# `-o "bin\Debug\net10.0"` on Linux and then swept into a commit by `git add -A`. MSBuild reads the
# backslash as a separator while expanding the SDK's default globs, so `**/*.cs` stayed literal and
# `dotnet publish` failed with CS2021/CS2001 inside the container while the host build was fine.
BACKSLASHED="$(cd "${ROOT}" && git ls-files | grep -F '\\' | head -3)"
if [ -n "${BACKSLASHED}" ]; then
  fail "no tracked path contains a backslash" "${BACKSLASHED}"
else
  pass "no tracked path contains a backslash"
fi

# ── the docs must not describe a stack that was removed ──────────────────────
# The admin console dropped shadcn, every @radix-ui package and oidc-client-ts; the README still
# described all three, which is worse than no README for anyone onboarding.
DOC_STALE=""
for term in "@radix-ui" "shadcn" "oidc-client-ts" "recharts"; do
  if grep -qi -- "${term}" "${ROOT}/frontend/admin/README.md" 2>/dev/null; then
    DOC_STALE="${DOC_STALE} ${term}"
  fi
done
if [ -n "${DOC_STALE}" ]; then
  fail "frontend/admin/README.md describes the stack that ships" "still mentions:${DOC_STALE}"
else
  pass "frontend/admin/README.md describes the stack that ships"
fi

# ── documented test counts must match the suites ─────────────────────────────
# docs/TESTING.md stated 1345 backend tests and said twice that neither SPA has any, while 162 of
# them run in seconds. A number nobody maintains is worse than no number.
BACKEND_CLAIMED="$(grep -oE '\*\*1[0-9]{3}\*\*|\b1[0-9]{3}\b' "${ROOT}/docs/TESTING.md" | tr -d '*' | head -1)"
BACKEND_ACTUAL="$(grep -rhoE '\[(Fact|Theory)\]|\[InlineData' "${ROOT}/tests/RediensIAM.IntegrationTests/Tests" \
                    | grep -c . )"
if [ -n "${BACKEND_CLAIMED}" ] && [ "${BACKEND_CLAIMED}" -lt "$((BACKEND_ACTUAL - 200))" ]; then
  fail "docs/TESTING.md's backend test count is in the right range" \
       "documented ${BACKEND_CLAIMED}, attributes on disk ${BACKEND_ACTUAL}"
else
  pass "docs/TESTING.md's backend test count is in the right range"
fi

if grep -qiE "neither SPA has (a single test|any test)" "${ROOT}/docs/TESTING.md" 2>/dev/null; then
  fail "docs/TESTING.md does not claim the SPAs are untested" "both suites run and pass"
else
  pass "docs/TESTING.md does not claim the SPAs are untested"
fi

# ── every script parses ──────────────────────────────────────────────────────
for script in "${DIR}"/*.sh "${DIR}"/monitoring/*.sh "${ROOT}/sonar-scan.sh"; do
  name="$(basename "${script}")"
  if bash -n "${script}" 2>/dev/null; then pass "bash -n ${name}"; else fail "bash -n ${name}"; fi
done

# ── no string carries an unintended command substitution ─────────────────────
# A mechanical documentation rewrite once put backticks inside double-quoted strings across four
# operator scripts; each printed `bash: …: command not found` during a real install.
for script in "${DIR}"/*.sh "${DIR}"/monitoring/*.sh "${ROOT}/sonar-scan.sh"; do
  name="$(basename "${script}")"
  if grep -nE '"[^"#]*`[^"]*"' "${script}" | grep -q .; then
    fail "no backticks inside a double-quoted string in ${name}" \
         "$(grep -nE '"[^"#]*`[^"]*"' "${script}" | head -2)"
  else
    pass "no backticks inside a double-quoted string in ${name}"
  fi
done

echo ""
echo "  ${PASS} passed, ${FAIL} failed"
[ "${FAIL}" -eq 0 ] || exit 1
