#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# RediensIAM — bare cluster to working IdP.
#
#   ./deploy/setup.sh --dev            # zero to running, no questions, no manual steps
#   ./deploy/setup.sh --prod           # interviews you, then deploys
#   ./deploy/setup.sh --prod --plan    # interview only; writes the override, deploys nothing
#   ./deploy/setup.sh --dev --upgrade  # also refresh the Hydra/Keto subcharts
#
#   NAMESPACE=rediensiam ./deploy/setup.sh --prod    # install into its own namespace
#
# Order: preflight → build+deploy → verify → tell the operator what they now have.
# Every stage is a separate script you can also run on its own:
#   deploy/preflight.sh · deploy/deploy.sh · deploy/verify-deployment.sh
#
# Full guide: docs/DEPLOYMENT.md
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

ENVIRONMENT=""
PLAN_ONLY=false
UPGRADE_ARG=""
SKIP_PREFLIGHT=false

while [ $# -gt 0 ]; do
  case "$1" in
    --dev)  ENVIRONMENT=dev;  shift ;;
    --prod) ENVIRONMENT=prod; shift ;;
    --plan) PLAN_ONLY=true; shift ;;
    --upgrade) UPGRADE_ARG="--upgrade"; shift ;;
    --skip-preflight) SKIP_PREFLIGHT=true; shift ;;
    -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -z "${ENVIRONMENT}" ] && { echo "ERROR: pass --dev or --prod. There is no default — they are different systems." >&2; exit 2; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
CHART="${SCRIPT_DIR}/rediensiam"
export NAMESPACE="${NAMESPACE:-default}"
export NS="${NAMESPACE}"
RELEASE="${RELEASE:-rediensiam}"
ENV_FILE="${CHART}/values.${ENVIRONMENT}.yaml"
OVERRIDE_FILE="${CHART}/values.${ENVIRONMENT}.override.yaml"
SECRETS_FILE="${CHART}/values.secret.yaml"
[ "${ENVIRONMENT}" = prod ] && SECRETS_FILE="${CHART}/values.prod.secret.yaml"

hdr() { echo ""; echo "═══════════════════════════════════════════════════════════════"; echo " $1"; echo "═══════════════════════════════════════════════════════════════"; }
die() { echo ""; echo "ERROR: $1" >&2; [ $# -gt 1 ] && echo "       $2" >&2; exit 1; }

# ─────────────────────────────────────────────────────────────────────────────
# Prod interview. Every question here is one the audit deferred to a human, and
# none of them has a safe default. An unanswered question stops the install.
# ─────────────────────────────────────────────────────────────────────────────
ask() {  # ask VAR "prompt" ["default"]
  local __var="$1" __prompt="$2" __default="${3:-}" __ans=""
  while :; do
    if [ -n "${__default}" ]; then printf '  %s [%s]: ' "${__prompt}" "${__default}"
    else printf '  %s: ' "${__prompt}"; fi
    # `|| die` on EOF: a closed stdin must stop the interview, not spin on it.
    read -r __ans || die "stdin closed while asking: ${__prompt}" "prod setup needs a real answer to every question"
    __ans="${__ans:-${__default}}"
    [ -n "${__ans}" ] && break
    echo "      (required — there is no default for this)"
  done
  printf -v "${__var}" '%s' "${__ans}"
}

ask_choice() {  # ask_choice VAR "prompt" opt1 opt2 …
  local __var="$1" __prompt="$2"; shift 2
  local __opts=("$@") __ans=""
  while :; do
    printf '  %s (%s): ' "${__prompt}" "$(IFS='/'; echo "${__opts[*]}")"
    read -r __ans || die "stdin closed while asking: ${__prompt}" "expected one of: ${__opts[*]}"
    for o in "${__opts[@]}"; do [ "${__ans}" = "${o}" ] && { printf -v "${__var}" '%s' "${o}"; return 0; }; done
    echo "      (answer one of: ${__opts[*]})"
  done
}

confirm_typed() {  # confirm_typed "phrase" "why"
  echo "  $2"
  printf '  Type exactly "%s" to accept that: ' "$1"
  local a; read -r a || a=""
  [ "${a}" = "$1" ] || die "not accepted — nothing was written" "re-run ./deploy/setup.sh --prod when you have decided"
}

issuer_exists() { kubectl get clusterissuer "$1" >/dev/null 2>&1; }

prod_interview() {
  hdr "Production install — decisions this script will not make for you"
  cat <<'EOF'

 Each answer goes into deploy/rediensiam/values.prod.override.yaml (gitignored).
 Nothing is deployed until you confirm at the end.

EOF

  # ── Hostnames ──────────────────────────────────────────────────────────────
  echo " ── Hostnames ────────────────────────────────────────────────"
  echo "  The public host serves login, registration and the OIDC endpoints."
  echo "  The admin host serves the management console and management API, and"
  echo "  must be reachable only from your management network (Tailscale, VPN)."
  echo ""
  ask PUBLIC_HOST "Public hostname (e.g. auth.example.com)"
  case "${PUBLIC_HOST}" in
    *localhost*|*.local|127.*|"") die "'${PUBLIC_HOST}' is not a public hostname" "no CA will certify it, and R-02 requires TLS on the auth surface" ;;
    *://*) die "give a hostname, not a URL" "e.g. auth.example.com" ;;
  esac
  ask ADMIN_HOST "Admin console hostname (e.g. auth.ts.example.com)"
  [ "${ADMIN_HOST}" = "${PUBLIC_HOST}" ] && die "the admin host must differ from the public host" \
    "P-04: the ingress denies /admin, /org, /project and /service-accounts on the public host. Same name means no separation."

  # ── Public TLS ─────────────────────────────────────────────────────────────
  echo ""
  echo " ── TLS for ${PUBLIC_HOST} ───────────────────────────────────"
  echo "  acme     Let's Encrypt HTTP-01. Needs public DNS for ${PUBLIC_HOST}"
  echo "           pointing at this node, and port 80 open to the internet."
  echo "  existing Name a ClusterIssuer you already run (internal CA, DNS-01, …)."
  ask_choice PUB_TLS "Public issuer" acme existing
  if [ "${PUB_TLS}" = acme ]; then
    ask ACME_EMAIL "ACME account email (Let's Encrypt sends expiry warnings here)"
    case "${ACME_EMAIL}" in *@*.*) : ;; *) die "'${ACME_EMAIL}' is not an email address" ;; esac
    ask ACME_SERVER "ACME directory URL" "https://acme-v02.api.letsencrypt.org/directory"
    PUB_ISSUER=letsencrypt
    if ! getent hosts "${PUBLIC_HOST}" >/dev/null 2>&1; then
      confirm_typed "dns is not ready yet" \
        "${PUBLIC_HOST} does not resolve from this host. The HTTP-01 challenge will fail until it does. Only you can create that record."
    fi
  else
    ask PUB_ISSUER "ClusterIssuer name"
    issuer_exists "${PUB_ISSUER}" || die "ClusterIssuer '${PUB_ISSUER}' does not exist in this cluster" \
      "create it first (kubectl get clusterissuer), or choose 'acme'"
    ACME_EMAIL=""; ACME_SERVER=""
  fi

  # ── Admin TLS ──────────────────────────────────────────────────────────────
  echo ""
  echo " ── TLS for ${ADMIN_HOST} ────────────────────────────────────"
  echo "  existing   A ClusterIssuer you already run — an internal CA whose root"
  echo "             is distributed to operator devices, or an ACME DNS-01 issuer."
  echo "  selfsigned The chart's built-in issuer. Known defect: it trains operators"
  echo "             to click through a certificate warning on the single most"
  echo "             privileged UI in the system. .security-hardening/09 §6.3."
  echo "  NOTE: ACME HTTP-01 cannot certify a name that only resolves inside a"
  echo "        Tailscale mesh — the challenge is fetched from the public internet."
  ask_choice ADM_TLS "Admin issuer" existing selfsigned
  if [ "${ADM_TLS}" = existing ]; then
    ask ADM_ISSUER "ClusterIssuer name"
    issuer_exists "${ADM_ISSUER}" || die "ClusterIssuer '${ADM_ISSUER}' does not exist in this cluster" \
      "create it first, or accept 'selfsigned' and its known warning"
  else
    ADM_ISSUER=selfsigned
    confirm_typed "i accept the browser warning" \
      "Operators will get a certificate warning on ${ADMIN_HOST} every time. Treat it as a known defect, not as normal."
  fi

  # ── Database backend ───────────────────────────────────────────────────────
  echo ""
  echo " ── Database ─────────────────────────────────────────────────"
  echo "  builtin  The chart's PostgreSQL StatefulSet. One PVC on this node,"
  echo "           four least-privilege roles, nightly pg_dumpall to a second PVC."
  echo "  cnpg     An external CloudNativePG Cluster you operate. Continuous WAL"
  echo "           archiving to object storage; the chart's CronJob stands down."
  ask_choice DB_MODE "Database backend" builtin cnpg
  if [ "${DB_MODE}" = cnpg ]; then
    kubectl get crd clusters.postgresql.cnpg.io >/dev/null 2>&1 \
      || die "the CloudNativePG operator is not installed in this cluster" \
             "this chart does not install it. See .security-hardening/18-cnpg-tls-rls.md §1."
    ask CNPG_CLUSTER "CNPG Cluster name (its read/write Service is <name>-rw)" "rediensiam-db"
    ask CNPG_NS "Namespace of that Cluster (blank = ${NAMESPACE})" "${NAMESPACE}"
    kubectl get cluster.postgresql.cnpg.io -n "${CNPG_NS}" "${CNPG_CLUSTER}" >/dev/null 2>&1 \
      || die "no CNPG Cluster '${CNPG_CLUSTER}' in namespace '${CNPG_NS}'" \
             "create it first — with the four-role split from 18-cnpg-tls-rls.md §1, or you silently reinstate C-4"
    echo ""
    echo "  In CNPG mode this chart does NOT render:"
    echo "    · rediensiam-postgres-lockdown — the NetworkPolicy that keeps :5432"
    echo "      reachable only from the app, Hydra and Keto. You must write the"
    echo "      equivalent on the CNPG side. This is the easiest thing to lose here."
    echo "    · the nightly backup CronJob. If you have not configured .spec.backup"
    echo "      and an ObjectStore on the Cluster, you have NO backup and nothing"
    echo "      in this chart will tell you."
    confirm_typed "cnpg backup and netpol are mine" \
      "Both of the above are yours to provide."
    BACKUP_DEST="CNPG ObjectStore (operator-managed)"
  else
    ask PG_STORAGE "PostgreSQL volume size" "20Gi"
    ask BACKUP_SCHEDULE "Backup schedule (cron)" "0 3 * * *"
    ask BACKUP_STORAGE "Backup volume size" "50Gi"
    ask BACKUP_RETAIN "Nightly dumps to retain" "14"
    echo ""
    echo "  The nightly dump lands on a PVC on THIS node — the same disk and the"
    echo "  same failure domain as the database it protects. That covers a bad"
    echo "  migration or a dropped table. It does not cover losing the node."
    ask BACKUP_DEST "Where does the off-node copy go? (a path, a bucket, or the word 'none')"
    if [ "${BACKUP_DEST}" = none ]; then
      confirm_typed "one node one copy" \
        "With no off-node copy, a disk failure destroys every tenant permanently."
    fi
  fi

  # ── RLS ────────────────────────────────────────────────────────────────────
  echo ""
  echo " ── Row-level security (S-5 phase 2) ─────────────────────────"
  echo "  The policies are fail-closed: a connection that has not issued"
  echo "  SET rediensiam.org_id sees zero rows in every tenant table. For an IdP"
  echo "  that is a total outage, not a degraded mode."
  echo ""
  echo "  It can only be turned on AFTER an application build that sets that"
  echo "  variable on every pooled connection is deployed and verified against a"
  echo "  live connection. On a first install that build is not running yet, so"
  echo "  this script leaves RLS OFF and does not ask."
  echo "  Enable it later with the runbook: .security-hardening/18-cnpg-tls-rls.md §3"
  RLS=false

  # ── SMTP ───────────────────────────────────────────────────────────────────
  echo ""
  echo " ── Outbound email (optional; per-org SMTP overrides it) ─────"
  echo "  Blank means no global SMTP: password reset and verification mail only"
  echo "  works for organisations that configure their own server."
  printf '  SMTP host (blank for none): '; read -r SMTP_HOST
  SMTP_USER=""; SMTP_FROM="noreply@${PUBLIC_HOST}"
  if [ -n "${SMTP_HOST}" ]; then
    ask SMTP_USER "SMTP username"
    ask SMTP_FROM "From address" "noreply@${PUBLIC_HOST}"
    echo "  The SMTP PASSWORD is a secret: put it in rediensiam.secrets.smtpPassword"
    echo "  in $(basename "${SECRETS_FILE}") after this script generates that file."
  fi

  # ── Write the override ─────────────────────────────────────────────────────
  hdr "Review"
  {
    echo "# RediensIAM — production decisions, written by deploy/setup.sh on $(date -Is)."
    echo "# Gitignored: these are per-installation, not project defaults."
    echo "# Layered after values.prod.yaml and before the secrets file."
    echo "#"
    echo "# Off-node backup destination (recorded here, NOT automated by the chart):"
    echo "#   ${BACKUP_DEST}"
    echo ""
    echo "rediensiam:"
    echo "  publicUrl: \"https://${PUBLIC_HOST}\""
    echo "  adminUrl:  \"https://${ADMIN_HOST}\""
    echo ""
    echo "  ingress:"
    echo "    public:"
    echo "      host: \"${PUBLIC_HOST}\""
    echo "      tls:"
    echo "        enabled: true"
    echo "        clusterIssuer: ${PUB_ISSUER}"
    echo "    admin:"
    echo "      enabled: true"
    echo "      host: \"${ADMIN_HOST}\""
    echo "      clusterIssuer: ${ADM_ISSUER}"
    echo ""
    echo "  certManager:"
    echo "    acme:"
    if [ "${PUB_TLS}" = acme ]; then
      echo "      enabled: true"
      echo "      email: \"${ACME_EMAIL}\""
      echo "      server: \"${ACME_SERVER}\""
      echo "      issuerName: ${PUB_ISSUER}"
    else
      echo "      enabled: false"
    fi
    echo ""
    echo "  postgres:"
    if [ "${DB_MODE}" = cnpg ]; then
      echo "    local:"
      echo "      enabled: false"
      echo "    external:"
      echo "      podSelector:"
      echo "        cnpg.io/cluster: ${CNPG_CLUSTER}"
      echo "      namespace: \"$([ "${CNPG_NS}" = "${NAMESPACE}" ] && echo "" || echo "${CNPG_NS}")\""
    else
      echo "    local:"
      echo "      enabled: true"
      echo "      storage: ${PG_STORAGE}"
    fi
    echo "    rls:"
    echo "      enabled: ${RLS}"
    echo ""
    echo "  backup:"
    if [ "${DB_MODE}" = cnpg ]; then
      echo "    # No-op in CNPG mode. Backups are the Cluster's .spec.backup + ObjectStore."
      echo "    enabled: false"
    else
      echo "    enabled: true"
      echo "    schedule: \"${BACKUP_SCHEDULE}\""
      echo "    storage: ${BACKUP_STORAGE}"
      echo "    retainCopies: ${BACKUP_RETAIN}"
    fi
    echo ""
    echo "  smtp:"
    echo "    host: \"${SMTP_HOST}\""
    echo "    username: \"${SMTP_USER}\""
    echo "    fromAddress: \"${SMTP_FROM}\""
    echo ""
    echo "hydra:"
    echo "  hydra:"
    echo "    config:"
    echo "      urls:"
    echo "        self:"
    echo "          issuer:              \"https://${PUBLIC_HOST}\""
    echo "        login:                 \"https://${PUBLIC_HOST}/login\""
    echo "        consent:               \"https://${PUBLIC_HOST}/auth/consent\""
    echo "        logout:                \"https://${PUBLIC_HOST}/auth/logout\""
    echo "        post_logout_redirect:  \"https://${ADMIN_HOST}/admin/\""
    echo "      serve:"
    echo "        public:"
    echo "          cors:"
    echo "            enabled: true"
    echo "            allowed_origins:"
    echo "              - \"https://${ADMIN_HOST}\""
  } > "${OVERRIDE_FILE}"

  echo ""
  cat "${OVERRIDE_FILE}"
  echo ""
  echo " Written to ${OVERRIDE_FILE}"
}

