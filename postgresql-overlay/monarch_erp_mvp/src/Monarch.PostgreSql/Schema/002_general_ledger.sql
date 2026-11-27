-- 002: Authenticated tenant context, membership lifecycle and immutable General Ledger foundation.
CREATE EXTENSION IF NOT EXISTS pgcrypto;

DO $do$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'monarch_security_definer') THEN
   CREATE ROLE monarch_security_definer NOLOGIN NOINHERIT BYPASSRLS;
 ELSIF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'monarch_security_definer' AND rolbypassrls AND NOT rolcanlogin) THEN
   RAISE EXCEPTION 'monarch_security_definer must be NOLOGIN and BYPASSRLS';
 END IF;
END
$do$;

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
CREATE SCHEMA IF NOT EXISTS monarch_security AUTHORIZATION monarch_security_definer;
GRANT USAGE ON SCHEMA public TO monarch_security_definer;
GRANT USAGE ON SCHEMA monarch_security TO PUBLIC;

CREATE TABLE monarch_security.context_secrets (
 secret_id smallint PRIMARY KEY CHECK(secret_id = 1),
 secret bytea NOT NULL CHECK(octet_length(secret) >= 32)
);
REVOKE ALL ON monarch_security.context_secrets FROM PUBLIC;
GRANT SELECT ON monarch_security.context_secrets TO monarch_security_definer;

ALTER TABLE monarch_user_branch_grants
 ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true,
 ADD COLUMN IF NOT EXISTS revoked_at timestamptz NULL,
 ADD COLUMN IF NOT EXISTS revoked_by uuid NULL;
CREATE INDEX IF NOT EXISTS ix_monarch_grants_active
 ON monarch_user_branch_grants(company_id, branch_id, user_id) WHERE is_active;

CREATE TABLE monarch_membership_audit (
 audit_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 company_id uuid NOT NULL,
 branch_id uuid NOT NULL,
 user_id uuid NOT NULL,
 action varchar(16) NOT NULL CHECK(action IN ('GRANTED','REVOKED')),
 actor_user_id uuid NOT NULL,
 occurred_at timestamptz NOT NULL DEFAULT now(),
 reason varchar(500) NULL,
 FOREIGN KEY(company_id) REFERENCES monarch_companies(company_id),
 FOREIGN KEY(company_id, branch_id) REFERENCES monarch_branches(company_id, branch_id),
 FOREIGN KEY(company_id, user_id) REFERENCES monarch_tenant_users(company_id, user_id),
 FOREIGN KEY(company_id, actor_user_id) REFERENCES monarch_tenant_users(company_id, user_id)
);
ALTER TABLE monarch_membership_audit ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_membership_audit FORCE ROW LEVEL SECURITY;

GRANT SELECT, INSERT, UPDATE ON monarch_user_branch_grants, monarch_membership_audit TO monarch_security_definer;
GRANT SELECT ON monarch_tenant_users, monarch_branches TO monarch_security_definer;

CREATE OR REPLACE FUNCTION monarch_security.context_is_valid()
RETURNS boolean LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security AS $context$
DECLARE c text; b text; u text; issued text; signature text; secret_value bytea;
BEGIN
 c := current_setting('monarch.company_id', true);
 b := current_setting('monarch.branch_id', true);
 u := current_setting('monarch.user_id', true);
 issued := current_setting('monarch.context_issued_at', true);
 signature := current_setting('monarch.context_signature', true);
 IF c IS NULL OR b IS NULL OR u IS NULL OR issued IS NULL OR signature IS NULL THEN RETURN false; END IF;
 IF abs(extract(epoch FROM statement_timestamp()) - issued::bigint) > 300 THEN RETURN false; END IF;
 SELECT secret INTO secret_value FROM monarch_security.context_secrets WHERE secret_id = 1;
 IF secret_value IS NULL THEN RETURN false; END IF;
 RETURN hmac(convert_to(c || ':' || b || ':' || u || ':' || issued, 'UTF8'), secret_value, 'sha256') = decode(signature, 'hex');
