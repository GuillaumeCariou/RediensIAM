#!/usr/bin/env bash
set -euo pipefail

# ── Args ───────────────────────────────────────────────────────────────────────
DEV=false
UPGRADE=false
for arg in "$@"; do
  case "$arg" in
    --dev)     DEV=true ;;
    --upgrade) UPGRADE=true ;;
    *) echo "Unknown argument: $arg"; exit 1 ;;
  esac
done

# ── Config ─────────────────────────────────────────────────────────────────────
NAMESPACE=default
REGISTRY="localhost:5000"
IMAGE="${REGISTRY}/rediensiam:dev"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
CHART="${SCRIPT_DIR}/rediensiam"

echo "════════════════════════════════════════════════"
echo " RediensIAM — Dev Deployment"
echo " Registry:  ${REGISTRY}"
echo " Namespace: ${NAMESPACE}"
echo " Dev mode:  ${DEV} | Upgrade: ${UPGRADE}"
echo "════════════════════════════════════════════════"

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

DEV_FLAGS=""
[ "${DEV}" = "true" ] && DEV_FLAGS="--set hydra.hydra.dev=true --set hydra.hydra.dangerousForceHttp=true --set env.ASPNETCORE_ENVIRONMENT=Development"

kubectl delete job -n "${NAMESPACE}" -l "app.kubernetes.io/instance=rediensiam" 2>/dev/null || true

helm_deploy rediensiam "${CHART}" \
  -f "${CHART}/values.secret.yaml" \
  --set image.repository="${REGISTRY}/rediensiam" \
  --set image.tag=dev \
  --set image.pullPolicy=Always \
  ${DEV_FLAGS} \
  --wait --timeout 10m

# ── 5. Bootstrap Hydra admin client ───────────────────────────────────────────
echo ""
echo "──── [5/5] Bootstrap ────────────────────────────"
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
    --redirect-uri "http://localhost/admin/callback" \
    --token-endpoint-auth-method none \
  && echo "  client_admin_system created"

# ── Summary ────────────────────────────────────────────────────────────────────
echo ""
echo "════════════════════════════════════════════════"
echo " Deployment complete!"
echo ""
echo " Pods:"
kubectl get pods -n "${NAMESPACE}" --no-headers | awk '{printf "   %-40s %s\n", $1, $3}'
echo ""
NODE_IP=$(kubectl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}' 2>/dev/null | awk '{print $1}')
NODE_IP="${NODE_IP:-$(hostname -I | awk '{print $1}')}"
echo " Access:"
echo "   Login  →  http://localhost/login"
echo "   Admin  →  http://localhost/admin/"
echo "════════════════════════════════════════════════"
