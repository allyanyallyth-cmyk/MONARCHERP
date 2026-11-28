-- 003: OIDC subject mapping and database-enforced RBAC.
-- This migration is additive. It does not modify or remove the SQLite provider or prior migrations.

DO $roles$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'monarch_api_runtime') THEN
        CREATE ROLE monarch_api_runtime NOLOGIN NOINHERIT NOBYPASSRLS;
    ELSIF NOT EXISTS (
        SELECT 1 FROM pg_roles
        WHERE rolname = 'monarch_api_runtime'
          AND NOT rolcanlogin AND NOT rolbypassrls AND NOT rolinherit
    ) THEN
        RAISE EXCEPTION 'monarch_api_runtime must remain NOLOGIN, NOINHERIT and NOBYPASSRLS';
    END IF;
END
$roles$;

CREATE TABLE monarch_security.external_identities (
    identity_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    issuer varchar(512) NOT NULL CHECK (length(issuer) BETWEEN 1 AND 512),
    subject varchar(512) NOT NULL CHECK (length(subject) BETWEEN 1 AND 512),
    company_id uuid NOT NULL,
    user_id uuid NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    disabled_at timestamptz NULL,
    UNIQUE (issuer, subject, company_id),
    FOREIGN KEY (company_id, user_id)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    CHECK ((is_active AND disabled_at IS NULL) OR (NOT is_active AND disabled_at IS NOT NULL))
);

CREATE TABLE monarch_security.roles (
    company_id uuid NOT NULL REFERENCES public.monarch_companies(company_id),
    role_id uuid NOT NULL DEFAULT gen_random_uuid(),
    role_code varchar(80) NOT NULL,
    role_name varchar(180) NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (company_id, role_id),
    UNIQUE (company_id, role_code),
    CHECK (length(btrim(role_code)) > 0),
    CHECK (length(btrim(role_name)) > 0)
);

CREATE TABLE monarch_security.permissions (
    permission_code varchar(128) PRIMARY KEY,
    description varchar(300) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (length(btrim(permission_code)) > 0)
);

CREATE TABLE monarch_security.role_permissions (
    company_id uuid NOT NULL,
    role_id uuid NOT NULL,
    permission_code varchar(128) NOT NULL
        REFERENCES monarch_security.permissions(permission_code),
    granted_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (company_id, role_id, permission_code),
    FOREIGN KEY (company_id, role_id)
        REFERENCES monarch_security.roles(company_id, role_id)
);

CREATE TABLE monarch_security.user_roles (
    company_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role_id uuid NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    assigned_at timestamptz NOT NULL DEFAULT now(),
    assigned_by uuid NULL,
    revoked_at timestamptz NULL,
    revoked_by uuid NULL,
    PRIMARY KEY (company_id, user_id, role_id),
    FOREIGN KEY (company_id, user_id)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    FOREIGN KEY (company_id, role_id)
        REFERENCES monarch_security.roles(company_id, role_id),
    FOREIGN KEY (company_id, assigned_by)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    FOREIGN KEY (company_id, revoked_by)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    CHECK ((is_active AND revoked_at IS NULL AND revoked_by IS NULL)
        OR (NOT is_active AND revoked_at IS NOT NULL))
);

CREATE INDEX ix_monarch_external_identity_lookup
    ON monarch_security.external_identities(issuer, subject, company_id)
    WHERE is_active;
CREATE INDEX ix_monarch_user_roles_active
    ON monarch_security.user_roles(company_id, user_id, role_id)
    WHERE is_active;
CREATE INDEX ix_monarch_role_permissions_permission
    ON monarch_security.role_permissions(company_id, permission_code, role_id);

INSERT INTO monarch_security.permissions(permission_code, description)
VALUES
    ('gl.accounts.read', 'Read general ledger accounts'),
    ('gl.accounts.manage', 'Create and manage general ledger accounts'),
    ('gl.journal.read', 'Read general ledger journals'),
    ('gl.journal.create', 'Create general ledger journal drafts'),
    ('gl.journal.post', 'Post general ledger journals'),
    ('gl.report.trial_balance', 'Read general ledger trial balance')
ON CONFLICT (permission_code) DO NOTHING;

