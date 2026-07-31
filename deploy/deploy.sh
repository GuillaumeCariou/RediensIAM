#!/usr/bin/env bash
set -euo pipefail

# ── Args ───────────────────────────────────────────────────────────────────────
DEV=false
PROD=false
UPGRADE=false
ROTATE=false
for arg in "$@"; do
  case "$arg" in
    --dev)     DEV=true ;;
    --prod)    PROD=true ;;
    --upgrade) UPGRADE=true ;;
    # Dev only. Destroys the dev secrets file and the state encrypted under it, then regenerates.
    # See §1b for why this cannot be a prod operation.
    --rotate-secrets) ROTATE=true ;;
    *) echo "Unknown argument: $arg"; exit 1 ;;
  esac
done

if [ "${DEV}" = "true" ] && [ "${PROD}" = "true" ]; then
  echo "ERROR: --dev and --prod are mutually exclusive"; exit 1
fi

# ── Config ─────────────────────────────────────────────────────────────────────
# NOTE: `default` is a shared namespace on this cluster. The chart's default-deny is
# release-scoped for that reason. Moving this release to its own namespace is the
# prerequisite for a true namespace-wide baseline deny — see .security-hardening/09.
# 15c recommends moving the release out of `default` at the FIRST install — it cannot be done
# as an upgrade (no cross-namespace PVC move, and helm cannot change a release's namespace).
# Overridable so that decision is available without editing this file:  NAMESPACE=rediensiam ./deploy/setup.sh --prod
NAMESPACE="${NAMESPACE:-default}"
REGISTRY="localhost:5000"
# R-16: the registry listens on loopback only. Anyone who could reach TCP/5000 on this host
# could previously push a replacement rediensiam:prod and own the next pod restart.
REGISTRY_BIND="127.0.0.1"
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
  SECRETS_FILE="${CHART}/values.secret.yaml"
  ENV_FILE="${CHART}/values.dev.yaml"
  echo "════════════════════════════════════════════════"
  echo " RediensIAM — Dev Deployment"
  echo " Config:    values.yaml + values.dev.yaml"
  echo " Registry:  ${REGISTRY}"
  echo " Namespace: ${NAMESPACE}"
  echo " Upgrade:   ${UPGRADE}"
  echo "════════════════════════════════════════════════"
fi

# Operator decisions written by `./deploy/setup.sh --prod` (gitignored, not a secrets file).
# Layered after the env file and before the secrets file, so it wins over the committed
# defaults and never over a credential.
OVERRIDE_FILE="${ENV_FILE%.yaml}.override.yaml"
OVERRIDE_ARGS=()
if [ -f "${OVERRIDE_FILE}" ]; then
  OVERRIDE_ARGS=(-f "${OVERRIDE_FILE}")
  echo " Overrides: $(basename "${OVERRIDE_FILE}")"
fi

# Read URLs from the env-specific values file, then let the override correct them.
read_url() { grep "^\s*$2:" "$1" 2>/dev/null | head -1 | sed "s/.*$2:[[:space:]]*//" | tr -d '"' | cut -d'#' -f1 | tr -d ' '; }
PUBLIC_URL=$(read_url "${ENV_FILE}" publicUrl)
ADMIN_URL=$(read_url "${ENV_FILE}" adminUrl)
if [ -f "${OVERRIDE_FILE}" ]; then
  O=$(read_url "${OVERRIDE_FILE}" publicUrl); [ -n "${O}" ] && PUBLIC_URL="${O}"
  O=$(read_url "${OVERRIDE_FILE}" adminUrl);  [ -n "${O}" ] && ADMIN_URL="${O}"
fi
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
start_registry() {
  docker volume create registry-data 2>/dev/null || true
  docker run -d -p ${REGISTRY_BIND}:5000:5000 --name registry --restart=always \
    -v registry-data:/var/lib/registry \
    -e REGISTRY_STORAGE_DELETE_ENABLED=true registry:2
  sleep 3
}

# If an existing container is bound to anything but loopback, replace it. The images live in
# the named volume, so this is not destructive — the container is recreated with the same
# storage and a narrower bind.
registry_bind_of() {
  docker inspect -f '{{ range $p, $c := .HostConfig.PortBindings }}{{ range $c }}{{ .HostIp }}{{ end }}{{ end }}' registry 2>/dev/null
}

