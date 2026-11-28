-- MONARCH ERP central PostgreSQL foundation.
CREATE TABLE IF NOT EXISTS monarch_companies (
 company_id uuid PRIMARY KEY, company_code varchar(40) NOT NULL UNIQUE,
 legal_name varchar(240) NOT NULL, is_active boolean NOT NULL DEFAULT true, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS monarch_branches (
 company_id uuid NOT NULL REFERENCES monarch_companies(company_id), branch_id uuid NOT NULL,
 branch_code varchar(40) NOT NULL, branch_name varchar(200) NOT NULL, is_active boolean NOT NULL DEFAULT true,
 PRIMARY KEY(company_id, branch_id), UNIQUE(company_id, branch_code)
);
CREATE TABLE IF NOT EXISTS monarch_tenant_users (
 company_id uuid NOT NULL REFERENCES monarch_companies(company_id), user_id uuid NOT NULL,
 normalized_login varchar(180) NOT NULL, display_name varchar(200) NOT NULL, is_active boolean NOT NULL DEFAULT true,
 PRIMARY KEY(company_id, user_id), UNIQUE(company_id, normalized_login)
);
CREATE TABLE IF NOT EXISTS monarch_user_branch_grants (
 company_id uuid NOT NULL, branch_id uuid NOT NULL, user_id uuid NOT NULL, granted_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(company_id, branch_id, user_id),
 FOREIGN KEY(company_id, branch_id) REFERENCES monarch_branches(company_id, branch_id),
 FOREIGN KEY(company_id, user_id) REFERENCES monarch_tenant_users(company_id, user_id)
);
CREATE TABLE IF NOT EXISTS monarch_outbox (
 outbox_id uuid PRIMARY KEY DEFAULT gen_random_uuid(), company_id uuid NOT NULL, branch_id uuid NOT NULL,
 event_id uuid NOT NULL, aggregate_type varchar(100) NOT NULL, aggregate_id varchar(160) NOT NULL,
 event_type varchar(160) NOT NULL, payload jsonb NOT NULL, occurred_at timestamptz NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(), published_at timestamptz NULL,
 attempts integer NOT NULL DEFAULT 0 CHECK(attempts >= 0), last_error text NULL,
 UNIQUE(company_id, event_id),
 FOREIGN KEY(company_id, branch_id) REFERENCES monarch_branches(company_id, branch_id)
);
CREATE INDEX IF NOT EXISTS ix_monarch_outbox_pending ON monarch_outbox(created_at) WHERE published_at IS NULL;

ALTER TABLE monarch_companies ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_companies FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_company_tenant_policy ON monarch_companies;
CREATE POLICY monarch_company_tenant_policy ON monarch_companies
 USING(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid)
 WITH CHECK(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid);

ALTER TABLE monarch_branches ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_branches FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_branch_tenant_policy ON monarch_branches;
CREATE POLICY monarch_branch_tenant_policy ON monarch_branches
 USING(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid)
 WITH CHECK(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid);

ALTER TABLE monarch_tenant_users ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_tenant_users FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_users_tenant_policy ON monarch_tenant_users;
CREATE POLICY monarch_users_tenant_policy ON monarch_tenant_users
 USING(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND user_id = NULLIF(current_setting('monarch.user_id', true), '')::uuid)
 WITH CHECK(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid);

ALTER TABLE monarch_user_branch_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_user_branch_grants FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_grants_tenant_policy ON monarch_user_branch_grants;
CREATE POLICY monarch_grants_tenant_policy ON monarch_user_branch_grants
 USING(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid
   AND user_id = NULLIF(current_setting('monarch.user_id', true), '')::uuid)
 WITH CHECK(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid);

ALTER TABLE monarch_outbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_outbox FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS monarch_outbox_tenant_policy ON monarch_outbox;
CREATE POLICY monarch_outbox_tenant_policy ON monarch_outbox
 USING(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid)
 WITH CHECK(company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
   AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid);