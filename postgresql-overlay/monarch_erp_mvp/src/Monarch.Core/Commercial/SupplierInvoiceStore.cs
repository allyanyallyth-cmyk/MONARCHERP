using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

public sealed record SupplierInvoiceLineInput(Guid ReceiptLineId, long QuantityMilli);
public sealed record SupplierInvoiceDraft(Guid Id, string Number, DateTime InvoiceDate, IReadOnlyList<SupplierInvoiceLineInput> Lines);
public sealed record SupplierInvoiceResult(Guid InvoiceId, long SubtotalMinor, long TaxMinor, long TotalMinor);
public sealed record SupplierInvoiceReceiptableLine(Guid ReceiptLineId, string ReceiptNumber, string Sku, string ItemName,
    long ReceivedQuantityMilli, long PreviouslyInvoicedQuantityMilli, long AvailableQuantityMilli, long UnitPriceMinor, int TaxRateBasisPoints);
public sealed record SupplierInvoiceSummary(Guid Id, string Number, string PurchaseOrderNumber, string Status,
    DateTime InvoiceDate, long TotalMinor, string CurrencyCode);

/// <summary>
/// Supplier invoice three-way match: invoice quantities must be allocated to posted receipt
/// lines belonging to the selected approved purchase order. Unit price and tax are inherited
/// from the approved PO line (no silent variance). Posting creates an accounting hand-off only;
/// it does not create a payable balance or a general-ledger journal.
/// </summary>
public sealed class SupplierInvoiceStore
{
    private readonly string _connectionString;
    public SupplierInvoiceStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public SupplierInvoiceResult CreateDraft(Guid userId, Guid companyId, Guid branchId, Guid purchaseOrderId, SupplierInvoiceDraft draft)
    {
        if (draft.Id == Guid.Empty || purchaseOrderId == Guid.Empty || string.IsNullOrWhiteSpace(draft.Number) || draft.Number.Trim().Length > 60 || draft.Lines is null || draft.Lines.Count is < 1 or > 500)
            throw new ArgumentException("رقم الفاتورة وأمر الشراء وبند واحد على الأقل مطلوبة.");
        if (draft.Lines.Any(x => x.ReceiptLineId == Guid.Empty || x.QuantityMilli <= 0) || draft.Lines.Select(x => x.ReceiptLineId).Distinct().Count() != draft.Lines.Count)
            throw new ArgumentException("بنود الفاتورة مكررة أو تحتوي كميات غير صحيحة.");
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        string supplier, currency; int scale;
        using (var order = c.CreateCommand())
        {
            order.Transaction = tx; order.CommandText = "SELECT supplier_id,currency_code,currency_scale,status FROM monarch_purchase_orders WHERE company_id=$co AND branch_id=$br AND id=$id;";
            order.Parameters.AddWithValue("$co", companyId.ToString()); order.Parameters.AddWithValue("$br", branchId.ToString()); order.Parameters.AddWithValue("$id", purchaseOrderId.ToString());
            using var r = order.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("أمر الشراء غير موجود ضمن الفرع الحالي.");
            supplier = r.GetString(0); currency = r.GetString(1); scale = r.GetInt32(2);
            if (r.GetString(3) is not ("Approved" or "PartiallyReceived" or "Received")) throw new InvalidOperationException("يجب اعتماد أمر الشراء أولاً.");
        }
        var now = DateTimeOffset.UtcNow.ToString("O"); long subtotal = 0, tax = 0;
        using (var head = c.CreateCommand())
        {
            head.Transaction = tx; head.CommandText = @"INSERT INTO monarch_supplier_invoices(id,company_id,branch_id,supplier_id,purchase_order_id,invoice_number,supplier_invoice_date,currency_code,currency_scale,status,created_utc,created_by)
VALUES($id,$co,$br,$supplier,$po,$number,$date,$currency,$scale,'Draft',$now,$user);";
            head.Parameters.AddWithValue("$id", draft.Id.ToString()); head.Parameters.AddWithValue("$co", companyId.ToString()); head.Parameters.AddWithValue("$br", branchId.ToString());
            head.Parameters.AddWithValue("$supplier", supplier); head.Parameters.AddWithValue("$po", purchaseOrderId.ToString()); head.Parameters.AddWithValue("$number", draft.Number.Trim());
            head.Parameters.AddWithValue("$date", draft.InvoiceDate.ToString("yyyy-MM-dd")); head.Parameters.AddWithValue("$currency", currency); head.Parameters.AddWithValue("$scale", scale);
            head.Parameters.AddWithValue("$now", now); head.Parameters.AddWithValue("$user", userId.ToString()); head.ExecuteNonQuery();
        }
        int n = 0;
        foreach (var input in draft.Lines)
        {
            string poLine, item, sku, name; long receiptQty, cost; int rate;
            using (var read = c.CreateCommand())
            {
                read.Transaction = tx; read.CommandText = @"SELECT rl.purchase_order_line_id,rl.item_id,pl.item_sku_snapshot,pl.item_name_snapshot,rl.quantity_milli,pl.unit_cost_minor,pl.tax_rate_bps
FROM monarch_purchase_receipt_lines rl JOIN monarch_purchase_receipts rr ON rr.company_id=rl.company_id AND rr.id=rl.receipt_id
JOIN monarch_purchase_order_lines pl ON pl.company_id=rl.company_id AND pl.id=rl.purchase_order_line_id
WHERE rl.company_id=$co AND rr.branch_id=$br AND rr.purchase_order_id=$po AND rr.status='Posted' AND rl.id=$receipt;";
                read.Parameters.AddWithValue("$co", companyId.ToString()); read.Parameters.AddWithValue("$br", branchId.ToString()); read.Parameters.AddWithValue("$po", purchaseOrderId.ToString()); read.Parameters.AddWithValue("$receipt", input.ReceiptLineId.ToString());
                using var r = read.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("سطر الاستلام لا يتبع أمر الشراء أو الفرع المحدد.");
                poLine = r.GetString(0); item = r.GetString(1); sku = r.GetString(2); name = r.GetString(3); receiptQty = r.GetInt64(4); cost = r.GetInt64(5); rate = r.GetInt32(6);
            }
            if (input.QuantityMilli > receiptQty) throw new InvalidOperationException($"كمية الفاتورة تتجاوز المستلم للصنف {sku}.");
            long net = checked((long)decimal.Round((decimal)input.QuantityMilli * cost / 1000m, 0, MidpointRounding.AwayFromZero));
            long lineTax = checked((long)decimal.Round((decimal)net * rate / 10000m, 0, MidpointRounding.AwayFromZero));
            subtotal = checked(subtotal + net); tax = checked(tax + lineTax);
            using var line = c.CreateCommand(); line.Transaction = tx;
            line.CommandText = @"INSERT INTO monarch_supplier_invoice_lines(id,company_id,invoice_id,line_number,purchase_order_line_id,receipt_line_id,item_id,item_sku_snapshot,item_name_snapshot,quantity_milli,unit_price_minor,tax_rate_bps,net_minor,tax_minor,total_minor)
VALUES($id,$co,$invoice,$no,$poLine,$receipt,$item,$sku,$name,$qty,$cost,$rate,$net,$tax,$total);";
            line.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); line.Parameters.AddWithValue("$co", companyId.ToString()); line.Parameters.AddWithValue("$invoice", draft.Id.ToString()); line.Parameters.AddWithValue("$no", ++n);
            line.Parameters.AddWithValue("$poLine", poLine); line.Parameters.AddWithValue("$receipt", input.ReceiptLineId.ToString()); line.Parameters.AddWithValue("$item", item); line.Parameters.AddWithValue("$sku", sku); line.Parameters.AddWithValue("$name", name);
            line.Parameters.AddWithValue("$qty", input.QuantityMilli); line.Parameters.AddWithValue("$cost", cost); line.Parameters.AddWithValue("$rate", rate); line.Parameters.AddWithValue("$net", net); line.Parameters.AddWithValue("$tax", lineTax); line.Parameters.AddWithValue("$total", checked(net + lineTax)); line.ExecuteNonQuery();
        }
        using (var totals = c.CreateCommand()) { totals.Transaction = tx; totals.CommandText = "UPDATE monarch_supplier_invoices SET subtotal_minor=$s,tax_minor=$t,total_minor=$all WHERE company_id=$co AND id=$id AND status='Draft';"; totals.Parameters.AddWithValue("$s", subtotal); totals.Parameters.AddWithValue("$t", tax); totals.Parameters.AddWithValue("$all", checked(subtotal + tax)); totals.Parameters.AddWithValue("$co", companyId.ToString()); totals.Parameters.AddWithValue("$id", draft.Id.ToString()); totals.ExecuteNonQuery(); }
        Audit(c, tx, userId, companyId, branchId, "SupplierInvoiceDraftCreated", draft.Id);
        tx.Commit(); return new(draft.Id, subtotal, tax, checked(subtotal + tax));
    }


    public IReadOnlyList<SupplierInvoiceReceiptableLine> ListReceiptableLines(Guid userId, Guid companyId, Guid branchId, Guid purchaseOrderId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT rl.id,rr.receipt_number,pl.item_sku_snapshot,pl.item_name_snapshot,rl.quantity_milli,
COALESCE((SELECT SUM(il.quantity_milli) FROM monarch_supplier_invoice_lines il JOIN monarch_supplier_invoices si ON si.company_id=il.company_id AND si.id=il.invoice_id WHERE il.company_id=rl.company_id AND il.receipt_line_id=rl.id AND si.status='Posted'),0),
pl.unit_cost_minor,pl.tax_rate_bps
FROM monarch_purchase_receipt_lines rl JOIN monarch_purchase_receipts rr ON rr.company_id=rl.company_id AND rr.id=rl.receipt_id
JOIN monarch_purchase_order_lines pl ON pl.company_id=rl.company_id AND pl.id=rl.purchase_order_line_id
WHERE rl.company_id=$co AND rr.branch_id=$br AND rr.purchase_order_id=$po AND rr.status='Posted'
AND rl.quantity_milli>COALESCE((SELECT SUM(il.quantity_milli) FROM monarch_supplier_invoice_lines il JOIN monarch_supplier_invoices si ON si.company_id=il.company_id AND si.id=il.invoice_id WHERE il.company_id=rl.company_id AND il.receipt_line_id=rl.id AND si.status='Posted'),0)
ORDER BY rr.posted_utc,pl.line_number;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$po", purchaseOrderId.ToString());
        var rows = new List<SupplierInvoiceReceiptableLine>(); using var r = q.ExecuteReader();
        while (r.Read()) { var received=r.GetInt64(4); var invoiced=r.GetInt64(5); rows.Add(new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),received,invoiced,received-invoiced,r.GetInt64(6),r.GetInt32(7))); }
        tx.Commit(); return rows;
    }

    public IReadOnlyList<SupplierInvoiceSummary> List(Guid userId, Guid companyId, Guid branchId, Guid? purchaseOrderId = null)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT i.id,i.invoice_number,p.order_number,i.status,i.supplier_invoice_date,i.total_minor,i.currency_code
