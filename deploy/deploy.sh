#!/usr/bin/env bash
set -euo pipefail

# ── Args ───────────────────────────────────────────────────────────────────────
DEV=false
PROD=false
UPGRADE=false
for arg in "$@"; do
  case "$arg" in
    --dev)     DEV=true ;;
    --prod)    PROD=true ;;
    --upgrade) UPGRADE=true ;;
    *) echo "Unknown argument: $arg"; exit 1 ;;
  esac
done

if [ "${DEV}" = "true" ] && [ "${PROD}" = "true" ]; then
  echo "ERROR: --dev and --prod are mutually exclusive"; exit 1
fi

# ── Config ─────────────────────────────────────────────────────────────────────
NAMESPACE=default
REGISTRY="localhost:5000"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
CHART="${SCRIPT_DIR}/rediensiam"

if [ "${PROD}" = "true" ]; then
  IMAGE="${REGISTRY}/rediensiam:prod"
  SECRETS_FILE="${CHART}/values.prod.secret.yaml"
  ENV_FILE="${CHART}/values.prod.yaml"
  echo "════════════════════════════════════════════════"
  echo " RediensIAM — Prod Deployment"
  echo " Config:    values.yaml + values.prod.yaml"
  echo " Registry:  ${REGISTRY}"
  echo " Namespace: ${NAMESPACE}"
  echo "════════════════════════════════════════════════"
else
  IMAGE="${REGISTRY}/rediensiam:dev"
  ENV_FILE="${CHART}/values.dev.yaml"
  echo "════════════════════════════════════════════════"
  echo " RediensIAM — Dev Deployment"
  echo " Config:    values.yaml + values.dev.yaml"
  echo " Registry:  ${REGISTRY}"
  echo " Namespace: ${NAMESPACE}"
  echo " Upgrade:   ${UPGRADE}"
  echo "════════════════════════════════════════════════"
fi

# Read URLs from the env-specific values file
PUBLIC_URL=$(grep '^\s*publicUrl:' "${ENV_FILE}" | head -1 | sed 's/.*publicUrl:[[:space:]]*//' | tr -d '"' | cut -d'#' -f1 | tr -d ' ')
ADMIN_URL=$(grep '^\s*adminUrl:' "${ENV_FILE}" | head -1 | sed 's/.*adminUrl:[[:space:]]*//' | tr -d '"' | cut -d'#' -f1 | tr -d ' ')
PUBLIC_HOST=$(echo "${PUBLIC_URL}" | sed 's|https\?://||' | cut -d: -f1)

# ── Helpers ────────────────────────────────────────────────────────────────────
wait_api() {
  for i in $(seq 1 60); do
    kubectl get nodes --request-timeout=5s &>/dev/null && return 0
    echo "    [k3s] waiting for API… ($i/60)"; sleep 10
  done
  echo "  ERROR: cluster API not ready after 10m"; exit 1
}

helm_deploy() {
  local release="$1"; local chart="$2"; shift 2
  for attempt in $(seq 1 3); do
    helm rollback "${release}" 0 -n "${NAMESPACE}" 2>/dev/null \
      || helm uninstall "${release}" -n "${NAMESPACE}" --no-hooks 2>/dev/null \
      || true
    helm upgrade --install "${release}" "${chart}" --namespace "${NAMESPACE}" "$@" && return 0
    echo "  helm failed (attempt $attempt/3)"; wait_api
  done
  echo "  ERROR: helm failed after 3 attempts"; return 1
}

# ── 1. Docker Registry ─────────────────────────────────────────────────────────
echo ""
echo "──── [1/4] Docker Registry ──────────────────────"
if docker ps | grep -q "registry"; then
  echo "  Running"
elif docker ps -a | grep -q "registry"; then
  docker start registry; sleep 2
else
  docker volume create registry-data 2>/dev/null || true
  docker run -d -p 5000:5000 --name registry --restart=always \
    -v registry-data:/var/lib/registry \
    -e REGISTRY_STORAGE_DELETE_ENABLED=true registry:2
  sleep 3