if docker ps -a --format '{{.Names}}' | grep -qx "registry"; then
  CURRENT_BIND="$(registry_bind_of)"
  if [ "${CURRENT_BIND}" != "${REGISTRY_BIND}" ]; then
    echo "  Registry is bound to '${CURRENT_BIND:-0.0.0.0}' — rebinding to ${REGISTRY_BIND} (R-16)"
    docker rm -f registry >/dev/null
    start_registry
  elif ! docker ps --format '{{.Names}}' | grep -qx "registry"; then
    docker start registry >/dev/null; sleep 2
  else
    echo "  Running (bound to ${REGISTRY_BIND})"
  fi
else
  start_registry
fi
curl -fs http://${REGISTRY}/v2/ >/dev/null || { echo "  ERROR: registry not accessible"; exit 1; }
echo "  Ready at ${REGISTRY} (loopback only)"

# ── 1b. Secrets ────────────────────────────────────────────────────────────────
# R-06. Neither environment has a default credential any more. `--prod` already generated its
# own; `--dev` used to load a hand-written values.secret.yaml whose values were identical on
# every machine that copied it — `changeme`, `Admin1234!`, `CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS`
# — so a dev cluster that ended up routable leaked credentials an attacker already knew. Dev is
# now generated per install, with no prompt: a dev password an operator has to invent is a
# password that ends up in a shell history or a README.
#
# R-07. `umask 077` in write_secrets_file covers *creation*. It does nothing for a file that
# already exists from an earlier run under a wider umask, and `cat >` does not change the mode
# of an existing file — hence the unconditional chmod on the reuse path below.

# Literals this repo shipped as dev defaults. A match means the value is public knowledge, which
# is a different and worse thing than the value being weak.
KNOWN_DEFAULTS='changeme|CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS|Admin1234!'

# R-15. The DSNs this function writes have to agree with the chart's Postgres TLS
# posture, or a fresh install deadlocks: `requireSsl` makes pg_hba.conf admit
# `hostssl` only, and a cleartext DSN against that connects to nothing.
# `requireSsl` is grepped rather than `tls: enabled:` because the key name is
# unique in these files — there are three separate `tls:` blocks and matching the
# wrong one is how this kind of check becomes a lie.
# The chart has a matching `fail` guard, so a mismatch is a template error and not
# a 3am connection refusal; this is what stops that guard from ever firing.
REQUIRE_SSL=false
if grep -Eqs '^[[:space:]]*requireSsl:[[:space:]]*true' "${ENV_FILE}" "${CHART}/values.yaml"; then
  REQUIRE_SSL=true
fi
# The override file layers last, so it can turn requireSsl back OFF as well as on.
if [ -f "${OVERRIDE_FILE}" ] && grep -Eqs '^[[:space:]]*requireSsl:' "${OVERRIDE_FILE}"; then
  grep -Eqs '^[[:space:]]*requireSsl:[[:space:]]*true' "${OVERRIDE_FILE}" && REQUIRE_SSL=true || REQUIRE_SSL=false
fi

# R-15, the cache half. Same obligation as REQUIRE_SSL above and a harder one: with
# `--tls` Dragonfly stops answering cleartext entirely, so a cacheUrl without `ssl=true`
# does not degrade — it disconnects the app from the cache, and the cache holds the
# DataProtection key ring.
#
# `dragonfly.local.tls.enabled` has no unique key name to grep — there are three `tls:`
# blocks in these files and `enabled:` under the wrong one is how a check like this
# becomes a lie. So the block is cut out by indentation first (the `dragonfly:` key down
# to the next sibling at the same indent) and only then is `enabled:` matched inside its
# `tls:` sub-block. `templates/dragonfly.yaml` carries a matching `fail` guard in both
# directions, so if this reader is ever wrong the deploy stops at template time rather
# than at connection time.
cache_tls_in() {
  [ -f "$1" ] || return 1
  sed -n '/^[[:space:]]\{2\}dragonfly:/,/^[[:space:]]\{2\}[a-zA-Z]/p' "$1" \
    | sed -n '/^[[:space:]]*tls:/,/^[[:space:]]\{0,6\}[a-zA-Z]/p' \
    | grep -Eq '^[[:space:]]*enabled:[[:space:]]*true'
}
CACHE_TLS=false
if cache_tls_in "${CHART}/values.yaml" || cache_tls_in "${ENV_FILE}"; then
  CACHE_TLS=true
