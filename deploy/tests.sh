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