EXCEPTION WHEN OTHERS THEN
 RETURN false;
END
$context$;

CREATE OR REPLACE FUNCTION monarch_security.set_tenant_context(
 p_company uuid, p_branch uuid, p_user uuid, p_issued_at bigint, p_signature text)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security AS $context$
DECLARE secret_value bytea; message text;
BEGIN
 IF p_company IS NULL OR p_branch IS NULL OR p_user IS NULL OR p_issued_at IS NULL OR p_signature IS NULL THEN
   RAISE EXCEPTION 'Signed tenant context is required';
 END IF;
 IF abs(extract(epoch FROM clock_timestamp()) - p_issued_at) > 300 THEN
   RAISE EXCEPTION 'Tenant context signature expired';
 END IF;
 SELECT secret INTO secret_value FROM monarch_security.context_secrets WHERE secret_id = 1;
 IF secret_value IS NULL THEN RAISE EXCEPTION 'Tenant context secret is not provisioned'; END IF;
 message := p_company::text || ':' || p_branch::text || ':' || p_user::text || ':' || p_issued_at::text;
 IF hmac(convert_to(message, 'UTF8'), secret_value, 'sha256') <> decode(p_signature, 'hex') THEN
   RAISE EXCEPTION 'Invalid tenant context signature';
 END IF;
 IF NOT EXISTS (
   SELECT 1 FROM public.monarch_user_branch_grants g
   JOIN public.monarch_tenant_users u ON u.company_id=g.company_id AND u.user_id=g.user_id AND u.is_active
   JOIN public.monarch_branches b ON b.company_id=g.company_id AND b.branch_id=g.branch_id AND b.is_active
   WHERE g.company_id=p_company AND g.branch_id=p_branch AND g.user_id=p_user AND g.is_active) THEN
   RAISE EXCEPTION 'Tenant membership is inactive or not authorized';
 END IF;
 PERFORM set_config('monarch.company_id', p_company::text, true);
 PERFORM set_config('monarch.branch_id', p_branch::text, true);
 PERFORM set_config('monarch.user_id', p_user::text, true);
 PERFORM set_config('monarch.context_issued_at', p_issued_at::text, true);
 PERFORM set_config('monarch.context_signature', p_signature, true);
END
$context$;

ALTER FUNCTION monarch_security.context_is_valid() OWNER TO monarch_security_definer;
ALTER FUNCTION monarch_security.set_tenant_context(uuid,uuid,uuid,bigint,text) OWNER TO monarch_security_definer;
GRANT EXECUTE ON FUNCTION monarch_security.context_is_valid() TO PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.set_tenant_context(uuid,uuid,uuid,bigint,text) TO PUBLIC;

CREATE OR REPLACE FUNCTION monarch_security.grant_branch_access(
 p_company uuid, p_branch uuid, p_user uuid, p_actor uuid, p_reason varchar DEFAULT NULL)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, monarch_security AS $membership$
BEGIN
 IF p_company IS NULL OR p_branch IS NULL OR p_user IS NULL OR p_actor IS NULL THEN
   RAISE EXCEPTION 'Membership identifiers are required';
 END IF;
 INSERT INTO public.monarch_user_branch_grants(company_id, branch_id, user_id, granted_at, is_active, revoked_at, revoked_by)
 VALUES(p_company, p_branch, p_user, now(), true, NULL, NULL)
 ON CONFLICT(company_id, branch_id, user_id) DO UPDATE
 SET granted_at = now(), is_active = true, revoked_at = NULL, revoked_by = NULL;
 INSERT INTO public.monarch_membership_audit(company_id, branch_id, user_id, action, actor_user_id, reason)
 VALUES(p_company, p_branch, p_user, 'GRANTED', p_actor, p_reason);
END
$membership$;

