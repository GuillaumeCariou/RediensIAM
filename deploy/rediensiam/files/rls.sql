-- ═══════════════════════════════════════════════════════════════════════════════
-- S-5 phase 2 — PostgreSQL row-level security for the RediensIAM application
-- database.
--
-- Applied by the `<release>-rls` hook Job when postgres.rls.enabled is true, as the
-- table owner (`iam_app`). Idempotent and re-runnable: it re-creates the policies
-- rather than adding to them.
--
-- WHAT THIS IS FOR
-- Tenant isolation is currently ~200 hand-written `&& OrgId == …` conjuncts in C#
-- (SECURITY-AUDIT-LOG.md step 03 §3.1). Every one of them is an opportunity to forget,
-- and one already went wrong in a way the codebase documents in a comment
-- (ServiceAccountController.cs:29-33). Query filters in the ORM fix that for LINQ;
-- they do not survive raw SQL, a psql session, or a second service pointed at the
-- same database. These policies do.
--
-- FAIL-CLOSED, DELIBERATELY
-- A connection that has not set `rediensiam.org_id` sees ZERO rows in every table
-- below. That is not a degraded mode for this application — it is a total outage.
-- The alternative (treat "unset" as "unscoped") is a control that is off whenever
-- anyone forgets, which is the failure this whole finding is about.
--
-- FORCE, NOT JUST ENABLE
-- `iam_app` OWNS these tables, and a table owner is exempt from its own RLS unless
-- the table is FORCEd. `ENABLE ROW LEVEL SECURITY` alone would render policies that
-- have no effect on the only role that connects — a control that verifies as
-- present and does nothing.
--
-- THE SESSION VARIABLE
--   SET rediensiam.org_id = '<uuid>'   → that organisation only
--   SET rediensiam.org_id = 'system'   → unscoped; for login before a tenant is
--                                        known, bootstrap, the audit retention
--                                        sweep, SuperAdmin listings, EF migrations
--   unset, empty, or malformed         → nothing is visible
--
-- `SET`, not `SET LOCAL`: most EF Core reads run outside an explicit transaction,
-- where `SET LOCAL` silently does nothing. Session scope is safe with Npgsql
-- pooling because Npgsql issues `DISCARD ALL` when a connection returns to the
-- pool — do NOT set `No Reset On Close=true` in the DSN, or one tenant's scope
-- leaks into the next request that rents that connection.
--
-- WHAT IT DOES NOT COVER
--   - `Instances` and `__EFMigrationsHistory` are deployment-global and carry no
--     tenant column. They are listed explicitly below; a table that is neither
--     tenant-scoped nor listed makes this script FAIL, so a future EF migration
--     cannot quietly add an unprotected tenant table.
--   - Hydra's and Keto's databases. They have no tenant column at all; their
--     isolation is the T-04 role split.
--   - `pg_dumpall` run by `iam_backup`. libpq sets `row_security = off`, which
--     ERRORS for a role that cannot bypass RLS, so the nightly backup ABORTS on the
--     first protected table. `ALTER ROLE iam_backup BYPASSRLS` (superuser, once) is
--     a hard prerequisite — see SECURITY-AUDIT-LOG.md step 18 §3.
-- ═══════════════════════════════════════════════════════════════════════════════

\set ON_ERROR_STOP on

BEGIN;

-- ── Prerequisite: the backup must be able to bypass these policies ────────────
-- This is checked, not documented, because the failure it prevents is the worst
-- shape a control can have. pg_dump sets `row_security = off`; for a role without
-- BYPASSRLS that ERRORS, so the nightly pg_dumpall aborts on the first policied
-- table — and the moment that stops working is the moment the policies start.
-- Refusing to apply a single policy is recoverable in one statement; discovering
-- three weeks of failed backups is not. This is T-03's whole lesson.
--
-- iam_app cannot grant it (ALTER ROLE … BYPASSRLS is superuser-only), which is
-- exactly why this is an abort with the command in it rather than a fix-up.
DO $pre$
DECLARE backup_role constant text := 'iam_backup';
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = backup_role)
     AND NOT (SELECT rolbypassrls FROM pg_roles WHERE rolname = backup_role) THEN
    RAISE EXCEPTION USING
      MESSAGE = format('%s cannot bypass RLS — enabling these policies would stop the nightly backup', backup_role),
      HINT    = format('run as superuser, once, BEFORE this deploy: ALTER ROLE %I BYPASSRLS;', backup_role);
  END IF;
