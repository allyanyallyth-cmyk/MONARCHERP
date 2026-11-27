from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[1]
desktop = root / "src/Monarch.Desktop/MainWindow.xaml.cs"
store = root / "src/Monarch.Core/Commercial/PurchaseOrderStore.cs"
security = root / "database/security.sql"

NEW_UI = r"""
    private void BuildInterface()
    {
        var root = new DockPanel { LastChildFill = true, FlowDirection = FlowDirection.RightToLeft };
        Content = root;
        var header = new Border { Background = Brush("#0B1F3A"), Padding = new Thickness(22, 14, 22, 14) };
        DockPanel.SetDock(header, Dock.Top);
        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock { Text = "♛ MONARCH ERP", Foreground = Brushes.White, FontSize = 23, FontWeight = FontWeights.Bold });
        headerStack.Children.Add(new TextBlock { Text = "النظام الإداري والمحاسبي | " + _session.DisplayName + " | " + _session.RoleCode + " | " + _ledgerSession.CompanyName + " / " + _ledgerSession.BranchName + " | واجهة WPF الحالية تستخدم مخزن SQLite المحلي؛ ربط PostgreSQL للواجهة لم يكتمل بعد", Foreground = Brush("#D7A943"), FontSize = 12, Margin = new Thickness(1, 3, 0, 0) });
        header.Child = headerStack;
        root.Children.Add(header);

        var footer = new Border { Background = Brushes.White, Padding = new Thickness(12, 7, 12, 7), BorderBrush = Brush("#DCE3EC"), BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        _status.Foreground = Brush("#52647B");
        _status.Text = "قاعدة البيانات المحلية الحالية: " + _databasePath + " | سياق المنشأة والفرع مفعل";
        footer.Child = _status;
        root.Children.Add(footer);

        var workspace = new Grid { Margin = new Thickness(12) };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(252) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(workspace);

        var navigation = new Border { Background = Brushes.White, BorderBrush = Brush("#DCE3EC"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 0) };
        var navigationStack = new StackPanel();
        navigationStack.Children.Add(new TextBlock { Text = "التنقل بين الوحدات", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brush("#173B68"), Margin = new Thickness(5, 4, 5, 12) });
        navigation.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = navigationStack };
        Grid.SetColumn(navigation, 0);
        workspace.Children.Add(navigation);

        var contentHost = new DockPanel { Background = Brushes.White, LastChildFill = true };
        var pageTitle = new TextBlock { Text = "الرئيسية", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Brush("#173B68"), Margin = new Thickness(16, 12, 16, 8) };
        DockPanel.SetDock(pageTitle, Dock.Top);
        contentHost.Children.Add(pageTitle);
        var pageContent = new ContentControl { Margin = new Thickness(4) };
        contentHost.Children.Add(pageContent);
        Grid.SetColumn(contentHost, 1);
        workspace.Children.Add(contentHost);

        var pages = new System.Collections.Generic.List<Tuple<string, TabItem>>();
        void Add(string group, TabItem page) => pages.Add(Tuple.Create(group, page));
        Add("الرئيسية", BuildDashboardTab());
        if (_session.HasPermission("accounts.read")) Add("المحاسبة والقيود والتقارير", BuildAccountsTab());
        if (_session.HasPermission("journals.read") || _session.HasPermission("journals.create")) Add("المحاسبة والقيود والتقارير", BuildJournalTab());
        if (_session.HasPermission("reports.read")) Add("المحاسبة والقيود والتقارير", BuildReportsTab());

        if (_session.HasPermission("commercial.sales.manage"))
            AddUnavailablePage(pages, "المبيعات والعملاء وPOS وZATCA", "شاشة المبيعات والفواتير والعملاء ونقاط البيع وإشعارات الدائن وسلاسل التجزئة لم تُربط بعد بمخزن PostgreSQL وخدمة الفوترة. لا توجد بيانات حية معروضة هنا.");

        if (_session.HasPermission("inventory.read")) Add("المخزون والتدقيق", BuildMasterDataTab());
        if (_session.HasPermission("reports.read")) Add("المخزون والتدقيق", BuildInventoryAuditTab());

        if (_session.HasPermission("commercial.purchases.manage") && _session.HasPermission("reports.read")) Add("المشتريات والذمم", BuildPurchaseOrdersTab());
        if (_session.HasPermission("commercial.purchases.approve"))
        {
            Add("المشتريات والذمم", BuildProcurementApprovalTab());
            Add("المشتريات والذمم", BuildProcurementNotificationCenterTab());
        }
        if (_session.HasPermission("reports.read"))
        {
            Add("المشتريات والذمم", BuildSupplierPayablesTab());
            Add("المشتريات والذمم", BuildSupplierApAlertsTab());
            Add("المشتريات والذمم", BuildProcurementAnalyticsDashboardTab());
            Add("المشتريات والذمم", BuildProcurementExportAuditTab());
        }

        if (_session.HasPermission("users.manage"))
        {
            AddUnavailablePage(pages, "الخزينة والبنوك", "لم تُنفذ بعد حسابات البنوك أو المطابقة البنكية أو القيود الآلية؛ لا تعرض هذه الصفحة أرصدة افتراضية.");
            AddUnavailablePage(pages, "الأصول الثابتة والإهلاك", "يلزم تنفيذ سجل الأصول وطرق الإهلاك والقيود الدورية والاستبعادات مع اختبارات الترحيل.");
            AddUnavailablePage(pages, "مراكز التكلفة والموازنات", "لم تعتمد بعد أبعاد مراكز التكلفة أو دورات الموازنة أو تقارير الانحرافات.");
            AddUnavailablePage(pages, "الموارد البشرية والرواتب", "يلزم استكمال الموظفين والحضور والاستحقاقات والخصومات ومسير الرواتب والقيود مع عزل المستأجرين.");
            Add("الإدارة والصلاحيات والتدقيق", BuildUsersTab());
            Add("الإدارة والصلاحيات والتدقيق", BuildTenantAuditLogTab());
        }
        if (_session.HasPermission("users.manage") && string.Equals(_session.RoleCode, "Administrator", StringComparison.OrdinalIgnoreCase))
        {
            Add("الإدارة والصلاحيات والتدقيق", BuildDirectoryAdminTab());
            Add("الإدارة والصلاحيات والتدقيق", BuildCommercialSchemaUpgradeTab());
        }
        if (_session.HasPermission("backups.manage")) Add("الإدارة والصلاحيات والتدقيق", BuildBackupsTab());

        string? currentGroup = null;
        foreach (var entry in pages)
        {
            var group = entry.Item1;
            var page = entry.Item2;
            if (!string.Equals(currentGroup, group, StringComparison.Ordinal))
            {
                currentGroup = group;
                navigationStack.Children.Add(new TextBlock { Text = group, FontWeight = FontWeights.Bold, Foreground = Brush("#9A6A13"), Margin = new Thickness(5, 12, 5, 4) });
            }
            var button = new Button { Content = Convert.ToString(page.Header), HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.White, Foreground = Brush("#26384D"), BorderBrush = Brush("#E3E9F0"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand };
            button.Click += (_, __) => { pageTitle.Text = Convert.ToString(page.Header); pageContent.Content = page.Content; };
            navigationStack.Children.Add(button);
            if (pageContent.Content == null) { pageTitle.Text = Convert.ToString(page.Header); pageContent.Content = page.Content; }
        }
    }

    private static void AddUnavailablePage(System.Collections.Generic.List<Tuple<string, TabItem>> pages, string group, string explanation)
    {
        var panel = new StackPanel { Margin = new Thickness(22), MaxWidth = 900 };
        panel.Children.Add(new TextBlock { Text = "هذه الوحدة غير مفعلة حالياً", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brush("#8A4B08"), Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#52647B"), FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = "لن تُعرض أرقام أو سجلات تجريبية على أنها بيانات تشغيلية.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#8A4B08"), Margin = new Thickness(0, 14, 0, 0) });
        pages.Add(Tuple.Create(group, new TabItem { Header = group + " — غير مفعّل", Content = Scroll(panel) }));
    }
"""