fi
# The override file layers last, so it can turn cache TLS back OFF as well as on.
if [ -f "${OVERRIDE_FILE}" ] && sed -n '/^[[:space:]]\{2\}dragonfly:/,/^[[:space:]]\{2\}[a-zA-Z]/p' "${OVERRIDE_FILE}" | grep -Eq '^[[:space:]]*enabled:'; then
  cache_tls_in "${OVERRIDE_FILE}" && CACHE_TLS=true || CACHE_TLS=false
fi

write_secrets_file() {
  local file="$1" email="$2" password="$3"
  local app_ssl="" ory_ssl="disable" cache_ssl=""
  # StackExchange.Redis spells it `ssl=true`. CacheTls.BuildOptions turns that into a
  # certificate-validation callback pinned to the CA the chart mounts at
  # /etc/cache-tls/ca.crt — it is not `TrustServerCertificate`'s cache equivalent, and
  # there is deliberately no knob here that would make it one.
  if [ "${CACHE_TLS}" = "true" ]; then
    cache_ssl=",ssl=true"
  fi
  if [ "${REQUIRE_SSL}" = "true" ]; then
    # `Trust Server Certificate=true` is the honest ceiling while the issuer is the
    # `selfsigned` ClusterIssuer: encryption, no server authentication. `verify-full`
    # needs a real CA whose root is distributed to app, Hydra and Keto.
    app_ssl=";SSL Mode=Require;Trust Server Certificate=true"
    ory_ssl="require"
  fi
  local db db_app db_hydra db_keto db_backup dfly hydra_sys enc_key pepper
  # The prod password is operator-chosen, so it can contain " \ or $, all of which break a
  # double-quoted YAML scalar. A single-quoted scalar only needs ' doubled.
  local password_yaml="'${password//\'/\'\'}'"
  db=$(openssl rand -hex 20)
  # T-04. Four separate credentials, one per component, so that a leaked DSN is a leak of one
  # database rather than of the whole cluster. `db` above is now only the bootstrap SUPERUSER
  # `iam`, which appears in no DSN below.
  db_app=$(openssl rand -hex 20)
  db_hydra=$(openssl rand -hex 20)
  db_keto=$(openssl rand -hex 20)
  db_backup=$(openssl rand -hex 20)
  # R-15. This was never generated. `dragonfly.local.password` defaulted to "" and this
  # function never set it, so the chart rendered `--requirepass=` and the cache ran with
  # NO authentication — protected only by `dragonfly-lockdown`, one NetworkPolicy away
  # from being open to every pod in the namespace. It also holds the DataProtection key
  # ring, so an unauthenticated writer there can mint session cookies.
  # Found because Dragonfly refuses to start with TLS and no auth method:
  #   E server_family.cc:292] TLS configured but no authentication method is used!
  dfly=$(openssl rand -hex 24)
  hydra_sys=$(openssl rand -hex 32)
  enc_key=$(openssl rand -hex 32)   # must be exactly 64 hex chars (32 bytes) — HKDF root
  pepper=$(openssl rand -hex 32)    # Security__Argon2Pepper; was generated and then dropped

  # R-15: the DSNs below say sslmode=disable because the bundled Postgres ships without TLS.
  # Turning on rediensiam.postgres.local.tls.enabled makes the server offer TLS; the DSNs then
  # have to be changed to sslmode=require in this file for it to be used. Both steps are in the
  # R-15 runbook in .security-hardening/09-infra-security.md. Do not raise the sslmode here
  # without enabling the server side first — `require` against a non-TLS server fails to connect.
  #
  # The subshell keeps the umask local: leaking 077 into the rest of the script would silently
  # narrow the mode of anything else it creates.
  ( umask 077
    cat > "${file}" <<EOF
rediensiam:
  secrets:
    databaseUrl: "Host=rediensiam-postgres;Database=rediensiam;Username=iam_app;Password=${db_app}${app_ssl}"
    cacheUrl: "rediensiam-dragonfly:6379${cache_ssl},abortConnect=false,password=${dfly}"
    encryptionKey: "${enc_key}"
    smtpPassword: ""
    bootstrapEmail: "${email}"
    bootstrapPassword: ${password_yaml}
  security:
    argon2Pepper: "${pepper}"
  postgres:
    local:
      password: ${db}
      roles:
        appPassword: ${db_app}
        hydraPassword: ${db_hydra}
        ketoPassword: ${db_keto}
        backupPassword: ${db_backup}
  dragonfly:
    local:
      password: ${dfly}

hydra:
  hydra:
    config:
      dsn: "postgres://iam_hydra:${db_hydra}@rediensiam-postgres:5432/hydra?sslmode=${ory_ssl}"
      secrets:
        # Hydra reads this list newest-first: element 0 encrypts, the rest only decrypt. That is
        # what makes a rotation non-destructive — prepend, upgrade, then drop the tail once the
        # old tokens have expired. See 10-secrets-management.md §4.
        system:
          - "${hydra_sys}"

keto:
  keto:
    config:
      dsn: "postgres://iam_keto:${db_keto}@rediensiam-postgres:5432/keto?sslmode=${ory_ssl}"
EOF
  )
  chmod 600 "${file}"
}

