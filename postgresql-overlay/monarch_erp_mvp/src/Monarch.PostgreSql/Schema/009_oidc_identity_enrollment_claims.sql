-- 009: Cryptographic OIDC identity enrollment with one-time proof and independent approval.
-- The API validates the ID token against trusted discovery metadata before calling redeem_identity_enrollment_claim.
-- Only hashes of the invitation secret and OIDC nonce are persisted.

INSERT INTO monarch_security.permissions(permission_code, description)
VALUES
    ('security.identity.invite', 'Create short-lived identity enrollment invitations for active tenant users'),
    ('security.identity.enrollment.approve', 'Approve an identity enrollment after the target user proves OIDC ownership')
ON CONFLICT (permission_code) DO NOTHING;

CREATE TABLE monarch_security.identity_enrollment_claims (
    enrollment_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    target_user_id uuid NOT NULL,
    expected_issuer varchar(512) NOT NULL CHECK (length(expected_issuer) BETWEEN 1 AND 512),
    token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash) = 32),
    nonce_hash bytea NOT NULL CHECK (octet_length(nonce_hash) = 32),
    status varchar(12) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN ('PENDING', 'PROVEN', 'APPROVED', 'EXPIRED', 'REVOKED')),
    reason varchar(500) NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 500),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at timestamptz NOT NULL,
    redeemed_issuer varchar(512) NULL,
    redeemed_subject varchar(512) NULL,
    redeemed_at timestamptz NULL,
    approved_by uuid NULL,
    approved_at timestamptz NULL,
    FOREIGN KEY (company_id, branch_id)
        REFERENCES public.monarch_branches(company_id, branch_id),
    FOREIGN KEY (company_id, target_user_id)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    FOREIGN KEY (company_id, created_by)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    FOREIGN KEY (company_id, approved_by)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    CHECK (
        (status = 'PENDING' AND redeemed_issuer IS NULL AND redeemed_subject IS NULL
            AND redeemed_at IS NULL AND approved_by IS NULL AND approved_at IS NULL)
        OR
        (status = 'PROVEN' AND redeemed_issuer IS NOT NULL AND redeemed_subject IS NOT NULL
            AND redeemed_at IS NOT NULL AND approved_by IS NULL AND approved_at IS NULL)
        OR
        (status = 'APPROVED' AND redeemed_issuer IS NOT NULL AND redeemed_subject IS NOT NULL
            AND redeemed_at IS NOT NULL AND approved_by IS NOT NULL AND approved_at IS NOT NULL)
        OR
        (status IN ('EXPIRED', 'REVOKED'))
    )
);

CREATE INDEX ix_identity_enrollment_pending
    ON monarch_security.identity_enrollment_claims(company_id, branch_id, status, created_at DESC);

ALTER TABLE monarch_security.identity_enrollment_claims ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_security.identity_enrollment_claims FORCE ROW LEVEL SECURITY;
CREATE POLICY monarch_identity_enrollment_scope
    ON monarch_security.identity_enrollment_claims
    USING (
        monarch_security.context_is_valid()
        AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
        AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid
    )
    WITH CHECK (
        monarch_security.context_is_valid()
        AND company_id = NULLIF(current_setting('monarch.company_id', true), '')::uuid
        AND branch_id = NULLIF(current_setting('monarch.branch_id', true), '')::uuid
    );

GRANT SELECT, INSERT, UPDATE ON monarch_security.identity_enrollment_claims TO monarch_security_definer;
REVOKE ALL ON monarch_security.identity_enrollment_claims FROM PUBLIC, monarch_api_runtime;

CREATE TRIGGER trg_identity_enrollment_claim_audit
AFTER INSERT OR UPDATE ON monarch_security.identity_enrollment_claims
FOR EACH ROW EXECUTE FUNCTION monarch_security.capture_authorization_change();

