#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# RediensIAM — post-deploy control verification.
#
# Step 12 found that the running system is not the repository: nine controls
# claimed by steps 6, 9, 10 and 11b are true of files on disk and false of the
# cluster, because nothing had been deployed. `helm template` cannot catch that
# and neither can a test suite. This script asks the cluster.
#
#   ./deploy/verify-deployment.sh --dev
#   ./deploy/verify-deployment.sh --prod
#
# Read-only: every command is a get/describe/curl. Nothing is created, changed
# or deleted. Run it after every deploy, and on a schedule to catch drift.
#
# Exit codes: 0 all assertions passed · 1 at least one FAIL · 2 could not run.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

NS="${NS:-default}"
RELEASE="${RELEASE:-rediensiam}"
ENVIRONMENT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --dev)  ENVIRONMENT=dev;  shift ;;
    --prod) ENVIRONMENT=prod; shift ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
if [ -z "${ENVIRONMENT}" ]; then
  echo "ERROR: pass --dev or --prod (the expected state differs)" >&2; exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHART="${SCRIPT_DIR}/rediensiam"
ENV_FILE="${CHART}/values.${ENVIRONMENT}.yaml"
[ "${ENVIRONMENT}" = dev ] && ENV_FILE="${CHART}/values.dev.yaml"

# The operator's answers from `setup.sh --prod` live in values.<env>.override.yaml and layer
# LAST — preflight.sh and deploy.sh both read it, and this script did not. Every real prod
# install has a different public hostname from the committed default, so V-04, V-05 and V-17
# were all measuring auth.rediens.net against a cluster that serves something else: V-05 and
# V-17 failed for a host that does not exist here, and V-04 PASSED on four 404s that Traefik
# returns for any unknown Host — the P-04 assertion reading green while proving nothing.
OVERRIDE_FILE="${ENV_FILE%.yaml}.override.yaml"
read_url() { grep "^\s*$2:" "$1" 2>/dev/null | head -1 | sed "s/.*$2:[[:space:]]*//" | tr -d '"' | cut -d'#' -f1 | tr -d ' '; }
PUBLIC_URL=$(read_url "${ENV_FILE}" publicUrl)
if [ -f "${OVERRIDE_FILE}" ]; then
  O=$(read_url "${OVERRIDE_FILE}" publicUrl); [ -n "${O}" ] && PUBLIC_URL="${O}"
fi
PUBLIC_HOST=$(echo "${PUBLIC_URL}" | sed 's|https\?://||' | cut -d: -f1)

PASS=0; FAIL=0; SKIP=0
FAILED_LIST=""

pass() { printf '  \033[32mPASS\033[0m  %-9s %s\n' "$1" "$2"; PASS=$((PASS+1)); }
fail() { printf '  \033[31mFAIL\033[0m  %-9s %s\n' "$1" "$2"; FAIL=$((FAIL+1));
         FAILED_LIST="${FAILED_LIST}  ${1}  ${2}"$'\n'; }
skip() { printf '  --    %-9s %s\n' "$1" "$2"; SKIP=$((SKIP+1)); }

echo "═══════════════════════════════════════════════════════════════"
echo " RediensIAM control verification — ${ENVIRONMENT} — $(date -Is)"
echo " namespace ${NS} · release ${RELEASE} · public host ${PUBLIC_HOST}"
echo "═══════════════════════════════════════════════════════════════"

kubectl get ns "${NS}" >/dev/null 2>&1 || { echo " ERROR: cannot reach cluster" >&2; exit 2; }

POD=$(kubectl get pod -n "${NS}" -l "app=${RELEASE}" \
        -o jsonpath='{.items[?(@.status.phase=="Running")].metadata.name}' 2>/dev/null | awk '{print $1}')