echo ""
echo "──── [1b/4] Secrets ─────────────────────────────"

if [ "${ROTATE}" = "true" ]; then
  # Rotating the DB password, the HKDF root and the Hydra system secret together invalidates
  # everything derived from them at once: the Postgres data dir keeps the OLD password, every
  # AES-GCM ciphertext in the DB (TOTP secrets, webhook secrets, per-org SMTP passwords) becomes
  # undecryptable, and every Hydra token and consent session dies. The app has no key-versioned
  # ciphertext and no re-encrypt pass, so the only coherent form of this is a full state reset —
  # which is why it is dev-only. Prod rotation is per-secret and ordered.
  if [ "${PROD}" = "true" ]; then
    echo "  ERROR: --rotate-secrets is dev-only. Prod rotation is per-secret and ordered;"
    echo "         follow .security-hardening/10-secrets-management.md §4."
    exit 1
  fi
  echo "  Rotating dev secrets — this discards dev DB and cache state."
  rm -f "${SECRETS_FILE}"
  helm uninstall rediensiam -n "${NAMESPACE}" --no-hooks 2>/dev/null || true
  kubectl delete pvc -n "${NAMESPACE}" data-rediensiam-postgres-0 2>/dev/null || true
fi

if [ ! -f "${SECRETS_FILE}" ]; then
  if [ "${PROD}" = "true" ]; then
    read -rp "  Bootstrap admin email    [admin@rediens.net]: " BOOTSTRAP_EMAIL
    BOOTSTRAP_EMAIL="${BOOTSTRAP_EMAIL:-admin@rediens.net}"
    read -rsp "  Bootstrap admin password: " BOOTSTRAP_PASS
    echo ""
    if [ -z "${BOOTSTRAP_PASS}" ]; then
      echo "  ERROR: bootstrap password cannot be empty"; exit 1
    fi
    write_secrets_file "${SECRETS_FILE}" "${BOOTSTRAP_EMAIL}" "${BOOTSTRAP_PASS}"
    echo "  Generated ${SECRETS_FILE} (mode 600)"
    echo "  (move this file somewhere safe after the deploy)"
  else
    BOOTSTRAP_EMAIL="admin@dev.local"
    # tr strips the base64 chars that need shell quoting; the suffix guarantees one of each
    # character class so the value also satisfies a tightened project password policy.
    BOOTSTRAP_PASS="$(openssl rand -base64 24 | tr -d '/+=')Aa1!"
    write_secrets_file "${SECRETS_FILE}" "${BOOTSTRAP_EMAIL}" "${BOOTSTRAP_PASS}"
    echo "  Generated ${SECRETS_FILE} (mode 600)"
    echo ""
    echo "  ┌─ DEV BOOTSTRAP ADMIN — unique to this machine, shown once ──────────"
    echo "  │  email:    ${BOOTSTRAP_EMAIL}"
    echo "  │  password: ${BOOTSTRAP_PASS}"
    echo "  └─ also in ${SECRETS_FILE}; re-roll with --dev --rotate-secrets"
    echo ""
  fi