CREATE OR REPLACE FUNCTION monarch_security.revoke_branch_access(
 p_company uuid, p_branch uuid, p_user uuid, p_actor uuid, p_reason varchar DEFAULT NULL)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, monarch_security AS $membership$
DECLARE changed integer;
BEGIN
 IF p_company IS NULL OR p_branch IS NULL OR p_user IS NULL OR p_actor IS NULL THEN
   RAISE EXCEPTION 'Membership identifiers are required';
 END IF;
 UPDATE public.monarch_user_branch_grants
 SET is_active = false, revoked_at = now(), revoked_by = p_actor
 WHERE company_id = p_company AND branch_id = p_branch AND user_id = p_user AND is_active;
 GET DIAGNOSTICS changed = ROW_COUNT;
 IF changed = 1 THEN
   INSERT INTO public.monarch_membership_audit(company_id, branch_id, user_id, action, actor_user_id, reason)
   VALUES(p_company, p_branch, p_user, 'REVOKED', p_actor, p_reason);
 END IF;
 RETURN changed = 1;
END
$membership$;

ALTER FUNCTION monarch_security.grant_branch_access(uuid,uuid,uuid,uuid,varchar) OWNER TO monarch_security_definer;
ALTER FUNCTION monarch_security.revoke_branch_access(uuid,uuid,uuid,uuid,varchar) OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.grant_branch_access(uuid,uuid,uuid,uuid,varchar) FROM PUBLIC;
REVOKE ALL ON FUNCTION monarch_security.revoke_branch_access(uuid,uuid,uuid,uuid,varchar) FROM PUBLIC;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON monarch_user_branch_grants FROM PUBLIC;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON monarch_membership_audit FROM PUBLIC;

-- Replace the initial GUC-only policies with signed-context policies.
ALTER TABLE monarch_companies ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_companies FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_company_tenant_policy ON monarch_companies;
CREATE POLICY monarch_company_tenant_policy ON monarch_companies
 USING(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid)
 WITH CHECK(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid);

ALTER TABLE monarch_branches ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_branches FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_branch_tenant_policy ON monarch_branches;
CREATE POLICY monarch_branch_tenant_policy ON monarch_branches
 USING(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid)
 WITH CHECK(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid);

ALTER TABLE monarch_tenant_users ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_tenant_users FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_users_tenant_policy ON monarch_tenant_users;
CREATE POLICY monarch_users_tenant_policy ON monarch_tenant_users
 USING(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND user_id = NULLIF(current_setting('monarch.user_id', true), '')::uuid)
 WITH CHECK(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid);

ALTER TABLE monarch_user_branch_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_user_branch_grants FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_grants_tenant_policy ON monarch_user_branch_grants;
CREATE POLICY monarch_grants_tenant_policy ON monarch_user_branch_grants
 USING(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid
   AND user_id = NULLIF(current_setting('monarch.user_id', true), '')::uuid)
 WITH CHECK(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid);

ALTER TABLE monarch_outbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_outbox FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_outbox_tenant_policy ON monarch_outbox;
CREATE POLICY monarch_outbox_tenant_policy ON monarch_outbox
 USING(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid)
 WITH CHECK(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid);

CREATE POLICY monarch_membership_audit_read ON monarch_membership_audit
 FOR SELECT USING(monarch_security.context_is_valid()
   AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid
   AND user_id = NULLIF(current_setting('monarch.user_id', true), '')::uuid);