fi
curl -fs http://${REGISTRY}/v2/ >/dev/null || { echo "  ERROR: registry not accessible"; exit 1; }
echo "  Ready at ${REGISTRY}"

# ── 1b. Generate prod secrets (if prod and file missing) ───────────────────────
if [ "${PROD}" = "true" ] && [ ! -f "${SECRETS_FILE}" ]; then
  echo ""
  echo "──── [1b/5] Generating prod secrets ─────────────"
  DB_PASS=$(openssl rand -hex 20)
  HYDRA_SECRET=$(openssl rand -hex 32)
  TOTP_KEY=$(openssl rand -hex 32)   # must be exactly 64 hex chars (32 bytes)
  ARGON_PEPPER=$(openssl rand -hex 32)

  read -rp "  Bootstrap admin email    [admin@rediens.net]: " BOOTSTRAP_EMAIL
  BOOTSTRAP_EMAIL="${BOOTSTRAP_EMAIL:-admin@rediens.net}"
  read -rsp "  Bootstrap admin password: " BOOTSTRAP_PASS
  echo ""
  if [ -z "${BOOTSTRAP_PASS}" ]; then
    echo "  ERROR: bootstrap password cannot be empty"; exit 1
  fi

  cat > "${SECRETS_FILE}" <<EOF
rediensiam:
  secrets:
    databaseUrl: "Host=rediensiam-postgres;Database=rediensiam;Username=iam;Password=${DB_PASS}"
    cacheUrl: "rediensiam-dragonfly:6379,abortConnect=false"
    encryptionKey: "${TOTP_KEY}"
    smtpPassword: ""
    bootstrapEmail: "${BOOTSTRAP_EMAIL}"
    bootstrapPassword: "${BOOTSTRAP_PASS}"
  postgres:
    local:
      password: ${DB_PASS}

hydra:
  hydra:
    config:
      dsn: "postgres://iam:${DB_PASS}@rediensiam-postgres:5432/hydra?sslmode=disable"
      secrets:
        system:
          - "${HYDRA_SECRET}"

keto:
  keto:
    config:
      dsn: "postgres://iam:${DB_PASS}@rediensiam-postgres:5432/keto?sslmode=disable"
EOF
  echo "  Secrets written to ${SECRETS_FILE}"
  echo "  (move this file somewhere safe before committing)"
fi

# ── 2. Build ───────────────────────────────────────────────────────────────────
echo ""
echo "──── [2/4] Build ────────────────────────────────"
cd "${ROOT_DIR}/frontend/login" && npm ci --silent && npm run build
echo "  Login SPA: $(du -sh dist | cut -f1)"
cd "${ROOT_DIR}/frontend/admin" && npm ci --silent && npm run build
echo "  Admin SPA: $(du -sh dist | cut -f1)"
cd "${ROOT_DIR}" && docker build -t "${IMAGE}" . && docker push "${IMAGE}"
echo "  Image pushed: ${IMAGE}"

# ── 3. Helm repos & chart deps ────────────────────────────────────────────────
echo ""
echo "──── [3/4] Helm ─────────────────────────────────"
helm repo add ory https://k8s.ory.sh/helm/charts --force-update 2>/dev/null || true
if [ "${UPGRADE}" = "true" ]; then
  helm repo update
  helm dependency update "${CHART}"
  echo "  Repos and dependencies updated"
else
  helm repo update ory 2>/dev/null || true
  helm dependency build "${CHART}" 2>/dev/null || helm dependency update "${CHART}"
fi

# ── 4. Deploy ──────────────────────────────────────────────────────────────────
echo ""
echo "──── [4/4] Deploy ───────────────────────────────"
wait_api

kubectl delete job -n "${NAMESPACE}" -l "app.kubernetes.io/instance=rediensiam" 2>/dev/null || true