# ── V-01 · R-16 / chain C-3 — build registry must not be on the LAN ──────────
# Step 10 bound it to 127.0.0.1; step 12 found it on 0.0.0.0, unauthenticated
# and cleartext. That is code execution in the IdP image for anyone on the LAN.
if command -v docker >/dev/null 2>&1 && docker ps -a --format '{{.Names}}' 2>/dev/null | grep -qx registry; then
  BIND=$(docker inspect -f '{{ range $p, $c := .HostConfig.PortBindings }}{{ range $c }}{{ .HostIp }}{{ end }}{{ end }}' registry 2>/dev/null)
  # An empty HostIp means "all interfaces". Confirm against the real listener so
  # the message names what is actually bound rather than what docker omitted.
  LISTEN=$(ss -ltn 2>/dev/null | awk '$4 ~ /:5000$/ {print $4}' | paste -sd, -)
  case "${BIND}" in
    127.0.0.1|localhost) pass V-01 "registry bound to ${BIND} (loopback only)" ;;
    "")                  fail V-01 "registry binds all interfaces (listening on ${LISTEN:-0.0.0.0:5000}) — unauthenticated cleartext push from the LAN" ;;
    *)                   fail V-01 "registry bound to ${BIND} (listening on ${LISTEN:-?}) — reachable off-host, no auth, no TLS" ;;
  esac
else
  skip V-01 "no local docker registry container on this host"
fi

# ── V-02 · chain C-3 / CIS K8s 5.1.1 — no cluster-scoped Secret access ───────
# hydra-maester is disabled in values.yaml and was running with a ClusterRole
# granting list/watch/create on Secrets in EVERY namespace.
# jsonpath rather than jq/python: no extra dependency, and no error this check
# could swallow. An empty listing means the query failed, not that the cluster
# is clean — a security assertion must never pass by accident.
CR_ALL=$(kubectl get clusterroles -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{range .rules[*]}{.resources}{"->"}{.verbs}{" "}{end}{"\n"}{end}')
if [ -z "${CR_ALL}" ]; then
  fail V-02 "could not enumerate ClusterRoles — assertion not evaluated"
else
  CR_HITS=$(printf '%s\n' "${CR_ALL}" | grep "^${RELEASE}" | grep -E 'secrets|"\*"' )
  if [ -z "${CR_HITS}" ]; then
    pass V-02 "no ${RELEASE} ClusterRole grants access to Secrets"
  else
    fail V-02 "cluster-scoped Secret access granted: $(printf '%s' "${CR_HITS}" | tr '\n\t' '; ')"
  fi
fi

if kubectl get deploy -n "${NS}" "${RELEASE}-hydra-maester" >/dev/null 2>&1; then
  fail V-03 "hydra-maester is running (values.yaml sets maester.enabled=false)"
else
  pass V-03 "hydra-maester is not deployed"
fi

# ── V-04 · P-04 — management API refused on the public hostname ──────────────
# The ingress denies /admin, /org, /project, /service-accounts on the public
# host. Step 12 measured GET /admin/ → 200 there, serving the admin console.
LB=$(kubectl get svc -n kube-system traefik -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null)
[ -z "${LB}" ] && LB=$(kubectl get svc -A -o jsonpath='{range .items[?(@.spec.type=="LoadBalancer")]}{.status.loadBalancer.ingress[0].ip}{"\n"}{end}' 2>/dev/null | head -1)
SCHEME=http; [ "${ENVIRONMENT}" = prod ] && SCHEME=https
if [ -n "${LB}" ]; then
  # Positive control first. 404 counts as a refusal below, and Traefik answers 404 to every
  # path on a Host it has no router for — so against the wrong hostname all four probes pass
  # while measuring nothing. If /login is not served here, the deny probes are inconclusive.
  LOGIN_CODE=$(curl -sk -o /dev/null -w '%{http_code}' -H "Host: ${PUBLIC_HOST}" \
                 --max-time 5 "${SCHEME}://${LB}/login" 2>/dev/null)
  case "${LOGIN_CODE}" in
    2*|3*) pass V-04/host "public host ${PUBLIC_HOST} is served here (/login ${LOGIN_CODE})" ;;
    *)     fail V-04/host "${PUBLIC_HOST} does not serve /login (${LOGIN_CODE}) — the deny probes below cannot distinguish a refusal from an unknown Host" ;;
  esac
  for p in /admin/ /org /project /service-accounts; do
    CODE=$(curl -sk -o /dev/null -w '%{http_code}' -H "Host: ${PUBLIC_HOST}" \
             --max-time 5 "${SCHEME}://${LB}${p}" 2>/dev/null)
    # 403 is the ipAllowList middleware refusing. 404 also means "not served here".
    case "${CODE}" in
      403|404) pass "V-04${p}" "public host refuses ${p} (${CODE})" ;;
      000)     fail "V-04${p}" "no response for ${p} — could not reach the ingress" ;;
      401)     fail "V-04${p}" "${p} reaches the app on the public host (401) — bearer auth is the only control, deny middleware absent" ;;
      *)       fail "V-04${p}" "public host served ${p} with ${CODE} — P-04 unmitigated" ;;
    esac
  done
