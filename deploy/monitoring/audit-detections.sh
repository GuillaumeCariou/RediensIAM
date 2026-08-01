#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# RediensIAM — detection rules over the audit_log table.
#
# There is no SIEM here and there should not be one: single-node k3s, one
# operator. This script IS the detection layer. It runs a fixed set of SQL
# rules against the running Postgres, prints the result, and pushes the
# "page" ones to $ALERT_URL if that is set.
#
#   ./deploy/monitoring/audit-detections.sh                 # last 24h, print only
#   ./deploy/monitoring/audit-detections.sh --window 7days  # weekly review
#   ALERT_URL=https://ntfy.sh/<topic> ./…/audit-detections.sh   # push page-rules
#
# Exit codes: 0 no page-severity hit · 1 page-severity hit · 2 could not run.
#
# Rules are labelled with the finding / chain ID they cover. See
# `SECURITY-AUDIT-LOG.md` step 13 for the reasoning behind each.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

NS="${NS:-default}"
PGPOD="${PGPOD:-rediensiam-postgres-0}"
PGUSER="${PGUSER:-iam_backup}"
PGDB="${PGDB:-rediensiam}"
WINDOW="24 hours"
# Must match Audit:RetentionDays (AppConfig.cs:116, default 365). A per-org value
# below this is an org that shortened its own retention.
GLOBAL_RETENTION_DAYS="${GLOBAL_RETENTION_DAYS:-365}"
# Auth-failure burst thresholds, per actor and per source IP, within the window.
FAIL_PER_ACTOR="${FAIL_PER_ACTOR:-20}"
FAIL_PER_IP="${FAIL_PER_IP:-50}"
QUIET=false

while [ $# -gt 0 ]; do
  case "$1" in
    --window) WINDOW="$2"; shift 2 ;;
    --quiet)  QUIET=true; shift ;;
    -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

PAGES=""
REVIEWS=""
RC=0

# Password pulled from the release Secret at run time, never held in a file. T-04 removed
# `trust` from pg_hba.conf, so an unauthenticated `psql -U iam` now prompts and every rule
# fails — which this script reported as "No hits" until the summary was fixed below.
# iam_backup is the read-only role: detection must never be able to write.
PGPASSWORD_CACHED=""
db_password() {
  [ -n "${PGPASSWORD_CACHED}" ] && { printf '%s' "${PGPASSWORD_CACHED}"; return; }
  PGPASSWORD_CACHED="$(kubectl get secret -n "${NS}" "${SECRET:-rediensiam-secrets}" \
    -o "jsonpath={.data.${PGPASSWORD_KEY:-postgres-backup-password}}" 2>/dev/null | base64 -d)"
  printf '%s' "${PGPASSWORD_CACHED}"
}

psql_q() {
  kubectl exec -n "${NS}" "${PGPOD}" -- \
    env PGPASSWORD="$(db_password)" \
    psql -U "${PGUSER}" -d "${PGDB}" -At -F ' | ' -c "$1" 2>&1
}

# run_rule <id> <page|review> <title> <sql>
# The SQL must return zero rows when nothing is wrong. Any row is a hit.
run_rule() {
  local id="$1" sev="$2" title="$3" sql="$4" out n
  out="$(psql_q "${sql}")"
  if [ $? -ne 0 ] || printf '%s' "${out}" | grep -q '^ERROR:\|^psql:'; then
    printf '  ??   %-5s %s\n' "${id}" "${title} — QUERY FAILED"
    printf '%s\n' "${out}" | sed 's/^/         /'
    RC=2
    return
  fi
  if [ -z "${out}" ]; then
    [ "${QUIET}" = true ] || printf '  ok   %-5s %s\n' "${id}" "${title}"
    return
  fi
  n="$(printf '%s\n' "${out}" | wc -l | tr -d ' ')"
  printf '  %s %-5s %s  [%s row(s)]\n' \
    "$([ "${sev}" = page ] && echo 'PAGE' || echo 'note')" "${id}" "${title}" "${n}"
  printf '%s\n' "${out}" | head -20 | sed 's/^/         /'
  if [ "${sev}" = page ]; then
    PAGES="${PAGES}${id} ${title} — ${n} row(s)"$'\n'
    RC=1
  else
    REVIEWS="${REVIEWS}${id} ${title} — ${n} row(s)"$'\n'
  fi
}

echo "═══════════════════════════════════════════════════════════════"
echo " RediensIAM audit detections — window: ${WINDOW} — $(date -Is)"
echo "═══════════════════════════════════════════════════════════════"

if ! kubectl get pod -n "${NS}" "${PGPOD}" >/dev/null 2>&1; then
  echo "  ERROR: pod ${PGPOD} not found in namespace ${NS}" >&2
  exit 2