else
  chmod 600 "${SECRETS_FILE}"
  echo "  Using ${SECRETS_FILE} (mode $(stat -c %a "${SECRETS_FILE}"))"
  if grep -Eq "${KNOWN_DEFAULTS}" "${SECRETS_FILE}"; then
    if [ "${PROD}" = "true" ]; then
      echo "  ERROR (R-06): ${SECRETS_FILE} still holds a credential this repo shipped as a"
      echo "                default. Refusing to deploy it. Generate a fresh one:"
      echo "                  mv ${SECRETS_FILE} ${SECRETS_FILE}.old && ./deploy/deploy.sh --prod"
      echo "                then follow 10-secrets-management.md §4 to migrate existing state."
      exit 1
    fi
    echo "  WARNING (R-06): this file still holds credentials this repo shipped as defaults."
    echo "                  Every copy of the repo knows them. Re-roll with:"
    echo "                    ./deploy/deploy.sh --dev --rotate-secrets"
    echo "                  (discards dev DB state — dev data is disposable)"
  fi

  # T-04. The chart now expects four per-component Postgres roles. A secrets file written
  # before that change still names the shared SUPERUSER `iam` in every DSN and carries no
  # postgres.local.roles block — which would render an EMPTY backup password into the Secret
  # and leave the superuser split undone, silently. Deploying the new chart over an old
  # database is a migration, not an upgrade, and it must not happen by accident.
  if grep -Eq 'Username=iam;|postgres://iam:' "${SECRETS_FILE}" \
     || ! grep -q 'appPassword:' "${SECRETS_FILE}"; then
    echo ""
    echo "  ┌─ T-04: this install predates the Postgres role split ──────────────"
    echo "  │  ${SECRETS_FILE} still points every component at the shared"
    echo "  │  SUPERUSER 'iam'. postgres.yaml's init.sh only runs against an EMPTY"
    echo "  │  data directory, so a helm upgrade will NOT create the new roles."
    echo "  │"
    echo "  │  Run the migration first — it is one psql session and a redeploy:"
    echo "  │    .security-hardening/15c-infra-residuals.md  §T-04 migration runbook"
    echo "  │"
    echo "  │  Or, for a dev box with disposable data, start clean:"
    echo "  │    ./deploy/deploy.sh --dev --rotate-secrets"
    echo "  └────────────────────────────────────────────────────────────────────"
    if [ "${PROD}" = "true" ]; then
      echo "  ERROR: refusing to deploy prod across this migration."
      exit 1
    fi
    echo "  WARNING: continuing (dev). The backup CronJob will fail until you migrate."
    echo ""
  fi

  # R-15. A secrets file written before the cache had a password renders
  # `--requirepass=` and an unauthenticated Dragonfly. Unlike the T-04 case this is
  # recoverable in place: add the two lines, redeploy, and the cache restarts.
  if ! grep -q '^      password:.*' <(sed -n '/^  dragonfly:/,/^[a-z]/p' "${SECRETS_FILE}") 2>/dev/null; then
    echo ""
    echo "  ┌─ R-15: this install has no cache password ─────────────────────────"
    echo "  │  ${SECRETS_FILE} sets no rediensiam.dragonfly.local.password, so"
    echo "  │  Dragonfly runs with --requirepass= (no authentication) and cannot"
    echo "  │  have TLS enabled at all — it refuses to start without an auth method."
    echo "  │  Fix (dev or prod, no data loss beyond the cache itself):"
    echo "  │    PW=\$(openssl rand -hex 24)"
    echo "  │    add to ${SECRETS_FILE}:"
    echo "  │      rediensiam.dragonfly.local.password: \$PW"
    echo "  │    and append ',password='\$PW to rediensiam.secrets.cacheUrl"
    echo "  └────────────────────────────────────────────────────────────────────"
    if [ "${PROD}" = "true" ]; then
      echo "  ERROR: refusing to deploy prod with an unauthenticated cache."
      exit 1
    fi
    echo "  WARNING: continuing (dev) with an unauthenticated cache."
    echo ""
  fi

  # R-15, the cache TLS cutover on the reuse path. `cache_ssl` above only reaches a
  # secrets file this run generates; an existing one keeps whatever cacheUrl it was
  # written with, and the operator flipping dragonfly.local.tls.enabled has no reason
  # to know a second file has to move with it. The chart `fail`s on the mismatch, so
  # this cannot ship broken either way — but helm's message names a values key, and
  # what the operator needs is the file and the edit.
  if [ "${CACHE_TLS}" = "true" ] && ! grep -Eq 'cacheUrl:.*(^|,) *ssl *= *true' "${SECRETS_FILE}"; then
    echo ""
    echo "  ┌─ R-15: cache TLS is on but this install's DSN is cleartext ────────"
    echo "  │  rediensiam.dragonfly.local.tls.enabled renders Dragonfly with --tls,"
    echo "  │  which makes it stop answering cleartext. ${SECRETS_FILE}"
    echo "  │  still has a cacheUrl without ssl=true, so the app would lose the cache"
    echo "  │  — and with it the DataProtection key ring, i.e. every session."
    echo "  │  Fix (edit in place, the password on that line is not reprinted here):"
    echo "  │    sed -i 's|\\(cacheUrl: \"[^\"]*:6379\\)|\\1,ssl=true|' ${SECRETS_FILE}"
    echo "  └────────────────────────────────────────────────────────────────────"
    echo "  ERROR: refusing to deploy a TLS cache with a cleartext DSN."
    exit 1
  fi