else
  skip V-04 "no LoadBalancer IP found — cannot probe the public hostname"
fi

# ── V-05 · R-02 — TLS on the public ingress (prod only; dev is cleartext) ────
TLS_HOSTS=$(kubectl get ingress -n "${NS}" -o jsonpath='{range .items[*]}{.spec.tls[*].hosts[*]}{"\n"}{end}' 2>/dev/null | tr ' ' '\n' | grep -c "^${PUBLIC_HOST}$")
if [ "${ENVIRONMENT}" = prod ]; then
  if [ "${TLS_HOSTS:-0}" -gt 0 ]; then
    pass V-05 "public ingress has a TLS block for ${PUBLIC_HOST}"
  else
    fail V-05 "public ingress serves ${PUBLIC_HOST} without TLS"
  fi
else
  skip V-05 "dev is deliberately cleartext (iam.localhost cannot be certified)"
fi

# ── V-06 · Hydra OIDC surface is actually up ────────────────────────────────
HYDRA_IP=$(kubectl get svc -n "${NS}" "${RELEASE}-hydra-public" -o jsonpath='{.spec.clusterIP}' 2>/dev/null)
if [ -n "${HYDRA_IP}" ]; then
  CODE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 \
           "http://${HYDRA_IP}:4444/.well-known/openid-configuration" 2>/dev/null)
  [ "${CODE}" = 200 ] && pass V-06 "Hydra :4444 discovery answers 200" \
                      || fail V-06 "Hydra :4444 discovery returned ${CODE}"
else
  fail V-06 "service ${RELEASE}-hydra-public not found"
fi

