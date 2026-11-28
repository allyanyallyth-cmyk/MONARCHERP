from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PATCH = ROOT / "scripts" / "apply-desktop-navigation.py"
WINDOWS_REPAIR = ROOT / "scripts" / "patch-windows-source-strings.ps1"


def test_desktop_central_mode_guard_is_injected_before_sqlite_initialization():
    patch = PATCH.read_text(encoding="utf-8")
    assert 'Environment.GetEnvironmentVariable("MONARCH_DESKTOP_MODE")' in patch
    assert 'string.Equals(requestedMode, "central", StringComparison.OrdinalIgnoreCase)' in patch
    assert "وضع PostgreSQL المركزي غير متاح" in patch
    assert "لم يتم تهيئة SQLite ولم تُنفذ أي عملية مالية" in patch
    assert 'string.Equals(requestedMode, "local", StringComparison.OrdinalIgnoreCase)' in patch
    assert 'startup_anchor = "        InitializeComponent();\\n"' in patch
    assert "src = src.replace(startup_anchor, startup_anchor + central_mode_guard, 1)" in patch


def test_central_mode_guard_is_part_of_the_windows_source_repair_pipeline():
    repair = WINDOWS_REPAIR.read_text(encoding="utf-8")
    assert "apply-desktop-navigation.py" in repair
    assert "python $desktopNavigationPatch $rootPath" in repair
    assert "Desktop navigation and tax RBAC patch failed." in repair