# ─────────────────────────────────────────────────────────────────────────────
# Run
# ─────────────────────────────────────────────────────────────────────────────
cd "${ROOT_DIR}"

if [ "${ENVIRONMENT}" = prod ]; then
  if [ -f "${OVERRIDE_FILE}" ]; then
    hdr "Existing production decisions"
    cat "${OVERRIDE_FILE}"
    echo ""
    ask_choice REUSE "Reuse these answers" yes no
    [ "${REUSE}" = no ] && { [ -t 0 ] || die "prod setup is interactive by design"; prod_interview; }
  else
    [ -t 0 ] || die "prod setup is interactive by design" \
      "it asks for a hostname, a TLS issuer, a database backend and a backup destination. None of those has a safe default."
    prod_interview
  fi
  if [ "${PLAN_ONLY}" = true ]; then
    echo ""
    echo " --plan: nothing was deployed. Review $(basename "${OVERRIDE_FILE}"), then:"
    echo "   ./deploy/setup.sh --prod"
    exit 0
  fi
fi

# ── 1. Preflight ─────────────────────────────────────────────────────────────
if [ "${SKIP_PREFLIGHT}" = true ]; then
  echo " (preflight skipped by --skip-preflight)"
else
  hdr "1/3 Preflight"
  if ! "${SCRIPT_DIR}/preflight.sh" "--${ENVIRONMENT}"; then
    die "preflight failed — nothing was built or deployed" \
        "fix the items above and re-run. To install cert-manager: ./deploy/preflight.sh --${ENVIRONMENT} --install-cert-manager"
  fi