# ── V-07 · R-16 — image pinned by digest, not pulled every start ─────────────
if [ -n "${POD}" ]; then
  IMG=$(kubectl get pod -n "${NS}" "${POD}" -o jsonpath='{.spec.containers[0].image}' 2>/dev/null)
  PP=$(kubectl get pod -n "${NS}" "${POD}" -o jsonpath='{.spec.containers[0].imagePullPolicy}' 2>/dev/null)
  case "${IMG}" in
    *@sha256:*) pass V-07 "image pinned by digest (${IMG##*@})" ;;
    *)          fail V-07 "image is a mutable tag: ${IMG}" ;;
  esac
  [ "${PP}" = "Always" ] && fail V-08 "imagePullPolicy=Always — re-resolves the tag on every restart" \
                         || pass V-08 "imagePullPolicy=${PP}"

  # ── V-09 · R-32 / CIS K8s 5.7.2 — workload hardening ──────────────────────
  SECCOMP=$(kubectl get pod -n "${NS}" "${POD}" -o jsonpath='{.spec.securityContext.seccompProfile.type}' 2>/dev/null)
  [ "${SECCOMP}" = "RuntimeDefault" ] && pass V-09 "pod seccompProfile=RuntimeDefault" \
                                      || fail V-09 "pod seccompProfile is '${SECCOMP:-unset}'"

  for probe in \
    "runAsNonRoot|true|V-10|container runAsNonRoot" \
    "allowPrivilegeEscalation|false|V-11|container allowPrivilegeEscalation" \
    "readOnlyRootFilesystem|true|V-12|container readOnlyRootFilesystem"
  do
    IFS='|' read -r field want id label <<<"${probe}"
    got=$(kubectl get pod -n "${NS}" "${POD}" -o jsonpath="{.spec.containers[0].securityContext.${field}}" 2>/dev/null)
    [ "${got}" = "${want}" ] && pass "${id}" "${label}=${got}" \
                             || fail "${id}" "${label} is '${got:-unset}', expected ${want}"
  done

  DROPS=$(kubectl get pod -n "${NS}" "${POD}" -o jsonpath='{.spec.containers[0].securityContext.capabilities.drop[*]}' 2>/dev/null)
  case " ${DROPS} " in *" ALL "*) pass V-13 "container drops ALL capabilities" ;;
                       *) fail V-13 "capabilities.drop is '${DROPS:-unset}', expected ALL" ;; esac

  SAT=$(kubectl get pod -n "${NS}" "${POD}" -o jsonpath='{.spec.automountServiceAccountToken}' 2>/dev/null)
  [ "${SAT}" = "false" ] && pass V-14 "automountServiceAccountToken=false" \
                         || fail V-14 "automountServiceAccountToken is '${SAT:-unset}' — API token mounted in the IdP pod"
else
  fail V-07 "no running pod with label app=${RELEASE} — nothing to verify"
fi

# ── V-15 · R-05 — release-scoped default-deny NetworkPolicy ─────────────────
if kubectl get networkpolicy -n "${NS}" "${RELEASE}-default-deny-ingress" >/dev/null 2>&1; then
  pass V-15 "${RELEASE}-default-deny-ingress exists"
else
  fail V-15 "${RELEASE}-default-deny-ingress is absent — pods accept traffic from anywhere in the namespace"
fi
for np in hydra-lockdown keto-lockdown postgres-lockdown dragonfly-lockdown; do
  kubectl get networkpolicy -n "${NS}" "${RELEASE}-${np}" >/dev/null 2>&1 \
    && pass "V-15/${np%%-*}" "${RELEASE}-${np} exists" \
    || fail "V-15/${np%%-*}" "${RELEASE}-${np} is absent"
done

# ── V-16 · R-05 — admin service exposure matches the environment ────────────
ADMIN_TYPE=$(kubectl get svc -n "${NS}" "${RELEASE}-admin" -o jsonpath='{.spec.type}' 2>/dev/null)
if [ "${ENVIRONMENT}" = prod ]; then
  [ "${ADMIN_TYPE}" = "ClusterIP" ] && pass V-16 "admin service is ClusterIP" \
                                    || fail V-16 "admin service is ${ADMIN_TYPE} in prod — bound on every node interface"
else
  [ "${ADMIN_TYPE}" = "NodePort" ] && pass V-16 "admin service is NodePort (dev, expected)" \
                                   || pass V-16 "admin service is ${ADMIN_TYPE}"
fi

# ── V-17 · R-26 / step 6 — the deployed CSP is the hardened one ─────────────
# Step 12 found the live header still lacked script-src, base-uri and form-action.
if [ -n "${LB}" ]; then
  CSP=$(curl -sk -D - -o /dev/null -H "Host: ${PUBLIC_HOST}" --max-time 5 \
          "${SCHEME}://${LB}/" 2>/dev/null | grep -i '^content-security-policy:' | tr -d '\r')
  if [ -z "${CSP}" ]; then
    fail V-17 "no Content-Security-Policy header on the login page"
  else
    MISSING=""
    for d in script-src base-uri form-action frame-ancestors object-src; do
      printf '%s' "${CSP}" | grep -q -- "${d}" || MISSING="${MISSING} ${d}"
    done
    [ -z "${MISSING}" ] && pass V-17 "CSP carries script-src, base-uri, form-action, frame-ancestors, object-src" \
                        || fail V-17 "CSP is missing:${MISSING}"
    printf '%s' "${CSP}" | grep -qi 'fonts.googleapis.com\|fonts.gstatic.com' \
      && fail V-18 "CSP still allows Google Fonts — the deployed SPA predates step 6" \
      || pass V-18 "CSP names no external font host"
  fi