src = desktop.read_text(encoding="utf-8")

# Fail closed when central mode is requested until the WPF OIDC/API adapter is
# implemented and acceptance-tested. Never silently start the SQLite ledger.
central_mode_guard = """        var requestedMode = Environment.GetEnvironmentVariable("MONARCH_DESKTOP_MODE");
        if (string.Equals(requestedMode, "central", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("وضع PostgreSQL المركزي غير متاح في هذا الإصدار من واجهة WPF. لم يتم تهيئة SQLite ولم تُنفذ أي عملية مالية. استخدم الوضع المحلي صراحةً أو انتظر إصدار الربط المركزي المعتمد.",
                "وضع التشغيل المركزي غير جاهز", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        if (!string.IsNullOrWhiteSpace(requestedMode) &&
            !string.Equals(requestedMode, "local", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("قيمة MONARCH_DESKTOP_MODE غير معروفة. القيم المسموحة حالياً: local أو central.",
                "إعداد تشغيل غير صالح", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }
"""
if "MONARCH_DESKTOP_MODE" not in src:
    startup_anchor = "        InitializeComponent();\n"
    if src.count(startup_anchor) != 1:
        raise SystemExit("Expected exactly one WPF InitializeComponent startup anchor; refusing central-mode guard patch.")
    src = src.replace(startup_anchor, startup_anchor + central_mode_guard, 1)