CREATE TABLE monarch_gl_accounts (
 company_id uuid NOT NULL,
 account_id uuid NOT NULL,
 account_code varchar(40) NOT NULL,
 account_name varchar(240) NOT NULL,
 account_type varchar(24) NOT NULL CHECK(account_type IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE')),
 normal_side char(1) NOT NULL CHECK(normal_side IN ('D','C')),
 is_active boolean NOT NULL DEFAULT true,
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(company_id, account_id),
 UNIQUE(company_id, account_code),
 FOREIGN KEY(company_id) REFERENCES monarch_companies(company_id)
);

CREATE TABLE monarch_gl_journal_sequences (
 company_id uuid NOT NULL,
 branch_id uuid NOT NULL,
 fiscal_year integer NOT NULL CHECK(fiscal_year BETWEEN 2000 AND 2200),
 next_number bigint NOT NULL CHECK(next_number > 0),
 PRIMARY KEY(company_id, branch_id, fiscal_year),
 FOREIGN KEY(company_id, branch_id) REFERENCES monarch_branches(company_id, branch_id)
);

CREATE TABLE monarch_gl_journals (
 company_id uuid NOT NULL,
 branch_id uuid NOT NULL,
 journal_id uuid NOT NULL,
 fiscal_year integer NOT NULL,
 journal_number bigint NOT NULL,
 journal_date date NOT NULL,
 description varchar(500) NOT NULL,
 currency_code char(3) NOT NULL,
 status varchar(12) NOT NULL DEFAULT 'DRAFT' CHECK(status IN ('DRAFT','POSTED','REVERSED')),
 created_by uuid NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 posted_by uuid NULL,
 posted_at timestamptz NULL,
 reverses_journal_id uuid NULL,
 PRIMARY KEY(company_id, branch_id, journal_id),
 UNIQUE(company_id, branch_id, fiscal_year, journal_number),
 CHECK(fiscal_year = EXTRACT(YEAR FROM journal_date)::integer),
 FOREIGN KEY(company_id, branch_id) REFERENCES monarch_branches(company_id, branch_id),
 FOREIGN KEY(company_id, branch_id, reverses_journal_id) REFERENCES monarch_gl_journals(company_id, branch_id, journal_id)
);
CREATE UNIQUE INDEX ux_monarch_gl_single_reversal ON monarch_gl_journals(company_id, branch_id, reverses_journal_id) WHERE reverses_journal_id IS NOT NULL;

CREATE TABLE monarch_gl_journal_lines (
 company_id uuid NOT NULL,
 branch_id uuid NOT NULL,
 journal_id uuid NOT NULL,
 line_number integer NOT NULL CHECK(line_number > 0),
 account_id uuid NOT NULL,
 description varchar(300) NULL,
 debit_minor bigint NOT NULL DEFAULT 0 CHECK(debit_minor >= 0),
 credit_minor bigint NOT NULL DEFAULT 0 CHECK(credit_minor >= 0),
 PRIMARY KEY(company_id, branch_id, journal_id, line_number),
 CHECK ((debit_minor > 0 AND credit_minor = 0) OR (credit_minor > 0 AND debit_minor = 0)),
 FOREIGN KEY(company_id, branch_id, journal_id)
   REFERENCES monarch_gl_journals(company_id, branch_id, journal_id) ON DELETE RESTRICT,
 FOREIGN KEY(company_id, account_id) REFERENCES monarch_gl_accounts(company_id, account_id)
);

CREATE INDEX ix_monarch_gl_journals_reporting ON monarch_gl_journals(company_id, branch_id, journal_date, status);
CREATE INDEX ix_monarch_gl_lines_account ON monarch_gl_journal_lines(company_id, account_id, journal_id);

ALTER TABLE monarch_gl_accounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_gl_accounts FORCE ROW LEVEL SECURITY;
CREATE POLICY monarch_gl_accounts_scope ON monarch_gl_accounts
 USING(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid)
 WITH CHECK(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid);

ALTER TABLE monarch_gl_journal_sequences ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_gl_journal_sequences FORCE ROW LEVEL SECURITY;
CREATE POLICY monarch_gl_sequences_scope ON monarch_gl_journal_sequences
 USING(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid)
 WITH CHECK(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid);

ALTER TABLE monarch_gl_journals ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_gl_journals FORCE ROW LEVEL SECURITY;
CREATE POLICY monarch_gl_journals_scope ON monarch_gl_journals
 USING(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid)
 WITH CHECK(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid);

ALTER TABLE monarch_gl_journal_lines ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_gl_journal_lines FORCE ROW LEVEL SECURITY;
CREATE POLICY monarch_gl_lines_scope ON monarch_gl_journal_lines
 USING(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid)
 WITH CHECK(monarch_security.context_is_valid() AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid);

CREATE OR REPLACE FUNCTION monarch_gl_guard_posted_journal()
RETURNS trigger LANGUAGE plpgsql AS $guard$
DECLARE target_company uuid; target_branch uuid; target_journal uuid; debit_total numeric; credit_total numeric;
BEGIN
 target_company := COALESCE(NEW.company_id, OLD.company_id);
 target_branch := COALESCE(NEW.branch_id, OLD.branch_id);
 target_journal := COALESCE(NEW.journal_id, OLD.journal_id);
 IF TG_TABLE_NAME = 'monarch_gl_journals' THEN
   IF TG_OP = 'INSERT' AND NEW.status <> 'DRAFT' THEN
     RAISE EXCEPTION 'New journals must start in DRAFT status';
   END IF;
   IF TG_OP = 'UPDATE' AND OLD.status IN ('POSTED','REVERSED') THEN
     RAISE EXCEPTION 'Posted or reversed journals are immutable; use a reversal journal';
   END IF;
   IF TG_OP = 'DELETE' AND OLD.status IN ('POSTED','REVERSED') THEN
     RAISE EXCEPTION 'Posted or reversed journals cannot be deleted';
   END IF;
   IF TG_OP = 'UPDATE' AND NEW.status <> OLD.status AND NOT (OLD.status = 'DRAFT' AND NEW.status = 'POSTED') THEN
     RAISE EXCEPTION 'Invalid journal status transition';
   END IF;
   IF TG_OP = 'UPDATE' AND NEW.status = 'POSTED' THEN
     SELECT COALESCE(sum(debit_minor),0), COALESCE(sum(credit_minor),0)
       INTO debit_total, credit_total FROM monarch_gl_journal_lines
       WHERE company_id=NEW.company_id AND branch_id=NEW.branch_id AND journal_id=NEW.journal_id;
     IF debit_total <= 0 OR debit_total <> credit_total THEN
       RAISE EXCEPTION 'Journal is not balanced: debit %, credit %', debit_total, credit_total;
     END IF;
     NEW.posted_at := now();
   END IF;
   IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
 END IF;
 IF EXISTS (SELECT 1 FROM monarch_gl_journals j
   WHERE j.company_id=target_company AND j.branch_id=target_branch AND j.journal_id=target_journal
     AND j.status IN ('POSTED','REVERSED')) THEN
   RAISE EXCEPTION 'Lines of posted or reversed journals are immutable';
 END IF;
 IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
END
$guard$;

CREATE TRIGGER trg_monarch_gl_journal_guard
 BEFORE INSERT OR UPDATE OR DELETE ON monarch_gl_journals
 FOR EACH ROW EXECUTE FUNCTION monarch_gl_guard_posted_journal();
CREATE TRIGGER trg_monarch_gl_line_guard
 BEFORE INSERT OR UPDATE OR DELETE ON monarch_gl_journal_lines
 FOR EACH ROW EXECUTE FUNCTION monarch_gl_guard_posted_journal();

CREATE OR REPLACE FUNCTION monarch_gl_next_journal_number(p_company uuid, p_branch uuid, p_year integer)
RETURNS bigint LANGUAGE plpgsql AS $sequence$
DECLARE allocated bigint;
BEGIN
 INSERT INTO monarch_gl_journal_sequences(company_id, branch_id, fiscal_year, next_number)
 VALUES(p_company, p_branch, p_year, 2)
 ON CONFLICT(company_id, branch_id, fiscal_year)
 DO UPDATE SET next_number = monarch_gl_journal_sequences.next_number + 1
 RETURNING next_number - 1 INTO allocated;
 RETURN allocated;
END
$sequence$;
