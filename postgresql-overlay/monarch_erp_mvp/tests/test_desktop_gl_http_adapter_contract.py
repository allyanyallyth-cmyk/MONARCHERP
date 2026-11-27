from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ADAPTER = ROOT / "src" / "Monarch.Core" / "Remote" / "HttpGeneralLedgerStore.cs"
TOKEN = ROOT / "src" / "Monarch.Core" / "Abstractions" / "IAccessTokenProvider.cs"
PATCH = ROOT / "scripts" / "apply-desktop-navigation.py"
WORKFLOW = ROOT.parent.parent / ".github" / "workflows" / "postgresql-overlay-validation.yml"


def test_central_http_store_implements_all_gl_routes_and_tenant_authentication():
    source = ADAPTER.read_text(encoding="utf-8")
    assert "IGeneralLedgerStore" in source
    for route in (
        '"accounts"',
        '"journals"',
        '"journals/" + journalId.ToString("D")',
        '"/post"',
        '"reports/trial-balance?asOfDate="',
    ):
        assert route in source
    assert 'new AuthenticationHeaderValue("Bearer", token)' in source
    assert '"X-Monarch-Company-Id"' in source
    assert '"X-Monarch-Branch-Id"' in source
    assert "HttpCompletionOption.ResponseContentRead" in source
    assert "The central API transport requires HTTPS" not in source  # exact guard text is checked below
    assert "requires HTTPS; HTTP is allowed only for loopback tests" in source
    assert "Do not automatically retry" in source or "no automatic retries" in source.lower()
    assert "MissingMemberHandling.Error" not in source


def test_access_token_provider_is_a_separate_oidc_boundary():
    source = TOKEN.read_text(encoding="utf-8")
    assert "interface IAccessTokenProvider" in source
    assert "GetAccessTokenAsync" in source
    assert "must never return an ID token" in source


def test_windows_overlay_injects_serializer_and_runs_http_contract_tests():
    patch = PATCH.read_text(encoding="utf-8")
    assert "Newtonsoft.Json" in patch
    assert "HttpGeneralLedgerStore.cs" in patch
    workflow = WORKFLOW.read_text(encoding="utf-8")
    assert "Monarch.Desktop.Http.Tests.csproj" in workflow
