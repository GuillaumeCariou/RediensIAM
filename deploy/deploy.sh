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
  echo "════════════════════════════════════════════════"
  echo " RediensIAM — Prod Deployment"
  echo " Config:    values.yaml + values.prod.yaml"
  echo " Registry:  ${REGISTRY}"
  echo " Namespace: ${NAMESPACE}"
  echo "════════════════════════════════════════════════"
else
  IMAGE="${REGISTRY}/rediensiam:dev"
  echo "════════════════════════════════════════════════"
  echo " RediensIAM — Dev Deployment"
  echo " Config:    values.yaml + values.dev.yaml"
  echo " Registry:  ${REGISTRY}"
  echo " Namespace: ${NAMESPACE}"
  echo " Upgrade:   ${UPGRADE}"
  echo "════════════════════════════════════════════════"
fi

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
echo "──── [1/5] Docker Registry ──────────────────────"
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
echo "──── [2/5] Build ────────────────────────────────"
cd "${ROOT_DIR}/frontend/login" && npm ci --silent && npm run build
echo "  Login SPA: $(du -sh dist | cut -f1)"
cd "${ROOT_DIR}/frontend/admin" && npm ci --silent && npm run build
echo "  Admin SPA: $(du -sh dist | cut -f1)"
cd "${ROOT_DIR}" && docker build -t "${IMAGE}" . && docker push "${IMAGE}"
echo "  Image pushed: ${IMAGE}"

# ── 3. Helm repos & chart deps ────────────────────────────────────────────────
echo ""
echo "──── [3/5] Helm ─────────────────────────────────"
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
echo "──── [4/5] Deploy ───────────────────────────────"
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

# ── 5. Bootstrap Hydra admin client ───────────────────────────────────────────
echo ""
echo "──── [5/5] Bootstrap ────────────────────────────"

# Admin SPA is always on NodePort 30501 — same redirect URI for dev and prod
REDIRECT_URI="http://localhost:30501/admin/callback"

kubectl exec -n "${NAMESPACE}" deployment/rediensiam-hydra -- \
  hydra delete oauth2-client --endpoint "http://localhost:4445" client_admin_system 2>/dev/null || true

kubectl exec -n "${NAMESPACE}" deployment/rediensiam-hydra -- \
  hydra create oauth2-client \
    --endpoint "http://localhost:4445" \
    --id client_admin_system \
    --name "RediensIAM Admin Console" \
    --grant-type authorization_code \
    --grant-type refresh_token \
    --response-type code \
    --scope openid \
    --scope offline \
    --redirect-uri "${REDIRECT_URI}" \
    --token-endpoint-auth-method none \
  && echo "  client_admin_system created (redirect: ${REDIRECT_URI})"


# ── Summary ────────────────────────────────────────────────────────────────────
echo ""
echo "════════════════════════════════════════════════"
echo " Deployment complete!"
echo ""
echo " Pods:"
kubectl get pods -n "${NAMESPACE}" --no-headers | awk '{printf "   %-40s %s\n", $1, $3}'
echo ""
echo " Access:"
if [ "${PROD}" = "true" ]; then
  echo "   Login  →  ${PROD_URL}/login"
  echo "   Admin  →  ${PROD_URL}/admin/"
  echo ""
  echo " Remember:"
  echo "   - Update Traefik routing on the proxy host to point ${PROD_DOMAIN} → k3s node :80"
  echo "   - Keep ${SECRETS_FILE} safe (not committed)"
else
  echo "   Login  →  http://localhost/login"
  echo "   Admin  →  http://localhost/admin/"
fi
echo "════════════════════════════════════════════════"