CREATE TABLE monarch_security.authorization_audit (
    audit_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    entity_table varchar(80) NOT NULL,
    action varchar(12) NOT NULL CHECK (action IN ('INSERT', 'UPDATE', 'DELETE')),
    entity_key jsonb NOT NULL,
    before_row jsonb NULL,
    after_row jsonb NULL,
    database_actor name NOT NULL DEFAULT session_user,
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION monarch_security.capture_authorization_change()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, monarch_security
AS $audit$
DECLARE before_value jsonb; after_value jsonb; key_value jsonb;
BEGIN
    IF TG_OP <> 'INSERT' THEN before_value := to_jsonb(OLD); END IF;
    IF TG_OP <> 'DELETE' THEN after_value := to_jsonb(NEW); END IF;
    key_value := COALESCE(after_value, before_value);
    INSERT INTO monarch_security.authorization_audit(
        entity_table, action, entity_key, before_row, after_row, database_actor
    ) VALUES (
        TG_TABLE_NAME, TG_OP, key_value, before_value, after_value, session_user
    );
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END
$audit$;

ALTER FUNCTION monarch_security.capture_authorization_change() OWNER TO monarch_security_definer;
GRANT INSERT ON monarch_security.authorization_audit TO monarch_security_definer;
REVOKE ALL ON monarch_security.authorization_audit FROM PUBLIC;

DROP TRIGGER IF EXISTS trg_monarch_external_identity_audit ON monarch_security.external_identities;
CREATE TRIGGER trg_monarch_external_identity_audit
AFTER INSERT OR UPDATE OR DELETE ON monarch_security.external_identities
FOR EACH ROW EXECUTE FUNCTION monarch_security.capture_authorization_change();

DROP TRIGGER IF EXISTS trg_monarch_role_audit ON monarch_security.roles;
CREATE TRIGGER trg_monarch_role_audit
AFTER INSERT OR UPDATE OR DELETE ON monarch_security.roles
FOR EACH ROW EXECUTE FUNCTION monarch_security.capture_authorization_change();

DROP TRIGGER IF EXISTS trg_monarch_role_permission_audit ON monarch_security.role_permissions;
CREATE TRIGGER trg_monarch_role_permission_audit
AFTER INSERT OR UPDATE OR DELETE ON monarch_security.role_permissions
FOR EACH ROW EXECUTE FUNCTION monarch_security.capture_authorization_change();

DROP TRIGGER IF EXISTS trg_monarch_user_role_audit ON monarch_security.user_roles;
CREATE TRIGGER trg_monarch_user_role_audit
AFTER INSERT OR UPDATE OR DELETE ON monarch_security.user_roles
FOR EACH ROW EXECUTE FUNCTION monarch_security.capture_authorization_change();

REVOKE ALL ON monarch_security.external_identities,
    monarch_security.roles,
    monarch_security.permissions,
    monarch_security.role_permissions,
    monarch_security.user_roles,
    monarch_security.authorization_audit
FROM PUBLIC, monarch_api_runtime;

GRANT SELECT ON monarch_security.external_identities,
    monarch_security.roles,
    monarch_security.permissions,
    monarch_security.role_permissions,
    monarch_security.user_roles
TO monarch_security_definer;

GRANT USAGE ON SCHEMA monarch_security TO monarch_api_runtime;

-- A valid OIDC identity, active tenant membership and active role permission
-- must all be present before the transaction-local tenant context is installed.
CREATE OR REPLACE FUNCTION monarch_security.set_authorized_tenant_context(
    p_company uuid,
    p_branch uuid,
    p_user uuid,
    p_issuer text,
    p_subject text,
    p_permission text,
    p_issued_at bigint,
    p_signature text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $authorize$
DECLARE
    secret_value bytea;
    message text;
BEGIN
    IF p_company IS NULL OR p_branch IS NULL OR p_user IS NULL
       OR p_issuer IS NULL OR length(p_issuer) NOT BETWEEN 1 AND 512
       OR p_subject IS NULL OR length(p_subject) NOT BETWEEN 1 AND 512
       OR p_permission IS NULL OR length(p_permission) NOT BETWEEN 1 AND 128
       OR p_issued_at IS NULL OR p_signature IS NULL THEN
        RAISE EXCEPTION 'Complete authenticated tenant authorization context is required'
            USING ERRCODE = '28000';
    END IF;

    IF abs(extract(epoch FROM clock_timestamp()) - p_issued_at) > 300 THEN
        RAISE EXCEPTION 'Tenant authorization context signature expired'
            USING ERRCODE = '28000';
    END IF;

    SELECT secret INTO secret_value
    FROM monarch_security.context_secrets
    WHERE secret_id = 1;

    IF secret_value IS NULL THEN
        RAISE EXCEPTION 'Tenant context secret is not provisioned'
            USING ERRCODE = '28000';
    END IF;

    message := p_company::text || ':' || p_branch::text || ':' || p_user::text || ':'
        || p_issued_at::text || ':' || p_issuer || ':' || p_subject || ':' || p_permission;

    IF hmac(convert_to(message, 'UTF8'), secret_value, 'sha256')
        <> decode(p_signature, 'hex') THEN
        RAISE EXCEPTION 'Invalid tenant authorization context signature'
            USING ERRCODE = '28000';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM public.monarch_companies c
        JOIN public.monarch_branches b
          ON b.company_id = c.company_id AND b.branch_id = p_branch AND b.is_active
        JOIN public.monarch_tenant_users u
          ON u.company_id = c.company_id AND u.user_id = p_user AND u.is_active
        JOIN public.monarch_user_branch_grants g
          ON g.company_id = c.company_id AND g.branch_id = b.branch_id
         AND g.user_id = u.user_id AND g.is_active
        JOIN monarch_security.external_identities ei
          ON ei.company_id = c.company_id AND ei.user_id = u.user_id
         AND ei.issuer = p_issuer AND ei.subject = p_subject AND ei.is_active
        JOIN monarch_security.user_roles ur
          ON ur.company_id = c.company_id AND ur.user_id = u.user_id AND ur.is_active
        JOIN monarch_security.roles r
          ON r.company_id = ur.company_id AND r.role_id = ur.role_id AND r.is_active
        JOIN monarch_security.role_permissions rp
          ON rp.company_id = r.company_id AND rp.role_id = r.role_id
         AND rp.permission_code = p_permission
        WHERE c.company_id = p_company AND c.is_active
    ) THEN
        RAISE EXCEPTION 'OIDC identity, tenant membership or requested permission is inactive or unauthorized'
            USING ERRCODE = '28000';
    END IF;

    PERFORM set_config('monarch.company_id', p_company::text, true);
    PERFORM set_config('monarch.branch_id', p_branch::text, true);
    PERFORM set_config('monarch.user_id', p_user::text, true);
    PERFORM set_config('monarch.context_issued_at', p_issued_at::text, true);
    PERFORM set_config('monarch.context_signature', p_signature, true);
    PERFORM set_config('monarch.identity_issuer', p_issuer, true);
    PERFORM set_config('monarch.identity_subject', p_subject, true);
    PERFORM set_config('monarch.required_permission', p_permission, true);
END
$authorize$;

ALTER FUNCTION monarch_security.set_authorized_tenant_context(uuid,uuid,uuid,text,text,text,bigint,text)
    OWNER TO monarch_security_definer;
GRANT EXECUTE ON FUNCTION monarch_security.set_authorized_tenant_context(uuid,uuid,uuid,text,text,text,bigint,text)
    TO monarch_api_runtime;
REVOKE ALL ON FUNCTION monarch_security.set_authorized_tenant_context(uuid,uuid,uuid,text,text,text,bigint,text)
    FROM PUBLIC;

-- RLS validates the extended signature whenever the request carries OIDC authorization GUCs.
CREATE OR REPLACE FUNCTION monarch_security.context_is_valid()
RETURNS boolean
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $context$
DECLARE
    c text; b text; u text; issued text; signature text;
    issuer text; subject text; permission text; secret_value bytea; message text;
BEGIN
    c := current_setting('monarch.company_id', true);
    b := current_setting('monarch.branch_id', true);
    u := current_setting('monarch.user_id', true);
    issued := current_setting('monarch.context_issued_at', true);
    signature := current_setting('monarch.context_signature', true);
    issuer := NULLIF(current_setting('monarch.identity_issuer', true), '');
    subject := NULLIF(current_setting('monarch.identity_subject', true), '');
    permission := NULLIF(current_setting('monarch.required_permission', true), '');

    IF c IS NULL OR b IS NULL OR u IS NULL OR issued IS NULL OR signature IS NULL THEN
        RETURN false;
    END IF;
    IF abs(extract(epoch FROM statement_timestamp()) - issued::bigint) > 300 THEN
        RETURN false;
    END IF;

    SELECT secret INTO secret_value
    FROM monarch_security.context_secrets
    WHERE secret_id = 1;
    IF secret_value IS NULL THEN RETURN false; END IF;

    IF issuer IS NULL AND subject IS NULL AND permission IS NULL THEN
        message := c || ':' || b || ':' || u || ':' || issued;
    ELSIF issuer IS NOT NULL AND subject IS NOT NULL AND permission IS NOT NULL THEN
        message := c || ':' || b || ':' || u || ':' || issued || ':'
            || issuer || ':' || subject || ':' || permission;
    ELSE
        RETURN false;
    END IF;

    RETURN hmac(convert_to(message, 'UTF8'), secret_value, 'sha256') = decode(signature, 'hex');
EXCEPTION WHEN OTHERS THEN
    RETURN false;
END
$context$;

ALTER FUNCTION monarch_security.context_is_valid() OWNER TO monarch_security_definer;
GRANT EXECUTE ON FUNCTION monarch_security.context_is_valid() TO PUBLIC;
