#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Self-test for audit-detections.sh.
#
# The live audit_log is empty, so "0 rows" from every rule proves only that the
# SQL parses. This proves the six non-trivial predicates actually FIRE, by
# running them against synthetic rows supplied as CTEs. Nothing is written:
# every statement is a SELECT over VALUES, and psql runs inside a read-only
# transaction as a second belt.
#
#   ./deploy/monitoring/selftest.sh
#
# ponytail: the predicates are restated here rather than shared with the rule
# script, so a rule edited without editing its test will pass a stale check.
# Upgrade path if that bites: move each rule's SQL to its own .sql file and have
# both scripts read it.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

NS="${NS:-default}"
PGPOD="${PGPOD:-rediensiam-postgres-0}"
PGUSER="${PGUSER:-iam}"
PGDB="${PGDB:-rediensiam}"
FAILS=0

psql_ro() {
  kubectl exec -n "${NS}" "${PGPOD}" -- \
    psql -U "${PGUSER}" -d "${PGDB}" -qAt -v ON_ERROR_STOP=1 \
      -c 'BEGIN READ ONLY;' -c "$1" -c 'ROLLBACK;' 2>&1
}

# assert <name> <expected-count> <sql returning one integer>
assert() {
  local name="$1" want="$2" sql="$3" got
  got="$(psql_ro "${sql}")"
  got="$(printf '%s' "${got}" | tr -d '[:space:]')"
  if [ "${got}" = "${want}" ]; then
    printf '  ok    %-6s expected %s, got %s\n' "${name}" "${want}" "${got}"
  else
    printf '  FAIL  %-6s expected %s, got %s\n' "${name}" "${want}" "${got}"
    FAILS=$((FAILS + 1))
  fi
}

echo "── audit-detections self-test ──────────────────────────────────"

# D-01 — three service accounts: one legitimately org-scoped, one on the
# __system__ list (UserList.OrgId NULL, legitimate), one forged cross-org.
assert D-01 1 "
WITH sa(\"Id\",\"UserListId\") AS (VALUES
    ('11111111-1111-1111-1111-111111111111'::uuid,'aaaaaaaa-0000-0000-0000-000000000001'::uuid),
    ('22222222-2222-2222-2222-222222222222'::uuid,'aaaaaaaa-0000-0000-0000-000000000002'::uuid),
    ('33333333-3333-3333-3333-333333333333'::uuid,'aaaaaaaa-0000-0000-0000-000000000003'::uuid)),
 ul(\"Id\",\"OrgId\") AS (VALUES
    ('aaaaaaaa-0000-0000-0000-000000000001'::uuid,'0a000000-0000-0000-0000-00000000000a'::uuid),
    ('aaaaaaaa-0000-0000-0000-000000000002'::uuid, NULL::uuid),
    ('aaaaaaaa-0000-0000-0000-000000000003'::uuid,'0a000000-0000-0000-0000-00000000000a'::uuid)),
 r(\"Id\",\"ServiceAccountId\",\"OrgId\") AS (VALUES
    (1,'11111111-1111-1111-1111-111111111111'::uuid,'0a000000-0000-0000-0000-00000000000a'::uuid),
    (2,'22222222-2222-2222-2222-222222222222'::uuid,'0b000000-0000-0000-0000-00000000000b'::uuid),
    (3,'33333333-3333-3333-3333-333333333333'::uuid,'0b000000-0000-0000-0000-00000000000b'::uuid))
SELECT count(*) FROM r
  JOIN sa ON sa.\"Id\" = r.\"ServiceAccountId\"
  JOIN ul ON ul.\"Id\" = sa.\"UserListId\"
 WHERE r.\"OrgId\" IS NOT NULL AND ul.\"OrgId\" IS NOT NULL AND r.\"OrgId\" <> ul.\"OrgId\";"