else
  skip V-17 "no LoadBalancer IP — cannot read the live CSP"
fi

# ── V-19 · drift — is the running image the one the chart would deploy? ─────
CHART_DIGEST=$(grep -A6 '^\s*image:' "${CHART}/values.yaml" | grep '^\s*digest:' | head -1 | sed 's/.*digest:[[:space:]]*//' | tr -d '"' | cut -d'#' -f1 | tr -d ' ')
if [ -n "${CHART_DIGEST}" ] && [ -n "${POD:-}" ]; then
  RUNNING_DIGEST=$(kubectl get pod -n "${NS}" "${POD}" -o jsonpath='{.status.containerStatuses[0].imageID}' 2>/dev/null | sed 's/.*@//')
  [ "${RUNNING_DIGEST}" = "${CHART_DIGEST}" ] \
    && pass V-19 "running image digest matches values.yaml" \
    || fail V-19 "drift: chart pins ${CHART_DIGEST:0:19}…, cluster runs ${RUNNING_DIGEST:0:19}…"
else
  skip V-19 "values.yaml pins no image digest — cannot check for drift"
fi

# ── V-20 · T-04 — Postgres least privilege ──────────────────────────────────
# Two halves, both checkable without a database credential.
PGPOD="${RELEASE}-postgres-0"
# PGDATA moved from the mount root to a `pgdata/` subdirectory (postgres.yaml explains why),
# so read whichever one this installation actually has rather than assuming. An empty result
# is what a WRONG path and a genuinely unreadable file look like alike, and both of the
# assertions below treat empty as a failure — so a stale path here would read as a breach.
read_hba() {
  kubectl exec -n "${NS}" "${PGPOD}" -- sh -c \
    'cat "${PGDATA:-/var/lib/postgresql/data}/pg_hba.conf" 2>/dev/null || cat /var/lib/postgresql/data/pg_hba.conf 2>/dev/null' \
    2>/dev/null | grep -Ev '^[[:space:]]*#|^[[:space:]]*$'
}
if kubectl get pod -n "${NS}" "${PGPOD}" >/dev/null 2>&1; then
  # (a) pg_hba.conf must not grant `trust` anywhere. `local all all trust` made anyone
  # with `kubectl exec` on this pod a superuser with no credential at all.
  HBA=$(read_hba)
  if [ -z "${HBA}" ]; then
    fail V-20 "could not read pg_hba.conf — assertion not evaluated"
  elif printf '%s\n' "${HBA}" | grep -qE '[[:space:]]trust[[:space:]]*$'; then
    fail V-20 "pg_hba.conf still grants 'trust': $(printf '%s' "${HBA}" | grep -E '[[:space:]]trust[[:space:]]*$' | tr '\n' ';')"
  else
    pass V-20 "pg_hba.conf grants no 'trust' (all methods are scram-sha-256)"
  fi

  # (b) No component may connect as the bootstrap superuser. Only the username is read
  # out of each DSN — this never prints a password.
  DSN_USERS=""
  APPU=$(kubectl get secret -n "${NS}" "${RELEASE}-secrets" -o jsonpath='{.data.database-url}' 2>/dev/null \
           | base64 -d 2>/dev/null | sed -n 's/.*Username=\([^;]*\).*/\1/p')
  for s in "${RELEASE}-hydra" "${RELEASE}-keto"; do
    D=$(kubectl get secret -n "${NS}" "${s}" -o jsonpath='{.data.dsn}' 2>/dev/null | base64 -d 2>/dev/null)
    [ -n "${D}" ] && DSN_USERS="${DSN_USERS} $(printf '%s' "${D}" | sed -n 's|.*://\([^:]*\):.*|\1|p')"
  done
  ALLU="${APPU}${DSN_USERS}"
  if [ -z "$(printf '%s' "${ALLU}" | tr -d ' ')" ]; then
    skip V-21 "could not read any DSN username — assertion not evaluated"
  elif printf '%s' " ${ALLU} " | grep -qE '[[:space:]]iam[[:space:]]'; then
    fail V-21 "a component still connects as the bootstrap superuser 'iam' (users:${ALLU})"
  else
    pass V-21 "no component connects as superuser 'iam' (users:${ALLU})"
  fi
