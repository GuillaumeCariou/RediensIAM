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
  # Key names, within one root block. Two earlier versions of this were wrong in opposite ways:
  # comparing the text after the colon reported two keys sharing a value as a duplicate key, and
  # comparing names across the whole file reported ingress and hydra, which legitimately appear
  # once under rediensiam and once under a root block of their own.
  dupes="$(awk '
    /^[a-zA-Z]/            { section = $0; next }
    /^  [a-zA-Z][a-zA-Z0-9_]*:/ { key = $1; sub(/:.*/, "", key); print section "/" key }
  ' "${f}" | sort | uniq -d | sed 's#.*/##')"
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

# ── the default image tag must not drift from the chart ──────────────────────
# values.yaml pinned tag: "0.2.3" while Chart.yaml said appVersion 0.3.0. deploy.sh always passes
# --set image.digest, so nothing here noticed — but a plain `helm install` of the published chart
# pulls an image four releases old, which is the one case a published chart exists for.
APPVERSION="$(grep -E '^appVersion:' "${DIR}/rediensiam/Chart.yaml" | sed 's/.*: *//' | tr -d '"')"
RENDERED_TAG="$(helm template rel "${DIR}/rediensiam" -f "${DIR}/rediensiam/values.dev.yaml" 2>/dev/null \
                  | grep -oE 'image: rediensiam:[^ ]+' | head -1 | sed 's/.*://')"
if [ "${RENDERED_TAG}" = "${APPVERSION}" ]; then
  pass "the default image tag follows the chart's appVersion"
else
  fail "the default image tag follows the chart's appVersion" \
       "chart says ${APPVERSION}, the render asks for ${RENDERED_TAG:-<nothing>}"
fi

# ── the ingress must not pin the controller's entrypoints or force TLS ───────
# Both Ingresses hardcoded Traefik entrypoint names, and the admin one emitted its tls: block with
# no guard at all. A deployment running two Traefik controllers, or terminating TLS upstream, could
# not express either. The P-04 deny router must survive every variation of this: it is the control
# that keeps /admin, /org, /project and /service-accounts off the public host.
CHART="${DIR}/rediensiam"
BASE=(-f "${CHART}/values.prod.yaml")   # prod is the environment where the admin ingress renders

r_default="$(helm template rel "${CHART}" "${BASE[@]}" 2>&1)"
r_notls="$(helm template rel "${CHART}" "${BASE[@]}" --set rediensiam.ingress.admin.tls.enabled=false 2>&1)"
r_web="$(helm template rel "${CHART}" "${BASE[@]}" --set rediensiam.ingress.public.entrypoints=web 2>&1)"

# The admin Ingress is the one named <release>-admin-internal; read only its own block.
admin_block() { printf '%s' "$1" | awk '/name: rel-admin-internal/,/^---/'; }

if [[ "$(admin_block "${r_default}")" == *'tls:'* ]]; then
  pass "the admin ingress still asks for TLS by default"
else
  fail "the admin ingress still asks for TLS by default" "the default render lost its tls: block"
fi
if [[ "$(admin_block "${r_notls}")" == *'tls:'* ]]; then
  fail "admin.tls.enabled=false drops the tls block" "the block rendered anyway"
else
  pass "admin.tls.enabled=false drops the tls block"
fi
if [[ "${r_web}" == *$'router.entrypoints: web\n'* ]]; then
  pass "public.entrypoints reaches the annotation"
else
  fail "public.entrypoints reaches the annotation" \
       "$(printf '%s' "${r_web}" | grep 'router.entrypoints' | head -2)"
fi
for variant in default notls web; do
  # Indirect expansion, not eval: the value is a whole rendered chart, and eval re-parses every
  # quote and backtick in it.
  name="r_${variant}"; body="${!name}"
  if [[ "${body}" == *'rel-public-admin-deny'* ]]; then
    pass "the P-04 deny router survives the ${variant} render"
  else
    fail "the P-04 deny router survives the ${variant} render" \
         "the deny router keeps the management API off the public host"
  fi
done

# ── the Postgres host must be overridable ────────────────────────────────────
# The generated secrets file hardcoded rediensiam-postgres in all three DSNs. Under CloudNativePG
# the service is <cluster>-rw.<namespace>.svc, and the chart already knows how to talk to an
# external Postgres (postgres.external.podSelector / .namespace) — only the script was behind, with
# no variable to set and no way to reach the value.
HARDCODED="$(grep -nE '(Host=|@)rediensiam-postgres' "${DIR}/deploy.sh" | head -3)"
if [ -n "${HARDCODED}" ]; then
  fail "deploy.sh reads the Postgres host from a variable" "${HARDCODED}"
else
  pass "deploy.sh reads the Postgres host from a variable"
fi
if grep -qE '^PG_HOST="\$\{PG_HOST:-rediensiam-postgres\}"' "${DIR}/deploy.sh"; then
  pass "PG_HOST defaults to the name the chart installs"
else
  fail "PG_HOST defaults to the name the chart installs" \
       "without the default, an existing install would be pointed somewhere else by an upgrade"
fi

