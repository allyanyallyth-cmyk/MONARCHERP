from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROGRAM = (ROOT / "src/Monarch.Api/Program.cs").read_text(encoding="utf-8")
ENDPOINTS = (ROOT / "src/Monarch.Api/Endpoints/GeneralLedgerEndpoints.cs").read_text(encoding="utf-8")
SERVICE = (ROOT / "src/Monarch.Api/Services/GeneralLedgerApplicationService.cs").read_text(encoding="utf-8")
TESTS = (ROOT / "tests/Monarch.PostgreSql.IntegrationTests/Program.cs").read_text(encoding="utf-8")


def test_financial_http_routes_are_only_mapped_in_authenticated_postgresql_mode():
    assert "if (jwtAuthenticationConfigured && string.IsNullOrWhiteSpace(postgresConnectionString))" in PROGRAM
    assert 'app.MapGeneralLedgerEndpoints();' in PROGRAM
    assert "if (jwtAuthenticationConfigured && !string.IsNullOrWhiteSpace(postgresConnectionString))" in PROGRAM
    assert "var group = endpoints.MapGroup(\"/api/v1/general-ledger\").RequireAuthorization();" in ENDPOINTS


def test_gl_http_surface_exposes_only_permission_checked_operations():
    for route in (
        'MapGet("/accounts"',
        'MapPost("/accounts"',
        'MapGet("/journals/{journalId:guid}"',
        'MapPost("/journals"',
        'MapPost("/journals/{journalId:guid}/post"',
        'MapGet("/reports/trial-balance"',
    ):
        assert route in ENDPOINTS
    for permission in (
        '"gl.accounts.read"',
        '"gl.accounts.manage"',
        '"gl.journal.read"',
        '"gl.journal.create"',
        '"gl.journal.post"',
        '"gl.report.trial_balance"',
    ):
        assert permission in SERVICE


def test_tenant_selection_is_untrusted_and_oidc_subject_is_resolved_server_side():
    assert 'CompanyHeader = "X-Monarch-Company-Id"' in SERVICE
    assert 'BranchHeader = "X-Monarch-Branch-Id"' in SERVICE
    assert 'IssuerClaim = "iss"' in SERVICE
    assert 'SubjectClaim = "sub"' in SERVICE
    assert "new TenantScope(companyId, branchId, Guid.Empty, issuer, subject, permission)" in SERVICE
    assert "TryGetTenantSelection" in ENDPOINTS
    assert "Guid.TryParseExact(companies[0], \"D\", out companyId)" in ENDPOINTS
    assert "Guid.TryParseExact(branches[0], \"D\", out branchId)" in ENDPOINTS
    assert "monarch:permission" not in SERVICE
    assert "monarch:user_id" not in SERVICE


def test_http_errors_do_not_turn_permission_denial_into_success():
    assert "StatusCodes.Status403Forbidden" in ENDPOINTS
    assert "StatusCodes.Status400BadRequest" in ENDPOINTS
    assert "StatusCodes.Status409Conflict" in ENDPOINTS
    assert "return Results.NoContent();" in ENDPOINTS


def test_database_selected_permissions_and_oidc_identity_are_regression_tested():
    assert "CreateAccountAsync(validBoundaryPrincipal, boundaryCompany, boundaryBranch, boundaryAccount)" in TESTS
    assert 'RequiredPermission == "gl.accounts.manage"' in TESTS
    assert "PostgreSQL resolves OIDC issuer/subject to the internal tenant user" in TESTS
    assert "database RBAC rejects an OIDC user without the requested journal-post permission" in TESTS


def test_gl_account_creation_is_audited_in_the_same_transaction():
    store = (ROOT / "src/Monarch.PostgreSql/Infrastructure/PostgreSqlGeneralLedgerStore.cs").read_text(encoding="utf-8")
    assert 'new PostgreSqlOutboxWriter().EnqueueAsync(scope, account.AccountId' in store
    assert '"GeneralLedger.AccountCreated"' in store
    assert "await scope.CommitAsync(cancellationToken)" in store
    assert "GL account creation writes one transactional, idempotent audit/outbox event per account" in TESTS
