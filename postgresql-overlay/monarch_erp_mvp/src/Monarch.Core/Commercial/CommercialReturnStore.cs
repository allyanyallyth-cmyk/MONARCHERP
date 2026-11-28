using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

public enum CommercialReturnType { SalesReturn, PurchaseReturn }
public sealed record CommercialReturnLineInput(Guid SourceLineId, long QuantityMilli);
public sealed record CommercialReturnDraft(Guid Id, string Number, CommercialReturnType Type,
    Guid SourceDocumentId, IReadOnlyList<CommercialReturnLineInput> Lines);
public sealed record CommercialReturnTotals(long SubtotalMinor, long TaxMinor, long TotalMinor);

/// <summary>
/// Creates and posts returns against posted invoices. Posting rechecks cumulative returned
/// quantities inside one SQLite transaction, then commits stock movements, return state and
/// accounting outbox together. The GL worker is a separate stage.
/// </summary>
public sealed class CommercialReturnStore
{
    private readonly string _connectionString;
    public CommercialReturnStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public CommercialReturnTotals CreateDraft(Guid userId, Guid companyId, Guid branchId, CommercialReturnDraft draft)
    {
        if (draft.Type != CommercialReturnType.SalesReturn && draft.Type != CommercialReturnType.PurchaseReturn)
            throw new ArgumentOutOfRangeException(nameof(draft), "Unsupported commercial return type.");
        if (draft.Id == Guid.Empty || string.IsNullOrWhiteSpace(draft.Number) || draft.Lines is null || draft.Lines.Count == 0)
            throw new ArgumentException("Return identity, number and at least one source line are required.");
        if (draft.Lines.Any(x => x.SourceLineId == Guid.Empty || x.QuantityMilli <= 0))
            throw new ArgumentOutOfRangeException(nameof(draft), "Return quantities must be positive.");
        if (draft.Lines.GroupBy(x => x.SourceLineId).Any(g => g.Count() > 1))
            throw new ArgumentException("A source invoice line may appear only once in a return.");

        using var c = Open(); using var tx = c.BeginTransaction();
        RequirePermission(c, tx, userId, companyId, branchId,
            draft.Type == CommercialReturnType.SalesReturn ? "commercial.sales.manage" : "commercial.purchases.manage");
        var source = LoadSource(c, tx, companyId, branchId, draft.SourceDocumentId, draft.Type);
        long subtotal = 0, tax = 0; int lineNo = 0;
        using (var head = c.CreateCommand())
        {
            head.Transaction = tx;
            head.CommandText = @"INSERT INTO monarch_commercial_returns(id,company_id,branch_id,return_type,return_number,source_document_id,party_id,warehouse_id,status,currency_code,currency_scale,created_utc,created_by)
VALUES($id,$co,$br,$type,$number,$source,$party,$wh,'Draft',$currency,$scale,$now,$user);";
            head.Parameters.AddWithValue("$id", draft.Id.ToString()); head.Parameters.AddWithValue("$co", companyId.ToString());
            head.Parameters.AddWithValue("$br", branchId.ToString()); head.Parameters.AddWithValue("$type", draft.Type.ToString());
            head.Parameters.AddWithValue("$number", draft.Number.Trim()); head.Parameters.AddWithValue("$source", draft.SourceDocumentId.ToString());
            head.Parameters.AddWithValue("$party", source.PartyId); head.Parameters.AddWithValue("$wh", source.WarehouseId);
            head.Parameters.AddWithValue("$currency", source.CurrencyCode); head.Parameters.AddWithValue("$scale", source.CurrencyScale);
            head.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); head.Parameters.AddWithValue("$user", userId.ToString()); head.ExecuteNonQuery();
        }
        foreach (var input in draft.Lines)
        {
            var original = LoadSourceLine(c, tx, companyId, draft.SourceDocumentId, input.SourceLineId);
            EnsureRemaining(c, tx, companyId, input.SourceLineId, input.QuantityMilli, null);
            var net = checked((long)decimal.Round((decimal)original.NetMinor * input.QuantityMilli / original.QuantityMilli, 0, MidpointRounding.AwayFromZero));
            var lineTax = checked((long)decimal.Round((decimal)original.TaxMinor * input.QuantityMilli / original.QuantityMilli, 0, MidpointRounding.AwayFromZero));
            subtotal = checked(subtotal + net); tax = checked(tax + lineTax);
            using var insert = c.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = @"INSERT INTO monarch_commercial_return_lines(id,company_id,return_id,source_line_id,line_number,item_id,item_sku_snapshot,item_name_snapshot,quantity_milli,unit_price_minor,tax_rate_bps,net_minor,tax_minor,total_minor)
VALUES($id,$co,$ret,$sourceLine,$no,$item,$sku,$name,$qty,$price,$rate,$net,$tax,$total);";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); insert.Parameters.AddWithValue("$co", companyId.ToString());
            insert.Parameters.AddWithValue("$ret", draft.Id.ToString()); insert.Parameters.AddWithValue("$sourceLine", input.SourceLineId.ToString());
            insert.Parameters.AddWithValue("$no", ++lineNo); insert.Parameters.AddWithValue("$item", original.ItemId);
            insert.Parameters.AddWithValue("$sku", original.Sku); insert.Parameters.AddWithValue("$name", original.Name);
            insert.Parameters.AddWithValue("$qty", input.QuantityMilli); insert.Parameters.AddWithValue("$price", original.UnitPriceMinor);
            insert.Parameters.AddWithValue("$rate", original.TaxRateBasisPoints); insert.Parameters.AddWithValue("$net", net);
            insert.Parameters.AddWithValue("$tax", lineTax); insert.Parameters.AddWithValue("$total", checked(net + lineTax)); insert.ExecuteNonQuery();
        }
        var total = checked(subtotal + tax);
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx; update.CommandText = "UPDATE monarch_commercial_returns SET subtotal_minor=$s,tax_minor=$t,total_minor=$all WHERE company_id=$co AND id=$id AND status='Draft';";
            update.Parameters.AddWithValue("$s", subtotal); update.Parameters.AddWithValue("$t", tax); update.Parameters.AddWithValue("$all", total);
            update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$id", draft.Id.ToString()); update.ExecuteNonQuery();
        }
        Audit(c, tx, userId, companyId, branchId, draft.Type + "DraftCreated", draft.Id,
            "sourceDocument=" + draft.SourceDocumentId + ";lines=" + draft.Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ";subtotalMinor=" + subtotal.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ";taxMinor=" + tax.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ";totalMinor=" + total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tx.Commit(); return new(subtotal, tax, total);
    }

    public void Post(Guid userId, Guid companyId, Guid branchId, Guid returnId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        string type, warehouse, sourceDocument;
        using (var h = c.CreateCommand())
        {
            h.Transaction = tx; h.CommandText = "SELECT return_type,warehouse_id,source_document_id,status FROM monarch_commercial_returns WHERE company_id=$co AND branch_id=$br AND id=$id;";
            h.Parameters.AddWithValue("$co", companyId.ToString()); h.Parameters.AddWithValue("$br", branchId.ToString()); h.Parameters.AddWithValue("$id", returnId.ToString());
            using var r = h.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("Return not found in selected company/branch.");
            type = r.GetString(0); warehouse = r.GetString(1); sourceDocument = r.GetString(2);
            if (r.GetString(3) != "Draft") throw new InvalidOperationException("Only draft returns can be posted.");
        }
        RequirePermission(c, tx, userId, companyId, branchId,
            type == "SalesReturn" ? "commercial.sales.manage" : type == "PurchaseReturn" ? "commercial.purchases.manage" :
            throw new InvalidOperationException("Unsupported commercial return type."));
        // Purchase receipt postings now accrue to GRNI. Until supplier credit notes are
        // implemented, a purchase return against an already invoiced receipt is blocked
        // rather than incorrectly debiting GRNI or AP without matching the invoice liability.
        if (type == "PurchaseReturn")
        {
            using var invoiced = c.CreateCommand(); invoiced.Transaction = tx;
            invoiced.CommandText = @"SELECT COUNT(*) FROM monarch_purchase_receipts pr
JOIN monarch_purchase_receipt_lines rl ON rl.company_id=pr.company_id AND rl.receipt_id=pr.id
JOIN monarch_supplier_invoice_lines il ON il.company_id=rl.company_id AND il.receipt_line_id=rl.id
JOIN monarch_supplier_invoices si ON si.company_id=il.company_id AND si.id=il.invoice_id AND si.status='Posted'
WHERE pr.company_id=$co AND pr.commercial_document_id=$doc;";
            invoiced.Parameters.AddWithValue("$co", companyId.ToString()); invoiced.Parameters.AddWithValue("$doc", sourceDocument);
            if (Convert.ToInt64(invoiced.ExecuteScalar()) > 0)
                throw new InvalidOperationException("لا يمكن ترحيل مرتجع شراء بعد فوترة سند الاستلام؛ يلزم إشعار دائن للمورد قبل دعم هذا المسار.");
        }
        var lines = new List<(Guid ReturnLineId, Guid SourceLineId, Guid ItemId, long Qty, long Cost, Guid SourceAllocationId, long TotalCost)>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = @"SELECT rl.id,rl.source_line_id,rl.item_id,rl.quantity_milli,i.item_type
FROM monarch_commercial_return_lines rl JOIN monarch_items i ON i.company_id=rl.company_id AND i.id=rl.item_id
WHERE rl.company_id=$co AND rl.return_id=$ret ORDER BY rl.line_number;";
            q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$ret", returnId.ToString());
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var returnLine = Guid.Parse(r.GetString(0)); var sourceLine = Guid.Parse(r.GetString(1));
                var item = Guid.Parse(r.GetString(2)); var qty = r.GetInt64(3);
                EnsureRemaining(c, tx, companyId, sourceLine, qty, returnId);
                if (r.GetString(4) == "Stock")
                {
                    var allocation = LoadOriginalCostAllocation(c, tx, companyId, sourceLine, type);
                    var priorReturnedQty = SumReturnedCostQuantity(c, tx, allocation.Id);
                    var priorReturnedCost = SumReturnedCost(c, tx, allocation.Id);
                    var cumulativeQty = checked(priorReturnedQty + qty);
                    if (cumulativeQty > allocation.QuantityMilli)
                        throw new InvalidOperationException("Return quantity exceeds the original cost allocation quantity.");
                    var cumulativeCost = checked((long)decimal.Round((decimal)allocation.TotalCostMinor * cumulativeQty / allocation.QuantityMilli, 0, MidpointRounding.AwayFromZero));
                    var returnCost = checked(cumulativeCost - priorReturnedCost);
                    var unitCost = allocation.UnitCostMinor;
                    lines.Add((returnLine, sourceLine, item, qty, unitCost, allocation.Id, returnCost));
                }
            }
        }
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var movementId = InsertMovement(c, tx, userId, companyId, branchId, Guid.Parse(warehouse), line.ItemId,
                type == "SalesReturn" ? "SalesReturn" : "PurchaseReturn", line.Qty, line.Cost, $"{returnId:N}:{i}");
            InsertReturnCostAllocation(c, tx, companyId, branchId, returnId, line.ReturnLineId,
                line.SourceAllocationId, movementId, type == "SalesReturn" ? "SalesReturnReversal" : "PurchaseReturnReversal",
                line.Qty, line.Cost, line.TotalCost);
        }
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx; update.CommandText = "UPDATE monarch_commercial_returns SET status='Posted',posted_utc=$now,posted_by=$user WHERE company_id=$co AND branch_id=$br AND id=$id AND status='Draft';";
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); update.Parameters.AddWithValue("$user", userId.ToString());
            update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$br", branchId.ToString()); update.Parameters.AddWithValue("$id", returnId.ToString());
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Return state changed; posting aborted.");
        }
        using (var outbox = c.CreateCommand())
        {
            outbox.Transaction = tx; outbox.CommandText = "INSERT INTO monarch_return_accounting_outbox(id,company_id,branch_id,return_id,event_type,created_utc) VALUES($id,$co,$br,$ret,$event,$now);";
            outbox.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); outbox.Parameters.AddWithValue("$co", companyId.ToString());
            outbox.Parameters.AddWithValue("$br", branchId.ToString()); outbox.Parameters.AddWithValue("$ret", returnId.ToString());
            outbox.Parameters.AddWithValue("$event", type + "Posted"); outbox.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); outbox.ExecuteNonQuery();
        }
        using (var audit = c.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,$action,'CommercialReturn',$ret,$details,$now);";
            audit.Parameters.AddWithValue("$co", companyId.ToString()); audit.Parameters.AddWithValue("$br", branchId.ToString());
            audit.Parameters.AddWithValue("$user", userId.ToString()); audit.Parameters.AddWithValue("$action", type + "Posted");
            audit.Parameters.AddWithValue("$ret", returnId.ToString()); audit.Parameters.AddWithValue("$details", "warehouse=" + warehouse + ";sourceDocument=" + sourceDocument + ";stockLines=" + lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); audit.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static SourceDocument LoadSource(SqliteConnection c, SqliteTransaction tx, Guid co, Guid br, Guid id, CommercialReturnType type)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT party_id,warehouse_id,currency_code,currency_scale FROM monarch_commercial_documents WHERE company_id=$co AND branch_id=$br AND id=$id AND status='Posted' AND document_type=$type;";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$br", br.ToString()); q.Parameters.AddWithValue("$id", id.ToString());
        q.Parameters.AddWithValue("$type", type == CommercialReturnType.SalesReturn ? "Sale" : "Purchase");
        using var r = q.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("A matching posted source invoice is required.");
        return new SourceDocument(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3));
    }
    private static SourceLine LoadSourceLine(SqliteConnection c, SqliteTransaction tx, Guid co, Guid doc, Guid line)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT l.item_id,l.item_sku_snapshot,l.item_name_snapshot,l.quantity_milli,l.unit_price_minor,l.tax_rate_bps,l.net_minor,l.tax_minor FROM monarch_commercial_document_lines l JOIN monarch_commercial_documents d ON d.company_id=l.company_id AND d.id=l.document_id WHERE l.company_id=$co AND l.document_id=$doc AND l.id=$line AND d.status='Posted';";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$doc", doc.ToString()); q.Parameters.AddWithValue("$line", line.ToString());
        using var r = q.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("Source invoice line not found.");
        return new SourceLine(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5), r.GetInt64(6), r.GetInt64(7));
    }
    private static void EnsureRemaining(SqliteConnection c, SqliteTransaction tx, Guid co, Guid sourceLine, long requested, Guid? currentReturn)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT l.quantity_milli-COALESCE((SELECT SUM(rl.quantity_milli) FROM monarch_commercial_return_lines rl
JOIN monarch_commercial_returns r ON r.company_id=rl.company_id AND r.id=rl.return_id
WHERE rl.company_id=l.company_id AND rl.source_line_id=l.id AND r.status='Posted' AND ($exclude IS NULL OR r.id<>$exclude)),0)
FROM monarch_commercial_document_lines l WHERE l.company_id=$co AND l.id=$line;";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$line", sourceLine.ToString());
        q.Parameters.AddWithValue("$exclude", currentReturn.HasValue ? currentReturn.Value.ToString() : (object)DBNull.Value);
        var value = q.ExecuteScalar(); if (value is null || value is DBNull) throw new InvalidOperationException("Source invoice line not found.");
        if (requested > Convert.ToInt64(value)) throw new InvalidOperationException("Return quantity exceeds the remaining unreturned invoice quantity.");
    }
    private static Guid InsertMovement(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, Guid wh, Guid item, string type, long qty, long cost, string key)
    {
        var id = Guid.NewGuid();
        using var m = c.CreateCommand(); m.Transaction = tx;
        m.CommandText = @"INSERT INTO monarch_stock_movements(id,company_id,branch_id,warehouse_id,item_id,ledger_sequence,movement_type,quantity_milli,unit_cost_minor,source_type,source_id,idempotency_key,occurred_utc,actor_user_id)
VALUES($id,$co,$br,$wh,$item,(SELECT COALESCE(MAX(ledger_sequence),0)+1 FROM monarch_stock_movements WHERE company_id=$co AND warehouse_id=$wh AND item_id=$item),$type,$qty,$cost,'CommercialReturn',$source,$key,$now,$user);";
        m.Parameters.AddWithValue("$id", id.ToString()); m.Parameters.AddWithValue("$co", co.ToString()); m.Parameters.AddWithValue("$br", br.ToString());
        m.Parameters.AddWithValue("$wh", wh.ToString()); m.Parameters.AddWithValue("$item", item.ToString()); m.Parameters.AddWithValue("$type", type);
        m.Parameters.AddWithValue("$qty", qty); m.Parameters.AddWithValue("$cost", cost); m.Parameters.AddWithValue("$source", key.Substring(0, key.LastIndexOf(':')));
        m.Parameters.AddWithValue("$key", key); m.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); m.Parameters.AddWithValue("$user", user.ToString()); m.ExecuteNonQuery();
        return id;
    }

    private static OriginalCostAllocation LoadOriginalCostAllocation(SqliteConnection c, SqliteTransaction tx, Guid co, Guid sourceLine, string returnType)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT id,quantity_milli,unit_cost_minor,total_cost_minor FROM monarch_inventory_cost_allocations