END
$pre$;

-- ── Scope accessors ───────────────────────────────────────────────────────────
-- Plain SQL, so the planner can inline them into the policy expression; a plpgsql
-- function with an EXCEPTION block opens a subtransaction on every row evaluated.

-- TRUE only for the explicit opt-out. Absence is never unscoped.
CREATE OR REPLACE FUNCTION rls_unscoped() RETURNS boolean
LANGUAGE sql STABLE AS $fn$
  SELECT coalesce(current_setting('rediensiam.org_id', true), '') = 'system'
$fn$;

-- NULL for anything that is not a UUID, including 'system', empty and unset. NULL
-- compares equal to nothing, so every policy below denies rather than erroring —
-- a malformed scope is a bug in the caller, not a reason to 500 mid-request.
CREATE OR REPLACE FUNCTION rls_org() RETURNS uuid
LANGUAGE sql STABLE AS $fn$
  SELECT CASE
    WHEN coalesce(current_setting('rediensiam.org_id', true), '') ~*
         '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    THEN current_setting('rediensiam.org_id', true)::uuid
  END
$fn$;

-- ── Policies ──────────────────────────────────────────────────────────────────
DO $do$
DECLARE
  -- Tables with no tenant column, by design. Anything in `public` that is neither
  -- here nor in the policy table below aborts this script.
  -- Deployment-global by construction: they carry no OrgId and belong to no tenant.
  -- shared_state / rate_counters / webhook_pending / data_protection_keys replaced Dragonfly —
  -- a session cookie, a lockout counter and a delivery job are the deployment's, not an
  -- organisation's. Scoping them would be a fail-closed outage the moment RLS is switched on.
  global_tables constant text[] := ARRAY[
    'Instances', '__EFMigrationsHistory',
    'shared_state', 'rate_counters', 'webhook_pending', 'data_protection_keys'];
  t      record;
  scoped text[] := '{}';
  stray  text;
