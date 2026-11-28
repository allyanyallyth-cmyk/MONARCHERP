using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

public sealed record PurchaseOrderLineInput(Guid ItemId, long QuantityMilli, long UnitCostMinor, int TaxRateBasisPoints);
public sealed record PurchaseOrderDraft(Guid Id, string Number, Guid SupplierId, Guid WarehouseId,
    string CurrencyCode, int CurrencyScale, IReadOnlyList<PurchaseOrderLineInput> Lines,
    DateTimeOffset? ExpectedAt = null, string? Notes = null);
public sealed record PurchaseOrderTotals(long SubtotalMinor, long TaxMinor, long TotalMinor);
public sealed record PurchaseOrderSummary(Guid Id, string Number, string Status, string SupplierName,
    string WarehouseName, long TotalMinor, string CurrencyCode, DateTimeOffset CreatedAt);
public sealed record PurchaseSupplierOption(Guid Id, string Code, string DisplayName);

/// <summary>
/// Purchase-order authoring and approval. Receiving and supplier invoicing remain separate
/// documents; this store deliberately does not create stock movements or GL entries.
/// </summary>
public sealed class PurchaseOrderStore
{
    private readonly string _connectionString;
    public PurchaseOrderStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public PurchaseOrderTotals CreateDraft(Guid userId, Guid companyId, Guid branchId, PurchaseOrderDraft draft)
    {
        if (draft.Id == Guid.Empty || string.IsNullOrWhiteSpace(draft.Number) || draft.Number.Trim().Length > 40 ||
            draft.Lines is null || draft.Lines.Count == 0 || draft.Lines.Count > 500)
            throw new ArgumentException("رقم أمر الشراء وبند واحد على الأقل مطلوبان.");
        if (draft.CurrencyCode is null || draft.CurrencyCode.Trim().Length != 3 || draft.CurrencyScale is < 0 or > 4)
            throw new ArgumentException("بيانات العملة غير صحيحة.");
        using var c = Open(); using var tx = c.BeginTransaction();
        Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        ValidateSupplierAndWarehouse(c, tx, companyId, branchId, draft.SupplierId, draft.WarehouseId);
        long subtotal = 0, tax = 0;
        using (var h = c.CreateCommand())
        {
            h.Transaction = tx;
            h.CommandText = @"INSERT INTO monarch_purchase_orders
(id,company_id,branch_id,order_number,supplier_id,warehouse_id,status,currency_code,currency_scale,expected_utc,notes,created_utc,created_by)
VALUES($id,$co,$br,$number,$supplier,$warehouse,'Draft',$currency,$scale,$expected,$notes,$now,$user);";
            h.Parameters.AddWithValue("$id", draft.Id.ToString()); h.Parameters.AddWithValue("$co", companyId.ToString());
            h.Parameters.AddWithValue("$br", branchId.ToString()); h.Parameters.AddWithValue("$number", draft.Number.Trim());
            h.Parameters.AddWithValue("$supplier", draft.SupplierId.ToString()); h.Parameters.AddWithValue("$warehouse", draft.WarehouseId.ToString());
            h.Parameters.AddWithValue("$currency", draft.CurrencyCode.Trim().ToUpperInvariant()); h.Parameters.AddWithValue("$scale", draft.CurrencyScale);
            Add(h, "$expected", draft.ExpectedAt?.ToUniversalTime().ToString("O")); Add(h, "$notes", Normalize(draft.Notes));
            h.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); h.Parameters.AddWithValue("$user", userId.ToString()); h.ExecuteNonQuery();
        }
        int lineNo = 0;
        foreach (var line in draft.Lines)
        {
            if (line.ItemId == Guid.Empty || line.QuantityMilli <= 0 || line.UnitCostMinor < 0 || line.TaxRateBasisPoints is < 0 or > 10000)
                throw new ArgumentOutOfRangeException(nameof(draft), "بيانات أحد بنود أمر الشراء غير صحيحة.");
            string sku, name;
            using (var item = c.CreateCommand())
            {
                item.Transaction = tx; item.CommandText = "SELECT sku,name FROM monarch_items WHERE company_id=$co AND id=$id AND is_active=1;";
                item.Parameters.AddWithValue("$co", companyId.ToString()); item.Parameters.AddWithValue("$id", line.ItemId.ToString());
                using var r = item.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("الصنف غير موجود أو غير نشط في المنشأة.");
                sku = r.GetString(0); name = r.GetString(1);
            }
            long net = checked((long)decimal.Round((decimal)line.QuantityMilli * line.UnitCostMinor / 1000m, 0, MidpointRounding.AwayFromZero));
            long lineTax = checked((long)decimal.Round((decimal)net * line.TaxRateBasisPoints / 10000m, 0, MidpointRounding.AwayFromZero));
            subtotal = checked(subtotal + net); tax = checked(tax + lineTax);
            using var insert = c.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = @"INSERT INTO monarch_purchase_order_lines
(id,company_id,order_id,line_number,item_id,item_sku_snapshot,item_name_snapshot,quantity_milli,unit_cost_minor,tax_rate_bps,net_minor,tax_minor,total_minor)
VALUES($id,$co,$order,$line,$item,$sku,$name,$qty,$cost,$rate,$net,$tax,$total);";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); insert.Parameters.AddWithValue("$co", companyId.ToString());
            insert.Parameters.AddWithValue("$order", draft.Id.ToString()); insert.Parameters.AddWithValue("$line", ++lineNo);
            insert.Parameters.AddWithValue("$item", line.ItemId.ToString()); insert.Parameters.AddWithValue("$sku", sku); insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$qty", line.QuantityMilli); insert.Parameters.AddWithValue("$cost", line.UnitCostMinor);
            insert.Parameters.AddWithValue("$rate", line.TaxRateBasisPoints); insert.Parameters.AddWithValue("$net", net);
            insert.Parameters.AddWithValue("$tax", lineTax); insert.Parameters.AddWithValue("$total", checked(net + lineTax)); insert.ExecuteNonQuery();
        }
        long total = checked(subtotal + tax);
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx; update.CommandText = "UPDATE monarch_purchase_orders SET subtotal_minor=$s,tax_minor=$t,total_minor=$total WHERE company_id=$co AND id=$id AND status='Draft';";
            update.Parameters.AddWithValue("$s", subtotal); update.Parameters.AddWithValue("$t", tax); update.Parameters.AddWithValue("$total", total);
            update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$id", draft.Id.ToString()); update.ExecuteNonQuery();
        }
        Audit(c, tx, userId, companyId, branchId, "PurchaseOrderCreated", draft.Id, "number=" + draft.Number.Trim());
        tx.Commit(); return new(subtotal, tax, total);
    }

    public void Approve(Guid userId, Guid companyId, Guid branchId, Guid orderId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"UPDATE monarch_purchase_orders SET status='Approved',approved_utc=$now,approved_by=$user
WHERE company_id=$co AND branch_id=$br AND id=$id AND status='Draft'
AND EXISTS(SELECT 1 FROM monarch_purchase_order_lines l WHERE l.company_id=$co AND l.order_id=$id);";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$user", userId.ToString());
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$br", branchId.ToString()); cmd.Parameters.AddWithValue("$id", orderId.ToString());
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("لا يمكن اعتماد أمر الشراء؛ تحقق من وجوده وحالته وبنوده.");
        Audit(c, tx, userId, companyId, branchId, "PurchaseOrderApproved", orderId, null); tx.Commit();
    }

    public void Cancel(Guid userId, Guid companyId, Guid branchId, Guid orderId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE monarch_purchase_orders SET status='Cancelled' WHERE company_id=$co AND branch_id=$br AND id=$id AND status IN('Draft','Approved');";
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$br", branchId.ToString()); cmd.Parameters.AddWithValue("$id", orderId.ToString());
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("لا يمكن إلغاء أمر الشراء في حالته الحالية.");
        Audit(c, tx, userId, companyId, branchId, "PurchaseOrderCancelled", orderId, null); tx.Commit();
    }

    public IReadOnlyList<PurchaseSupplierOption> ListActiveSuppliers(Guid userId, Guid companyId, Guid branchId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"SELECT id,code,display_name FROM monarch_parties
WHERE company_id=$co AND is_active=1 AND party_type IN('Supplier','Both') ORDER BY code COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString());
        var result = new List<PurchaseSupplierOption>(); using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(new PurchaseSupplierOption(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2)));
        tx.Commit(); return result;
    }

    public IReadOnlyList<PurchaseOrderSummary> List(Guid userId, Guid companyId, Guid branchId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"SELECT o.id,o.order_number,o.status,p.display_name,w.name,o.total_minor,o.currency_code,o.created_utc
FROM monarch_purchase_orders o JOIN monarch_parties p ON p.company_id=o.company_id AND p.id=o.supplier_id
JOIN monarch_warehouses w ON w.company_id=o.company_id AND w.id=o.warehouse_id
WHERE o.company_id=$co AND o.branch_id=$br ORDER BY o.created_utc DESC,o.order_number;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$br", branchId.ToString());
        var result = new List<PurchaseOrderSummary>(); using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetString(6), DateTimeOffset.Parse(r.GetString(7))));
        tx.Commit(); return result;
    }

    private static void ValidateSupplierAndWarehouse(SqliteConnection c, SqliteTransaction tx, Guid co, Guid br, Guid supplier, Guid warehouse)
    {
        using (var p = c.CreateCommand()) { p.Transaction = tx; p.CommandText = "SELECT party_type FROM monarch_parties WHERE company_id=$co AND id=$id AND is_active=1;"; p.Parameters.AddWithValue("$co", co.ToString()); p.Parameters.AddWithValue("$id", supplier.ToString()); var type = p.ExecuteScalar() as string; if (type is not ("Supplier" or "Both")) throw new InvalidOperationException("المورد غير نشط أو لا يحمل صفة مورد."); }
        using (var w = c.CreateCommand()) { w.Transaction = tx; w.CommandText = "SELECT 1 FROM monarch_warehouses WHERE company_id=$co AND branch_id=$br AND id=$id AND is_active=1;"; w.Parameters.AddWithValue("$co", co.ToString()); w.Parameters.AddWithValue("$br", br.ToString()); w.Parameters.AddWithValue("$id", warehouse.ToString()); if (w.ExecuteScalar() is null) throw new InvalidOperationException("المستودع غير نشط أو لا يتبع الفرع المحدد."); }
    }
    private static void Require(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id AND b.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1);";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$br", br.ToString()); q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) < 1) throw new UnauthorizedAccessException("المستخدم لا يملك صلاحية العملية ضمن المنشأة والفرع المحددين.");
    }
    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, string action, Guid id, string? details)
    {
        using var a = c.CreateCommand(); a.Transaction = tx;
        a.CommandText = "INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc) VALUES($co,$br,$user,$action,'PurchaseOrder',$id,$details,$now);";
        a.Parameters.AddWithValue("$co", co.ToString()); a.Parameters.AddWithValue("$br", br.ToString()); a.Parameters.AddWithValue("$user", user.ToString());
        a.Parameters.AddWithValue("$action", action); a.Parameters.AddWithValue("$id", id.ToString()); Add(a, "$details", details); a.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); a.ExecuteNonQuery();
    }
    private static void Add(SqliteCommand c, string name, string? value) => c.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c; }
}