fi

# ── 2. Build and deploy ──────────────────────────────────────────────────────
hdr "2/3 Build and deploy"
if [ "${ENVIRONMENT}" = prod ]; then
  echo " About to build the image and deploy to namespace ${NAMESPACE}."
  echo " On a first install you will be asked for the bootstrap admin email and password."
  printf ' Continue? (yes/no): '; read -r GO
  [ "${GO}" = yes ] || die "aborted — nothing was deployed"
fi

SECRETS_EXISTED=true
[ -f "${SECRETS_FILE}" ] || SECRETS_EXISTED=false

# shellcheck disable=SC2086
"${SCRIPT_DIR}/deploy.sh" "--${ENVIRONMENT}" ${UPGRADE_ARG} || die "deploy.sh failed" \
  "the cluster may be half-changed. 'helm history ${RELEASE} -n ${NAMESPACE}' shows what happened; 'helm rollback' reverts."

# ── 3. Verify ────────────────────────────────────────────────────────────────
hdr "3/3 Verify the controls are live in the cluster"
"${SCRIPT_DIR}/verify-deployment.sh" "--${ENVIRONMENT}"
VERIFY_RC=$?

# ── What the operator now has ────────────────────────────────────────────────
PUBLIC_URL=$(grep '^\s*publicUrl:' "${ENV_FILE}" | head -1 | sed 's/.*publicUrl:[[:space:]]*//' | tr -d '"' | cut -d'#' -f1 | tr -d ' ')
ADMIN_URL=$(grep '^\s*adminUrl:' "${ENV_FILE}" | head -1 | sed 's/.*adminUrl:[[:space:]]*//' | tr -d '"' | cut -d'#' -f1 | tr -d ' ')
if [ -f "${OVERRIDE_FILE}" ]; then
  O=$(grep '^\s*publicUrl:' "${OVERRIDE_FILE}" | head -1 | sed 's/.*publicUrl:[[:space:]]*//' | tr -d '"' | tr -d ' '); [ -n "${O}" ] && PUBLIC_URL="${O}"
  O=$(grep '^\s*adminUrl:'  "${OVERRIDE_FILE}" | head -1 | sed 's/.*adminUrl:[[:space:]]*//'  | tr -d '"' | tr -d ' '); [ -n "${O}" ] && ADMIN_URL="${O}"
