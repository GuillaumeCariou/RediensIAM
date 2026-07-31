#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# RediensIAM — dev clean slate. DESTRUCTIVE.
#
#   ./deploy/reset-dev.sh              # lists what it will destroy, then asks
#   ./deploy/reset-dev.sh --dry-run    # lists only
#   ./deploy/reset-dev.sh --yes        # no prompt (CI)
#   ./deploy/reset-dev.sh --registry   # also drop the local image registry
#   ./deploy/reset-dev.sh --keep-secrets
#
# Removes the Helm release, its PersistentVolumeClaims and the generated dev
# credentials, so that the next `./deploy/setup.sh --dev` is a first install.
#
# It refuses to run against anything that does not look like the dev install.
# There is no --force: the prod teardown is a decision, not a flag.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

NS="${NS:-default}"
RELEASE="${RELEASE:-rediensiam}"
ASSUME_YES=false
DRY_RUN=false
DROP_REGISTRY=false
KEEP_SECRETS=false

while [ $# -gt 0 ]; do
  case "$1" in
    --yes|-y)      ASSUME_YES=true; shift ;;
    --dry-run|-n)  DRY_RUN=true; shift ;;
    --registry)    DROP_REGISTRY=true; shift ;;
    --keep-secrets) KEEP_SECRETS=true; shift ;;
    -h|--help)     sed -n '2,18p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHART="${SCRIPT_DIR}/rediensiam"
SECRETS_FILE="${CHART}/values.secret.yaml"

kubectl get ns "${NS}" >/dev/null 2>&1 || { echo "ERROR: cannot reach cluster / namespace ${NS}" >&2; exit 2; }

# ── Refuse anything that is not the dev install ──────────────────────────────
# The dev install is the one whose issuer is a localhost name. A prod release
# has a real hostname, a real user population, and no business being here.
if helm status "${RELEASE}" -n "${NS}" >/dev/null 2>&1; then
  ISSUER=$(helm get values "${RELEASE}" -n "${NS}" -a -o json 2>/dev/null \
             | grep -oE '"issuer":[[:space:]]*"[^"]*"' | head -1 | cut -d'"' -f4)
  case "${ISSUER}" in
    *localhost*|*127.0.0.1*|"") : ;;
    *)
      echo "REFUSING: release '${RELEASE}' in namespace '${NS}' has issuer ${ISSUER}."
      echo "          That is not a dev install. This script only clears localhost releases."
      echo "          To take down a real environment, do it deliberately:"
      echo "            helm uninstall ${RELEASE} -n ${NS}"
      echo "          and decide about the PVCs by hand — they hold every tenant."
      exit 1 ;;
  esac
fi
if [ -f "${CHART}/values.prod.secret.yaml" ] && [ ! -f "${SECRETS_FILE}" ]; then
  echo "REFUSING: only values.prod.secret.yaml exists on this machine."
  echo "          There is no dev install here to reset."
  exit 1
fi

# ── Inventory ────────────────────────────────────────────────────────────────
echo "═══════════════════════════════════════════════════════════════"
echo " RediensIAM dev reset — namespace ${NS}, release ${RELEASE}"
echo "═══════════════════════════════════════════════════════════════"
echo ""
echo " This DESTROYS, permanently:"
echo ""

HAVE_RELEASE=false
if helm status "${RELEASE}" -n "${NS}" >/dev/null 2>&1; then
  HAVE_RELEASE=true
  echo "  · Helm release ${RELEASE} (revision $(helm list -n "${NS}" -f "^${RELEASE}\$" -o json 2>/dev/null | grep -oE '"revision":"[0-9]*"' | head -1 | cut -d'"' -f4))"
  kubectl get pods -n "${NS}" --no-headers -o custom-columns=':metadata.name' 2>/dev/null \
    | grep "^${RELEASE}" | sed 's/^/      pod /'
else
  echo "  · (no Helm release installed)"
fi

# Every PVC the release owns, including ones left behind by earlier installs.
# StatefulSet PVCs are not garbage-collected by `helm uninstall`, which is how a
# cluster ends up with orphans that silently keep the OLD database password.
PVCS=$(kubectl get pvc -n "${NS}" --no-headers -o custom-columns=':metadata.name,:spec.resources.requests.storage,:status.phase' 2>/dev/null \
         | awk -v r="${RELEASE}" '$1 ~ "^(data-)?" r {print}')