if [ "${PROD}" = "true" ]; then
  helm_deploy rediensiam "${CHART}" \
    -f "${CHART}/values.yaml" \
    -f "${CHART}/values.prod.yaml" \
    -f "${SECRETS_FILE}" \
    --set rediensiam.image.repository="${REGISTRY}/rediensiam" \
    --set rediensiam.image.tag=prod \
    --set rediensiam.image.pullPolicy=Always \
    --wait --timeout 10m
else
  helm_deploy rediensiam "${CHART}" \
    -f "${CHART}/values.yaml" \
    -f "${CHART}/values.dev.yaml" \
    -f "${CHART}/values.secret.yaml" \
    --set rediensiam.image.repository="${REGISTRY}/rediensiam" \
    --set rediensiam.image.tag=dev \
    --set rediensiam.image.pullPolicy=Always \
    --wait --timeout 10m
fi

# client_admin_system is registered by the app on startup (EnsureAdminSpaClientAsync)
# with token_endpoint_auth_method=none and redirect_uris from App__AdminSpaOrigin.

# ── Summary ────────────────────────────────────────────────────────────────────
echo ""
echo "════════════════════════════════════════════════"
echo " Deployment complete!"
echo ""
echo " Pods:"
kubectl get pods -n "${NAMESPACE}" --no-headers | awk '{printf "   %-40s %s\n", $1, $3}'
echo ""
echo " Links:"
echo "   Login            →  ${PUBLIC_URL}/login"
echo "   Register         →  ${PUBLIC_URL}/register"
echo "   OIDC discovery   →  ${PUBLIC_URL}/.well-known/openid-configuration"
echo "   Health           →  ${PUBLIC_URL}/health"
echo "   Admin SPA        →  ${ADMIN_URL}/admin/  (Tailscale only)"
echo ""
echo " Smoke tests:"

check() {
  local label="$1"; local url="$2"; local expected="$3"; local host="${4:-${PUBLIC_HOST}}"
  local code
  code=$(curl -sk -o /dev/null -w "%{http_code}" -H "Host: ${host}" --max-time 5 "${url}" 2>/dev/null)
  if [ "${code}" = "${expected}" ]; then
    echo "   ✓  ${label} (${code})"
  else
    echo "   ✗  ${label} — expected ${expected}, got ${code}  [${url}]"
  fi
}

# Resolve internal cluster IP for the public service
PUBLIC_IP=$(kubectl get svc -n "${NAMESPACE}" rediensiam-public -o jsonpath='{.spec.clusterIP}' 2>/dev/null)
ADMIN_IP=$(kubectl get svc -n "${NAMESPACE}" rediensiam-admin -o jsonpath='{.spec.clusterIP}' 2>/dev/null)
ADMIN_HOST=$(echo "${ADMIN_URL}" | sed 's|https\?://||' | cut -d: -f1)

if [ -n "${PUBLIC_IP}" ]; then
  check "Health"          "http://${PUBLIC_IP}:5000/health"                           "200"
  check "OIDC discovery"  "http://${PUBLIC_IP}:5000/.well-known/openid-configuration" "200"
  check "Login page"      "http://${PUBLIC_IP}:5000/login"                            "200"
fi
if [ -n "${ADMIN_IP}" ]; then
  check "Admin SPA"       "http://${ADMIN_IP}:5001/admin/"                            "200" "${ADMIN_HOST}"
fi
if [ -z "${PUBLIC_IP}" ] && [ -z "${ADMIN_IP}" ]; then
  echo "   (could not resolve cluster IPs — skipping curl checks)"
fi

if [ "${PROD}" = "true" ]; then
  echo ""
  echo " Prod reminders:"
  echo "   - Point ${PUBLIC_HOST} → this node's :80 in Traefik"
  echo "   - Keep ${SECRETS_FILE} off this machine after deploy"
  echo "   - Admin requires Tailscale — enroll devices via headscale"
fi
echo "════════════════════════════════════════════════"