WHERE company_id=$co AND document_line_id=$line AND allocation_type=$kind ORDER BY created_utc,id LIMIT 1;";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$line", sourceLine.ToString());
        q.Parameters.AddWithValue("$kind", returnType == "SalesReturn" ? "SalesIssue" : "PurchaseReceipt");
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("The source invoice has no persisted inventory cost allocation; return posting is blocked.");
        return new OriginalCostAllocation(Guid.Parse(r.GetString(0)), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }

    private static long SumReturnedCostQuantity(SqliteConnection c, SqliteTransaction tx, Guid sourceAllocation)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT COALESCE(SUM(quantity_milli),0) FROM monarch_inventory_return_cost_allocations WHERE source_allocation_id=$id;";
        q.Parameters.AddWithValue("$id", sourceAllocation.ToString()); return Convert.ToInt64(q.ExecuteScalar());
    }
    private static long SumReturnedCost(SqliteConnection c, SqliteTransaction tx, Guid sourceAllocation)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT COALESCE(SUM(total_cost_minor),0) FROM monarch_inventory_return_cost_allocations WHERE source_allocation_id=$id;";
        q.Parameters.AddWithValue("$id", sourceAllocation.ToString()); return Convert.ToInt64(q.ExecuteScalar());
    }
    private static void InsertReturnCostAllocation(SqliteConnection c, SqliteTransaction tx, Guid co, Guid br, Guid returnId,
        Guid returnLineId, Guid sourceAllocationId, Guid movementId, string type, long qty, long unitCost, long totalCost)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"INSERT INTO monarch_inventory_return_cost_allocations(id,company_id,branch_id,return_id,return_line_id,source_allocation_id,movement_id,allocation_type,quantity_milli,unit_cost_minor,total_cost_minor,created_utc)