fi

# ── State rules: not time-windowed, they describe the database as it stands ───

# D-01 · P-01 (step 11 / 11b §1) — forged cross-org service-account grants.
# The write path is fixed; rows written before the fix still confer the foreign
# org scope, because PatService:115 prefers the role's OrgId. ul."OrgId" IS NULL
# is the __system__ list and is legitimate.
run_rule D-01 page "P-01 forged cross-org service-account role rows" '
SELECT r."Id", r."ServiceAccountId", sa."Name", r."Role",
       r."OrgId" AS granted_org, ul."OrgId" AS owning_org, r."GrantedAt", r."GrantedBy"
  FROM service_account_roles r
  JOIN service_accounts sa ON sa."Id" = r."ServiceAccountId"
  JOIN user_lists ul       ON ul."Id" = sa."UserListId"
 WHERE r."OrgId" IS NOT NULL AND ul."OrgId" IS NOT NULL AND r."OrgId" <> ul."OrgId"
 ORDER BY r."GrantedAt" DESC;'

# D-02 · C-1 / R-23 / T-N3 — tenant role names that read as platform authority.
# Roles.ProjectRoleNameError refuses the three exact management names. A downstream
# resource server that splits the "projectId/name" claim on "/" and compares the
# suffix will still honour "admin" or "superadmin". Those names are legal here.
run_rule D-02 page "C-1 tenant role name reads as management authority" $'
SELECT p."OrgId", r."ProjectId", p."Name" AS project, r."Name" AS role, r."CreatedAt", r."CreatedBy"
  FROM roles r JOIN projects p ON p."Id" = r."ProjectId"
 WHERE lower(r."Name") ~ \'^(super[ _-]?admin|admin|administrator|root|owner|superuser|sysadmin|system|platform[ _-]?admin)$\'
 ORDER BY r."CreatedAt" DESC;'