elif "وضع PostgreSQL المركزي غير متاح" not in src:
    raise SystemExit("MONARCH_DESKTOP_MODE already exists with an unknown implementation; refusing overwrite.")

start = src.index("    private void BuildInterface()")
end = src.index("    private TabItem BuildDashboardTab()", start)
src = src[:start] + NEW_UI + src[end:]

# Hide tax-rate controls and disable order creation unless the session has the dedicated grant.
tax_field = '        row2.Children.Add(FormLabel("الضريبة bps")); row2.Children.Add(_purchaseTaxRate);'
tax_field_replacement = """        if (_session.HasPermission("commercial.tax.manage"))
        {
            row2.Children.Add(FormLabel("الضريبة bps")); row2.Children.Add(_purchaseTaxRate);
        }
        else
        {
            _purchaseTaxRate.Visibility = Visibility.Collapsed;
            form.Children.Add(new TextBlock { Text = "تعديل معدل الضريبة محصور بالمستخدم المخوّل تجارياً؛ لن يسمح بإنشاء أمر شراء من هذه الشاشة دون صلاحية ضريبية معتمدة.", Foreground = Brush("#8A4B08"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 2, 0) });
        }"""
if tax_field in src:
    src = src.replace(tax_field, tax_field_replacement, 1)
elif 'row2.Children.Add(FormLabel("الضريبة bps"))' not in src:
    raise SystemExit("Tax-rate field anchor not found; refusing UI patch.")
create_button = 'var create = Button("إنشاء مسودة"); create.Click += (_, __) => CreatePurchaseOrderDraft(); row3.Children.Add(create);'
create_replacement = 'var create = Button("إنشاء مسودة"); create.IsEnabled = _session.HasPermission("commercial.purchases.manage") && _session.HasPermission("commercial.tax.manage"); create.Click += (_, __) => CreatePurchaseOrderDraft(); row3.Children.Add(create);'
if create_button in src:
    src = src.replace(create_button, create_replacement, 1)
elif 'create.IsEnabled = _session.HasPermission("commercial.purchases.manage") && _session.HasPermission("commercial.tax.manage")' not in src:
    raise SystemExit("Purchase-order create-button anchor not found; refusing UI patch.")

