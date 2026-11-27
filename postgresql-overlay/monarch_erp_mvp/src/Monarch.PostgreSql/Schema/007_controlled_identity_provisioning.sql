-- 007: Controlled, two-person identity and role provisioning.
-- The API runtime can execute these narrowly scoped functions but cannot mutate RBAC tables directly.
-- Role definitions and permission catalog changes remain privileged deployment/security-administration operations.

INSERT INTO monarch_security.permissions(permission_code, description)
VALUES
    ('security.provisioning.request', 'Submit scoped identity and role provisioning requests'),
    ('security.provisioning.approve', 'Approve or reject provisioning requests submitted by another user')
ON CONFLICT (permission_code) DO NOTHING;

CREATE TABLE monarch_security.provisioning_requests (
    request_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    request_type varchar(24) NOT NULL
        CHECK (request_type IN ('LINK_IDENTITY', 'ASSIGN_ROLE', 'REVOKE_IDENTITY', 'REVOKE_ROLE')),
    target_user_id uuid NOT NULL,
    identity_issuer varchar(512) NULL,
    identity_subject varchar(512) NULL,
    role_id uuid NULL,
    reason varchar(500) NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 500),
    status varchar(12) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED')),
    requested_by uuid NOT NULL,
    requested_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    decided_by uuid NULL,
    decided_at timestamptz NULL,
    decision_reason varchar(500) NULL,
    FOREIGN KEY (company_id, branch_id)
        REFERENCES public.monarch_branches(company_id, branch_id),
    FOREIGN KEY (company_id, target_user_id)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    FOREIGN KEY (company_id, requested_by)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    FOREIGN KEY (company_id, decided_by)
        REFERENCES public.monarch_tenant_users(company_id, user_id),
    FOREIGN KEY (company_id, role_id)
        REFERENCES monarch_security.roles(company_id, role_id),
    CHECK (
        (request_type IN ('LINK_IDENTITY', 'REVOKE_IDENTITY')
            AND identity_issuer IS NOT NULL AND length(identity_issuer) BETWEEN 1 AND 512
            AND identity_subject IS NOT NULL AND length(identity_subject) BETWEEN 1 AND 512
            AND role_id IS NULL)
        OR
        (request_type IN ('ASSIGN_ROLE', 'REVOKE_ROLE')
            AND identity_issuer IS NULL AND identity_subject IS NULL
            AND role_id IS NOT NULL)
    ),
    CHECK (
        (status = 'PENDING' AND decided_by IS NULL AND decided_at IS NULL)
        OR
        (status IN ('APPROVED', 'REJECTED') AND decided_by IS NOT NULL AND decided_at IS NOT NULL)
    )
);

CREATE INDEX ix_monarch_provisioning_requests_scope
    ON monarch_security.provisioning_requests(company_id, branch_id, status, requested_at DESC);

CREATE UNIQUE INDEX ux_monarch_provisioning_pending_dedup
    ON monarch_security.provisioning_requests(
        company_id, branch_id, request_type, target_user_id,
        COALESCE(identity_issuer, ''),
        COALESCE(identity_subject, ''),
        COALESCE(role_id, '00000000-0000-0000-0000-000000000000'::uuid)
    )
    WHERE status = 'PENDING';

ALTER TABLE monarch_security.provisioning_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE monarch_security.provisioning_requests FORCE ROW LEVEL SECURITY;
CREATE POLICY monarch_provisioning_requests_scope
    ON monarch_security.provisioning_requests
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

GRANT SELECT, INSERT, UPDATE ON monarch_security.provisioning_requests TO monarch_security_definer;
GRANT INSERT, UPDATE ON monarch_security.external_identities TO monarch_security_definer;
GRANT INSERT, UPDATE ON monarch_security.user_roles TO monarch_security_definer;
REVOKE ALL ON monarch_security.provisioning_requests
    FROM PUBLIC, monarch_api_runtime;

DROP TRIGGER IF EXISTS trg_monarch_provisioning_request_audit
    ON monarch_security.provisioning_requests;
