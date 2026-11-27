-- 010: Prevent duplicate live invitations and expire stale claims before issuing a replacement.

CREATE UNIQUE INDEX ux_identity_enrollment_one_live_claim
    ON monarch_security.identity_enrollment_claims(company_id, branch_id, target_user_id, expected_issuer)
    WHERE status IN ('PENDING', 'PROVEN');

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

    UPDATE monarch_security.identity_enrollment_claims
    SET status = 'EXPIRED'
    WHERE company_id = p_company AND branch_id = p_branch
      AND target_user_id = p_target_user AND expected_issuer = p_expected_issuer
      AND (
          (status = 'PENDING' AND expires_at <= clock_timestamp())
          OR (status = 'PROVEN' AND redeemed_at <= clock_timestamp() - interval '24 hours')
      );

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
    FROM public.monarch_companies c
    JOIN public.monarch_branches b
      ON b.company_id = c.company_id AND b.branch_id = claim_row.branch_id AND b.is_active
    JOIN public.monarch_tenant_users u
      ON u.company_id = c.company_id AND u.user_id = claim_row.target_user_id AND u.is_active
    JOIN public.monarch_user_branch_grants g
      ON g.company_id = c.company_id AND g.user_id = u.user_id
     AND g.branch_id = b.branch_id AND g.is_active
    WHERE c.company_id = claim_row.company_id AND c.is_active
    FOR SHARE OF c, b, u, g;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Target user or invited company/branch is no longer active'
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