# ── the admin ingress must be able to name its own controller ────────────────
# All three Ingresses read one `ingress.className`. With two Traefik controllers — a public one on
# an address the router forwards, an admin one on an address it never does — the admin Ingress
# inherits the public class and is therefore served by the public controller. No public DNS record
# points at the admin host and the topology looks like the protection; it is not. A request
# carrying `Host: <admin host>` sent to the public address serves the console from the internet.
CLASSED="$(helm template rel "${DIR}/rediensiam" -f "${DIR}/rediensiam/values.prod.yaml" \
             --set rediensiam.ingress.className=public-class \
             --set rediensiam.ingress.admin.className=admin-class 2>&1)"
# Per YAML document, not per line range: a Service is also called rel-public, and a range that
# started at its name ran on until the next `rules:` — which belongs to a different object.
class_of() {
  printf '%s' "${CLASSED}" | awk -v want="$1" '
    /^---/            { named = 0; next }
    $0 == "  name: " want { named = 1; next }
    named && /ingressClassName:/ { sub(/.*: */, ""); print; exit }
  '
}
admin_class="$(class_of rel-admin-internal)"
public_class="$(class_of rel-public)"
deny_class="$(class_of rel-public-admin-deny)"

if [ "${admin_class}" = "admin-class" ]; then
  pass "the admin ingress takes its own IngressClass"
else
  fail "the admin ingress takes its own IngressClass" \
       "it rendered '${admin_class:-<nothing>}' — served by whichever controller owns the public class"
fi
if [ "${public_class}" = "public-class" ] && [ "${deny_class}" = "public-class" ]; then
  pass "the public ingress and the P-04 deny router stay on the public class"
else
  fail "the public ingress and the P-04 deny router stay on the public class" \
       "public='${public_class:-<nothing>}' deny='${deny_class:-<nothing>}' — the deny router only works on the controller that serves the public host"
fi

# ── naming a real issuer must stop the self-signed one rendering ─────────────
# The self-signed Issuer exists for the case where no ClusterIssuer is named. When the admin
# ingress names one, rendering it anyway leaves an unused object whose only role is to make the
# most privileged UI a click-through warning — and the condition read the old key path for a while
# after the value moved under `tls:`, so it never saw the answer.
# Chart defaults, where Postgres and cache TLS are both off: the admin ingress is then the only
# thing that could ask for an Issuer. Built on values.prod.yaml the first version of this check
# rendered nothing at all — prod sets requireSsl, which refuses to render with tls off — so it
# passed by failing.
ISSUER_NAMED="$(helm template rel "${DIR}/rediensiam" \
                  --set rediensiam.ingress.admin.enabled=true \
                  --set rediensiam.ingress.admin.tls.clusterIssuer=letsencrypt 2>&1)"
if [[ "${ISSUER_NAMED}" != *'kind: Ingress'* ]]; then
  fail "a named ClusterIssuer suppresses the self-signed Issuer" \
       "the render produced nothing to judge: ${ISSUER_NAMED%%$'\n'*}"
elif [[ "${ISSUER_NAMED}" == *'kind: Issuer'* ]]; then
  fail "a named ClusterIssuer suppresses the self-signed Issuer" \
       "the chart rendered its own Issuer while the admin ingress pointed at letsencrypt"
else
  pass "a named ClusterIssuer suppresses the self-signed Issuer"
fi