CREATE TRIGGER trg_monarch_provisioning_request_audit
AFTER INSERT OR UPDATE ON monarch_security.provisioning_requests
FOR EACH ROW EXECUTE FUNCTION monarch_security.capture_authorization_change();

CREATE OR REPLACE FUNCTION monarch_security.assert_provisioning_actor(
    p_company uuid,
    p_branch uuid,
    p_permission text
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $actor$
DECLARE
    actor_id uuid;
    context_company uuid;
    context_branch uuid;
    selected_permission text;
BEGIN
    IF NOT monarch_security.context_is_valid() THEN
        RAISE EXCEPTION 'A valid signed tenant context is required'
            USING ERRCODE = '28000';
    END IF;

    context_company := NULLIF(current_setting('monarch.company_id', true), '')::uuid;
    context_branch := NULLIF(current_setting('monarch.branch_id', true), '')::uuid;
    actor_id := NULLIF(current_setting('monarch.user_id', true), '')::uuid;
    selected_permission := NULLIF(current_setting('monarch.required_permission', true), '');

    IF context_company IS DISTINCT FROM p_company
       OR context_branch IS DISTINCT FROM p_branch
       OR actor_id IS NULL
       OR selected_permission IS DISTINCT FROM p_permission THEN
        RAISE EXCEPTION 'Provisioning operation is outside the authenticated tenant scope or permission'
            USING ERRCODE = '28000';
    END IF;

    PERFORM 1
    FROM public.monarch_companies c
    JOIN public.monarch_branches b
      ON b.company_id = c.company_id AND b.branch_id = p_branch AND b.is_active
    JOIN public.monarch_tenant_users u
      ON u.company_id = c.company_id AND u.user_id = actor_id AND u.is_active
    JOIN public.monarch_user_branch_grants g
      ON g.company_id = c.company_id AND g.branch_id = b.branch_id
     AND g.user_id = u.user_id AND g.is_active
    JOIN monarch_security.external_identities ei
      ON ei.company_id = c.company_id AND ei.user_id = u.user_id AND ei.is_active
     AND ei.issuer = NULLIF(current_setting('monarch.identity_issuer', true), '')
     AND ei.subject = NULLIF(current_setting('monarch.identity_subject', true), '')
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
        RAISE EXCEPTION 'Provisioning permission is not granted to the authenticated actor'
            USING ERRCODE = '28000';
    END IF;

    RETURN actor_id;
END
$actor$;

ALTER FUNCTION monarch_security.assert_provisioning_actor(uuid,uuid,text)
    OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.assert_provisioning_actor(uuid,uuid,text)
    FROM PUBLIC, monarch_api_runtime;
GRANT EXECUTE ON FUNCTION monarch_security.assert_provisioning_actor(uuid,uuid,text)
    TO monarch_security_definer;

CREATE OR REPLACE FUNCTION monarch_security.request_provisioning_change(
    p_company uuid,
    p_branch uuid,
    p_request_type text,
    p_target_user uuid,
    p_identity_issuer text,
    p_identity_subject text,
    p_role_id uuid,
    p_reason text
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $request$
DECLARE
    actor_id uuid;
    new_request_id uuid;
BEGIN
    actor_id := monarch_security.assert_provisioning_actor(
        p_company, p_branch, 'security.provisioning.request');

    IF p_target_user IS NULL OR p_target_user = actor_id THEN
        RAISE EXCEPTION 'A provisioning request cannot target the requesting user'
            USING ERRCODE = '28000';
    END IF;
    IF p_request_type NOT IN ('LINK_IDENTITY', 'ASSIGN_ROLE', 'REVOKE_IDENTITY', 'REVOKE_ROLE') THEN
        RAISE EXCEPTION 'Unsupported provisioning request type'
            USING ERRCODE = '22023';
    END IF;
    IF p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 1 AND 500 THEN
        RAISE EXCEPTION 'A reason between 1 and 500 characters is required'
            USING ERRCODE = '22023';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM public.monarch_tenant_users u
        JOIN public.monarch_user_branch_grants g
          ON g.company_id = u.company_id AND g.user_id = u.user_id
        WHERE u.company_id = p_company AND u.user_id = p_target_user
          AND u.is_active AND g.branch_id = p_branch AND g.is_active
    ) THEN
        RAISE EXCEPTION 'Target user is not an active member of the selected branch'
            USING ERRCODE = '28000';
    END IF;

    IF p_request_type IN ('LINK_IDENTITY', 'REVOKE_IDENTITY') THEN
        IF p_identity_issuer IS NULL OR length(p_identity_issuer) NOT BETWEEN 1 AND 512
           OR p_identity_subject IS NULL OR length(p_identity_subject) NOT BETWEEN 1 AND 512
           OR p_role_id IS NOT NULL THEN
            RAISE EXCEPTION 'Identity request requires issuer and subject only'
                USING ERRCODE = '22023';
        END IF;
        IF p_request_type = 'LINK_IDENTITY' AND EXISTS (
            SELECT 1 FROM monarch_security.external_identities
            WHERE company_id = p_company AND issuer = p_identity_issuer
              AND subject = p_identity_subject
              AND (is_active OR user_id <> p_target_user)
        ) THEN
            RAISE EXCEPTION 'OIDC identity is already active or belongs to a different internal user'
                USING ERRCODE = '23505';
        END IF;
        IF p_request_type = 'REVOKE_IDENTITY' AND NOT EXISTS (
            SELECT 1 FROM monarch_security.external_identities
            WHERE company_id = p_company AND user_id = p_target_user
              AND issuer = p_identity_issuer AND subject = p_identity_subject AND is_active
        ) THEN
            RAISE EXCEPTION 'Active OIDC identity mapping was not found for the target user'
                USING ERRCODE = 'P0002';
        END IF;
    ELSE
        IF p_role_id IS NULL OR p_identity_issuer IS NOT NULL OR p_identity_subject IS NOT NULL
           OR NOT EXISTS (
               SELECT 1 FROM monarch_security.roles
               WHERE company_id = p_company AND role_id = p_role_id AND is_active
           ) THEN
            RAISE EXCEPTION 'Role request requires an active role in the selected company'
                USING ERRCODE = '22023';
        END IF;
        IF p_request_type = 'ASSIGN_ROLE' AND EXISTS (
            SELECT 1 FROM monarch_security.user_roles
            WHERE company_id = p_company AND user_id = p_target_user
              AND role_id = p_role_id AND is_active
        ) THEN
            RAISE EXCEPTION 'Target user already has this active role'
                USING ERRCODE = '23505';
        END IF;
        IF p_request_type = 'REVOKE_ROLE' AND NOT EXISTS (
            SELECT 1 FROM monarch_security.user_roles
            WHERE company_id = p_company AND user_id = p_target_user
              AND role_id = p_role_id AND is_active
        ) THEN
            RAISE EXCEPTION 'Active role assignment was not found for the target user'
                USING ERRCODE = 'P0002';
        END IF;
    END IF;

    INSERT INTO monarch_security.provisioning_requests(
        company_id, branch_id, request_type, target_user_id,
        identity_issuer, identity_subject, role_id, reason, requested_by
    )
    VALUES (
        p_company, p_branch, p_request_type, p_target_user,
        p_identity_issuer, p_identity_subject, p_role_id, btrim(p_reason), actor_id
    )
    RETURNING request_id INTO new_request_id;

    RETURN new_request_id;
END
$request$;

ALTER FUNCTION monarch_security.request_provisioning_change(uuid,uuid,text,uuid,text,text,uuid,text)
    OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.request_provisioning_change(uuid,uuid,text,uuid,text,text,uuid,text)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.request_provisioning_change(uuid,uuid,text,uuid,text,text,uuid,text)
    TO monarch_api_runtime;

CREATE OR REPLACE FUNCTION monarch_security.approve_provisioning_change(
    p_request_id uuid,
    p_decision_reason text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $approve$
DECLARE
    actor_id uuid;
    company_id_value uuid;
    branch_id_value uuid;
    request_row monarch_security.provisioning_requests%ROWTYPE;
    changed_rows integer;
BEGIN
    company_id_value := NULLIF(current_setting('monarch.company_id', true), '')::uuid;
    branch_id_value := NULLIF(current_setting('monarch.branch_id', true), '')::uuid;
    actor_id := monarch_security.assert_provisioning_actor(
        company_id_value, branch_id_value, 'security.provisioning.approve');

    IF p_decision_reason IS NULL OR length(btrim(p_decision_reason)) NOT BETWEEN 1 AND 500 THEN
        RAISE EXCEPTION 'An approval reason between 1 and 500 characters is required'
            USING ERRCODE = '22023';
    END IF;

    SELECT * INTO request_row
    FROM monarch_security.provisioning_requests
    WHERE request_id = p_request_id
      AND company_id = company_id_value
      AND branch_id = branch_id_value
    FOR UPDATE;

    IF NOT FOUND OR request_row.status <> 'PENDING' THEN
        RAISE EXCEPTION 'Pending provisioning request was not found in the selected scope'
            USING ERRCODE = 'P0002';
    END IF;
    IF actor_id = request_row.requested_by OR actor_id = request_row.target_user_id THEN
        RAISE EXCEPTION 'Requester and target user cannot approve this provisioning request'
            USING ERRCODE = '28000';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM public.monarch_tenant_users u
        JOIN public.monarch_user_branch_grants g
          ON g.company_id = u.company_id AND g.user_id = u.user_id
        WHERE u.company_id = company_id_value AND u.user_id = request_row.target_user_id
          AND u.is_active AND g.branch_id = branch_id_value AND g.is_active
    ) THEN
        RAISE EXCEPTION 'Target user is no longer an active member of the selected branch'
            USING ERRCODE = '28000';
    END IF;

    CASE request_row.request_type
        WHEN 'LINK_IDENTITY' THEN
            INSERT INTO monarch_security.external_identities(
                issuer, subject, company_id, user_id, is_active, created_at, disabled_at
            )
            VALUES (
                request_row.identity_issuer, request_row.identity_subject,
                company_id_value, request_row.target_user_id, true, clock_timestamp(), NULL
            )
            ON CONFLICT (issuer, subject, company_id) DO UPDATE
            SET is_active = true, disabled_at = NULL
            WHERE monarch_security.external_identities.user_id = EXCLUDED.user_id
              AND monarch_security.external_identities.is_active = false;
            GET DIAGNOSTICS changed_rows = ROW_COUNT;
            IF changed_rows <> 1 THEN
                RAISE EXCEPTION 'OIDC identity is active or is linked to a different internal user'
                    USING ERRCODE = '23505';
            END IF;
        WHEN 'ASSIGN_ROLE' THEN
            PERFORM 1
            FROM monarch_security.roles
            WHERE company_id = company_id_value
              AND role_id = request_row.role_id AND is_active
            FOR SHARE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'Requested role is no longer active'
                    USING ERRCODE = '28000';
            END IF;

            PERFORM 1
            FROM monarch_security.role_permissions
            WHERE company_id = company_id_value
              AND role_id = request_row.role_id
            FOR SHARE;

            IF EXISTS (
                SELECT 1
                FROM monarch_security.role_permissions requested_permission
                WHERE requested_permission.company_id = company_id_value
                  AND requested_permission.role_id = request_row.role_id
                  AND NOT EXISTS (
                      SELECT 1
                      FROM monarch_security.user_roles actor_assignment
                      JOIN monarch_security.roles actor_role
                        ON actor_role.company_id = actor_assignment.company_id
                       AND actor_role.role_id = actor_assignment.role_id
                       AND actor_role.is_active
                      JOIN monarch_security.role_permissions actor_permission
                        ON actor_permission.company_id = actor_role.company_id
                       AND actor_permission.role_id = actor_role.role_id
                      WHERE actor_assignment.company_id = company_id_value
                        AND actor_assignment.user_id = actor_id
                        AND actor_assignment.is_active
                        AND actor_permission.permission_code = requested_permission.permission_code
                  )
            ) THEN
                RAISE EXCEPTION 'Approver cannot delegate permissions they do not hold'
                    USING ERRCODE = '28000';
            END IF;

            INSERT INTO monarch_security.user_roles(
                company_id, user_id, role_id, is_active, assigned_at, assigned_by,
                revoked_at, revoked_by
            )
            VALUES (
                company_id_value, request_row.target_user_id, request_row.role_id,
                true, clock_timestamp(), actor_id, NULL, NULL
            )
            ON CONFLICT (company_id, user_id, role_id) DO UPDATE
            SET is_active = true, assigned_at = EXCLUDED.assigned_at,
                assigned_by = EXCLUDED.assigned_by, revoked_at = NULL, revoked_by = NULL
            WHERE monarch_security.user_roles.is_active = false;

            GET DIAGNOSTICS changed_rows = ROW_COUNT;
            IF changed_rows <> 1 THEN
                RAISE EXCEPTION 'Role assignment already exists or could not be activated'
                    USING ERRCODE = '23505';
            END IF;
        WHEN 'REVOKE_IDENTITY' THEN
            UPDATE monarch_security.external_identities
            SET is_active = false, disabled_at = clock_timestamp()
            WHERE company_id = company_id_value
              AND user_id = request_row.target_user_id
              AND issuer = request_row.identity_issuer
              AND subject = request_row.identity_subject
              AND is_active;
            GET DIAGNOSTICS changed_rows = ROW_COUNT;
            IF changed_rows <> 1 THEN
                RAISE EXCEPTION 'Identity mapping was already revoked or changed'
                    USING ERRCODE = 'P0002';
            END IF;
        WHEN 'REVOKE_ROLE' THEN
            UPDATE monarch_security.user_roles
            SET is_active = false, revoked_at = clock_timestamp(), revoked_by = actor_id
            WHERE company_id = company_id_value
              AND user_id = request_row.target_user_id
              AND role_id = request_row.role_id
              AND is_active;
            GET DIAGNOSTICS changed_rows = ROW_COUNT;
            IF changed_rows <> 1 THEN
                RAISE EXCEPTION 'Role assignment was already revoked or changed'
                    USING ERRCODE = 'P0002';
            END IF;
        ELSE
            RAISE EXCEPTION 'Unsupported provisioning request type'
                USING ERRCODE = '22023';
    END CASE;

    UPDATE monarch_security.provisioning_requests
    SET status = 'APPROVED', decided_by = actor_id,
        decided_at = clock_timestamp(), decision_reason = btrim(p_decision_reason)
    WHERE request_id = p_request_id AND status = 'PENDING';
END
$approve$;

ALTER FUNCTION monarch_security.approve_provisioning_change(uuid,text)
    OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.approve_provisioning_change(uuid,text)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.approve_provisioning_change(uuid,text)
    TO monarch_api_runtime;

CREATE OR REPLACE FUNCTION monarch_security.reject_provisioning_change(
    p_request_id uuid,
    p_decision_reason text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $reject$
DECLARE
    actor_id uuid;
    company_id_value uuid;
    branch_id_value uuid;
    request_row monarch_security.provisioning_requests%ROWTYPE;
    changed_rows integer;
BEGIN
    company_id_value := NULLIF(current_setting('monarch.company_id', true), '')::uuid;
    branch_id_value := NULLIF(current_setting('monarch.branch_id', true), '')::uuid;
    actor_id := monarch_security.assert_provisioning_actor(
        company_id_value, branch_id_value, 'security.provisioning.approve');

    IF p_decision_reason IS NULL OR length(btrim(p_decision_reason)) NOT BETWEEN 1 AND 500 THEN
        RAISE EXCEPTION 'A rejection reason between 1 and 500 characters is required'
            USING ERRCODE = '22023';
    END IF;

    SELECT * INTO request_row
    FROM monarch_security.provisioning_requests
    WHERE request_id = p_request_id
      AND company_id = company_id_value
      AND branch_id = branch_id_value
    FOR UPDATE;

    IF NOT FOUND OR request_row.status <> 'PENDING' THEN
        RAISE EXCEPTION 'Pending provisioning request was not found in the selected scope'
            USING ERRCODE = 'P0002';
    END IF;
    IF actor_id = request_row.requested_by OR actor_id = request_row.target_user_id THEN
        RAISE EXCEPTION 'Requester and target user cannot reject this provisioning request'
            USING ERRCODE = '28000';
    END IF;

    UPDATE monarch_security.provisioning_requests
    SET status = 'REJECTED', decided_by = actor_id,
        decided_at = clock_timestamp(), decision_reason = btrim(p_decision_reason)
    WHERE request_id = p_request_id AND status = 'PENDING';
    GET DIAGNOSTICS changed_rows = ROW_COUNT;
    IF changed_rows <> 1 THEN
        RAISE EXCEPTION 'Provisioning request changed concurrently'
            USING ERRCODE = '40001';
    END IF;
END
$reject$;

ALTER FUNCTION monarch_security.reject_provisioning_change(uuid,text)
    OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.reject_provisioning_change(uuid,text)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.reject_provisioning_change(uuid,text)
    TO monarch_api_runtime;

CREATE OR REPLACE FUNCTION monarch_security.list_provisioning_requests(p_status text DEFAULT 'PENDING')
RETURNS TABLE (
    request_id uuid,
    request_type varchar,
    target_user_id uuid,
    identity_issuer varchar,
    identity_subject varchar,
    role_id uuid,
    reason varchar,
    status varchar,
    requested_by uuid,
    requested_at timestamptz,
    decided_by uuid,
    decided_at timestamptz,
    decision_reason varchar
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, monarch_security
AS $list$
DECLARE
    actor_id uuid;
    company_id_value uuid;
    branch_id_value uuid;
    selected_permission text;
    can_approve boolean;
BEGIN
    company_id_value := NULLIF(current_setting('monarch.company_id', true), '')::uuid;
    branch_id_value := NULLIF(current_setting('monarch.branch_id', true), '')::uuid;
    selected_permission := NULLIF(current_setting('monarch.required_permission', true), '');

    IF selected_permission NOT IN ('security.provisioning.request', 'security.provisioning.approve') THEN
        RAISE EXCEPTION 'Provisioning read permission is required'
            USING ERRCODE = '28000';
    END IF;
    actor_id := monarch_security.assert_provisioning_actor(
        company_id_value, branch_id_value, selected_permission);

    IF p_status NOT IN ('PENDING', 'APPROVED', 'REJECTED', 'ALL') THEN
        RAISE EXCEPTION 'Unsupported provisioning request status filter'
            USING ERRCODE = '22023';
    END IF;

    SELECT EXISTS (
        SELECT 1
        FROM monarch_security.user_roles ur
        JOIN monarch_security.roles r
          ON r.company_id = ur.company_id AND r.role_id = ur.role_id AND r.is_active
        JOIN monarch_security.role_permissions rp
          ON rp.company_id = r.company_id AND rp.role_id = r.role_id
        WHERE ur.company_id = company_id_value AND ur.user_id = actor_id
          AND ur.is_active AND rp.permission_code = 'security.provisioning.approve'
    ) INTO can_approve;

    RETURN QUERY
    SELECT pr.request_id, pr.request_type, pr.target_user_id,
           pr.identity_issuer, pr.identity_subject, pr.role_id, pr.reason,
           pr.status, pr.requested_by, pr.requested_at, pr.decided_by,
           pr.decided_at, pr.decision_reason
    FROM monarch_security.provisioning_requests pr
    WHERE pr.company_id = company_id_value
      AND pr.branch_id = branch_id_value
      AND (p_status = 'ALL' OR pr.status = p_status)
      AND (can_approve OR pr.requested_by = actor_id)
    ORDER BY pr.requested_at DESC;
END
$list$;

ALTER FUNCTION monarch_security.list_provisioning_requests(text)
    OWNER TO monarch_security_definer;
REVOKE ALL ON FUNCTION monarch_security.list_provisioning_requests(text)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.list_provisioning_requests(text)
    TO monarch_api_runtime;