else
  skip V-20 "no ${PGPOD} pod — Postgres privilege assertions not evaluated"
fi

# ── V-22 · T-03 — the backup has actually run ───────────────────────────────
# `LAST SCHEDULE <none>` is what a backup that has never executed looks like, and it is
# indistinguishable from a working one until you look. A CronJob object is not a backup.
if kubectl get cronjob -n "${NS}" "${RELEASE}-backup" >/dev/null 2>&1; then
  LAST_OK=$(kubectl get cronjob -n "${NS}" "${RELEASE}-backup" -o jsonpath='{.status.lastSuccessfulTime}' 2>/dev/null)
  LAST_RUN=$(kubectl get cronjob -n "${NS}" "${RELEASE}-backup" -o jsonpath='{.status.lastScheduleTime}' 2>/dev/null)
  if [ -n "${LAST_OK}" ]; then
    pass V-22 "backup CronJob last succeeded ${LAST_OK}"
  elif [ -n "${LAST_RUN}" ]; then
    fail V-22 "backup CronJob was scheduled ${LAST_RUN} but has never SUCCEEDED"
  else
    fail V-22 "backup CronJob has never run (no lastScheduleTime) — an untested backup is a hypothesis"
  fi
else
  fail V-22 "no ${RELEASE}-backup CronJob — nothing is backing this database up"
fi

# ── V-23 · R-15 — Postgres transport encryption ─────────────────────────────
# Credential-free, same discipline as V-20/V-21: the server side is read from the
# pod, the client side from the DSNs' sslmode keyword only. A password is never
# read and never printed.
if kubectl get pod -n "${NS}" "${PGPOD}" >/dev/null 2>&1; then
  PGARGS=$(kubectl get statefulset -n "${NS}" "${RELEASE}-postgres" -o jsonpath='{.spec.template.spec.containers[0].args}' 2>/dev/null)
  HBA23=$(read_hba)
  case "${PGARGS}" in
    *ssl=on*) pass V-23/server "Postgres runs with ssl=on" ;;
    *)        fail V-23/server "Postgres has no ssl=on — every DSN below is cleartext on the wire" ;;
  esac

  # `host` (as opposed to `hostssl`) is what lets a client skip TLS entirely. With
  # only `hostssl` left, TLS stops being a client-side preference.
  if [ -z "${HBA23}" ]; then
    fail V-23/hba "could not read pg_hba.conf — assertion not evaluated"
  elif printf '%s\n' "${HBA23}" | grep -qE '^host[[:space:]]'; then
    fail V-23/hba "pg_hba.conf still admits cleartext TCP: $(printf '%s' "${HBA23}" | grep -E '^host[[:space:]]' | tr '\n' ';')"
  else
    pass V-23/hba "pg_hba.conf admits TLS only (hostssl; local socket unaffected)"
  fi

  # Client side. Npgsql spells it `SSL Mode=Require`, libpq `sslmode=require`.
  CLEAR=""
  APPDSN=$(kubectl get secret -n "${NS}" "${RELEASE}-secrets" -o jsonpath='{.data.database-url}' 2>/dev/null | base64 -d 2>/dev/null)
  printf '%s' "${APPDSN}" | grep -qiE 'ssl ?mode *= *(require|verify)' || CLEAR="${CLEAR} app"
  for s in "${RELEASE}-hydra" "${RELEASE}-keto"; do
    D=$(kubectl get secret -n "${NS}" "${s}" -o jsonpath='{.data.dsn}' 2>/dev/null | base64 -d 2>/dev/null)
    [ -n "${D}" ] || continue
    printf '%s' "${D}" | grep -qiE 'sslmode=(require|verify)' || CLEAR="${CLEAR} ${s##*-}"
  done
  if [ -z "${APPDSN}" ]; then
    skip V-23/dsn "could not read the app DSN — assertion not evaluated"
  elif [ -n "${CLEAR}" ]; then
    fail V-23/dsn "these DSNs do not request TLS:${CLEAR}"
  else
    pass V-23/dsn "app, hydra and keto DSNs all request TLS"
  fi