# ── the subcharts have to be present before anything renders ─────────────────
# charts/ holds hydra-*.tgz and keto-*.tgz, fetched from the versions Chart.lock pins. It is
# gitignored — a fetched artefact, not repository content — so a fresh clone has none, and every
# helm command below then fails with a dependency error rather than with what it was checking.
if [ -d "${DIR}/rediensiam/charts" ] && ls "${DIR}/rediensiam/charts"/*.tgz >/dev/null 2>&1; then
  pass "the pinned subcharts are present"
else
  fail "the pinned subcharts are present" \
       "run: helm dependency build ${DIR}/rediensiam — needs network, versions come from Chart.lock"
fi

# ── the chart must render on its own defaults ────────────────────────────────
# A publishable chart has to render with no -f at all: that is what an offline validator does,
# what `helm lint` does, and what anyone reading it for the first time does. publicUrl and adminUrl
# had no default, so a bare render reached urlParse with a nil and died on "wrong type for value;
# expected string; got interface {}". They now carry placeholders — and emptying either on purpose
# still fails naming the key, which is the half worth keeping.
if helm template "${DIR}/rediensiam" >/dev/null 2>&1; then
  pass "helm template renders on the chart's own defaults"
else
  fail "helm template renders on the chart's own defaults" \
       "$(helm template "${DIR}/rediensiam" 2>&1 | head -1)"
fi
EMPTIED="$(helm template "${DIR}/rediensiam" --set rediensiam.publicUrl="" 2>&1 || true)"
if [[ "${EMPTIED}" == *'rediensiam.publicUrl is required'* ]]; then
  pass "emptying a required value names the key"
else
  fail "emptying a required value names the key" "${EMPTIED%%$'\n'*}"
fi

# The default render is what a validator scans for values belonging to one deployment: a hostname,
# an IP or a registry address describes WHERE a service runs and belongs to the infrastructure
# repository, not to the chart. Keeping the check here means the chart fails at home first.
# The canonical RFC 1918 blocks are dropped before the search. A chart that denies egress to
# 192.168.0.0/16, or lets Hydra accept TLS termination from it, is describing private address space
# in general — the opposite of naming one network. Only a host inside such a range is a value that
# belongs to an environment.
DEFAULT_RENDER="$(helm template "${DIR}/rediensiam" 2>/dev/null \
                    | sed -E 's#(10\.0\.0\.0/8|172\.16\.0\.0/12|192\.168\.0\.0/16|169\.254\.0\.0/16|100\.64\.0\.0/10|127\.0\.0\.0/8)##g')"
ENV_SPECIFIC=""
for pattern in 'rediens\.net' 'yandee\.fr' '192\.168\.[0-9]' '10\.14[23]\.' 'registry\.'; do
  # Herestring, not a pipe: grep -q exits at the first match, the writer takes a SIGPIPE and
  # pipefail turns the whole pipeline into a failure — which reads here as "pattern not found".
  if grep -qiE "${pattern}" <<<"${DEFAULT_RENDER}"; then
    ENV_SPECIFIC="${ENV_SPECIFIC} ${pattern}"
  fi
done
if [ -z "${ENV_SPECIFIC}" ]; then
  pass "the default render names no particular deployment"
else
  fail "the default render names no particular deployment" "found:${ENV_SPECIFIC}"
fi

# …and the environment files must still render, which is what the placeholders are there to be
# replaced by.
for env in dev prod; do
  if helm template "${DIR}/rediensiam" -f "${DIR}/rediensiam/values.${env}.yaml" >/dev/null 2>&1; then
    pass "helm template renders with values.${env}.yaml"
  else
    fail "helm template renders with values.${env}.yaml" \
         "$(helm template "${DIR}/rediensiam" -f "${DIR}/rediensiam/values.${env}.yaml" 2>&1 | head -1)"
  fi
done

# ── Hydra must send the browser to a page, not to an API ─────────────────────
# hydra.urls.* are browser redirect targets. `logout` pointed at http://<host>/auth/logout, which
# is a controller returning JSON — the same mistake the invite mail made when it linked at the POST
# endpoint instead of the /set-password page. And `error` was never set at all, which is why an
# OAuth2 failure rendered Hydra's own "configuration key urls.error is not set" page.
#
# `login` and `consent` are deliberately not in this loop. /login is an SPA route already, and
# /auth/consent is an API endpoint on purpose: it decides and answers 302 without ever rendering,
# so there is nothing for the browser to display. Adding it here would be a "fix" that breaks it.
#
# The routes the login SPA actually serves are the list in frontend/login/src/App.tsx.
SPA_ROUTES="$(grep -oE '<Route path="[^"]+"' "${ROOT}/frontend/login/src/App.tsx" | sed 's/.*path="//;s/"//')"
for env in dev prod; do
  VALUES="${DIR}/rediensiam/values.${env}.yaml"
  for key in logout error; do
    URL="$(grep -E "^\s+${key}:" "${VALUES}" | head -1 | sed "s/.*${key}:[[:space:]]*//" | tr -d '"' | tr -d ' ')"
    if [ -z "${URL}" ]; then
      fail "hydra.urls.${key} is set in values.${env}.yaml" \
           "unset means Hydra renders its own page instead of ${ROOT##*/}'s"
      continue
    fi
    # Everything after the origin. `logout` and `error` must name a route the SPA renders.
    URL_PATH="/$(printf '%s' "${URL}" | sed -E 's#^[a-z]+://[^/]+/?##')"
    if printf '%s\n' ${SPA_ROUTES} | grep -Fxq "${URL_PATH}"; then
      pass "hydra.urls.${key} (${env}) points at a page the login SPA renders"
    else
      fail "hydra.urls.${key} (${env}) points at a page the login SPA renders" \
           "${URL_PATH} is not a route in frontend/login/src/App.tsx — the browser lands on the API"
    fi
  done
done

# ── a tracked path with a backslash breaks the image build ───────────────────
# `src/bin\Debug` — one directory, literally named with a backslash, left by a Windows-style
# `-o "bin\Debug\net10.0"` on Linux and then swept into a commit by `git add -A`. MSBuild reads the
# backslash as a separator while expanding the SDK's default globs, so `**/*.cs` stayed literal and
# `dotnet publish` failed with CS2021/CS2001 inside the container while the host build was fine.
#
# Tracked is not the test that matters. `docker build` copies the WORKING TREE, so an untracked
# `bin\Debug` breaks the image just as thoroughly — and this check passed the day it happened,
# because the directory was still untracked when it ran and only became tracked a `git add -A`
# later. Walk the tree, not the index.
BACKSLASHED="$(cd "${ROOT}" && find . -name '*\\*' -not -path './.git/*' | head -3)"
if [ -n "${BACKSLASHED}" ]; then
  fail "no path contains a backslash" "${BACKSLASHED}"
else
  pass "no path contains a backslash"
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
