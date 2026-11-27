-- 008: Fail closed on unproven OIDC identity linking.
-- Until an authenticated OIDC enrollment/claim flow exists, the legacy two-person
-- request path must not be able to bind an arbitrary issuer/subject to an internal user.
-- This is intentionally a forward-only guard; migration 007 remains immutable.

CREATE OR REPLACE FUNCTION monarch_security.reject_unproven_identity_link()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, monarch_security
AS $guard$
BEGIN
    IF NEW.request_type = 'LINK_IDENTITY'
       AND (TG_OP = 'INSERT' OR NEW.status = 'APPROVED') THEN
        RAISE EXCEPTION
            'OIDC identity linking is disabled until the target user completes authenticated ownership proof'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$guard$;

REVOKE ALL ON FUNCTION monarch_security.reject_unproven_identity_link()
    FROM PUBLIC, monarch_api_runtime, monarch_security_definer;

DROP TRIGGER IF EXISTS trg_reject_unproven_identity_link
    ON monarch_security.provisioning_requests;

CREATE TRIGGER trg_reject_unproven_identity_link
BEFORE INSERT OR UPDATE OF request_type, status
ON monarch_security.provisioning_requests
FOR EACH ROW
EXECUTE FUNCTION monarch_security.reject_unproven_identity_link();

COMMENT ON FUNCTION monarch_security.reject_unproven_identity_link() IS
    'Fail-closed guard: prevents arbitrary OIDC issuer/subject linking until authenticated ownership proof is implemented.';
COMMENT ON TRIGGER trg_reject_unproven_identity_link
    ON monarch_security.provisioning_requests IS
    'Blocks new LINK_IDENTITY requests and approval of any legacy pending LINK_IDENTITY request.';