fi

hdr "RediensIAM is installed"
echo ""
echo " Sign in"
echo "   Login          ${PUBLIC_URL}/login"
echo "   Register       ${PUBLIC_URL}/register"
echo "   Admin console  ${ADMIN_URL}/admin/"
echo "   OIDC discovery ${PUBLIC_URL}/.well-known/openid-configuration"
echo ""

# The bootstrap credential. deploy.sh prints it once at generation; repeat it
# here on a first install so the operator does not have to scroll, and never
# print it for an install that already existed.
if [ "${SECRETS_EXISTED}" = false ] && [ -f "${SECRETS_FILE}" ]; then
  BE=$(grep '^\s*bootstrapEmail:' "${SECRETS_FILE}" | head -1 | sed 's/.*bootstrapEmail:[[:space:]]*//' | sed "s/^[\"']//;s/[\"']$//")
  echo " ┌─ BOOTSTRAP SUPER-ADMIN — created on this install ──────────────────"
  echo " │  email:    ${BE}"
  echo " │  password: in ${SECRETS_FILE} (mode 600), key rediensiam.secrets.bootstrapPassword"
  if [ "${ENVIRONMENT}" = prod ]; then
    echo " │"
    echo " │  Sign in, create a named super-admin, then move this file off this"
    echo " │  machine. It also holds the HKDF root key and the Argon2 pepper."
  fi
  echo " └───────────────────────────────────────────────────────────────────"
  echo ""