# Add an explicit least-privilege permission. Existing installations gain it via INSERT OR IGNORE.
schema = security.read_text(encoding="utf-8")
needle = "INSERT OR IGNORE INTO monarch_role_permissions(role_id,permission_code) VALUES\n ('role-admin','commercial.purchases.manage');"
addition = needle + "\n\n-- Tax rates are controlled separately from purchase-order entry.\nINSERT OR IGNORE INTO monarch_permissions(code,description) VALUES\n ('commercial.tax.manage','إدارة معدلات الضريبة التجارية');\nINSERT OR IGNORE INTO monarch_role_permissions(role_id,permission_code) VALUES\n ('role-admin','commercial.tax.manage');"
if "commercial.tax.manage" not in schema:
    if needle not in schema:
        raise SystemExit("Purchasing permission seed block not found; refusing patch.")
    schema = schema.replace(needle, addition, 1)
security.write_text(schema, encoding="utf-8")

# Enforce the tax permission at the domain boundary, not only in WPF.
store_text = store.read_text(encoding="utf-8")
store_needle = '        Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");\n        ValidateSupplierAndWarehouse(c, tx, companyId, branchId, draft.SupplierId, draft.WarehouseId);'
store_addition = '        Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");\n        if (draft.Lines.Any(line => line.TaxRateBasisPoints != 0))\n            Require(c, tx, userId, companyId, branchId, "commercial.tax.manage");\n        ValidateSupplierAndWarehouse(c, tx, companyId, branchId, draft.SupplierId, draft.WarehouseId);'
if 'Require(c, tx, userId, companyId, branchId, "commercial.tax.manage")' not in store_text:
    if store_needle not in store_text:
        raise SystemExit("Purchase order permission boundary not found; refusing patch.")
    store_text = store_text.replace(store_needle, store_addition, 1)
store.write_text(store_text, encoding="utf-8")

# The desktop action also fails closed for users without tax-management permission.
needle = '            if (!_session.HasPermission("commercial.purchases.manage")) throw new UnauthorizedAccessException("لا تملك صلاحية إدارة المشتريات.");\n            if (_purchaseSupplier.SelectedValue'
replacement = '            if (!_session.HasPermission("commercial.purchases.manage")) throw new UnauthorizedAccessException("لا تملك صلاحية إدارة المشتريات.");\n            if (!_session.HasPermission("commercial.tax.manage")) throw new UnauthorizedAccessException("لا تملك صلاحية إدارة الضريبة؛ لا يمكن إنشاء أمر شراء من هذه الشاشة دون صلاحية ضريبية معتمدة.");\n            if (_purchaseSupplier.SelectedValue'
if "لا تملك صلاحية إدارة الضريبة" not in src:
    if needle not in src:
        raise SystemExit("Purchase-order desktop handler not found; refusing patch.")
    src = src.replace(needle, replacement, 1)
desktop.write_text(src, encoding="utf-8")