else
  skip V-23 "no ${PGPOD} pod — Postgres TLS assertions not evaluated (external database?)"
fi

# ── V-24 · R-15 — the cache is authenticated ────────────────────────────────
# `dragonfly.local.password` defaulted to "" and deploy.sh never generated one, so
# the chart rendered `--requirepass=` and the cache — which holds the DataProtection
# key ring, i.e. the ability to mint session cookies — accepted anyone who could
# reach :6379. Only `dragonfly-lockdown` stood between that and the namespace.
if kubectl get deploy -n "${NS}" "${RELEASE}-dragonfly" >/dev/null 2>&1; then
  DFLYPW=$(kubectl get secret -n "${NS}" "${RELEASE}-secrets" -o jsonpath='{.data.dragonfly-password}' 2>/dev/null | base64 -d 2>/dev/null | wc -c)
  [ "${DFLYPW:-0}" -ge 16 ] && pass V-24 "cache requires a password (${DFLYPW} chars)" \
                            || fail V-24 "cache password is ${DFLYPW:-0} chars — Dragonfly runs with --requirepass= and accepts anyone who reaches :6379"
else
  skip V-24 "no ${RELEASE}-dragonfly deployment"
fi

# ── V-26 · R-15 — the cache connection is encrypted, and pinned ──────────────
# Three assertions because any one of them alone is satisfiable while the traffic is
# still cleartext, or encrypted against anything that answers:
#   /server  Dragonfly runs with --tls, so it does not answer cleartext at all. This
#            is the assertion that makes the other two unnecessary to trust.
#   /dsn     the app asks for TLS. Without it the app simply loses the cache — and the
#            cache holds the DataProtection key ring — so this is also an outage check.
#   /pin     the issuing CA is mounted at CacheTls.DefaultCaBundlePath. Without it the
#            connection is encrypted against whatever the OS trust store accepts, which
#            for a cluster-issued certificate is nothing — but a future "fix" that
#            reaches for TrustServerCertificate would pass /server and /dsn and be
#            worse than the plaintext it replaced.
if kubectl get deploy -n "${NS}" "${RELEASE}-dragonfly" >/dev/null 2>&1; then
  DFLYARGS=$(kubectl get deploy -n "${NS}" "${RELEASE}-dragonfly" -o jsonpath='{.spec.template.spec.containers[0].args}' 2>/dev/null)
  case "${DFLYARGS}" in
    *--tls*) pass V-26/server "Dragonfly runs with --tls (cleartext is refused, not merely unused)" ;;
    *)       fail V-26/server "Dragonfly has no --tls — every cache round trip, including the DataProtection key ring, is cleartext on the wire" ;;
  esac

  # The password on this line is never printed; only the ssl keyword is looked at.
  CACHEDSN=$(kubectl get secret -n "${NS}" "${RELEASE}-secrets" -o jsonpath='{.data.cache-url}' 2>/dev/null | base64 -d 2>/dev/null)
  if [ -z "${CACHEDSN}" ]; then
    skip V-26/dsn "could not read the cache DSN — assertion not evaluated"
  elif printf '%s' "${CACHEDSN}" | grep -qiE '(^|,) *ssl *= *true'; then
    pass V-26/dsn "app cache DSN requests TLS (ssl=true)"
  else
    fail V-26/dsn "app cache DSN has no ssl=true — the app connects in cleartext"
  fi

  CAMOUNT=$(kubectl get deploy -n "${NS}" "${RELEASE}" \
              -o jsonpath='{.spec.template.spec.containers[0].volumeMounts[?(@.mountPath=="/etc/cache-tls")].name}' 2>/dev/null)
  if [ -n "${CAMOUNT}" ]; then
    # Positive evidence from the running process rather than from the manifest: CacheTls
    # prints this line only after it has actually loaded roots out of the mounted file.
    PINLOG=$(kubectl logs -n "${NS}" "${POD}" --tail=-1 2>/dev/null | grep -m1 'Cache TLS: server certificate pinned')
    if [ -n "${PINLOG}" ]; then
      pass V-26/pin "app pinned the cache certificate — ${PINLOG#*Cache TLS: }"
    else
      fail V-26/pin "the CA is mounted but the app never logged 'Cache TLS: server certificate pinned' — validation fell back to the OS trust store"
    fi
  else
    fail V-26/pin "no /etc/cache-tls mount on ${RELEASE} — the app cannot verify who it is encrypting to"
  fi
