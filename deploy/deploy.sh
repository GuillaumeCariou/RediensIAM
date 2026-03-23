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
  PROD_DOMAIN="authentication.rediens.net"
  PROD_URL="https://${PROD_DOMAIN}"
  SECRETS_FILE="${CHART}/values.prod.secret.yaml"
  HOMELAB_TRAEFIK="${HOME}/Desktop/Workspace/homelab/proxy/conf.d/authentication-routing.yml"
  echo "════════════════════════════════════════════════"
  echo " RediensIAM — Prod Deployment"
  echo " Domain:    ${PROD_DOMAIN}"
  echo " Registry:  ${REGISTRY}"
  echo " Namespace: ${NAMESPACE}"
  echo "════════════════════════════════════════════════"
else
  IMAGE="${REGISTRY}/rediensiam:dev"
  echo "════════════════════════════════════════════════"
  echo " RediensIAM — Dev Deployment"
  echo " Registry:  ${REGISTRY}"
  echo " Namespace: ${NAMESPACE}"
  echo " Dev mode:  ${DEV} | Upgrade: ${UPGRADE}"
  echo "════════════════════════════════════════════════"
fi

# ── Helpers ────────────────────────────────────────────────────────────────────
wait_api() {
  for i in $(seq 1 30); do
    kubectl get nodes --request-timeout=5s &>/dev/null && return 0
    echo "    [k3s] waiting for API… ($i/30)"; sleep 5
  done
  echo "  ERROR: cluster API not ready"; exit 1
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
  TOTP_KEY=$(openssl rand -base64 32)
  ARGON_PEPPER=$(openssl rand -hex 32)

  read -rp "  Bootstrap admin email    [admin@rediens.net]: " BOOTSTRAP_EMAIL
  BOOTSTRAP_EMAIL="${BOOTSTRAP_EMAIL:-admin@rediens.net}"
  read -rsp "  Bootstrap admin password: " BOOTSTRAP_PASS
  echo ""
  if [ -z "${BOOTSTRAP_PASS}" ]; then
    echo "  ERROR: bootstrap password cannot be empty"; exit 1
  fi

  cat > "${SECRETS_FILE}" <<EOF
env:
  IAM_BOOTSTRAP_EMAIL: "${BOOTSTRAP_EMAIL}"
  IAM_BOOTSTRAP_PASSWORD: "${BOOTSTRAP_PASS}"

secrets:
  databaseUrl: "Host=rediensiam-postgres;Database=rediensiam;Username=iam;Password=${DB_PASS}"
  cacheUrl: "rediensiam-dragonfly:6379,abortConnect=false"
  totpEncryptionKey: "${TOTP_KEY}"
  argon2Pepper: "${ARGON_PEPPER}"

postgres:
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
    -f "${SECRETS_FILE}" \
    --set image.repository="${REGISTRY}/rediensiam" \
    --set image.tag=prod \
    --set image.pullPolicy=Always \
    --set appUrl="${PROD_URL}" \
    --set ingress.host="${PROD_DOMAIN}" \
    --set "env.App__PublicUrl=${PROD_URL}" \
    --set "env.App__Domain=${PROD_DOMAIN}" \
    --set "hydra.hydra.config.urls.self.issuer=${PROD_URL}" \
    --set "hydra.hydra.config.urls.login=${PROD_URL}/login" \
    --set "hydra.hydra.config.urls.consent=${PROD_URL}/auth/consent" \
    --set "hydra.hydra.config.urls.logout=${PROD_URL}/auth/logout" \
    --set "hydra.hydra.config.urls.post_logout_redirect=${PROD_URL}/admin/" \
    --set "hydra.ingress.public.hosts[0].host=${PROD_DOMAIN}" \
    --set "hydra.ingress.public.hosts[0].paths[0].path=/oauth2" \
    --set "hydra.ingress.public.hosts[0].paths[0].pathType=Prefix" \
    --set "hydra.ingress.public.hosts[0].paths[1].path=/.well-known" \
    --set "hydra.ingress.public.hosts[0].paths[1].pathType=Prefix" \
    --set "hydra.ingress.public.hosts[0].paths[2].path=/userinfo" \
    --set "hydra.ingress.public.hosts[0].paths[2].pathType=Prefix" \
    --wait --timeout 10m
else
  DEV_FLAGS=""
  [ "${DEV}" = "true" ] && DEV_FLAGS="--set hydra.hydra.dev=true --set hydra.hydra.dangerousForceHttp=true --set env.ASPNETCORE_ENVIRONMENT=Development"

  helm_deploy rediensiam "${CHART}" \
    -f "${CHART}/values.secret.yaml" \
    --set image.repository="${REGISTRY}/rediensiam" \
    --set image.tag=dev \
    --set image.pullPolicy=Always \
    ${DEV_FLAGS} \
    --wait --timeout 10m
fi

# ── 5. Bootstrap Hydra admin client ───────────────────────────────────────────
echo ""
echo "──── [5/5] Bootstrap ────────────────────────────"

if [ "${PROD}" = "true" ]; then
  REDIRECT_URI="${PROD_URL}/admin/callback"
else
  REDIRECT_URI="http://localhost/admin/callback"
fi

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

# ── 5b. Update external Traefik routing (prod only) ────────────────────────────
if [ "${PROD}" = "true" ]; then
  echo ""
  echo "──── [5b/5] Updating external Traefik routing ───"
  NODE_IP=$(kubectl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}' 2>/dev/null | awk '{print $1}')
  NODE_IP="${NODE_IP:-$(hostname -I | awk '{print $1}')}"

  cat > "${HOMELAB_TRAEFIK}" <<EOF
http:
  routers:
    rediensiam-router:
      rule: "Host(\`${PROD_DOMAIN}\`)"
      priority: 10
      entryPoints:
        - websecure
      tls: true
      service: rediensiam-service
      middlewares:
        - rediensiam-headers

  services:
    rediensiam-service:
      loadBalancer:
        servers:
          - url: "http://${NODE_IP}:80"

  middlewares:
    rediensiam-headers:
      headers:
        customRequestHeaders:
          X-Forwarded-Proto: "https"
          X-Forwarded-Host: "${PROD_DOMAIN}"
EOF
  echo "  Traefik routing updated → http://${NODE_IP}:80"
  echo "  Reload Traefik to apply (e.g. systemctl reload traefik)"
fi

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
  echo "   - Reload Traefik on the proxy host"
  echo "   - Keep ${SECRETS_FILE} safe (not committed)"
else
  echo "   Login  →  http://localhost/login"
  echo "   Admin  →  http://localhost/admin/"
fi
echo "════════════════════════════════════════════════"