fi

# ── 2. Build ───────────────────────────────────────────────────────────────────
echo ""
echo "──── [2/4] Build ────────────────────────────────"
cd "${ROOT_DIR}/frontend/login" && npm ci --silent && npm run build
echo "  Login SPA: $(du -sh dist | cut -f1)"
cd "${ROOT_DIR}/frontend/admin" && npm ci --silent && npm run build
echo "  Admin SPA: $(du -sh dist | cut -f1)"
cd "${ROOT_DIR}" && docker build -t "${IMAGE}" . && docker push "${IMAGE}"
# R-16: resolve the digest the registry actually stored and deploy that, not the tag. This is
# what makes a pod restart replay the exact bytes that were reviewed instead of re-asking the
# registry what `:prod` means today.
IMAGE_DIGEST=$(docker inspect --format='{{index .RepoDigests 0}}' "${IMAGE}" 2>/dev/null | cut -d'@' -f2)
if [ -z "${IMAGE_DIGEST}" ]; then
  echo "  ERROR: could not resolve image digest after push — refusing to deploy a mutable tag"
  exit 1
fi
echo "  Image pushed: ${IMAGE}"
echo "  Digest:       ${IMAGE_DIGEST}"

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
    ${OVERRIDE_ARGS[@]+"${OVERRIDE_ARGS[@]}"} \
    -f "${SECRETS_FILE}" \
    --set rediensiam.image.repository="${REGISTRY}/rediensiam" \
    --set rediensiam.image.tag=prod \
    --set rediensiam.image.digest="${IMAGE_DIGEST}" \
    --set rediensiam.image.pullPolicy=IfNotPresent \
    --wait --timeout 10m
else
  helm_deploy rediensiam "${CHART}" \
    -f "${CHART}/values.yaml" \
    -f "${CHART}/values.dev.yaml" \
    ${OVERRIDE_ARGS[@]+"${OVERRIDE_ARGS[@]}"} \
    -f "${SECRETS_FILE}" \
    --set rediensiam.image.repository="${REGISTRY}/rediensiam" \
    --set rediensiam.image.tag=dev \
    --set rediensiam.image.digest="${IMAGE_DIGEST}" \
    --set rediensiam.image.pullPolicy=IfNotPresent \
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
ADMIN_SVC_TYPE=$(kubectl get svc -n "${NAMESPACE}" rediensiam-admin -o jsonpath='{.spec.type}' 2>/dev/null)
if [ -n "${ADMIN_IP}" ] && [ "${ADMIN_SVC_TYPE}" = "NodePort" ]; then
  check "Admin SPA"       "http://${ADMIN_IP}:5001/admin/"                            "200" "${ADMIN_HOST}"
elif [ -n "${ADMIN_IP}" ]; then
  # ClusterIP + NetworkPolicy scopes :5001 to the ingress controller, so a curl from this
  # shell is expected to be refused. That is the control working, not a failure — reach the
  # console through the admin ingress instead.
  echo "   -  Admin SPA — not probed: :5001 is ClusterIP and admitted only from the ingress"
  echo "                  controller. Verify at ${ADMIN_URL}/admin/ over Tailscale."
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