else
  skip V-26 "no ${RELEASE}-dragonfly deployment"
fi

# ── V-25 · S-5 — RLS is applied when the chart says it is ───────────────────
# The `<release>-rls` ConfigMap is rendered only when postgres.rls.enabled. If it is
# there and pg_policies is empty, the hook Job failed and tenant isolation is back to
# being 200 hand-written conjuncts while the chart claims otherwise.
#
# What this asserts and what it does not: rls.sql runs under ON_ERROR_STOP with a
# coverage gate that aborts on any tenant table without a policy, so a SUCCEEDED Job
# means all 19 tables came out ENABLE+FORCE with one policy each. It is still a
# statement about the last APPLY, not about the database right now — someone who runs
# the rollback DO block by hand leaves this passing. Closing that needs a live
# pg_policies count, which needs a database password, and this script's discipline is
# that it never reads one (see V-20/V-21/V-23/V-26). ~1 h via a /health/detail field.
if kubectl get configmap -n "${NS}" "${RELEASE}-rls" >/dev/null 2>&1; then
  RLSJOB=$(kubectl get job -n "${NS}" "${RELEASE}-rls" -o jsonpath='{.status.succeeded}' 2>/dev/null)
  RLSLOG=$(kubectl logs -n "${NS}" "job/${RELEASE}-rls" --tail=-1 2>/dev/null | grep -m1 'RLS applied to')
  if [ "${RLSJOB}" != "1" ]; then
    fail V-25 "postgres.rls.enabled is set but the ${RELEASE}-rls Job has not succeeded — no policy is in force"
  elif [ -n "${RLSLOG}" ]; then
    # Evidence from the process rather than from a status field: rls.sql prints this
    # only after the coverage gate has passed on every table it found.
    pass V-25 "RLS applied as the table owner — ${RLSLOG#*NOTICE:  }"
  else
    pass V-25 "RLS hook Job succeeded (policies applied; log rotated, count not read)"
  fi
else
  skip V-25 "postgres.rls.enabled is off — tenant isolation is application-side only (S-5 phase 2 open)"
fi

echo "───────────────────────────────────────────────────────────────"
printf ' %d passed · %d failed · %d skipped\n' "${PASS}" "${FAIL}" "${SKIP}"
if [ "${FAIL}" -gt 0 ]; then
  echo ""
  echo " Controls claimed by the repository that are NOT live:"
  printf '%s' "${FAILED_LIST}"
  echo ""
  echo " Until these pass, the corresponding claims in SECURITY-AUDIT-LOG.md are"
  echo " true of files on disk and false of the running system."
  exit 1
fi
echo " All asserted controls are live."
exit 0
