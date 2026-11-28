using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

public sealed record PurchaseOrderReceiptLineInput(Guid PurchaseOrderLineId, long QuantityMilli);
public sealed record PurchaseOrderReceiptDraft(Guid Id, string Number, Guid PurchaseOrderId,
    IReadOnlyList<PurchaseOrderReceiptLineInput> Lines);
public sealed record PurchaseOrderReceiptResult(Guid ReceiptId, Guid CommercialDocumentId,
    string PurchaseOrderStatus, long SubtotalMinor, long TaxMinor, long TotalMinor);
public sealed record PurchaseOrderReceiptSummary(Guid Id, string Number, Guid PurchaseOrderId,
    string PurchaseOrderNumber, Guid CommercialDocumentId, DateTimeOffset PostedAt, long TotalMinor, string CurrencyCode);
public sealed record PurchaseOrderReceiptableLine(Guid Id, string Sku, string ItemName,
    long OrderedQuantityMilli, long ReceivedQuantityMilli, long RemainingQuantityMilli, long UnitCostMinor, int TaxRateBasisPoints);

/// <summary>
/// Posts partial/full receipts against approved purchase orders. The receipt document,
/// stock ledger movements, cost allocations, order quantities, accounting outbox and audit
/// record are committed in one SQLite transaction.
/// </summary>
public sealed class PurchaseOrderReceiptStore
{
    private readonly string _connectionString;
    public PurchaseOrderReceiptStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public PurchaseOrderReceiptResult Receive(Guid userId, Guid companyId, Guid branchId, PurchaseOrderReceiptDraft draft)
    {
        if (draft.Id == Guid.Empty || draft.PurchaseOrderId == Guid.Empty || string.IsNullOrWhiteSpace(draft.Number) ||
            draft.Number.Trim().Length > 40 || draft.Lines is null || draft.Lines.Count == 0 || draft.Lines.Count > 500)
            throw new ArgumentException("رقم الاستلام وأمر الشراء وبند واحد على الأقل مطلوبة.");
        if (draft.Lines.Any(x => x.PurchaseOrderLineId == Guid.Empty || x.QuantityMilli <= 0) ||
            draft.Lines.Select(x => x.PurchaseOrderLineId).Distinct().Count() != draft.Lines.Count)
            throw new ArgumentException("بنود الاستلام مكررة أو تحتوي كميات غير صحيحة.");

        using var c = Open();
        using var tx = c.BeginTransaction();
        Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        string supplierId, warehouseId, currency, orderNumber, orderStatus;
        int scale;
        using (var order = c.CreateCommand())
        {
            order.Transaction = tx;
            order.CommandText = @"SELECT supplier_id,warehouse_id,currency_code,currency_scale,order_number,status
FROM monarch_purchase_orders WHERE company_id=$co AND branch_id=$br AND id=$id;";
            order.Parameters.AddWithValue("$co", companyId.ToString()); order.Parameters.AddWithValue("$br", branchId.ToString());
            order.Parameters.AddWithValue("$id", draft.PurchaseOrderId.ToString());
            using var r = order.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("أمر الشراء غير موجود ضمن المنشأة والفرع الحاليين.");
            supplierId = r.GetString(0); warehouseId = r.GetString(1); currency = r.GetString(2); scale = r.GetInt32(3);
            orderNumber = r.GetString(4); orderStatus = r.GetString(5);
        }
        if (orderStatus is not ("Approved" or "PartiallyReceived"))
            throw new InvalidOperationException("لا يمكن الاستلام إلا لأمر شراء معتمد وغير مكتمل.");

        var receiptId = draft.Id;
        var documentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        long subtotal = 0, tax = 0;
        var rows = new List<(Guid PoLineId, Guid ItemId, Guid DocumentLineId, long Quantity, long Cost, int TaxRate, long Net, long Tax, string ItemType, string UnitCode)>();

        using (var head = c.CreateCommand())
        {
            head.Transaction = tx;
            head.CommandText = @"INSERT INTO monarch_commercial_documents
(id,company_id,branch_id,document_type,document_number,party_id,warehouse_id,status,currency_code,currency_scale,created_utc,created_by)
VALUES($id,$co,$br,'Purchase',$number,$party,$wh,'Draft',$currency,$scale,$now,$user);";
            head.Parameters.AddWithValue("$id", documentId.ToString()); head.Parameters.AddWithValue("$co", companyId.ToString());
            head.Parameters.AddWithValue("$br", branchId.ToString()); head.Parameters.AddWithValue("$number", draft.Number.Trim());
            head.Parameters.AddWithValue("$party", supplierId); head.Parameters.AddWithValue("$wh", warehouseId);
            head.Parameters.AddWithValue("$currency", currency); head.Parameters.AddWithValue("$scale", scale);
            head.Parameters.AddWithValue("$now", now); head.Parameters.AddWithValue("$user", userId.ToString()); head.ExecuteNonQuery();
        }

        int lineNo = 0;
        foreach (var input in draft.Lines)
        {
            string itemId, sku, name, itemType, unitCode; long ordered, received, cost; int taxRate;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = @"SELECT l.item_id,l.item_sku_snapshot,l.item_name_snapshot,l.quantity_milli,l.received_quantity_milli,
 l.unit_cost_minor,l.tax_rate_bps,i.item_type,u.code
FROM monarch_purchase_order_lines l JOIN monarch_items i ON i.company_id=l.company_id AND i.id=l.item_id JOIN monarch_units u ON u.company_id=i.company_id AND u.id=i.unit_id
WHERE l.company_id=$co AND l.order_id=$order AND l.id=$line;";
                q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$order", draft.PurchaseOrderId.ToString());
                q.Parameters.AddWithValue("$line", input.PurchaseOrderLineId.ToString());
                using var r = q.ExecuteReader();
                if (!r.Read()) throw new InvalidOperationException("أحد بنود الاستلام لا يتبع أمر الشراء المحدد.");
                itemId = r.GetString(0); sku = r.GetString(1); name = r.GetString(2); ordered = r.GetInt64(3);
                received = r.GetInt64(4); cost = r.GetInt64(5); taxRate = r.GetInt32(6); itemType = r.GetString(7); unitCode = r.GetString(8);
            }
            if (input.QuantityMilli > ordered - received)
                throw new InvalidOperationException($"الكمية المطلوبة للاستلام تتجاوز المتبقي للصنف {sku}.");
            long net = checked((long)decimal.Round((decimal)input.QuantityMilli * cost / 1000m, 0, MidpointRounding.AwayFromZero));
            long lineTax = checked((long)decimal.Round((decimal)net * taxRate / 10000m, 0, MidpointRounding.AwayFromZero));
            subtotal = checked(subtotal + net); tax = checked(tax + lineTax);
            var docLineId = Guid.NewGuid();
            using (var line = c.CreateCommand())
            {
                line.Transaction = tx;
                line.CommandText = @"INSERT INTO monarch_commercial_document_lines
(id,company_id,document_id,line_number,item_id,item_sku_snapshot,item_name_snapshot,quantity_milli,unit_price_minor,tax_rate_bps,net_minor,tax_minor,total_minor)
VALUES($id,$co,$doc,$no,$item,$sku,$name,$qty,$cost,$rate,$net,$tax,$total);";
                line.Parameters.AddWithValue("$id", docLineId.ToString()); line.Parameters.AddWithValue("$co", companyId.ToString());
                line.Parameters.AddWithValue("$doc", documentId.ToString()); line.Parameters.AddWithValue("$no", ++lineNo);
                line.Parameters.AddWithValue("$item", itemId); line.Parameters.AddWithValue("$sku", sku); line.Parameters.AddWithValue("$name", name);
                line.Parameters.AddWithValue("$qty", input.QuantityMilli); line.Parameters.AddWithValue("$cost", cost);
                line.Parameters.AddWithValue("$rate", taxRate); line.Parameters.AddWithValue("$net", net);
                line.Parameters.AddWithValue("$tax", lineTax); line.Parameters.AddWithValue("$total", checked(net + lineTax)); line.ExecuteNonQuery();
            }
            rows.Add((input.PurchaseOrderLineId, Guid.Parse(itemId), docLineId, input.QuantityMilli, cost, taxRate, net, lineTax, itemType, unitCode));
        }

