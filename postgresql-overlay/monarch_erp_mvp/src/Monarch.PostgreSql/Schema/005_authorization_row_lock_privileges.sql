-- 005: Permit SECURITY DEFINER to take row locks without granting business-data mutation rights.
-- SELECT ... FOR SHARE requires UPDATE privilege on at least one column of each locked table.
-- The dedicated version columns are never changed by the authorization code.

ALTER TABLE public.monarch_companies
    ADD COLUMN IF NOT EXISTS auth_lock_version integer NOT NULL DEFAULT 0;
ALTER TABLE public.monarch_branches
    ADD COLUMN IF NOT EXISTS auth_lock_version integer NOT NULL DEFAULT 0;
ALTER TABLE public.monarch_tenant_users
    ADD COLUMN IF NOT EXISTS auth_lock_version integer NOT NULL DEFAULT 0;
ALTER TABLE public.monarch_user_branch_grants
    ADD COLUMN IF NOT EXISTS auth_lock_version integer NOT NULL DEFAULT 0;

ALTER TABLE monarch_security.external_identities
    ADD COLUMN IF NOT EXISTS auth_lock_version integer NOT NULL DEFAULT 0;
ALTER TABLE monarch_security.roles
    ADD COLUMN IF NOT EXISTS auth_lock_version integer NOT NULL DEFAULT 0;
ALTER TABLE monarch_security.user_roles
    ADD COLUMN IF NOT EXISTS auth_lock_version integer NOT NULL DEFAULT 0;
ALTER TABLE monarch_security.role_permissions
    ADD COLUMN IF NOT EXISTS auth_lock_version integer NOT NULL DEFAULT 0;

GRANT UPDATE (auth_lock_version) ON public.monarch_companies,
    public.monarch_branches,
    public.monarch_tenant_users,
    public.monarch_user_branch_grants,
    monarch_security.external_identities,
    monarch_security.roles,
    monarch_security.user_roles,
    monarch_security.role_permissions
TO monarch_security_definer;