CREATE OR REPLACE FUNCTION monarch_security.create_identity_enrollment_claim(
    p_company uuid,
    p_branch uuid,
    p_target_user uuid,
    p_expected_issuer text,
    p_token_hash bytea,
    p_nonce_hash bytea,
    p_lifetime_seconds integer,
    p_reason text
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $create_claim$
DECLARE
    actor_id uuid;
    enrollment_id_value uuid;
BEGIN
    actor_id := monarch_security.assert_provisioning_actor(
        p_company, p_branch, 'security.identity.invite');

    IF p_target_user IS NULL OR p_target_user = actor_id THEN
        RAISE EXCEPTION 'Identity enrollment invitation cannot target its creator'
            USING ERRCODE = '28000';
    END IF;
    IF p_expected_issuer IS NULL OR length(p_expected_issuer) NOT BETWEEN 1 AND 512
       OR p_expected_issuer NOT LIKE 'https://%'
       OR p_token_hash IS NULL OR octet_length(p_token_hash) <> 32
       OR p_nonce_hash IS NULL OR octet_length(p_nonce_hash) <> 32
       OR p_lifetime_seconds NOT BETWEEN 60 AND 900
       OR p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 1 AND 500 THEN
        RAISE EXCEPTION 'Invalid identity enrollment invitation parameters'
            USING ERRCODE = '22023';
    END IF;

    PERFORM 1
    FROM public.monarch_tenant_users u
    JOIN public.monarch_user_branch_grants g
      ON g.company_id = u.company_id AND g.user_id = u.user_id
    WHERE u.company_id = p_company AND u.user_id = p_target_user
      AND u.is_active AND g.branch_id = p_branch AND g.is_active
    FOR SHARE OF u, g;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Target user is not an active member of the selected branch'
            USING ERRCODE = '28000';
    END IF;

    INSERT INTO monarch_security.identity_enrollment_claims(
        company_id, branch_id, target_user_id, expected_issuer,
        token_hash, nonce_hash, reason, created_by, expires_at
    )
    VALUES (
        p_company, p_branch, p_target_user, p_expected_issuer,
        p_token_hash, p_nonce_hash, btrim(p_reason), actor_id,
        clock_timestamp() + make_interval(secs => p_lifetime_seconds)
    )
    RETURNING enrollment_id INTO enrollment_id_value;

    RETURN enrollment_id_value;
END
$create_claim$;

ALTER FUNCTION monarch_security.create_identity_enrollment_claim(uuid,uuid,uuid,text,bytea,bytea,integer,text)
    OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.create_identity_enrollment_claim(uuid,uuid,uuid,text,bytea,bytea,integer,text)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.create_identity_enrollment_claim(uuid,uuid,uuid,text,bytea,bytea,integer,text)
    TO monarch_api_runtime;

CREATE OR REPLACE FUNCTION monarch_security.redeem_identity_enrollment_claim(
    p_token_hash bytea,
    p_issuer text,
    p_subject text,
    p_nonce text
)
RETURNS TABLE(enrollment_id uuid, company_id uuid, branch_id uuid, target_user_id uuid)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $redeem_claim$
DECLARE
    claim_row monarch_security.identity_enrollment_claims%ROWTYPE;
BEGIN
    IF p_token_hash IS NULL OR octet_length(p_token_hash) <> 32
       OR p_issuer IS NULL OR length(p_issuer) NOT BETWEEN 1 AND 512
       OR p_subject IS NULL OR length(p_subject) NOT BETWEEN 1 AND 512
       OR p_nonce IS NULL OR length(p_nonce) NOT BETWEEN 16 AND 256 THEN
        RAISE EXCEPTION 'Invalid identity enrollment proof'
            USING ERRCODE = '28000';
    END IF;

    SELECT * INTO claim_row
    FROM monarch_security.identity_enrollment_claims
    WHERE token_hash = p_token_hash
    FOR UPDATE;

    IF NOT FOUND OR claim_row.status <> 'PENDING'
       OR claim_row.expires_at <= clock_timestamp()
       OR claim_row.expected_issuer <> p_issuer
       OR claim_row.nonce_hash <> digest(convert_to(p_nonce, 'UTF8'), 'sha256') THEN
        RAISE EXCEPTION 'Identity enrollment claim is invalid, expired, already used or does not match the OIDC proof'
            USING ERRCODE = '28000';
    END IF;

    PERFORM 1
    FROM public.monarch_tenant_users u
    JOIN public.monarch_user_branch_grants g
      ON g.company_id = u.company_id AND g.user_id = u.user_id
    WHERE u.company_id = claim_row.company_id AND u.user_id = claim_row.target_user_id
      AND u.is_active AND g.branch_id = claim_row.branch_id AND g.is_active
    FOR SHARE OF u, g;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Target user is no longer an active member of the invited branch'
            USING ERRCODE = '28000';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM monarch_security.external_identities ei
        WHERE ei.company_id = claim_row.company_id
          AND ei.issuer = p_issuer AND ei.subject = p_subject
          AND (ei.is_active OR ei.user_id <> claim_row.target_user_id)
    ) THEN
        RAISE EXCEPTION 'OIDC identity is already active or belongs to another internal user'
            USING ERRCODE = '23505';
    END IF;

    UPDATE monarch_security.identity_enrollment_claims
    SET status = 'PROVEN',
        redeemed_issuer = p_issuer,
        redeemed_subject = p_subject,
        redeemed_at = clock_timestamp()
    WHERE identity_enrollment_claims.enrollment_id = claim_row.enrollment_id
      AND status = 'PENDING';

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Identity enrollment claim was consumed concurrently'
            USING ERRCODE = '40001';
    END IF;

    RETURN QUERY SELECT claim_row.enrollment_id, claim_row.company_id,
        claim_row.branch_id, claim_row.target_user_id;
END
$redeem_claim$;

ALTER FUNCTION monarch_security.redeem_identity_enrollment_claim(bytea,text,text,text)
    OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.redeem_identity_enrollment_claim(bytea,text,text,text)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.redeem_identity_enrollment_claim(bytea,text,text,text)
    TO monarch_api_runtime;

