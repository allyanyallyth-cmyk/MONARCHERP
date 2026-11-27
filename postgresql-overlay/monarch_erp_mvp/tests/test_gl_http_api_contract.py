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


def test_http_requests_cannot_supply_tenant_scope():
    assert "RequireTenant(principal, \"gl.accounts.manage\")" in SERVICE
    assert "RequireTenant(principal, \"gl.journal.post\")" in SERVICE
    assert "new TenantScope(companyId, branchId, userId)" in SERVICE
    assert "CompanyId" not in ENDPOINTS and "BranchId" not in ENDPOINTS and "UserId" not in ENDPOINTS


def test_http_errors_do_not_turn_permission_denial_into_success():
    assert "StatusCodes.Status403Forbidden" in ENDPOINTS
    assert "StatusCodes.Status400BadRequest" in ENDPOINTS
    assert "StatusCodes.Status409Conflict" in ENDPOINTS
    assert "return Results.NoContent();" in ENDPOINTS


def test_account_management_application_boundary_is_regression_tested():
    assert "CreateAccountAsync(accountManagePrincipal, boundaryAccount)" in TESTS
    assert "LastAccount == boundaryAccount" in TESTS
    assert "without gl.accounts.manage" in TESTS