        long total = checked(subtotal + tax);
        using (var totals = c.CreateCommand())
        {
            totals.Transaction = tx; totals.CommandText = "UPDATE monarch_commercial_documents SET subtotal_minor=$s,tax_minor=$t,total_minor=$all WHERE company_id=$co AND id=$id AND status='Draft';";
            totals.Parameters.AddWithValue("$s", subtotal); totals.Parameters.AddWithValue("$t", tax); totals.Parameters.AddWithValue("$all", total);
            totals.Parameters.AddWithValue("$co", companyId.ToString()); totals.Parameters.AddWithValue("$id", documentId.ToString()); totals.ExecuteNonQuery();
        }
        using (var receipt = c.CreateCommand())
        {
            receipt.Transaction = tx;
            receipt.CommandText = @"INSERT INTO monarch_purchase_receipts
(id,company_id,branch_id,purchase_order_id,commercial_document_id,receipt_number,status,created_utc,created_by,posted_utc,posted_by,total_minor,currency_code)
VALUES($id,$co,$br,$order,$doc,$number,'Posted',$now,$user,$now,$user,$total,$currency);";
            receipt.Parameters.AddWithValue("$id", receiptId.ToString()); receipt.Parameters.AddWithValue("$co", companyId.ToString());
            receipt.Parameters.AddWithValue("$br", branchId.ToString()); receipt.Parameters.AddWithValue("$order", draft.PurchaseOrderId.ToString());
            receipt.Parameters.AddWithValue("$doc", documentId.ToString()); receipt.Parameters.AddWithValue("$number", draft.Number.Trim());
            receipt.Parameters.AddWithValue("$now", now); receipt.Parameters.AddWithValue("$user", userId.ToString());
            receipt.Parameters.AddWithValue("$total", total); receipt.Parameters.AddWithValue("$currency", currency); receipt.ExecuteNonQuery();
        }

        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            using (var receiptLine = c.CreateCommand())
            {
                receiptLine.Transaction = tx;
                receiptLine.CommandText = @"INSERT INTO monarch_purchase_receipt_lines
(id,company_id,receipt_id,purchase_order_line_id,commercial_document_line_id,item_id,quantity_milli,unit_cost_minor)
VALUES($id,$co,$receipt,$poLine,$docLine,$item,$qty,$cost);";
                receiptLine.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); receiptLine.Parameters.AddWithValue("$co", companyId.ToString());
                receiptLine.Parameters.AddWithValue("$receipt", receiptId.ToString()); receiptLine.Parameters.AddWithValue("$poLine", row.PoLineId.ToString());
                receiptLine.Parameters.AddWithValue("$docLine", row.DocumentLineId.ToString()); receiptLine.Parameters.AddWithValue("$item", row.ItemId.ToString());
                receiptLine.Parameters.AddWithValue("$qty", row.Quantity); receiptLine.Parameters.AddWithValue("$cost", row.Cost); receiptLine.ExecuteNonQuery();
            }
            using (var updateLine = c.CreateCommand())
            {
                updateLine.Transaction = tx;
                updateLine.CommandText = @"UPDATE monarch_purchase_order_lines SET received_quantity_milli=received_quantity_milli+$qty
WHERE company_id=$co AND order_id=$order AND id=$line AND received_quantity_milli+$qty<=quantity_milli;";
                updateLine.Parameters.AddWithValue("$qty", row.Quantity); updateLine.Parameters.AddWithValue("$co", companyId.ToString());
                updateLine.Parameters.AddWithValue("$order", draft.PurchaseOrderId.ToString()); updateLine.Parameters.AddWithValue("$line", row.PoLineId.ToString());
                if (updateLine.ExecuteNonQuery() != 1) throw new InvalidOperationException("تغيرت كمية الاستلام بالتزامن؛ أعد تحميل أمر الشراء.");
            }
            if (row.ItemType == "Stock")
            {
                var movementId = Guid.NewGuid(); long sequence;
                using (var seq = c.CreateCommand())
                {
                    seq.Transaction = tx; seq.CommandText = "SELECT COALESCE(MAX(ledger_sequence),0)+1 FROM monarch_stock_movements WHERE company_id=$co AND warehouse_id=$wh AND item_id=$item;";
                    seq.Parameters.AddWithValue("$co", companyId.ToString()); seq.Parameters.AddWithValue("$wh", warehouseId);
                    seq.Parameters.AddWithValue("$item", row.ItemId.ToString()); sequence = Convert.ToInt64(seq.ExecuteScalar());
                }
                using (var movement = c.CreateCommand())
                {
                    movement.Transaction = tx;
                    movement.CommandText = @"INSERT INTO monarch_stock_movements
(id,company_id,branch_id,warehouse_id,item_id,ledger_sequence,movement_type,quantity_milli,unit_cost_minor,source_type,source_id,idempotency_key,occurred_utc,actor_user_id)
VALUES($id,$co,$br,$wh,$item,$seq,'Receipt',$qty,$cost,'PurchaseReceipt',$receipt,$key,$now,$user);";
                    movement.Parameters.AddWithValue("$id", movementId.ToString()); movement.Parameters.AddWithValue("$co", companyId.ToString());
                    movement.Parameters.AddWithValue("$br", branchId.ToString()); movement.Parameters.AddWithValue("$wh", warehouseId);
                    movement.Parameters.AddWithValue("$item", row.ItemId.ToString()); movement.Parameters.AddWithValue("$seq", sequence);
                    movement.Parameters.AddWithValue("$qty", row.Quantity); movement.Parameters.AddWithValue("$cost", row.Cost);
                    movement.Parameters.AddWithValue("$receipt", receiptId.ToString()); movement.Parameters.AddWithValue("$key", $"{receiptId:N}:{index}");
                    movement.Parameters.AddWithValue("$now", now); movement.Parameters.AddWithValue("$user", userId.ToString()); movement.ExecuteNonQuery();
                }
                long costTotal = checked((long)decimal.Round((decimal)row.Quantity * row.Cost / 1000m, 0, MidpointRounding.AwayFromZero));
                using var allocation = c.CreateCommand(); allocation.Transaction = tx;
                allocation.CommandText = @"INSERT INTO monarch_inventory_cost_allocations
(id,company_id,branch_id,document_id,document_line_id,movement_id,allocation_type,quantity_milli,unit_cost_minor,total_cost_minor,cost_method,created_utc)
VALUES($id,$co,$br,$doc,$line,$movement,'PurchaseReceipt',$qty,$cost,$total,'DocumentCost',$now);";
                allocation.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); allocation.Parameters.AddWithValue("$co", companyId.ToString());
                allocation.Parameters.AddWithValue("$br", branchId.ToString()); allocation.Parameters.AddWithValue("$doc", documentId.ToString());
                allocation.Parameters.AddWithValue("$line", row.DocumentLineId.ToString()); allocation.Parameters.AddWithValue("$movement", movementId.ToString());
                allocation.Parameters.AddWithValue("$qty", row.Quantity); allocation.Parameters.AddWithValue("$cost", row.Cost);
                allocation.Parameters.AddWithValue("$total", costTotal); allocation.Parameters.AddWithValue("$now", now); allocation.ExecuteNonQuery();
            }
        }

        using (var post = c.CreateCommand())
        {
            post.Transaction = tx;
            post.CommandText = "UPDATE monarch_commercial_documents SET status='Posted',posted_utc=$now,posted_by=$user WHERE company_id=$co AND branch_id=$br AND id=$id AND status='Draft';";
            post.Parameters.AddWithValue("$now", now); post.Parameters.AddWithValue("$user", userId.ToString());
            post.Parameters.AddWithValue("$co", companyId.ToString()); post.Parameters.AddWithValue("$br", branchId.ToString());
            post.Parameters.AddWithValue("$id", documentId.ToString()); if (post.ExecuteNonQuery() != 1) throw new InvalidOperationException("تعذر ترحيل مستند الاستلام.");
        }
        // Capture the approved, date-effective carbon-factor version in the same transaction
        // as receipt posting. Missing factors create a durable measurement gap instead of
        // blocking legitimate inventory/accounting operations or inventing an emission value.
        if (HasTable(c, tx, "monarch_carbon_factor_versions") && HasTable(c, tx, "monarch_carbon_measurement_gaps") && HasTable(c, tx, "monarch_carbon_document_snapshots"))
        foreach (var unitGroup in rows.GroupBy(x => x.UnitCode, StringComparer.OrdinalIgnoreCase))
        {
            var activity = "PURCHASED_GOODS:" + unitGroup.Key;
            var quantity = unitGroup.Sum(x => x.Quantity);
            string? factorId = null;
            decimal factor = 0m;
            using (var factorQuery = c.CreateCommand())
            {
                factorQuery.Transaction = tx;
                factorQuery.CommandText = @"SELECT id,kg_co2e_per_unit FROM monarch_carbon_factor_versions
WHERE company_id=$co AND activity_code='PURCHASED_GOODS' AND unit_code=$unit
 AND approval_state='Approved' AND effective_from_utc<=$at
 AND (effective_to_utc IS NULL OR $at<effective_to_utc)
ORDER BY effective_from_utc DESC LIMIT 1;";
                factorQuery.Parameters.AddWithValue("$co", companyId.ToString());
                factorQuery.Parameters.AddWithValue("$unit", unitGroup.Key);
                factorQuery.Parameters.AddWithValue("$at", now);
                using var factorReader = factorQuery.ExecuteReader();
                if (factorReader.Read())
                {
                    factorId = factorReader.GetString(0);
                    factor = decimal.Parse(factorReader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            if (factorId != null)
            {
                using var carbon = c.CreateCommand(); carbon.Transaction = tx;
                carbon.CommandText = @"INSERT INTO monarch_carbon_document_snapshots
(id,company_id,branch_id,document_type,document_id,activity_code,quantity,kg_co2e,factor_version_id,transaction_utc,recorded_by,recorded_utc)
VALUES($id,$co,$br,'PurchaseReceipt',$receipt,$activity,$qty,$kg,$factor,$at,$actor,$now);";
                carbon.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); carbon.Parameters.AddWithValue("$co", companyId.ToString());
                carbon.Parameters.AddWithValue("$br", branchId.ToString()); carbon.Parameters.AddWithValue("$receipt", receiptId.ToString());
                carbon.Parameters.AddWithValue("$activity", activity); carbon.Parameters.AddWithValue("$qty", (quantity / 1000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
                carbon.Parameters.AddWithValue("$kg", ((quantity / 1000m) * factor).ToString(System.Globalization.CultureInfo.InvariantCulture));
                carbon.Parameters.AddWithValue("$factor", factorId); carbon.Parameters.AddWithValue("$at", now);
                carbon.Parameters.AddWithValue("$actor", userId.ToString()); carbon.Parameters.AddWithValue("$now", now); carbon.ExecuteNonQuery();
                Audit(c, tx, userId, companyId, branchId, receiptId, draft.PurchaseOrderId, documentId,
                    "CarbonSnapshotRecorded:" + activity, 0, now);
            }
            else
            {
                using var gap = c.CreateCommand(); gap.Transaction = tx;
                gap.CommandText = @"INSERT INTO monarch_carbon_measurement_gaps
(id,company_id,branch_id,document_type,document_id,activity_code,unit_code,quantity_milli,transaction_utc,status,created_by,created_utc)
VALUES($id,$co,$br,'PurchaseReceipt',$receipt,'PURCHASED_GOODS',$unit,$qty,$at,'Pending',$actor,$now);";
                gap.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); gap.Parameters.AddWithValue("$co", companyId.ToString());
                gap.Parameters.AddWithValue("$br", branchId.ToString()); gap.Parameters.AddWithValue("$receipt", receiptId.ToString());
                gap.Parameters.AddWithValue("$unit", unitGroup.Key); gap.Parameters.AddWithValue("$qty", quantity);
                gap.Parameters.AddWithValue("$at", now); gap.Parameters.AddWithValue("$actor", userId.ToString()); gap.Parameters.AddWithValue("$now", now); gap.ExecuteNonQuery();
                Audit(c, tx, userId, companyId, branchId, receiptId, draft.PurchaseOrderId, documentId,
                    "CarbonMeasurementGapCreated:" + unitGroup.Key, 0, now);
            }
        }

        using (var outbox = c.CreateCommand())
        {
            outbox.Transaction = tx;
            outbox.CommandText = "INSERT INTO monarch_accounting_posting_outbox(id,company_id,branch_id,document_id,event_type,created_utc) VALUES($id,$co,$br,$doc,'PurchasePosted',$now);";
            outbox.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); outbox.Parameters.AddWithValue("$co", companyId.ToString());
            outbox.Parameters.AddWithValue("$br", branchId.ToString()); outbox.Parameters.AddWithValue("$doc", documentId.ToString());
            outbox.Parameters.AddWithValue("$now", now); outbox.ExecuteNonQuery();
        }
        string newStatus;
        using (var status = c.CreateCommand())
        {
            status.Transaction = tx;
            status.CommandText = @"SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM monarch_purchase_order_lines WHERE company_id=$co AND order_id=$order AND received_quantity_milli<quantity_milli)
THEN 'Received' ELSE 'PartiallyReceived' END;";
            status.Parameters.AddWithValue("$co", companyId.ToString()); status.Parameters.AddWithValue("$order", draft.PurchaseOrderId.ToString());
            newStatus = (string)status.ExecuteScalar()!;
        }
        using (var updateOrder = c.CreateCommand())
        {
            updateOrder.Transaction = tx; updateOrder.CommandText = "UPDATE monarch_purchase_orders SET status=$status WHERE company_id=$co AND branch_id=$br AND id=$id AND status IN('Approved','PartiallyReceived');";
            updateOrder.Parameters.AddWithValue("$status", newStatus); updateOrder.Parameters.AddWithValue("$co", companyId.ToString());
            updateOrder.Parameters.AddWithValue("$br", branchId.ToString()); updateOrder.Parameters.AddWithValue("$id", draft.PurchaseOrderId.ToString());
            if (updateOrder.ExecuteNonQuery() != 1) throw new InvalidOperationException("تعذر تحديث حالة أمر الشراء.");
        }
        Audit(c, tx, userId, companyId, branchId, receiptId, draft.PurchaseOrderId, documentId, draft.Number.Trim(), total, now);
        tx.Commit();
        return new(receiptId, documentId, newStatus, subtotal, tax, total);
    }

    public IReadOnlyList<PurchaseOrderReceiptableLine> ListReceiptableLines(Guid userId, Guid companyId, Guid branchId, Guid orderId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Require(c, tx, userId, companyId, branchId, "reports.read");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"SELECT l.id,l.item_sku_snapshot,l.item_name_snapshot,l.quantity_milli,l.received_quantity_milli,
 l.quantity_milli-l.received_quantity_milli,l.unit_cost_minor,l.tax_rate_bps
FROM monarch_purchase_order_lines l JOIN monarch_purchase_orders p ON p.company_id=l.company_id AND p.id=l.order_id
WHERE l.company_id=$co AND p.branch_id=$br AND p.id=$order AND p.status IN('Approved','PartiallyReceived')
 AND l.quantity_milli>l.received_quantity_milli ORDER BY l.line_number;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$br", branchId.ToString());
        cmd.Parameters.AddWithValue("$order", orderId.ToString());
        var result = new List<PurchaseOrderReceiptableLine>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7)));
        tx.Commit(); return result;
    }

    public IReadOnlyList<PurchaseOrderReceiptSummary> List(Guid userId, Guid companyId, Guid branchId, Guid? orderId = null)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Require(c, tx, userId, companyId, branchId, "reports.read");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"SELECT r.id,r.receipt_number,r.purchase_order_id,p.order_number,r.commercial_document_id,r.posted_utc,r.total_minor,r.currency_code
