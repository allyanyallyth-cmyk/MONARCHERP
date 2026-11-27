from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCHEMA_003 = (ROOT / "src/Monarch.PostgreSql/Schema/003_oidc_identity_rbac.sql").read_text(encoding="utf-8")
SCHEMA_004 = (ROOT / "src/Monarch.PostgreSql/Schema/004_oidc_subject_resolution_and_atomic_authorization.sql").read_text(encoding="utf-8")
SCHEMA_005 = (ROOT / "src/Monarch.PostgreSql/Schema/005_authorization_row_lock_privileges.sql").read_text(encoding="utf-8")
SCHEMA_006 = (ROOT / "src/Monarch.PostgreSql/Schema/006_append_only_authorization_audit.sql").read_text(encoding="utf-8")
TENANT_CONTEXT = (ROOT / "src/Monarch.PostgreSql/Infrastructure/TenantDatabaseContext.cs").read_text(encoding="utf-8")
CONNECTION_FACTORY = (ROOT / "src/Monarch.PostgreSql/Infrastructure/PostgreSqlConnectionFactory.cs").read_text(encoding="utf-8")
GL_STORE = (ROOT / "src/Monarch.PostgreSql/Infrastructure/PostgreSqlGeneralLedgerStore.cs").read_text(encoding="utf-8")
SERVICE = (ROOT / "src/Monarch.Api/Services/GeneralLedgerApplicationService.cs").read_text(encoding="utf-8")
ENDPOINTS = (ROOT / "src/Monarch.Api/Endpoints/GeneralLedgerEndpoints.cs").read_text(encoding="utf-8")


def test_oidc_identity_and_company_scoped_rbac_are_database_owned():
    for table in (
        "monarch_security.external_identities",
        "monarch_security.roles",
        "monarch_security.permissions",
        "monarch_security.role_permissions",
        "monarch_security.user_roles",
        "monarch_security.authorization_audit",
    ):
        assert f"CREATE TABLE {table}" in SCHEMA_003
    assert "UNIQUE (issuer, subject, company_id)" in SCHEMA_003
    assert "GRANT EXECUTE ON FUNCTION monarch_security.set_authorized_tenant_context" in SCHEMA_003
    assert "FROM PUBLIC, monarch_api_runtime" in SCHEMA_003


def test_oidc_user_is_resolved_and_permission_is_locked_in_the_business_transaction():
    assert "monarch_security.resolve_oidc_user" in SCHEMA_004
    assert "FOR SHARE OF c, b, u, g, ei, ur, r, rp" in SCHEMA_004
    assert "monarch_security.set_authorized_tenant_context" in TENANT_CONTEXT
    assert "monarch_security.resolve_oidc_user" in TENANT_CONTEXT
    assert "tenant.IdentityIssuer" in CONNECTION_FACTORY
    assert "scope.Tenant.UserId" in GL_STORE


def test_api_does_not_trust_tenant_user_or_permission_claims():
    assert 'IssuerClaim = "iss"' in SERVICE
    assert 'SubjectClaim = "sub"' in SERVICE
    assert "monarch:user_id" not in SERVICE
    assert "monarch:permission" not in SERVICE
    assert "TryGetTenantSelection" in ENDPOINTS
    assert "X-Monarch-Company-Id" in SERVICE
    assert "X-Monarch-Branch-Id" in SERVICE


def test_authorization_row_locks_do_not_grant_business_column_updates():
    assert "auth_lock_version integer NOT NULL DEFAULT 0" in SCHEMA_005
    assert "GRANT UPDATE (auth_lock_version)" in SCHEMA_005
    assert "TO monarch_security_definer" in SCHEMA_005
    assert "GRANT UPDATE ON public.monarch_companies" not in SCHEMA_005


def test_authorization_audit_rejects_update_delete_and_truncate():
    assert "BEFORE UPDATE OR DELETE" in SCHEMA_006
    assert "BEFORE TRUNCATE" in SCHEMA_006
    assert "USING ERRCODE = '55000'" in SCHEMA_006
    assert "REVOKE UPDATE, DELETE, TRUNCATE" in SCHEMA_006