BEGIN
  FOR t IN
    SELECT * FROM (VALUES
      -- Tenant root and the tables that carry OrgId directly.
      -- user_lists.OrgId IS NULL is the `__system__` list; NULL = rls_org() is NULL,
      -- so it is invisible to every tenant and reachable only when unscoped. That is
      -- precisely the ServiceAccountController.cs:29-33 bug class, closed in the
      -- schema instead of by convention.
      ('organisations',          '"Id" = rls_org()'),
      ('user_lists',             '"OrgId" = rls_org()'),
      ('org_roles',              '"OrgId" = rls_org()'),
      ('org_smtp_configs',       '"OrgId" = rls_org()'),
      ('projects',               '"OrgId" = rls_org()'),
      ('webhooks',               '"OrgId" = rls_org()'),
      ('service_account_roles',  '"OrgId" = rls_org()'),
      ('audit_log',              '"OrgId" = rls_org()'),
      -- A delegated session names the organisation it enters, so it is that tenant's row: an
      -- operator's session into Acme must be visible to Acme and to nobody else.
      ('impersonation_sessions', '"OrgId" = rls_org()'),

      -- Reached through user_lists. The sub-select is itself filtered by
      -- user_lists' own policy, which is the same predicate — consistent, and the
      -- reason these do not need their own OrgId column.
      ('users',
       'EXISTS (SELECT 1 FROM public.user_lists ul
                 WHERE ul."Id" = users."UserListId" AND ul."OrgId" = rls_org())'),
      ('service_accounts',
       'EXISTS (SELECT 1 FROM public.user_lists ul
                 WHERE ul."Id" = service_accounts."UserListId" AND ul."OrgId" = rls_org())'),

      -- Per-user credential material.
      ('backup_codes',
       'EXISTS (SELECT 1 FROM public.users u JOIN public.user_lists ul ON ul."Id" = u."UserListId"
                 WHERE u."Id" = backup_codes."UserId" AND ul."OrgId" = rls_org())'),
      ('email_tokens',
       'EXISTS (SELECT 1 FROM public.users u JOIN public.user_lists ul ON ul."Id" = u."UserListId"
                 WHERE u."Id" = email_tokens."UserId" AND ul."OrgId" = rls_org())'),
      ('user_social_accounts',
       'EXISTS (SELECT 1 FROM public.users u JOIN public.user_lists ul ON ul."Id" = u."UserListId"
                 WHERE u."Id" = user_social_accounts."UserId" AND ul."OrgId" = rls_org())'),
      ('webauthn_credentials',
       'EXISTS (SELECT 1 FROM public.users u JOIN public.user_lists ul ON ul."Id" = u."UserListId"
                 WHERE u."Id" = webauthn_credentials."UserId" AND ul."OrgId" = rls_org())'),

      -- Reached through projects.
      ('roles',
       'EXISTS (SELECT 1 FROM public.projects p
                 WHERE p."Id" = roles."ProjectId" AND p."OrgId" = rls_org())'),
      ('saml_idp_configs',
       'EXISTS (SELECT 1 FROM public.projects p
                 WHERE p."Id" = saml_idp_configs."ProjectId" AND p."OrgId" = rls_org())'),
      ('user_project_roles',
       'EXISTS (SELECT 1 FROM public.projects p
                 WHERE p."Id" = user_project_roles."ProjectId" AND p."OrgId" = rls_org())'),

      -- Reached through service_accounts and webhooks.
      ('personal_access_tokens',
       'EXISTS (SELECT 1 FROM public.service_accounts sa JOIN public.user_lists ul ON ul."Id" = sa."UserListId"
                 WHERE sa."Id" = personal_access_tokens."ServiceAccountId" AND ul."OrgId" = rls_org())'),
      ('webhook_deliveries',
       'EXISTS (SELECT 1 FROM public.webhooks w
                 WHERE w."Id" = webhook_deliveries."WebhookId" AND w."OrgId" = rls_org())')
    ) AS v(tbl, expr)
  LOOP
    -- A table named here that does not exist means this script and the schema have
    -- diverged. Skipping it silently would leave it unprotected on the next
    -- migration that creates it.
    IF to_regclass(format('public.%I', t.tbl)) IS NULL THEN
      RAISE EXCEPTION 'rls.sql names public.% but it does not exist — schema and policy set have diverged', t.tbl;
    END IF;

    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t.tbl);
    EXECUTE format('ALTER TABLE public.%I FORCE  ROW LEVEL SECURITY', t.tbl);
    EXECUTE format('DROP POLICY IF EXISTS rediensiam_tenant ON public.%I', t.tbl);
    -- FOR ALL with both USING and WITH CHECK: USING decides what SELECT/UPDATE/
    -- DELETE can see, WITH CHECK decides what INSERT/UPDATE may write. Without the
    -- WITH CHECK half a scoped connection can still INSERT a row into another
    -- tenant — it just cannot read it back.
    EXECUTE format(
      'CREATE POLICY rediensiam_tenant ON public.%I FOR ALL USING (rls_unscoped() OR (%s)) WITH CHECK (rls_unscoped() OR (%s))',
      t.tbl, t.expr, t.expr);

    scoped := scoped || t.tbl;
  END LOOP;

  -- Coverage gate. A new EF migration that adds a tenant-owned table and does not
  -- add it above fails the deploy here, instead of shipping a table that RLS does
  -- not cover and that nothing reports on.
  FOR stray IN
    SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')  -- 'p' too: a partitioned table is exactly the case this gate exists to catch
      AND NOT (c.relname = ANY (scoped))
      AND NOT (c.relname = ANY (global_tables))
  LOOP
    RAISE EXCEPTION 'public.% has no RLS policy and is not listed as deployment-global — add it to rls.sql or to global_tables', stray;
  END LOOP;

  RAISE NOTICE 'RLS applied to % tables', array_length(scoped, 1);
END
$do$;

COMMIT;

-- ── Verification ──────────────────────────────────────────────────────────────
-- Structural: every row must read t/t/1. Any f is an unprotected tenant table.
SELECT c.relname                AS table_name,
       c.relrowsecurity         AS rls_enabled,
       c.relforcerowsecurity    AS rls_forced,
       count(p.polname)         AS policies
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
LEFT JOIN pg_policy p ON p.polrelid = c.oid
WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')  -- 'p' too: a partitioned table is exactly the case this gate exists to catch
  AND c.relname NOT IN ('Instances', '__EFMigrationsHistory')
GROUP BY 1, 2, 3
ORDER BY rls_enabled, rls_forced, 1;