elif [ -f "${SECRETS_FILE}" ]; then
  echo " Bootstrap credentials: ${SECRETS_FILE} (existing install — not reprinted)"
  echo ""
fi

if [ "${ENVIRONMENT}" = prod ]; then
  echo " Before you call this done — the four things this script cannot do:"
  echo ""
  echo "   1. Confirm the CNI actually enforces NetworkPolicy. Two minutes, and"
  echo "      every policy in this chart is decorative if it fails:"
  echo "        kubectl run np-test -n ${NAMESPACE} --image=busybox --restart=Never -- sleep 3600"
  echo "        kubectl exec -n ${NAMESPACE} np-test -- wget -qO- --timeout=3 \\"
  echo "          http://${RELEASE}-hydra-admin:4445/admin/clients   # must TIME OUT"
  echo "        kubectl delete pod -n ${NAMESPACE} np-test"
  echo "   2. Enable k3s secret encryption at rest (needs root on the server node):"
  echo "        sudo k3s secrets-encrypt status"
  echo "      .security-hardening/10-secrets-management.md §7.3 — fifteen minutes."
  echo "   3. Prove the backup restores. A CronJob is not a backup:"
  echo "      .security-hardening/15c-infra-residuals.md §T-03 restore test."
  echo "   4. Move ${SECRETS_FILE} off this machine."
  echo ""
fi

echo " Guide: docs/DEPLOYMENT.md"
if [ "${VERIFY_RC}" -ne 0 ]; then
  echo ""
  echo " ⚠ verify-deployment.sh reported failures above. The install is running"
  echo "   but at least one asserted control is NOT live. Do not treat this as done."
  exit 1
fi
echo ""
