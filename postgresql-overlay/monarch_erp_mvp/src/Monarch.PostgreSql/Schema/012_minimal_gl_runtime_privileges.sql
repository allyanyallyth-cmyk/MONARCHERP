-- 012: Grant the API runtime only the business-table privileges required by the GL repository.
-- Tenant isolation remains enforced by FORCE ROW LEVEL SECURITY and the signed transaction context.
-- RBAC, identity, membership and audit tables remain inaccessible directly to the runtime role.

GRANT USAGE ON SCHEMA public, monarch_security TO monarch_api_runtime;

GRANT SELECT, INSERT
    ON public.monarch_gl_accounts
    TO monarch_api_runtime;

GRANT SELECT, INSERT, UPDATE
    ON public.monarch_gl_journals
    TO monarch_api_runtime;

GRANT SELECT, INSERT, UPDATE
    ON public.monarch_gl_journal_lines
    TO monarch_api_runtime;

GRANT SELECT, INSERT, UPDATE
    ON public.monarch_gl_journal_sequences
    TO monarch_api_runtime;

GRANT INSERT
    ON public.monarch_outbox
    TO monarch_api_runtime;

REVOKE ALL ON FUNCTION monarch_gl_next_journal_number(uuid, uuid, integer)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_gl_next_journal_number(uuid, uuid, integer)
    TO monarch_api_runtime;

REVOKE ALL ON FUNCTION monarch_security.set_tenant_context(uuid, uuid, uuid, bigint, text)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.set_tenant_context(uuid, uuid, uuid, bigint, text)
    TO monarch_api_runtime;

REVOKE ALL ON FUNCTION monarch_security.context_is_valid()
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION monarch_security.context_is_valid()
    TO monarch_api_runtime;