FROM monarch_supplier_invoices i JOIN monarch_purchase_orders p ON p.company_id=i.company_id AND p.id=i.purchase_order_id
WHERE i.company_id=$co AND i.branch_id=$br AND ($po IS NULL OR i.purchase_order_id=$po) ORDER BY i.created_utc DESC;";
        q.Parameters.AddWithValue("$co",companyId.ToString()); q.Parameters.AddWithValue("$br",branchId.ToString()); q.Parameters.AddWithValue("$po",(object?)purchaseOrderId?.ToString() ?? DBNull.Value);
        var rows=new List<SupplierInvoiceSummary>(); using var r=q.ExecuteReader();
        while(r.Read()) rows.Add(new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),DateTime.Parse(r.GetString(4)),r.GetInt64(5),r.GetString(6)));
        tx.Commit(); return rows;
    }

    public void Post(Guid userId, Guid companyId, Guid branchId, Guid invoiceId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        using (var match = c.CreateCommand())
        {
            match.Transaction = tx; match.CommandText = @"SELECT COUNT(*) FROM (
SELECT il.receipt_line_id,rl.quantity_milli,SUM(il.quantity_milli) AS current_qty,
COALESCE((SELECT SUM(old.quantity_milli) FROM monarch_supplier_invoice_lines old JOIN monarch_supplier_invoices oi ON oi.company_id=old.company_id AND oi.id=old.invoice_id WHERE old.company_id=il.company_id AND old.receipt_line_id=il.receipt_line_id AND oi.status='Posted'),0) AS prior_qty
FROM monarch_supplier_invoice_lines il JOIN monarch_purchase_receipt_lines rl ON rl.company_id=il.company_id AND rl.id=il.receipt_line_id
WHERE il.company_id=$co AND il.invoice_id=$id GROUP BY il.receipt_line_id,rl.quantity_milli
HAVING current_qty+prior_qty>rl.quantity_milli);";
            match.Parameters.AddWithValue("$co", companyId.ToString()); match.Parameters.AddWithValue("$id", invoiceId.ToString()); if (Convert.ToInt64(match.ExecuteScalar()) != 0) throw new InvalidOperationException("تجاوزت الفاتورة الكمية المستلمة بعد احتساب الفواتير المرحّلة السابقة.");
        }
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx; update.CommandText = "UPDATE monarch_supplier_invoices SET status='Posted',posted_utc=$now,posted_by=$user WHERE company_id=$co AND branch_id=$br AND id=$id AND status='Draft' AND EXISTS(SELECT 1 FROM monarch_supplier_invoice_lines WHERE company_id=$co AND invoice_id=$id);";
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); update.Parameters.AddWithValue("$user", userId.ToString()); update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$br", branchId.ToString()); update.Parameters.AddWithValue("$id", invoiceId.ToString());
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("الفاتورة غير موجودة أو ليست مسودة أو لا تحتوي بنوداً.");
        }
        // Materialize the AP liability in the same transaction as invoice posting.
        using (var payable = c.CreateCommand())
        {
            payable.Transaction = tx;
            payable.CommandText = @"INSERT INTO monarch_supplier_payables(company_id,invoice_id,branch_id,supplier_id,invoice_number,invoice_date,due_date,currency_code,currency_scale,original_minor,status,created_utc)
SELECT i.company_id,i.id,i.branch_id,i.supplier_id,i.invoice_number,i.supplier_invoice_date,
 date(i.supplier_invoice_date,'+' || p.payment_terms_days || ' days'),i.currency_code,i.currency_scale,i.total_minor,'Open',$now
FROM monarch_supplier_invoices i JOIN monarch_parties p ON p.company_id=i.company_id AND p.id=i.supplier_id
WHERE i.company_id=$co AND i.branch_id=$br AND i.id=$id AND i.status='Posted' AND i.total_minor>0;";
            payable.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); payable.Parameters.AddWithValue("$co", companyId.ToString());
            payable.Parameters.AddWithValue("$br", branchId.ToString()); payable.Parameters.AddWithValue("$id", invoiceId.ToString());
            if (payable.ExecuteNonQuery() != 1) throw new InvalidOperationException("تعذر إنشاء رصيد الذمم الدائنة للفاتورة.");
        }
        using (var outbox = c.CreateCommand()) { outbox.Transaction = tx; outbox.CommandText = "INSERT INTO monarch_supplier_invoice_accounting_outbox(id,company_id,branch_id,invoice_id,event_type,created_utc) VALUES($id,$co,$br,$invoice,'SupplierInvoicePosted',$now);"; outbox.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); outbox.Parameters.AddWithValue("$co", companyId.ToString()); outbox.Parameters.AddWithValue("$br", branchId.ToString()); outbox.Parameters.AddWithValue("$invoice", invoiceId.ToString()); outbox.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); outbox.ExecuteNonQuery(); }
        Audit(c, tx, userId, companyId, branchId, "SupplierInvoicePosted", invoiceId); tx.Commit();
    }

    private static void Require(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1 JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1 JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id AND b.is_active=1 JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1);";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$br", br.ToString()); q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) < 1) throw new UnauthorizedAccessException("المستخدم لا يملك صلاحية العملية ضمن المنشأة والفرع المحددين.");
    }
    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, string action, Guid id)
    {
        using var a = c.CreateCommand(); a.Transaction = tx; a.CommandText = "INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,occurred_utc) VALUES($co,$br,$user,$action,'SupplierInvoice',$id,$now);";
        a.Parameters.AddWithValue("$co", co.ToString()); a.Parameters.AddWithValue("$br", br.ToString()); a.Parameters.AddWithValue("$user", user.ToString()); a.Parameters.AddWithValue("$action", action); a.Parameters.AddWithValue("$id", id.ToString()); a.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); a.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c; }
}
