from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CENTRAL_UI = ROOT / "src/Monarch.Desktop.Central/MainWindow.xaml.cs"
LEDGER_HTTP = ROOT / "src/Monarch.Core/Remote/HttpGeneralLedgerStore.cs"


def test_central_ui_does_not_render_raw_exception_details():
    source = CENTRAL_UI.read_text(encoding="utf-8")
    assert "SafeFailureMessage(exception)" in source
    assert 'exception.Message' not in source
    assert "No local fallback was attempted" in source


def test_central_ui_gates_operations_on_authenticated_tenant_selection():
    source = CENTRAL_UI.read_text(encoding="utf-8")
    assert "private bool _signedIn;" in source
    assert "enabled && _signedIn && BranchBox.SelectedItem is TenantBranch" in source
    assert "if (_signedIn && CompanyBox.SelectedItem is TenantCompany company" in source


def test_busy_state_disables_all_mutating_navigation_surfaces():
    source = CENTRAL_UI.read_text(encoding="utf-8")
    assert "SetInteractionEnabled(false);" in source
    assert "SetInteractionEnabled(true);" in source
    assert "SignOutButton.IsEnabled = enabled && _signedIn;" in source
    assert "LedgerTabs.IsEnabled = enabled && _signedIn" in source


def test_user_identity_is_not_accepted_from_desktop_tenant_headers():
    source = LEDGER_HTTP.read_text(encoding="utf-8")
    assert 'request.Headers.Add("X-Monarch-Company-Id"' in source
    assert 'request.Headers.Add("X-Monarch-Branch-Id"' in source
    assert "X-Monarch-User-Id" not in source


def test_journal_detail_is_read_only_and_uses_authenticated_store():
    source = CENTRAL_UI.read_text(encoding="utf-8")
    assert "ViewJournalDetails_Click" in source
    assert "_ledger!.GetJournalAsync(tenant, selected.JournalId)" in source
    assert "JournalDetailsGrid.ItemsSource = detail.Lines" in source
    assert "No journal changes were made" in source
    assert "PostAsync(" not in source