if [ -n "${PVCS}" ]; then
  echo ""
  echo "  · PersistentVolumeClaims (and the data on them):"
  while read -r name size phase; do
    [ -z "${name}" ] && continue
    case "${name}" in
      data-*postgres*) what="every user, organisation, project, Hydra client, OAuth2 token, consent session, Keto relation tuple and audit record" ;;
      *backup*)        what="every retained nightly pg_dumpall" ;;
      *)               what="unknown contents" ;;
    esac
    printf '      %-34s %-6s %-9s — %s\n' "${name}" "${size}" "${phase}" "${what}"
  done <<<"${PVCS}"
else
  echo ""
  echo "  · (no PersistentVolumeClaims for this release)"
fi

if [ "${KEEP_SECRETS}" = false ] && [ -f "${SECRETS_FILE}" ]; then
  echo ""
  echo "  · $(basename "${SECRETS_FILE}") — the bootstrap admin password, the HKDF root"
  echo "      encryption key, the Argon2 pepper, the Hydra system secret and all four"
  echo "      Postgres role passwords. Not recoverable. (Moot once the DB above is gone:"
  echo "      nothing encrypted under that key survives either.)"
fi

if [ "${DROP_REGISTRY}" = true ]; then
  echo ""
  echo "  · the local Docker registry container and its 'registry-data' volume"
  echo "      — every image layer ever pushed to localhost:5000, for this project and any other."
fi

echo ""
echo " This does NOT touch: cert-manager, Traefik, the k3s cluster itself, or"
echo " anything outside namespace ${NS}."
echo ""

if [ "${DRY_RUN}" = true ]; then
  echo " --dry-run: nothing was changed."
  exit 0
fi

if [ "${ASSUME_YES}" != true ]; then
  printf ' Type exactly "destroy dev" to continue: '
  read -r ANSWER
  if [ "${ANSWER}" != "destroy dev" ]; then
    echo " Aborted — nothing was changed."
    exit 1
  fi
fi

# ── Teardown ─────────────────────────────────────────────────────────────────
echo ""
echo "──── Removing ──────────────────────────────────────────────────"

if [ "${HAVE_RELEASE}" = true ]; then
  # --no-hooks: the pre-delete hooks would try to reach a database that is about
  # to be deleted, and a hook timeout here leaves the release stuck.
  helm uninstall "${RELEASE}" -n "${NS}" --no-hooks --wait --timeout 3m 2>&1 | sed 's/^/  /'
fi

# Helm hook Jobs are not part of the release manifest and outlive an uninstall.
kubectl delete job -n "${NS}" -l "app.kubernetes.io/instance=${RELEASE}" --ignore-not-found 2>&1 | sed 's/^/  /'
for j in "${RELEASE}-rls" "${RELEASE}-backup"; do
  kubectl delete job -n "${NS}" "${j}" --ignore-not-found >/dev/null 2>&1
done
kubectl delete cronjob -n "${NS}" "${RELEASE}-backup" --ignore-not-found >/dev/null 2>&1

if [ -n "${PVCS}" ]; then
  while read -r name _; do
    [ -z "${name}" ] && continue
    kubectl delete pvc -n "${NS}" "${name}" --ignore-not-found --wait=false 2>&1 | sed 's/^/  /'
  done <<<"${PVCS}"
  # local-path releases the PV asynchronously; wait so the next install does not
  # bind a half-deleted volume.
  for i in $(seq 1 30); do
    LEFT=$(kubectl get pvc -n "${NS}" --no-headers -o custom-columns=':metadata.name' 2>/dev/null \
             | awk -v r="${RELEASE}" '$1 ~ "^(data-)?" r' | wc -l)
    [ "${LEFT}" -eq 0 ] && break
    sleep 2
  done
  echo "  PVCs released"
fi

# The chart's Secrets are part of the release and go with it, but a Secret left
# by an aborted install would carry the old credentials into the next one.
kubectl delete secret -n "${NS}" -l "app.kubernetes.io/instance=${RELEASE}" --ignore-not-found >/dev/null 2>&1
for s in "${RELEASE}-secrets" "${RELEASE}-hydra" "${RELEASE}-keto"; do
  kubectl delete secret -n "${NS}" "${s}" --ignore-not-found >/dev/null 2>&1
done

if [ "${KEEP_SECRETS}" = false ] && [ -f "${SECRETS_FILE}" ]; then
  rm -f "${SECRETS_FILE}"
  echo "  Removed $(basename "${SECRETS_FILE}")"
fi

if [ "${DROP_REGISTRY}" = true ]; then
  docker rm -f registry >/dev/null 2>&1 && echo "  Removed registry container"
  docker volume rm registry-data >/dev/null 2>&1 && echo "  Removed registry-data volume"
fi

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo " Clean. Next install is a first install:"
echo "   ./deploy/setup.sh --dev"
echo "═══════════════════════════════════════════════════════════════"