VALUES($id,$co,$br,$ret,$line,$source,$movement,$type,$qty,$unit,$total,$now);";
        q.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$br", br.ToString());
        q.Parameters.AddWithValue("$ret", returnId.ToString()); q.Parameters.AddWithValue("$line", returnLineId.ToString());
        q.Parameters.AddWithValue("$source", sourceAllocationId.ToString()); q.Parameters.AddWithValue("$movement", movementId.ToString());
        q.Parameters.AddWithValue("$type", type); q.Parameters.AddWithValue("$qty", qty); q.Parameters.AddWithValue("$unit", unitCost);
        q.Parameters.AddWithValue("$total", totalCost); q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); q.ExecuteNonQuery();
    }
    private static void RequirePermission(SqliteConnection c, SqliteTransaction tx,
        Guid user, Guid company, Guid branch, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id AND b.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id
JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1
AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1);";
        q.Parameters.AddWithValue("$co", company.ToString());
        q.Parameters.AddWithValue("$br", branch.ToString());
        q.Parameters.AddWithValue("$user", user.ToString());
        q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) < 1)
            throw new UnauthorizedAccessException("المستخدم لا يملك صلاحية إدارة المرتجعات ضمن المنشأة والفرع المحددين (No access to selected branch).");
    }

    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid user,
        Guid company, Guid branch, string action, Guid returnId, string details)
    {
        using var audit = c.CreateCommand(); audit.Transaction = tx;
        audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,$action,'CommercialReturn',$id,$details,$now);";
        audit.Parameters.AddWithValue("$co", company.ToString());
        audit.Parameters.AddWithValue("$br", branch.ToString());
        audit.Parameters.AddWithValue("$user", user.ToString());
        audit.Parameters.AddWithValue("$action", action);
        audit.Parameters.AddWithValue("$id", returnId.ToString());
        audit.Parameters.AddWithValue("$details", details);
        audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        audit.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON;"; p.ExecuteNonQuery(); return c; }
    private sealed record OriginalCostAllocation(Guid Id, long QuantityMilli, long UnitCostMinor, long TotalCostMinor);
    private sealed record SourceDocument(string PartyId, string WarehouseId, string CurrencyCode, int CurrencyScale);
    private sealed record SourceLine(string ItemId, string Sku, string Name, long QuantityMilli, long UnitPriceMinor, int TaxRateBasisPoints, long NetMinor, long TaxMinor);
}
