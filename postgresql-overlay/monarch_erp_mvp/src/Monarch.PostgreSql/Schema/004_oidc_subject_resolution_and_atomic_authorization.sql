-- 004: Resolve internal users from OIDC subjects and lock authorization rows for each business transaction.

CREATE OR REPLACE FUNCTION monarch_security.resolve_oidc_user(
    p_company uuid,
    p_branch uuid,
    p_issuer text,
    p_subject text
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $resolve$
DECLARE resolved_user uuid;
BEGIN
    IF p_company IS NULL OR p_branch IS NULL
       OR p_issuer IS NULL OR length(p_issuer) NOT BETWEEN 1 AND 512
       OR p_subject IS NULL OR length(p_subject) NOT BETWEEN 1 AND 512 THEN
        RAISE EXCEPTION 'OIDC identity and tenant selection are required'
            USING ERRCODE = '28000';
    END IF;

    SELECT ei.user_id INTO resolved_user
    FROM monarch_security.external_identities ei
    JOIN public.monarch_companies c
      ON c.company_id = ei.company_id AND c.is_active
    JOIN public.monarch_tenant_users u
      ON u.company_id = ei.company_id AND u.user_id = ei.user_id AND u.is_active
    JOIN public.monarch_branches b
      ON b.company_id = ei.company_id AND b.branch_id = p_branch AND b.is_active
    JOIN public.monarch_user_branch_grants g
      ON g.company_id = ei.company_id AND g.branch_id = b.branch_id
     AND g.user_id = ei.user_id AND g.is_active
    WHERE ei.company_id = p_company
      AND ei.issuer = p_issuer
      AND ei.subject = p_subject
      AND ei.is_active;

    IF resolved_user IS NULL THEN
        RAISE EXCEPTION 'OIDC identity or selected tenant membership is inactive or unknown'
            USING ERRCODE = '28000';
    END IF;

    RETURN resolved_user;
END
$resolve$;

ALTER FUNCTION monarch_security.resolve_oidc_user(uuid,uuid,text,text)
    OWNER TO monarch_security_definer;
GRANT EXECUTE ON FUNCTION monarch_security.resolve_oidc_user(uuid,uuid,text,text)
    TO monarch_api_runtime;
REVOKE ALL ON FUNCTION monarch_security.resolve_oidc_user(uuid,uuid,text,text)
    FROM PUBLIC;

-- Re-define authorization to lock all rows that establish identity, membership and RBAC.
-- Revocation or role changes must wait until the protected business transaction completes.
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
DECLARE secret_value bytea; message text;
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

    PERFORM 1
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
    FOR SHARE OF c, b, u, g, ei, ur, r, rp;

    IF NOT FOUND THEN
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