FROM monarch_purchase_receipts r JOIN monarch_purchase_orders p ON p.company_id=r.company_id AND p.id=r.purchase_order_id
WHERE r.company_id=$co AND r.branch_id=$br AND ($order IS NULL OR r.purchase_order_id=$order) ORDER BY r.posted_utc DESC;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$br", branchId.ToString());
        cmd.Parameters.AddWithValue("$order", (object?)orderId?.ToString() ?? DBNull.Value);
        var result = new List<PurchaseOrderReceiptSummary>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)),
            reader.GetString(3), Guid.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5)), reader.GetInt64(6), reader.GetString(7)));
        tx.Commit(); return result;
    }

    private static void Require(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id AND b.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1);";
        q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$br", branch.ToString());
        q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) < 1) throw new UnauthorizedAccessException("المستخدم لا يملك صلاحية العملية ضمن المنشأة والفرع المحددين.");
    }
    private static bool HasTable(SqliteConnection c, SqliteTransaction tx, string tableName)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        q.Parameters.AddWithValue("$name", tableName);
        return q.ExecuteScalar() != null;
    }

    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch,
        Guid receiptId, Guid orderId, Guid documentId, string number, long total, string now)
    {
        using var a = c.CreateCommand(); a.Transaction = tx;
        a.CommandText = @"INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,'PurchaseReceiptPosted','PurchaseReceipt',$id,$details,$now);";
        a.Parameters.AddWithValue("$co", company.ToString()); a.Parameters.AddWithValue("$br", branch.ToString());
        a.Parameters.AddWithValue("$user", user.ToString()); a.Parameters.AddWithValue("$id", receiptId.ToString());
        a.Parameters.AddWithValue("$details", $"order={orderId};document={documentId};number={number};total_minor={total}");
        a.Parameters.AddWithValue("$now", now); a.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand();
        p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c;
    }
}
