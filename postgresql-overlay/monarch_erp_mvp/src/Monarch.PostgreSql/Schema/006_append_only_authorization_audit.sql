-- 006: Authorization audit is append-only for all ordinary database principals.
-- A privileged superuser can always disable triggers; export production audit records to WORM storage.

CREATE OR REPLACE FUNCTION monarch_security.reject_authorization_audit_mutation()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, monarch_security
AS $immutable$
BEGIN
    RAISE EXCEPTION 'monarch_security.authorization_audit is append-only; mutation is prohibited'
        USING ERRCODE = '55000';
END
$immutable$;

ALTER FUNCTION monarch_security.reject_authorization_audit_mutation() OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.reject_authorization_audit_mutation() FROM PUBLIC;

DROP TRIGGER IF EXISTS trg_monarch_authorization_audit_no_mutation
    ON monarch_security.authorization_audit;
CREATE TRIGGER trg_monarch_authorization_audit_no_mutation
BEFORE UPDATE OR DELETE ON monarch_security.authorization_audit
FOR EACH ROW EXECUTE FUNCTION monarch_security.reject_authorization_audit_mutation();

DROP TRIGGER IF EXISTS trg_monarch_authorization_audit_no_truncate
    ON monarch_security.authorization_audit;
CREATE TRIGGER trg_monarch_authorization_audit_no_truncate
BEFORE TRUNCATE ON monarch_security.authorization_audit
FOR EACH STATEMENT EXECUTE FUNCTION monarch_security.reject_authorization_audit_mutation();

REVOKE UPDATE, DELETE, TRUNCATE
ON monarch_security.authorization_audit
FROM PUBLIC, monarch_api_runtime, monarch_security_definer;