CREATE OR REPLACE FUNCTION monarch_security.approve_identity_enrollment_claim(
    p_enrollment_id uuid,
    p_decision_reason text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $approve_claim$
DECLARE
    actor_id uuid;
    company_id_value uuid;
    branch_id_value uuid;
    claim_row monarch_security.identity_enrollment_claims%ROWTYPE;
    changed_rows integer;
BEGIN
    company_id_value := NULLIF(current_setting('monarch.company_id', true), '')::uuid;
    branch_id_value := NULLIF(current_setting('monarch.branch_id', true), '')::uuid;
    actor_id := monarch_security.assert_provisioning_actor(
        company_id_value, branch_id_value, 'security.identity.enrollment.approve');

    IF p_decision_reason IS NULL OR length(btrim(p_decision_reason)) NOT BETWEEN 1 AND 500 THEN
        RAISE EXCEPTION 'An approval reason between 1 and 500 characters is required'
            USING ERRCODE = '22023';
    END IF;

    SELECT * INTO claim_row
    FROM monarch_security.identity_enrollment_claims
    WHERE enrollment_id = p_enrollment_id
      AND company_id = company_id_value AND branch_id = branch_id_value
    FOR UPDATE;

    IF NOT FOUND OR claim_row.status <> 'PROVEN'
       OR claim_row.redeemed_at < clock_timestamp() - interval '24 hours' THEN
        RAISE EXCEPTION 'Proven identity enrollment claim was not found or has expired'
            USING ERRCODE = 'P0002';
    END IF;
    IF actor_id = claim_row.created_by OR actor_id = claim_row.target_user_id THEN
        RAISE EXCEPTION 'Invitation creator and target user cannot approve this enrollment'
            USING ERRCODE = '28000';
    END IF;

    PERFORM 1
    FROM public.monarch_tenant_users u
    JOIN public.monarch_user_branch_grants g
      ON g.company_id = u.company_id AND g.user_id = u.user_id
    WHERE u.company_id = company_id_value AND u.user_id = claim_row.target_user_id
      AND u.is_active AND g.branch_id = branch_id_value AND g.is_active
    FOR SHARE OF u, g;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Target user is no longer an active member of the selected branch'
            USING ERRCODE = '28000';
    END IF;

    INSERT INTO monarch_security.external_identities(
        issuer, subject, company_id, user_id, is_active, created_at, disabled_at
    )
    VALUES (
        claim_row.redeemed_issuer, claim_row.redeemed_subject,
        company_id_value, claim_row.target_user_id, true, clock_timestamp(), NULL
    );

    UPDATE monarch_security.identity_enrollment_claims
    SET status = 'APPROVED', approved_by = actor_id, approved_at = clock_timestamp()
    WHERE enrollment_id = p_enrollment_id AND status = 'PROVEN';
    GET DIAGNOSTICS changed_rows = ROW_COUNT;
    IF changed_rows <> 1 THEN
        RAISE EXCEPTION 'Identity enrollment approval changed concurrently'
            USING ERRCODE = '40001';
    END IF;
END
$approve_claim$;

ALTER FUNCTION monarch_security.approve_identity_enrollment_claim(uuid,text)
    OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.approve_identity_enrollment_claim(uuid,text)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.approve_identity_enrollment_claim(uuid,text)
    TO monarch_api_runtime;

CREATE OR REPLACE FUNCTION monarch_security.list_identity_enrollment_claims()
RETURNS TABLE(
    enrollment_id uuid,
    target_user_id uuid,
    redeemed_issuer varchar,
    redeemed_subject varchar,
    reason varchar,
    created_by uuid,
    created_at timestamptz,
    redeemed_at timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $list_claims$
DECLARE
    company_id_value uuid;
    branch_id_value uuid;
BEGIN
    company_id_value := NULLIF(current_setting('monarch.company_id', true), '')::uuid;
    branch_id_value := NULLIF(current_setting('monarch.branch_id', true), '')::uuid;
    PERFORM monarch_security.assert_provisioning_actor(
        company_id_value, branch_id_value, 'security.identity.enrollment.approve');

    RETURN QUERY
    SELECT c.enrollment_id, c.target_user_id, c.redeemed_issuer,
        c.redeemed_subject, c.reason, c.created_by, c.created_at, c.redeemed_at
    FROM monarch_security.identity_enrollment_claims c
    WHERE c.company_id = company_id_value
      AND c.branch_id = branch_id_value
      AND c.status = 'PROVEN'
      AND c.redeemed_at >= clock_timestamp() - interval '24 hours'
    ORDER BY c.redeemed_at;
END
$list_claims$;

ALTER FUNCTION monarch_security.list_identity_enrollment_claims()
    OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.list_identity_enrollment_claims()
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.list_identity_enrollment_claims()
    TO monarch_api_runtime;

COMMENT ON TABLE monarch_security.identity_enrollment_claims IS
    'One-time OIDC identity enrollment claims. Only SHA-256 token and nonce hashes are stored; identity linking requires verified redemption and independent approval.';