# D-02 — the lookalike regex. 'billing' and 'admin_of_reports' must NOT match;
# 'Admin', 'super-admin', 'SuperAdmin', 'root' must.
assert D-02 4 $'
WITH r("Name") AS (VALUES (\'billing\'),(\'admin_of_reports\'),(\'Admin\'),
                          (\'super-admin\'),(\'SuperAdmin\'),(\'root\'),(\'viewer\'))
SELECT count(*) FROM r
 WHERE lower(r."Name") ~ \'^(super[ _-]?admin|admin|administrator|root|owner|superuser|sysadmin|system|platform[ _-]?admin)$\';'

# D-06 — one row before suspension (ignored), one after (hit), one unsuspend
# (excluded by the action filter), one row in an active org (ignored).
assert D-06 1 $'
WITH o("Id","Active","SuspendedAt") AS (VALUES
    (\'0c000000-0000-0000-0000-00000000000c\'::uuid, false, now() - interval \'2 days\'),
    (\'0d000000-0000-0000-0000-00000000000d\'::uuid, true,  NULL::timestamptz)),
 a("OrgId","Action","CreatedAt") AS (VALUES
    (\'0c000000-0000-0000-0000-00000000000c\'::uuid,\'user.updated\', now() - interval \'3 days\'),
    (\'0c000000-0000-0000-0000-00000000000c\'::uuid,\'role.assigned\',now() - interval \'1 day\'),
    (\'0c000000-0000-0000-0000-00000000000c\'::uuid,\'org.unsuspended\',now()),
    (\'0d000000-0000-0000-0000-00000000000d\'::uuid,\'role.assigned\',now()))
SELECT count(*) FROM a JOIN o ON o."Id" = a."OrgId"
 WHERE o."Active" = false AND o."SuspendedAt" IS NOT NULL
   AND a."CreatedAt" > o."SuspendedAt"
   AND a."Action" NOT IN (\'org.unsuspended\',\'org.suspended\');'

# D-04 — actor A: two mutations 10 min apart from a known IP  → 2 rows (burst).
#        actor B: one mutation from a known IP                → 0 rows.
#        actor C: one mutation from an IP never seen before   → 1 row (new-source).
assert D-04 3 $'
WITH al("Id","ActorId","Action","IpAddress","CreatedAt") AS (VALUES
    (1::bigint,\'0000000a-0000-0000-0000-00000000000a\'::uuid,\'user.mfa.passkey_registered\',\'10.0.0.1\',now() - interval \'2 hours\'),
    (2,       \'0000000a-0000-0000-0000-00000000000a\'::uuid,\'user.mfa.phone_removed\',     \'10.0.0.1\',now() - interval \'110 minutes\'),
    (3,       \'0000000b-0000-0000-0000-00000000000b\'::uuid,\'user.mfa.passkey_registered\',\'10.0.0.2\',now() - interval \'3 hours\'),
    (4,       \'0000000c-0000-0000-0000-00000000000c\'::uuid,\'user.mfa.totp_replaced\',     \'203.0.113.9\',now() - interval \'1 hour\'),
    -- history, older than the window: establishes 10.0.0.1 and 10.0.0.2 as known
    (5,       \'0000000a-0000-0000-0000-00000000000a\'::uuid,\'user.login.success\',\'10.0.0.1\',now() - interval \'30 days\'),
    (6,       \'0000000b-0000-0000-0000-00000000000b\'::uuid,\'user.login.success\',\'10.0.0.2\',now() - interval \'30 days\')),
 mfa AS (SELECT * FROM al
          WHERE "Action" IN (\'user.mfa.totp_enabled\',\'user.mfa.totp_replaced\',
                             \'user.mfa.passkey_registered\',\'user.mfa.passkey_removed\',
                             \'user.mfa.phone_verified\',\'user.mfa.phone_removed\',
                             \'user.mfa.backup_codes_regenerated\')
            AND "CreatedAt" > now() - interval \'24 hours\')
SELECT count(*) FROM (
  SELECT DISTINCT m."CreatedAt", m."ActorId", m."Action", m."IpAddress"
    FROM mfa m
    LEFT JOIN LATERAL (
          SELECT m2."ActorId" FROM mfa m2
           WHERE m2."ActorId" = m."ActorId" AND m2."Id" <> m."Id"
             AND m2."CreatedAt" BETWEEN m."CreatedAt" - interval \'1 hour\'
                                    AND m."CreatedAt" + interval \'1 hour\'
           LIMIT 1) b ON true
   WHERE b."ActorId" IS NOT NULL
      OR NOT EXISTS (SELECT 1 FROM al h
                      WHERE h."ActorId" = m."ActorId" AND h."IpAddress" = m."IpAddress"
                        AND h."CreatedAt" < now() - interval \'24 hours\')) x;'

# D-09 — sa.* from a source with prior history (ignored) and from a new one (hit).
assert D-09 1 $'
WITH al("ActorId","Action","IpAddress","CreatedAt") AS (VALUES
    (\'0000000d-0000-0000-0000-00000000000d\'::uuid,\'sa.pat.created\',\'10.0.0.5\',now() - interval \'1 hour\'),
    (\'0000000d-0000-0000-0000-00000000000d\'::uuid,\'sa.key.added\',  \'198.51.100.7\',now() - interval \'2 hours\'),
    (\'0000000d-0000-0000-0000-00000000000d\'::uuid,\'sa.created\',    \'10.0.0.5\',now() - interval \'40 days\'))
SELECT count(*) FROM (
  SELECT a."ActorId", a."IpAddress"
    FROM al a
   WHERE a."Action" LIKE \'sa.%\' AND a."CreatedAt" > now() - interval \'24 hours\'
     AND a."IpAddress" IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM al h
                      WHERE h."ActorId" = a."ActorId" AND h."IpAddress" = a."IpAddress"
                        AND h."CreatedAt" <= now() - interval \'24 hours\')
   GROUP BY 1,2) x;'

# D-13 — nested providers[]: a relative path and a data: URI are fine, an
# absolute off-instance URL is the beacon. A theme with no providers key must
# not error (the jsonb_typeof guard).
assert D-13 1 $'
WITH p("Slug","LoginTheme") AS (VALUES
    (\'a\', \'{"providers":[{"logo_url":"/img/ok.svg"},{"logo_url":"data:image/png;base64,AA"}]}\'::jsonb),
    (\'b\', \'{"providers":[{"logo_url":"https://evil.example/beacon.png"}]}\'::jsonb),
    (\'c\', \'{"primary_color":"#fff"}\'::jsonb),
    (\'d\', \'{"providers":"not-an-array"}\'::jsonb))
SELECT count(*) FROM p,
     LATERAL jsonb_array_elements(
       CASE WHEN jsonb_typeof(p."LoginTheme"->\'providers\') = \'array\'
            THEN p."LoginTheme"->\'providers\' ELSE \'[]\'::jsonb END) prov
 WHERE prov->>\'logo_url\' IS NOT NULL
   AND prov->>\'logo_url\' !~ \'^(/|data:)\';'

echo "────────────────────────────────────────────────────────────────"
if [ "${FAILS}" -eq 0 ]; then
  echo " all detection predicates fire as intended"
  exit 0
fi
echo " ${FAILS} predicate(s) did not behave as intended" >&2
exit 1
