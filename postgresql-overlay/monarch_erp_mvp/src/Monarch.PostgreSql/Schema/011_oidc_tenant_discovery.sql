-- 011: OIDC-scoped company and branch discovery for authenticated desktop/API clients.
-- The API receives only EXECUTE on these functions; tenant tables remain inaccessible directly.

CREATE OR REPLACE FUNCTION monarch_security.list_oidc_companies(
    p_issuer text,
    p_subject text
)
RETURNS TABLE(company_id uuid, company_code varchar(40), legal_name varchar(240))
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $companies$
BEGIN
    IF p_issuer IS NULL OR length(p_issuer) NOT BETWEEN 1 AND 512
       OR p_subject IS NULL OR length(p_subject) NOT BETWEEN 1 AND 512 THEN
        RAISE EXCEPTION 'A validated OIDC issuer and subject are required'
            USING ERRCODE = '28000';
    END IF;

    RETURN QUERY
    SELECT DISTINCT c.company_id, c.company_code, c.legal_name
    FROM public.monarch_companies c
    JOIN monarch_security.external_identities ei
      ON ei.company_id = c.company_id AND ei.is_active
     AND ei.issuer = p_issuer AND ei.subject = p_subject
    JOIN public.monarch_tenant_users u
      ON u.company_id = ei.company_id AND u.user_id = ei.user_id AND u.is_active
    WHERE c.is_active
      AND EXISTS (
          SELECT 1
          FROM public.monarch_branches b
          JOIN public.monarch_user_branch_grants g
            ON g.company_id = b.company_id AND g.branch_id = b.branch_id
           AND g.user_id = u.user_id AND g.is_active
          WHERE b.company_id = c.company_id AND b.is_active
      )
    ORDER BY c.company_code, c.company_id;
END
$companies$;

CREATE OR REPLACE FUNCTION monarch_security.list_oidc_company_branches(
    p_company uuid,
    p_issuer text,
    p_subject text
)
RETURNS TABLE(branch_id uuid, branch_code varchar(40), branch_name varchar(200))
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $branches$
BEGIN
    IF p_company IS NULL OR p_company = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_issuer IS NULL OR length(p_issuer) NOT BETWEEN 1 AND 512
       OR p_subject IS NULL OR length(p_subject) NOT BETWEEN 1 AND 512 THEN
        RAISE EXCEPTION 'A company selection and validated OIDC issuer/subject are required'
            USING ERRCODE = '28000';
    END IF;

    RETURN QUERY
    SELECT b.branch_id, b.branch_code, b.branch_name
    FROM public.monarch_companies c
    JOIN monarch_security.external_identities ei
      ON ei.company_id = c.company_id AND ei.is_active
     AND ei.issuer = p_issuer AND ei.subject = p_subject
    JOIN public.monarch_tenant_users u
      ON u.company_id = ei.company_id AND u.user_id = ei.user_id AND u.is_active
    JOIN public.monarch_branches b
      ON b.company_id = c.company_id AND b.is_active
    JOIN public.monarch_user_branch_grants g
      ON g.company_id = b.company_id AND g.branch_id = b.branch_id
     AND g.user_id = u.user_id AND g.is_active
    WHERE c.company_id = p_company AND c.is_active
    ORDER BY b.branch_code, b.branch_id;
END
$branches$;

ALTER FUNCTION monarch_security.list_oidc_companies(text,text)
    OWNER TO monarch_security_definer;
ALTER FUNCTION monarch_security.list_oidc_company_branches(uuid,text,text)
    OWNER TO monarch_security_definer;

REVOKE ALL ON FUNCTION monarch_security.list_oidc_companies(text,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION monarch_security.list_oidc_company_branches(uuid,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.list_oidc_companies(text,text) TO monarch_api_runtime;
GRANT EXECUTE ON FUNCTION monarch_security.list_oidc_company_branches(uuid,text,text) TO monarch_api_runtime;
