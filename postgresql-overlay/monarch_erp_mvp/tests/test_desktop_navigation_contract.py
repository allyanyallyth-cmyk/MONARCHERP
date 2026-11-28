from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DESKTOP = ROOT / "src/Monarch.Desktop/MainWindow.xaml.cs"
PURCHASE_STORE = ROOT / "src/Monarch.Core/Commercial/PurchaseOrderStore.cs"
SECURITY_SCHEMA = ROOT / "database/security.sql"


def test_navigation_is_grouped_rtl_and_permission_filtered():
    source = DESKTOP.read_text(encoding="utf-8")
    for group in (
        "المحاسبة والقيود والتقارير",
        "المبيعات والعملاء وPOS وZATCA",
        "المشتريات والذمم",
        "المخزون والتدقيق",
        "الإدارة والصلاحيات والتدقيق",
        "الخزينة والبنوك",
        "الأصول الثابتة والإهلاك",
        "مراكز التكلفة والموازنات",
        "الموارد البشرية والرواتب",
    ):
        assert group in source
    assert "FlowDirection = FlowDirection.RightToLeft" in source
    assert '_session.HasPermission("accounts.read")' in source
    assert '_session.HasPermission("commercial.sales.manage")' in source


def test_unimplemented_modules_are_not_misrepresented_as_live_data():
    source = DESKTOP.read_text(encoding="utf-8")
    assert "هذه الوحدة غير مفعلة حالياً" in source
    assert "لن تُعرض أرقام أو سجلات تجريبية على أنها بيانات تشغيلية" in source
    assert "ربط PostgreSQL للواجهة لم يكتمل بعد" in source


def test_tax_field_and_domain_write_require_separate_permission():
    desktop = DESKTOP.read_text(encoding="utf-8")
    store = PURCHASE_STORE.read_text(encoding="utf-8")
    schema = SECURITY_SCHEMA.read_text(encoding="utf-8")
    assert '_session.HasPermission("commercial.tax.manage")' in desktop
    assert '_purchaseTaxRate.Visibility = Visibility.Collapsed' in desktop
    assert 'create.IsEnabled = _session.HasPermission("commercial.purchases.manage") && _session.HasPermission("commercial.tax.manage")' in desktop
    assert 'Require(c, tx, userId, companyId, branchId, "commercial.tax.manage")' in store
    assert "('commercial.tax.manage'" in schema
    assert "('role-admin','commercial.tax.manage')" in schema
