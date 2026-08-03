#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# RediensIAM — pre-install checks.
#
#   ./deploy/preflight.sh --dev
#   ./deploy/preflight.sh --prod
#   ./deploy/preflight.sh --dev --install-cert-manager
#
# Asks whether this host and this cluster can actually run the chart, BEFORE
# anything is built or applied. Every failure names the thing to fix.
#
# Why this exists: finding D was a one-line config mismatch that crash-looped
# deploys for three days. Every check below is a condition that produces a
# broken-but-plausible cluster rather than an error at `helm upgrade` time.
#
# Read-only except for --install-cert-manager, which is the one thing that is
# both required and mechanical.
#
# Exit codes: 0 ready · 1 at least one FAIL · 2 could not run.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

ENVIRONMENT=""
INSTALL_CM=false
NS="${NS:-default}"
RELEASE="${RELEASE:-rediensiam}"
CERT_MANAGER_VERSION="${CERT_MANAGER_VERSION:-v1.21.1}"

while [ $# -gt 0 ]; do
  case "$1" in
    --dev)  ENVIRONMENT=dev;  shift ;;
    --prod) ENVIRONMENT=prod; shift ;;
    --install-cert-manager) INSTALL_CM=true; shift ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -z "${ENVIRONMENT}" ] && { echo "ERROR: pass --dev or --prod (requirements differ)" >&2; exit 2; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
CHART="${SCRIPT_DIR}/rediensiam"
ENV_FILE="${CHART}/values.${ENVIRONMENT}.yaml"
OVERRIDE_FILE="${CHART}/values.${ENVIRONMENT}.override.yaml"
if [ "${ENVIRONMENT}" = prod ]; then
  SECRETS_FILE="${CHART}/values.prod.secret.yaml"
else
  SECRETS_FILE="${CHART}/values.secret.yaml"
fi

# The three checks below used to grep values.yaml. An operator with a
# values.<env>.override.yaml changing the ingress class or the trusted proxies had those
# overrides checked against the committed defaults — the check passed for a value the deploy
# would not use. Render the chart through the same file chain instead and read the manifests
# that will actually be applied. No secrets are needed: nothing read here comes from them.
RENDERED="$(mktemp)"
trap 'rm -f "${RENDERED}"' EXIT
RENDER_ARGS=(-f "${CHART}/values.yaml" -f "${ENV_FILE}")
[ -f "${SECRETS_FILE}" ]  && RENDER_ARGS+=(-f "${SECRETS_FILE}")
[ -f "${OVERRIDE_FILE}" ] && RENDER_ARGS+=(-f "${OVERRIDE_FILE}")
if ! helm template "${RELEASE}" "${CHART}" --namespace "${NS}" "${RENDER_ARGS[@]}" >"${RENDERED}" 2>/dev/null; then
  : >"${RENDERED}"   # unrenderable chart is its own check further down; do not abort here
fi

PASS=0; FAIL=0; WARN=0
FAILED_LIST=""

ok()   { printf '  \033[32mOK\033[0m    %s\n' "$1"; PASS=$((PASS+1)); }
bad()  { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; FAIL=$((FAIL+1))
         FAILED_LIST="${FAILED_LIST}  ${1}"$'\n'
         [ $# -gt 1 ] && printf '        → %s\n' "$2" && FAILED_LIST="${FAILED_LIST}      → ${2}"$'\n'
         return 0; }
warn() { printf '  \033[33mWARN\033[0m  %s\n' "$1"; WARN=$((WARN+1))
         [ $# -gt 1 ] && printf '        → %s\n' "$2"; return 0; }

echo "═══════════════════════════════════════════════════════════════"
echo " RediensIAM preflight — ${ENVIRONMENT} — $(date -Is)"
echo " namespace ${NS} · release ${RELEASE}"
echo "═══════════════════════════════════════════════════════════════"

# ── Host tools ───────────────────────────────────────────────────────────────
echo ""
echo "──── Host tools ────────────────────────────────────────────────"

need() {
  local bin="$1" hint="$2"
  command -v "${bin}" >/dev/null 2>&1 && return 0
  bad "${bin} not found on PATH" "${hint}"
  return 1
}

need kubectl "install kubectl matching your cluster: https://kubernetes.io/docs/tasks/tools/" \
  && ok "kubectl $(kubectl version --client -o json 2>/dev/null | grep -oE '"gitVersion":[[:space:]]*"[^"]*"' | head -1 | cut -d'"' -f4)"

if need helm "install Helm 3.12+: https://helm.sh/docs/intro/install/"; then
  HELM_V=$(helm version --short 2>/dev/null | sed 's/^v//;s/+.*//')
  HELM_MAJ=${HELM_V%%.*}; HELM_MIN=$(printf '%s' "${HELM_V}" | cut -d. -f2)
  if [ "${HELM_MAJ:-0}" -gt 3 ] || { [ "${HELM_MAJ:-0}" -eq 3 ] && [ "${HELM_MIN:-0}" -ge 12 ]; }; then
    ok "helm ${HELM_V}"
  else
    bad "helm ${HELM_V} is too old" "the chart needs Helm 3.12+ (lookup functions, --set list syntax)"
  fi
fi

if need docker "install Docker: the deploy builds and pushes the app image"; then
  if docker info >/dev/null 2>&1; then
    ok "docker daemon reachable ($(docker --version | cut -d, -f1))"
  else
    bad "docker is installed but the daemon is not reachable" \
        "start it (systemctl start docker) or add \$USER to the 'docker' group and re-login"
  fi
fi

# deploy.sh builds both SPAs with npm before the image, so node is a hard build
# dependency even though nothing in the cluster needs it.
if need node "install Node 20+: deploy.sh builds the login and admin SPAs with npm"; then
  NODE_MAJ=$(node --version | sed 's/^v//' | cut -d. -f1)
  [ "${NODE_MAJ:-0}" -ge 20 ] && ok "node $(node --version)" \
    || bad "node $(node --version) is too old" "the SPAs need Node 20+"
fi
need npm "npm ships with Node; reinstall Node" && ok "npm $(npm --version)"
need openssl "openssl generates every credential this install uses" && ok "openssl present"
need curl "curl is used for the registry and smoke checks" && ok "curl present"

# ── Cluster reachability ─────────────────────────────────────────────────────
echo ""
echo "──── Cluster ───────────────────────────────────────────────────"

if ! kubectl get nodes --request-timeout=10s >/dev/null 2>&1; then
  bad "cannot reach the Kubernetes API" \
      "check KUBECONFIG (currently '${KUBECONFIG:-~/.kube/config}') and that the cluster is up"
else
  READY=$(kubectl get nodes --no-headers 2>/dev/null | awk '$2 ~ /^Ready/ {n++} END {print n+0}')
  TOTAL=$(kubectl get nodes --no-headers 2>/dev/null | wc -l)
  [ "${READY}" -gt 0 ] && ok "cluster reachable, ${READY}/${TOTAL} node(s) Ready" \
                       || bad "no node is Ready" "kubectl get nodes -o wide"

  kubectl get ns "${NS}" >/dev/null 2>&1 \
    && ok "namespace ${NS} exists" \
    || bad "namespace ${NS} does not exist" "kubectl create namespace ${NS}"

  # A default StorageClass. Postgres, the backup PVC and (in dev) nothing else
  # claim storage with no storageClassName, so without a default they stay
  # Pending forever and the only symptom is a pod that never schedules.
  DEFAULT_SC=$(kubectl get storageclass -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.metadata.annotations.storageclass\.kubernetes\.io/is-default-class}{"\n"}{end}' 2>/dev/null \
                 | awk -F'\t' '$2=="true"{print $1}' | head -1)
  if [ -n "${DEFAULT_SC}" ]; then
    ok "default StorageClass: ${DEFAULT_SC}"
  else
    bad "no default StorageClass" \
        "the Postgres and backup PVCs request no class and will stay Pending. k3s: enable local-path. Otherwise: kubectl annotate sc <name> storageclass.kubernetes.io/is-default-class=true"
  fi

  # Ingress controller. The chart hardcodes ingressClassName from values.
  ING_CLASS=$(grep -E '^\s+ingressClassName:' "${RENDERED}" | head -1 | sed 's/.*ingressClassName:[[:space:]]*//' | tr -d '"' | tr -d ' ')
  if kubectl get ingressclass "${ING_CLASS}" >/dev/null 2>&1; then
    ok "IngressClass ${ING_CLASS} exists"
  else
    bad "IngressClass '${ING_CLASS}' not found" \
        "rediensiam.ingress.className in values.yaml must name a controller this cluster runs (kubectl get ingressclass)"
  fi

  # Traefik Middleware CRD. The public router's rate limit, body cap and the
  # P-04 management-API deny are all Middleware objects. Without the CRD helm
  # fails on apply — but only after the image has been built and pushed.
  if kubectl get crd middlewares.traefik.io >/dev/null 2>&1; then
    ok "Traefik Middleware CRD present (rate limit, body cap, P-04 deny)"
  else
    bad "CRD middlewares.traefik.io is absent" \
        "the public ingress renders Traefik Middlewares. Install Traefik's CRDs, or set rediensiam.ingress.public.rateLimit.enabled=false, maxBodyBytes=0 and adminOnlyPaths=[] — which drops the P-04 management-API deny. Do not do that on a public host."
  fi

  # App__TrustedProxies must contain the pod network or Program.cs refuses to
  # start (empty) / the app trusts the wrong hop (wrong CIDR). The default is
  # the k3s pod+service CIDR; any other cluster needs it changed.
  TP=$(grep -A1 -E 'name: App__TrustedProxies' "${RENDERED}" | grep -E '^\s+value:' | head -1 | sed 's/.*value:[[:space:]]*//' | tr -d '"' | tr -d ' ')
  POD_CIDRS=$(kubectl get nodes -o jsonpath='{range .items[*]}{.spec.podCIDR}{"\n"}{end}' 2>/dev/null | grep -v '^$')
  if [ -z "${TP}" ]; then
    bad "rediensiam.app.trustedProxies is empty" "Program.cs refuses to start rather than silently trusting RFC1918"
  elif [ -z "${POD_CIDRS}" ]; then
    warn "cluster reports no node .spec.podCIDR — cannot check trustedProxies (${TP})" \
         "confirm by hand that it covers your pod network"
  else
    MISSED=""
    while read -r cidr; do
      # /16-prefix comparison: enough to catch 10.42.x vs 10.244.x, which is the
      # mistake that actually happens (k3s vs kubeadm/flannel defaults).
      base=$(printf '%s' "${cidr}" | cut -d. -f1,2)
      printf '%s' "${TP}" | grep -q "${base}\." || MISSED="${MISSED} ${cidr}"
    done <<<"${POD_CIDRS}"
    [ -z "${MISSED}" ] && ok "trustedProxies covers the pod network (${TP})" \
      || bad "trustedProxies '${TP}' does not cover pod CIDR(s):${MISSED}" \
             "set rediensiam.app.trustedProxies in values.yaml. A wrong value means X-Forwarded-For is untrusted and every IP-based control (rate limit, admin allowlist, audit source IP) reads the ingress pod's address."
  fi

  # defaultDenyScope: namespace cuts off every pod in the namespace that has no
  # policy of its own — including neighbours this release did not install.
  SCOPE=release
grep -A3 -E 'name: .*-default-deny-ingress' "${RENDERED}" | grep -qE '^\s+\{\}\s*$' && SCOPE=namespace
  if [ "${SCOPE}" = "namespace" ]; then
    FOREIGN=$(kubectl get pods -n "${NS}" --no-headers -o custom-columns=':metadata.name' 2>/dev/null \
                | grep -v "^${RELEASE}" | tr '\n' ' ' | sed 's/ *$//')
    [ -z "${FOREIGN}" ] && ok "namespace ${NS} holds this release only (defaultDenyScope=namespace is safe)" \
      || bad "defaultDenyScope=namespace but ${NS} also holds: ${FOREIGN}" \
             "a namespace-wide default-deny is an outage for any neighbour with no NetworkPolicy of its own. Set rediensiam.networkPolicy.defaultDenyScope=release, or install into a namespace of your own (NS=rediensiam)."
  else
    ok "defaultDenyScope=${SCOPE} (release-scoped deny)"
  fi
fi

# ── cert-manager ─────────────────────────────────────────────────────────────
# Needed whenever the rendered chart contains a cert-manager object. Rendering
# is the only honest way to answer that: there are three separate `tls:` blocks
# across these files and grepping the wrong one is how a check becomes a lie.
echo ""
echo "──── cert-manager ──────────────────────────────────────────────"

render() {
  local extra=()
  if [ -f "${SECRETS_FILE}" ]; then
    extra=(-f "${SECRETS_FILE}")
  else
    # No secrets file yet (fresh install — deploy.sh generates it). Feed the
    # template placeholders that satisfy the chart's TLS/auth guards so the
    # render exercises the same branches the real deploy will.
    extra=(
      --set 'rediensiam.secrets.databaseUrl=Host=p;Database=d;Username=u;Password=x;SSL Mode=Require'
      --set 'hydra.hydra.config.dsn=postgres://u:x@p:5432/hydra?sslmode=require'
      --set 'keto.keto.config.dsn=postgres://u:x@p:5432/keto?sslmode=require'
      --set 'rediensiam.dragonfly.local.password=placeholder'
      --set 'hydra.hydra.config.secrets.system={placeholder}'
    )
  fi
  local files=(-f "${CHART}/values.yaml" -f "${ENV_FILE}")
  [ -f "${OVERRIDE_FILE}" ] && files+=(-f "${OVERRIDE_FILE}")
  helm template "${RELEASE}" "${CHART}" --namespace "${NS}" "${files[@]}" "${extra[@]}" 2>&1
}

RENDER_OUT=""
if [ -d "${CHART}/charts" ] || [ -f "${CHART}/Chart.lock" ]; then
  RENDER_OUT="$(render)"
  RENDER_RC=$?
else
  RENDER_RC=0
fi

NEEDS_CM=false
RENDER_OK=true
if [ ${RENDER_RC} -ne 0 ]; then
  RENDER_OK=false
  # Not fatal on its own: subchart tarballs may not be fetched yet on a fresh
  # clone, and deploy.sh runs `helm dependency build` before it templates.
  if printf '%s' "${RENDER_OUT}" | grep -q 'found in Chart.yaml, but missing in charts/'; then
    warn "chart dependencies not fetched yet — skipping the render check" \
         "deploy.sh runs 'helm dependency build' first; re-run preflight after it, or run: helm dependency build ${CHART}"
    NEEDS_CM=true   # every shipped values file turns on Postgres TLS
  else
    bad "the chart does not render with your values" \
        "$(printf '%s' "${RENDER_OUT}" | grep -iE 'error|fail' | head -3 | tr '\n' ' ')"
  fi
else
  ok "chart renders with values.yaml + $(basename "${ENV_FILE}")$([ -f "${OVERRIDE_FILE}" ] && printf ' + %s' "$(basename "${OVERRIDE_FILE}")")"
  printf '%s' "${RENDER_OUT}" | grep -q 'cert-manager.io/' && NEEDS_CM=true
fi

install_cert_manager() {
  echo "  Installing cert-manager ${CERT_MANAGER_VERSION}…"
  helm repo add jetstack https://charts.jetstack.io --force-update >/dev/null 2>&1
  helm repo update jetstack >/dev/null 2>&1
  helm upgrade --install cert-manager jetstack/cert-manager \
    --namespace cert-manager --create-namespace \
    --version "${CERT_MANAGER_VERSION}" --set crds.enabled=true \
    --wait --timeout 5m
}

CM_READY=false
if kubectl get crd certificates.cert-manager.io >/dev/null 2>&1; then
  # The CRD alone is not enough: the chart creates Certificate objects, and a
  # webhook that is not serving rejects them with a connection error at apply.
  if kubectl -n cert-manager get deploy cert-manager-webhook \
       -o jsonpath='{.status.readyReplicas}' 2>/dev/null | grep -qE '^[1-9]'; then
    CM_READY=true
  fi
fi

if [ "${RENDER_OK}" != true ] && [ "${NEEDS_CM}" != true ]; then
  # The render failed for a reason other than missing subcharts, so nothing is
  # known about what the chart would create. Saying "not required" here would be
  # a check that reads correctly and never ran.
  warn "cert-manager requirement not evaluated — fix the render error above first"
elif [ "${NEEDS_CM}" = true ]; then
  if [ "${CM_READY}" = true ]; then
    ok "cert-manager installed and its webhook is Ready"
  elif [ "${INSTALL_CM}" = true ]; then
    if install_cert_manager; then
      ok "cert-manager ${CERT_MANAGER_VERSION} installed"
    else
      bad "cert-manager install failed" "see the helm output above"
    fi
  else
    bad "this configuration needs cert-manager and it is not ready" \
        "the rendered chart contains cert-manager objects (Postgres TLS, admin ingress, or ACME). Install it: ./deploy/preflight.sh --${ENVIRONMENT} --install-cert-manager"
  fi
else
  ok "no cert-manager object in the rendered chart — not required"
fi

# ── Registry ─────────────────────────────────────────────────────────────────
echo ""
echo "──── Image registry ────────────────────────────────────────────"
REG_BIND=127.0.0.1
if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  if docker ps -a --format '{{.Names}}' | grep -qx registry; then
    BIND=$(docker inspect -f '{{ range $p, $c := .HostConfig.PortBindings }}{{ range $c }}{{ .HostIp }}{{ end }}{{ end }}' registry 2>/dev/null)
    case "${BIND}" in
      127.0.0.1|localhost) ok "registry container present, bound to ${BIND}" ;;
      *) warn "registry container is bound to '${BIND:-0.0.0.0}'" \
              "deploy.sh will recreate it on 127.0.0.1 (R-16). Images survive — they live in the named volume." ;;
    esac
  else
    # Something else on :5000 means deploy.sh's `docker run -p 5000` fails with a
    # port conflict after the SPAs have already been built.
    if command -v ss >/dev/null 2>&1 && ss -ltn 2>/dev/null | awk '{print $4}' | grep -q ':5000$'; then
      bad "TCP :5000 is already in use by something that is not the registry container" \
          "$(ss -ltnp 2>/dev/null | grep ':5000' | head -1 | sed 's/^/    /'). Free the port or change REGISTRY in deploy/deploy.sh."
    else
      ok "no registry container yet — deploy.sh will create it on ${REG_BIND}:5000"
    fi
  fi
else
  warn "docker unavailable — registry not checked"
fi

# ── Secrets file ─────────────────────────────────────────────────────────────
echo ""
echo "──── Credentials ───────────────────────────────────────────────"
KNOWN_DEFAULTS='changeme|CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS|Admin1234!'
if [ ! -f "${SECRETS_FILE}" ]; then
  if [ "${ENVIRONMENT}" = prod ]; then
    warn "$(basename "${SECRETS_FILE}") does not exist" \
         "deploy.sh --prod will prompt for the bootstrap admin and generate every other credential"
  else
    ok "no $(basename "${SECRETS_FILE}") yet — deploy.sh --dev generates one per machine"
  fi
else
  MODE=$(stat -c %a "${SECRETS_FILE}")
  [ "${MODE}" = 600 ] && ok "$(basename "${SECRETS_FILE}") exists (mode ${MODE})" \
                      || warn "$(basename "${SECRETS_FILE}") is mode ${MODE}" "deploy.sh will chmod it to 600"
  if grep -Eq "${KNOWN_DEFAULTS}" "${SECRETS_FILE}"; then
    MSG="$(basename "${SECRETS_FILE}") still holds a credential this repo shipped as a default — every copy of the repo knows it"
    if [ "${ENVIRONMENT}" = prod ]; then
      bad "${MSG}" "deploy.sh --prod refuses this. Regenerate, then migrate state per SECURITY-AUDIT-LOG.md step 10 §4."
    else
      warn "${MSG}" "re-roll with ./deploy/reset-dev.sh (destroys dev DB state) — dev data is disposable"
    fi
  else
    ok "no known-default credential in $(basename "${SECRETS_FILE}")"
  fi
  grep -q 'appPassword:' "${SECRETS_FILE}" \
    && ok "secrets file carries the T-04 four-role Postgres split" \
    || bad "secrets file predates the T-04 Postgres role split" \
           "every component would connect as the superuser 'iam'. Migration: SECURITY-AUDIT-LOG.md step 15c §T-04. Dev: ./deploy/reset-dev.sh"
fi

# ── Prod-only: decisions that must not be defaults ───────────────────────────
if [ "${ENVIRONMENT}" = prod ]; then
  echo ""
  echo "──── Production decisions ──────────────────────────────────────"
  PUB=$(grep '^\s*publicUrl:' "${ENV_FILE}" | head -1 | sed 's/.*publicUrl:[[:space:]]*//' | tr -d '"' | cut -d'#' -f1 | tr -d ' ')
  ADM=$(grep '^\s*adminUrl:' "${ENV_FILE}" | head -1 | sed 's/.*adminUrl:[[:space:]]*//' | tr -d '"' | cut -d'#' -f1 | tr -d ' ')
  [ -f "${OVERRIDE_FILE}" ] && {
    P2=$(grep '^\s*publicUrl:' "${OVERRIDE_FILE}" | head -1 | sed 's/.*publicUrl:[[:space:]]*//' | tr -d '"' | tr -d ' ')
    A2=$(grep '^\s*adminUrl:'  "${OVERRIDE_FILE}" | head -1 | sed 's/.*adminUrl:[[:space:]]*//'  | tr -d '"' | tr -d ' ')
    [ -n "${P2}" ] && PUB="${P2}"; [ -n "${A2}" ] && ADM="${A2}"
  }
  case "${PUB}" in
    ""|*localhost*) bad "publicUrl is '${PUB:-unset}'" "prod needs a real hostname. Run ./deploy/setup.sh --prod, which asks and refuses to guess." ;;
    https://*)      ok "publicUrl ${PUB}" ;;
    *)              bad "publicUrl ${PUB} is not https" "R-02: the public auth surface must be TLS" ;;
  esac
  case "${ADM}" in
    ""|*localhost*) bad "adminUrl is '${ADM:-unset}'" "the admin console needs its own hostname, reachable only from your management network" ;;
    *)              ok "adminUrl ${ADM}" ;;
  esac

  # DNS. An ACME HTTP-01 challenge against a name that does not resolve here
  # fails minutes into the deploy, after everything else has been applied.
  PUB_HOST=$(printf '%s' "${PUB}" | sed 's|https\?://||' | cut -d: -f1)
  if [ -n "${PUB_HOST}" ] && [ "${PUB_HOST}" != "" ]; then
    if getent hosts "${PUB_HOST}" >/dev/null 2>&1; then
      ok "${PUB_HOST} resolves to $(getent hosts "${PUB_HOST}" | awk '{print $1}' | paste -sd, -)"
    else
      warn "${PUB_HOST} does not resolve from this host" \
           "an ACME HTTP-01 challenge needs public DNS for it and port 80 open to this node. Only a human can create that record."
    fi
  fi

  if [ -n "${RENDER_OUT}" ] && printf '%s' "${RENDER_OUT}" | grep -q 'kind: CronJob'; then
    warn "backups are the bundled nightly pg_dumpall to a PVC on this node" \
         "a dump beside the database is not disaster recovery. Copy the ${RELEASE}-backup PVC off-node, or run CloudNativePG with WAL archiving (SECURITY-AUDIT-LOG.md step 18 §1)."
  fi
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo ""
echo "───────────────────────────────────────────────────────────────"
printf ' %d ok · %d failed · %d warnings\n' "${PASS}" "${FAIL}" "${WARN}"
if [ "${FAIL}" -gt 0 ]; then
  echo ""
  echo " Not ready to deploy:"
  printf '%s' "${FAILED_LIST}"
  echo ""
  echo " Full install guide: docs/DEPLOYMENT.md"
  exit 1
fi
echo " Preflight passed — ./deploy/setup.sh --${ENVIRONMENT} can run."
exit 0