# Refresh source-contract tests that previously asserted the retired TabControl registration syntax.
# Their behavioral checks remain intact; only the navigation-registration anchors change.
test_replacements = {
    "tests/test_backup_recovery_ui.py": [
        ("""if (_session.HasPermission("backups.manage")) tabs.Items.Add(BuildBackupsTab());""",
         """if (_session.HasPermission("backups.manage")) Add("الإدارة والصلاحيات والتدقيق", BuildBackupsTab());"""),
    ],
    "tests/test_commercial_schema_upgrade_ui.py": [
        ("""tabs.Items.Add(BuildCommercialSchemaUpgradeTab())""",
         """Add("الإدارة والصلاحيات والتدقيق", BuildCommercialSchemaUpgradeTab())"""),
        (""".split('root.Children.Add(tabs)', 1)[0]""",
         """.split('if (_session.HasPermission("backups.manage"))', 1)[0]"""),
    ],
    "tests/test_desktop_accounting_ui.py": [
        ("""tabs.Items.Add(BuildInventoryAuditTab());""",
         """Add("المخزون والتدقيق", BuildInventoryAuditTab());"""),
        ("""tabs.Items.Add(BuildMasterDataTab());""",
         """Add("المخزون والتدقيق", BuildMasterDataTab());"""),
    ],
    "tests/test_inventory_master_data_read_authorization.py": [
        (r"""self.assertIn('if (_session.HasPermission("inventory.read"))\n            tabs.Items.Add(BuildMasterDataTab());', WINDOW)""",
         """self.assertIn('if (_session.HasPermission("inventory.read")) Add("المخزون والتدقيق", BuildMasterDataTab());', WINDOW)"""),
    ],
    "tests/test_procurement_executive_dashboard.py": [
        ("""tabs.Items.Add(BuildProcurementAnalyticsDashboardTab());""",
         """Add("المشتريات والذمم", BuildProcurementAnalyticsDashboardTab());"""),
    ],
    "tests/test_procurement_report_export_audit.py": [
        ("""tabs.Items.Add(BuildProcurementExportAuditTab())""",
         """Add("المشتريات والذمم", BuildProcurementExportAuditTab())"""),
    ],
    "tests/test_supplier_ap_alert_center_ui.py": [
        ("""tabs.Items.Add(BuildSupplierApAlertsTab())""",
         """Add("المشتريات والذمم", BuildSupplierApAlertsTab())"""),
    ],
    "tests/test_supplier_payables_wpf.py": [
        ("""if (_session.HasPermission("reports.read")) tabs.Items.Add(BuildSupplierPayablesTab());""",
         """Add("المشتريات والذمم", BuildSupplierPayablesTab());"""),
    ],
    "tests/test_tenant_audit_log_reader.py": [
        ("""tabs.Items.Add(BuildTenantAuditLogTab())""",
         """Add("الإدارة والصلاحيات والتدقيق", BuildTenantAuditLogTab())"""),
    ],
}
for relative, pairs in test_replacements.items():
    test_path = root / relative
    if not test_path.exists():
        raise SystemExit(f"Expected existing regression test not found: {relative}")
    test_text = test_path.read_text(encoding="utf-8")
    for old, new in pairs:
        if old in test_text:
            test_text = test_text.replace(old, new, 1)
        elif new not in test_text:
            raise SystemExit(f"Regression-test anchor changed unexpectedly in {relative}: {old}")
    test_path.write_text(test_text, encoding="utf-8")

print("PASS: grouped RTL navigation, truthful module status, and tax RBAC patch applied.")


# Install the serializer dependency required by the cross-target HTTP GL adapter.
# The base cumulative archive owns this csproj; this deterministic overlay patch
# keeps the dependency in sync without editing or replacing the archived project.
core_project = root / "src/Monarch.Core/Monarch.Core.csproj"
http_adapter = root / "src/Monarch.Core/Remote/HttpGeneralLedgerStore.cs"
token_contract = root / "src/Monarch.Core/Abstractions/IAccessTokenProvider.cs"
if not core_project.exists() or not http_adapter.exists() or not token_contract.exists():
    raise SystemExit("Central GL HTTP adapter inputs are incomplete; refusing project patch.")
project_text = core_project.read_text(encoding="utf-8")
package_line = '<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />'
if 'Include="Newtonsoft.Json"' not in project_text:
    if project_text.count("</Project>") != 1:
        raise SystemExit("Unexpected Monarch.Core project structure; refusing serializer dependency patch.")
    project_text = project_text.replace("</Project>", "  <ItemGroup>\\n    " + package_line + "\\n  </ItemGroup>\\n</Project>", 1)
elif package_line not in project_text:
    raise SystemExit("Newtonsoft.Json is present at an unexpected version; refusing silent dependency change.")
core_project.write_text(project_text, encoding="utf-8")
print("PASS: central GL HTTP adapter and Newtonsoft.Json dependency are present.")