# D-06 · P-08 (step 11 §5b, 11b §4) — suspension does not suspend the org's
# administrators. Any audit row for a suspended org, dated after the suspension,
# is the bypass actually being used.
run_rule D-06 page "P-08 activity inside a suspended org after suspension" $'
SELECT a."CreatedAt", o."Slug", a."Action", a."ActorId", a."IpAddress"
  FROM audit_log a JOIN organisations o ON o."Id" = a."OrgId"
 WHERE o."Active" = false AND o."SuspendedAt" IS NOT NULL
   AND a."CreatedAt" > o."SuspendedAt"
   AND a."Action" NOT IN (\'org.unsuspended\',\'org.suspended\')
 ORDER BY a."CreatedAt" DESC;'

# D-07 · T-N5 / C-4 — audit retention shortened. AppConfig.ClampRetention has a
# 90-day floor and OrgController:66 refuses anything below it, so this catches a
# legitimate-but-notable shortening and a direct database write alike.
run_rule D-07 page "audit retention set below the deployment default" "
SELECT \"Id\", \"Slug\", \"AuditRetentionDays\", \"UpdatedAt\"
  FROM organisations
 WHERE \"AuditRetentionDays\" IS NOT NULL AND \"AuditRetentionDays\" < ${GLOBAL_RETENTION_DAYS}
 ORDER BY \"AuditRetentionDays\";"

# D-11 · T-N2 / C-4 — the audit trail itself went quiet. A purge, a crash-looping
# writer and a never-deployed build all look the same from here, and all three
# mean detection is off.
#
# Suppressed while the deployment has no organisations: a freshly bootstrapped
# instance has nothing to record, and paging every night on a dev cluster is how an
# operator learns to ignore this rule — which costs exactly the signal it exists to
# carry. One organisation is enough to arm it.
run_rule D-11 page "audit log is empty or silent for >48h" $'
SELECT CASE WHEN count(*) = 0 THEN \'audit_log is EMPTY - nothing is being recorded\'
            ELSE \'newest audit row is \' || age(now(), max("CreatedAt"))::text || \' old\'
       END AS state
  FROM audit_log
 HAVING (SELECT count(*) FROM organisations) > 0
    AND (count(*) = 0 OR max("CreatedAt") < now() - interval \'48 hours\');'

# D-13 · 11b §6 residual — nested providers[].logo_url is not validated by
# LoginThemeValidator, so a tenant admin can beacon every visitor to that
# project's login page.
run_rule D-13 review "11b §6 provider logo_url points off-instance" $'
SELECT p."OrgId", p."Slug", prov->>\'logo_url\' AS logo_url
  FROM projects p,
       LATERAL jsonb_array_elements(
         CASE WHEN jsonb_typeof(p."LoginTheme"->\'providers\') = \'array\'
              THEN p."LoginTheme"->\'providers\' ELSE \'[]\'::jsonb END) prov
 WHERE prov->>\'logo_url\' IS NOT NULL
   AND prov->>\'logo_url\' !~ \'^(/|data:)\';'

# ── Windowed rules ───────────────────────────────────────────────────────────

# D-03 · TB-3 / C-5 / P-05 — a service account asked about a token or a Keto
# object outside its own tenant. IntrospectionController:150-159 records only
# refusals, so every row here is a real cross-tenant probe, never normal traffic.
run_rule D-03 page "cross-tenant introspection / authorize refusal" "
SELECT \"CreatedAt\", \"Action\", \"OrgId\" AS caller_org, \"ActorId\", \"TargetId\", \"IpAddress\"
  FROM audit_log
 WHERE \"Action\" IN ('api.introspect.out_of_scope','api.authorize.out_of_scope')
   AND \"CreatedAt\" > now() - interval '${WINDOW}'
 ORDER BY \"CreatedAt\" DESC;"

# D-04 · C-2 / AT-2 / R-24 — MFA takeover. A single enrolment is a user doing
# their job; two factor mutations by one actor inside an hour, or a mutation
# from an address that actor has never used before, is the takeover shape.
run_rule D-04 page "MFA factor mutation burst or from an unseen address" "
WITH mfa AS (
  SELECT * FROM audit_log
   WHERE \"Action\" IN ('user.mfa.totp_enabled','user.mfa.totp_replaced',
                       'user.mfa.passkey_registered','user.mfa.passkey_removed',
                       'user.mfa.phone_verified','user.mfa.phone_removed',
                       'user.mfa.backup_codes_regenerated')
     AND \"CreatedAt\" > now() - interval '${WINDOW}')
SELECT DISTINCT m.\"CreatedAt\", m.\"ActorId\", m.\"Action\", m.\"IpAddress\",
       CASE WHEN b.\"ActorId\" IS NOT NULL THEN 'burst' ELSE 'new-source' END AS reason
  FROM mfa m
  LEFT JOIN LATERAL (
        SELECT m2.\"ActorId\" FROM mfa m2
         WHERE m2.\"ActorId\" = m.\"ActorId\" AND m2.\"Id\" <> m.\"Id\"
           AND m2.\"CreatedAt\" BETWEEN m.\"CreatedAt\" - interval '1 hour'
                                    AND m.\"CreatedAt\" + interval '1 hour'
         LIMIT 1) b ON true
 WHERE b.\"ActorId\" IS NOT NULL
    OR NOT EXISTS (SELECT 1 FROM audit_log h
                    WHERE h.\"ActorId\" = m.\"ActorId\" AND h.\"IpAddress\" = m.\"IpAddress\"
                      AND h.\"CreatedAt\" < now() - interval '${WINDOW}')
 ORDER BY 1 DESC;"

# D-05 · AT-2 / R-02 — credential-stuffing and re-auth grinding. RequireReauthAsync
# rate-limits per (ip, user, purpose) but records no audit row of its own, so the
# observable proxy is the login and factor-verification failures it sits behind.
run_rule D-05 page "authentication failure burst" "
SELECT 'actor' AS scope, \"ActorId\"::text AS subject, count(*), min(\"CreatedAt\"), max(\"CreatedAt\")
  FROM audit_log
 WHERE \"Action\" IN ('user.login.failure','user.login.locked','user.mfa.totp.failed','user.mfa.sms.failed')
   AND \"CreatedAt\" > now() - interval '${WINDOW}' AND \"ActorId\" IS NOT NULL
 GROUP BY 2 HAVING count(*) >= ${FAIL_PER_ACTOR}
UNION ALL
SELECT 'ip', \"IpAddress\", count(*), min(\"CreatedAt\"), max(\"CreatedAt\")
  FROM audit_log
 WHERE \"Action\" IN ('user.login.failure','user.login.locked','user.mfa.totp.failed','user.mfa.sms.failed')
   AND \"CreatedAt\" > now() - interval '${WINDOW}' AND \"IpAddress\" IS NOT NULL
 GROUP BY 2 HAVING count(*) >= ${FAIL_PER_IP};"

# D-09 · C-5 / AT-4 — a service-account credential used from somewhere new. The
# PAT hot path is deliberately not audited (a row per API request), so this keys
# on the sa.* management actions, which are the ones worth a page anyway.
run_rule D-09 page "service-account action from a source never seen before" "
SELECT min(a.\"CreatedAt\") AS first_seen, a.\"ActorId\", a.\"IpAddress\",
       string_agg(DISTINCT a.\"Action\", ',') AS actions
  FROM audit_log a
 WHERE a.\"Action\" LIKE 'sa.%' AND a.\"CreatedAt\" > now() - interval '${WINDOW}'
   AND a.\"IpAddress\" IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM audit_log h
                    WHERE h.\"ActorId\" = a.\"ActorId\" AND h.\"IpAddress\" = a.\"IpAddress\"
                      AND h.\"CreatedAt\" <= now() - interval '${WINDOW}')
 GROUP BY 2,3 ORDER BY 1 DESC;"

# D-08 · C-3 / C-4 — writes to something the whole deployment trusts: a new OAuth2
# client, a new service-account key or PAT, a SAML IdP, a webhook sink, a
# management-role grant. Individually legitimate, collectively the escalation
# surface. Review severity: these are expected during normal administration.
run_rule D-08 review "trust-anchor write" "
SELECT \"CreatedAt\", \"Action\", \"OrgId\", \"ActorId\", \"TargetType\", \"TargetId\", \"IpAddress\"
  FROM audit_log
 WHERE \"Action\" IN ('oauth2_client.created','oauth2_client.deleted',
                     'sa.created','sa.key.added','sa.pat.created','sa.role.assigned',
                     'saml_provider.created','saml_provider.updated',
                     'webhook.created','role.management.assigned','role.management.removed',
                     'export.users','export.audit_log')
   AND \"CreatedAt\" > now() - interval '${WINDOW}'
 ORDER BY \"CreatedAt\" DESC;"

# D-10 · 11b §6 residual — the dual-session revocation gap. Revocation builds a
# single subject string ("{org}:{id}" or "{id}"), so a user holding both a tenant
# and a management session keeps one of them across a password change. Listing
# them is the compensating control until the subject enumeration is fixed.
run_rule D-10 review "password change by a management-role holder (dual-session gap)" "
SELECT DISTINCT a.\"CreatedAt\", a.\"ActorId\", r.\"OrgId\", r.\"Role\"
  FROM audit_log a JOIN org_roles r ON r.\"UserId\" = a.\"ActorId\"
 WHERE a.\"Action\" IN ('user.password_changed','user.password.reset','user.password_reset_by_admin')
   AND a.\"CreatedAt\" > now() - interval '${WINDOW}'
 ORDER BY 1 DESC;"

# D-12 — every MFA mutation, for the weekly eyeball. Not a page: D-04 is the
# page. This is the "what changed about second factors this week" list.
run_rule D-12 review "all MFA factor mutations in window" "
SELECT \"CreatedAt\", \"ActorId\", \"Action\", \"IpAddress\"
  FROM audit_log
 WHERE \"Action\" LIKE 'user.mfa.%' AND \"Action\" NOT LIKE '%.failed'
   AND \"CreatedAt\" > now() - interval '${WINDOW}'
 ORDER BY 1 DESC;"

echo "───────────────────────────────────────────────────────────────"
if [ -n "${PAGES}" ]; then
  echo " PAGE:"; printf '%s' "${PAGES}" | sed 's/^/   /'
fi
if [ -n "${REVIEWS}" ]; then
  echo " REVIEW:"; printf '%s' "${REVIEWS}" | sed 's/^/   /'
fi
# "No hits" must mean the rules ran and found nothing — never that they failed to run.
# Removing `trust` from pg_hba.conf broke every query and this line still printed the
# all-clear, which is the same failure shape as V-02 in verify-deployment.sh.
if [ "${RC}" -eq 2 ]; then
  echo " RULES FAILED TO RUN — the output above is not an all-clear."
elif [ -z "${PAGES}${REVIEWS}" ]; then
  echo " No hits."
fi

# ── Routing ──────────────────────────────────────────────────────────────────
# One channel, page-severity only. ALERT_URL is anything that accepts a POST body
# and reaches a phone — an ntfy topic, a Slack/Discord webhook, a Telegram bot.
# Unset means print-only, which is the correct default for the weekly review run.
if [ -n "${PAGES}" ] && [ -n "${ALERT_URL:-}" ]; then
  if curl -fsS --max-time 10 \
       -H "Title: RediensIAM detection" -H "Priority: high" -H "Tags: rotating_light" \
       -d "$(printf 'host %s · window %s\n%s' "$(hostname)" "${WINDOW}" "${PAGES}")" \
       "${ALERT_URL}" >/dev/null; then
    echo " alert delivered to ALERT_URL"
  else
    # A silent alerting channel is worse than none: say so loudly and fail.
    echo " ALERT DELIVERY FAILED — ${ALERT_URL}" >&2
    RC=2
  fi
elif [ -n "${PAGES}" ]; then
  echo " (ALERT_URL not set — page-severity hits were printed, not delivered)"
fi

exit ${RC}
