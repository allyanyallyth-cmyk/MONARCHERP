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


def test_unproven_oidc_identity_linking_fails_closed():
    schema_007 = (ROOT / "src/Monarch.PostgreSql/Schema/007_controlled_identity_provisioning.sql").read_text(encoding="utf-8")
    schema_008 = (ROOT / "src/Monarch.PostgreSql/Schema/008_reject_unproven_oidc_identity_links.sql").read_text(encoding="utf-8")
    assert "LINK_IDENTITY" in schema_007
    assert "reject_unproven_identity_link" in schema_008
    assert "TG_OP = 'INSERT' OR NEW.status = 'APPROVED'" in schema_008
    assert "USING ERRCODE = '55000'" in schema_008
    assert "BEFORE INSERT OR UPDATE OF request_type, status" in schema_008
    assert "arbitrary issuer/subject" in schema_008


def test_oidc_identity_enrollment_requires_verified_single_use_proof_and_independent_approval():
    schema_009 = (ROOT / "src/Monarch.PostgreSql/Schema/009_oidc_identity_enrollment_claims.sql").read_text(encoding="utf-8")
    validator = (ROOT / "src/Monarch.Api/Security/OidcEnrollmentProofValidator.cs").read_text(encoding="utf-8")
    service = (ROOT / "src/Monarch.Api/Services/IdentityEnrollmentApplicationService.cs").read_text(encoding="utf-8")
    endpoints = (ROOT / "src/Monarch.Api/Endpoints/IdentityEnrollmentEndpoints.cs").read_text(encoding="utf-8")
    program = (ROOT / "src/Monarch.Api/Program.cs").read_text(encoding="utf-8")
    assert "token_hash bytea NOT NULL UNIQUE" in schema_009
    assert "nonce_hash bytea NOT NULL" in schema_009
    assert "FOR UPDATE" in schema_009
    assert "status = 'PROVEN'" in schema_009
    assert "approve_identity_enrollment_claim" in schema_009
    assert "actor_id = claim_row.created_by OR actor_id = claim_row.target_user_id" in schema_009
    assert "digest(convert_to(p_nonce, 'UTF8'), 'sha256')" in schema_009
    assert "ValidateIssuerSigningKey = true" in validator
    assert "ValidAudience = _clientId" in validator
    assert 'Single(jwt, "nonce", 256)' in validator
    assert 'Single(jwt, "auth_time", 32)' in validator
    assert "MaximumAuthenticationAge" in validator
    assert "SHA256.HashData(tokenBytes)" in service
    assert "SHA256.HashData(Encoding.UTF8.GetBytes(nonce))" in service
    assert "AllowAnonymous()" in endpoints
    assert "RequireAuthorization()" in endpoints
    assert endpoints.count('RequireRateLimiting("identity-enrollment")') == 4
    assert "MapIdentityEnrollmentEndpoints" in program


def test_oidc_tenant_discovery_is_identity_scoped_and_exposes_only_execute_functions():
    schema_011 = (ROOT / "src/Monarch.PostgreSql/Schema/011_oidc_tenant_discovery.sql").read_text(encoding="utf-8")
    service = (ROOT / "src/Monarch.Api/Services/TenantDiscoveryApplicationService.cs").read_text(encoding="utf-8")
    endpoints = (ROOT / "src/Monarch.Api/Endpoints/TenantDiscoveryEndpoints.cs").read_text(encoding="utf-8")
    program = (ROOT / "src/Monarch.Api/Program.cs").read_text(encoding="utf-8")
    assert "monarch_security.list_oidc_companies" in schema_011
    assert "monarch_security.list_oidc_company_branches" in schema_011
    assert "SECURITY DEFINER" in schema_011
    assert "SET search_path = pg_catalog, public, monarch_security" in schema_011
    assert "GRANT EXECUTE ON FUNCTION monarch_security.list_oidc_companies" in schema_011
    assert "GRANT EXECUTE ON FUNCTION monarch_security.list_oidc_company_branches" in schema_011
    assert "GRANT SELECT ON" not in schema_011
    assert "RequireIdentity" in service and 'RequiredSingleClaim(identities[0], "iss")' in service
    assert 'RequiredSingleClaim(identities[0], "sub")' in service
    assert "list_oidc_companies(@issuer,@subject)" in service
    assert "list_oidc_company_branches(@company,@issuer,@subject)" in service
    assert 'MapGroup("/api/v1/tenant-context").RequireAuthorization()' in endpoints
    assert "MapTenantDiscoveryEndpoints" in program
    assert "AddScoped<TenantDiscoveryApplicationService>" in program


def test_tenant_discovery_postgresql_data_checks_are_injected_into_the_live_integration_run():
    patch = (ROOT / "scripts/patch-tenant-discovery-integration.py").read_text(encoding="utf-8")
    workflow = (ROOT.parent.parent / ".github/workflows/postgresql-overlay-validation.yml").read_text(encoding="utf-8")
    assert "OIDC company discovery returns every active company linked to the same authenticated subject and no unrelated company" in patch
    assert "OIDC branch discovery excludes revoked grants and branches without membership" in patch
    assert "OIDC company discovery returns no data for an unmapped subject" in patch
    assert "OIDC branch discovery cannot enumerate branches from another company" in patch
    assert "Patch OIDC tenant-discovery integration assertions" in workflow
