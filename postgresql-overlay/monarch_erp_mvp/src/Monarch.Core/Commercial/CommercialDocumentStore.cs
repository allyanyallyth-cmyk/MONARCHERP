using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

public enum CommercialDocumentType { Purchase, Sale }
public sealed record CommercialDocumentLineInput(Guid ItemId, long QuantityMilli, long UnitPriceMinor, int TaxRateBasisPoints);
public sealed record CommercialDocumentDraft(Guid Id, string Number, CommercialDocumentType Type, Guid PartyId,
    Guid WarehouseId, string CurrencyCode, int CurrencyScale, IReadOnlyList<CommercialDocumentLineInput> Lines);
public sealed record CommercialDocumentTotals(long SubtotalMinor, long TaxMinor, long TotalMinor);

/// <summary>
/// Draft/post workflow for sales and purchases. Posting atomically commits document state,
/// stock ledger movements, and an accounting outbox event. The outbox is a hand-off, not GL posting.
/// </summary>
public sealed class CommercialDocumentStore
{
    private readonly string _connectionString;
    public CommercialDocumentStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public CommercialDocumentTotals CreateDraft(Guid userId, Guid companyId, Guid branchId, CommercialDocumentDraft draft)
    {
        if (draft.Id == Guid.Empty || string.IsNullOrWhiteSpace(draft.Number) || draft.Lines is null || draft.Lines.Count == 0)
            throw new ArgumentException("Document identity, number and at least one line are required.");
        if (draft.CurrencyCode.Length != 3 || draft.CurrencyScale is < 0 or > 4) throw new ArgumentException("Invalid currency metadata.");
        using var c = Open(); using var tx = c.BeginTransaction();
        RequirePermission(c, tx, userId, companyId, branchId,
            draft.Type == CommercialDocumentType.Sale ? "commercial.sales.manage" : "commercial.purchases.manage");
        ValidatePartyAndWarehouse(c, tx, companyId, branchId, draft.Type, draft.PartyId, draft.WarehouseId);
        long subtotal = 0, tax = 0;
        using (var head = c.CreateCommand())
        {
            head.Transaction = tx;
            head.CommandText = @"INSERT INTO monarch_commercial_documents(id,company_id,branch_id,document_type,document_number,party_id,warehouse_id,status,currency_code,currency_scale,created_utc,created_by)
VALUES($id,$co,$br,$type,$number,$party,$wh,'Draft',$currency,$scale,$now,$user);";
            head.Parameters.AddWithValue("$id", draft.Id.ToString()); head.Parameters.AddWithValue("$co", companyId.ToString());
            head.Parameters.AddWithValue("$br", branchId.ToString()); head.Parameters.AddWithValue("$type", draft.Type.ToString());
            head.Parameters.AddWithValue("$number", draft.Number.Trim()); head.Parameters.AddWithValue("$party", draft.PartyId.ToString());
            head.Parameters.AddWithValue("$wh", draft.WarehouseId.ToString()); head.Parameters.AddWithValue("$currency", draft.CurrencyCode.ToUpperInvariant());
            head.Parameters.AddWithValue("$scale", draft.CurrencyScale); head.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            head.Parameters.AddWithValue("$user", userId.ToString()); head.ExecuteNonQuery();
        }
        int lineNo = 0;
        foreach (var line in draft.Lines)
        {
            if (line.QuantityMilli <= 0 || line.UnitPriceMinor < 0 || line.TaxRateBasisPoints is < 0 or > 10000)
                throw new ArgumentOutOfRangeException(nameof(draft), "Line quantity, price or tax rate is invalid.");
            string sku, name, itemType;
            using (var item = c.CreateCommand())
            {
                item.Transaction = tx; item.CommandText = "SELECT sku,name,item_type,is_active FROM monarch_items WHERE company_id=$co AND id=$item;";
                item.Parameters.AddWithValue("$co", companyId.ToString()); item.Parameters.AddWithValue("$item", line.ItemId.ToString());
                using var r = item.ExecuteReader(); if (!r.Read() || r.GetInt64(3) != 1) throw new InvalidOperationException("An item is missing or inactive.");
                sku = r.GetString(0); name = r.GetString(1); itemType = r.GetString(2);
            }
            long net = checked((long)decimal.Round((decimal)line.QuantityMilli * line.UnitPriceMinor / 1000m, 0, MidpointRounding.AwayFromZero));
            long lineTax = checked((long)decimal.Round((decimal)net * line.TaxRateBasisPoints / 10000m, 0, MidpointRounding.AwayFromZero));
            subtotal = checked(subtotal + net); tax = checked(tax + lineTax);
            using var insert = c.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = @"INSERT INTO monarch_commercial_document_lines(id,company_id,document_id,line_number,item_id,item_sku_snapshot,item_name_snapshot,quantity_milli,unit_price_minor,tax_rate_bps,net_minor,tax_minor,total_minor)
VALUES($id,$co,$doc,$no,$item,$sku,$name,$qty,$price,$rate,$net,$tax,$total);";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); insert.Parameters.AddWithValue("$co", companyId.ToString());
            insert.Parameters.AddWithValue("$doc", draft.Id.ToString()); insert.Parameters.AddWithValue("$no", ++lineNo);
            insert.Parameters.AddWithValue("$item", line.ItemId.ToString()); insert.Parameters.AddWithValue("$sku", sku); insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$qty", line.QuantityMilli); insert.Parameters.AddWithValue("$price", line.UnitPriceMinor);
            insert.Parameters.AddWithValue("$rate", line.TaxRateBasisPoints); insert.Parameters.AddWithValue("$net", net);
            insert.Parameters.AddWithValue("$tax", lineTax); insert.Parameters.AddWithValue("$total", checked(net + lineTax)); insert.ExecuteNonQuery();
            _ = itemType; // Service/non-stock lines are valid and intentionally create no inventory movement.
        }
        long total = checked(subtotal + tax);
        using (var update = c.CreateCommand()) { update.Transaction = tx; update.CommandText = "UPDATE monarch_commercial_documents SET subtotal_minor=$s,tax_minor=$t,total_minor=$all WHERE company_id=$co AND id=$id AND status='Draft';";
            update.Parameters.AddWithValue("$s", subtotal); update.Parameters.AddWithValue("$t", tax); update.Parameters.AddWithValue("$all", total);
            update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$id", draft.Id.ToString()); update.ExecuteNonQuery(); }
        Audit(c, tx, userId, companyId, branchId, "CommercialDocumentDraftCreated",
            draft.Id, "type=" + draft.Type + ";number=" + draft.Number.Trim() + ";lines=" + lineNo.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tx.Commit(); return new(subtotal, tax, total);
    }

    public void Post(Guid userId, Guid companyId, Guid branchId, Guid documentId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        string type, warehouse; using (var h = c.CreateCommand())
        {
            h.Transaction = tx; h.CommandText = "SELECT document_type,warehouse_id,status FROM monarch_commercial_documents WHERE company_id=$co AND branch_id=$br AND id=$id;";
            h.Parameters.AddWithValue("$co", companyId.ToString()); h.Parameters.AddWithValue("$br", branchId.ToString()); h.Parameters.AddWithValue("$id", documentId.ToString());
            using var r = h.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("Document not found in the selected company/branch.");
            type = r.GetString(0); warehouse = r.GetString(1); if (r.GetString(2) != "Draft") throw new InvalidOperationException("Only draft documents can be posted.");
        }
        RequirePermission(c, tx, userId, companyId, branchId,
            type == "Sale" ? "commercial.sales.manage" : "commercial.purchases.manage");
        var stockLines = new List<(Guid LineId, Guid ItemId, long QuantityMilli, long PurchaseUnitCostMinor)>();
        using (var lines = c.CreateCommand())
        {
            lines.Transaction = tx; lines.CommandText = "SELECT l.id,l.item_id,l.quantity_milli,i.item_type,l.unit_price_minor FROM monarch_commercial_document_lines l JOIN monarch_items i ON i.company_id=l.company_id AND i.id=l.item_id WHERE l.company_id=$co AND l.document_id=$doc ORDER BY l.line_number;";
            lines.Parameters.AddWithValue("$co", companyId.ToString()); lines.Parameters.AddWithValue("$doc", documentId.ToString());
            using var r = lines.ExecuteReader();
            while (r.Read()) if (r.GetString(3) == "Stock") stockLines.Add((Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetInt64(2), r.GetInt64(4)));
        }
        for (int index = 0; index < stockLines.Count; index++)
        {
            var line = stockLines[index];
            var movementType = type == "Purchase" ? StockMovementType.Receipt : StockMovementType.Issue;
            // Purchase receipt cost comes from the posted document line, not the item's default cost.
            // Sales issue cost is calculated from the warehouse/item ledger inside this same transaction.
            long unitCost = type == "Purchase"
                ? line.PurchaseUnitCostMinor
                : CalculateMovingAverageIssueUnitCost(c, tx, companyId, branchId, Guid.Parse(warehouse), line.ItemId, line.QuantityMilli);
            var movementId = InsertMovement(c, tx, userId, companyId, branchId, Guid.Parse(warehouse), line.ItemId,
                movementType.ToString(), line.QuantityMilli, unitCost, $"{documentId:N}:{index}");
            InsertCostAllocation(c, tx, companyId, branchId, documentId, line.LineId, movementId,
                type == "Purchase" ? "PurchaseReceipt" : "SalesIssue", line.QuantityMilli, unitCost,
                type == "Purchase" ? "DocumentCost" : "MovingWeightedAverage");
        }
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx; update.CommandText = "UPDATE monarch_commercial_documents SET status='Posted',posted_utc=$now,posted_by=$user WHERE company_id=$co AND branch_id=$br AND id=$id AND status='Draft';";
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); update.Parameters.AddWithValue("$user", userId.ToString());
            update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$br", branchId.ToString()); update.Parameters.AddWithValue("$id", documentId.ToString());
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Document state changed; posting aborted.");
        }
        using (var outbox = c.CreateCommand())
        {
            outbox.Transaction = tx; outbox.CommandText = "INSERT INTO monarch_accounting_posting_outbox(id,company_id,branch_id,document_id,event_type,created_utc) VALUES($id,$co,$br,$doc,$event,$now);";
            outbox.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); outbox.Parameters.AddWithValue("$co", companyId.ToString());
            outbox.Parameters.AddWithValue("$br", branchId.ToString()); outbox.Parameters.AddWithValue("$doc", documentId.ToString());
            outbox.Parameters.AddWithValue("$event", type == "Purchase" ? "PurchasePosted" : "SalePosted"); outbox.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); outbox.ExecuteNonQuery();
        }
        using (var audit = c.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,$action,'CommercialDocument',$doc,$details,$now);";
            audit.Parameters.AddWithValue("$co", companyId.ToString()); audit.Parameters.AddWithValue("$br", branchId.ToString());
            audit.Parameters.AddWithValue("$user", userId.ToString()); audit.Parameters.AddWithValue("$action", type == "Purchase" ? "PurchaseStockPosted" : "SalesStockPosted");
            audit.Parameters.AddWithValue("$doc", documentId.ToString()); audit.Parameters.AddWithValue("$details", "warehouse=" + warehouse + ";stockLines=" + stockLines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); audit.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void ValidatePartyAndWarehouse(SqliteConnection c, SqliteTransaction tx, Guid co, Guid br, CommercialDocumentType type, Guid party, Guid warehouse)
    {
        using (var p = c.CreateCommand()) { p.Transaction = tx; p.CommandText = "SELECT party_type FROM monarch_parties WHERE company_id=$co AND id=$id AND is_active=1;";
            p.Parameters.AddWithValue("$co", co.ToString()); p.Parameters.AddWithValue("$id", party.ToString()); var value = p.ExecuteScalar() as string;
            if (value == null || !(value == "Both" || (type == CommercialDocumentType.Sale ? value == "Customer" : value == "Supplier"))) throw new InvalidOperationException("Party is inactive or has the wrong customer/supplier role."); }
        using (var w = c.CreateCommand()) { w.Transaction = tx; w.CommandText = "SELECT 1 FROM monarch_warehouses WHERE company_id=$co AND id=$wh AND branch_id=$br AND is_active=1;";
            w.Parameters.AddWithValue("$co", co.ToString()); w.Parameters.AddWithValue("$wh", warehouse.ToString()); w.Parameters.AddWithValue("$br", br.ToString());
            if (w.ExecuteScalar() == null) throw new InvalidOperationException("Active warehouse not found in selected branch."); }
    }
    private static long CalculateMovingAverageIssueUnitCost(SqliteConnection c, SqliteTransaction tx,
        Guid companyId, Guid branchId, Guid warehouseId, Guid itemId, long quantityMilli)
    {
        var movements = new List<InventoryValuationMovement>();
        long nextSequence = 1;
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = @"SELECT id,occurred_utc,ledger_sequence,movement_type,quantity_milli,unit_cost_minor
FROM monarch_stock_movements WHERE company_id=$co AND branch_id=$br AND warehouse_id=$wh AND item_id=$item
ORDER BY ledger_sequence;";
            read.Parameters.AddWithValue("$co", companyId.ToString()); read.Parameters.AddWithValue("$br", branchId.ToString());
            read.Parameters.AddWithValue("$wh", warehouseId.ToString()); read.Parameters.AddWithValue("$item", itemId.ToString());
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var sequence = r.GetInt64(2);
                nextSequence = Math.Max(nextSequence, checked(sequence + 1));
                movements.Add(new InventoryValuationMovement(Guid.Parse(r.GetString(0)),
                    DateTimeOffset.Parse(r.GetString(1)), sequence,
                    Enum.Parse<StockMovementType>(r.GetString(3), false), r.GetInt64(4), r.GetInt64(5)));
            }
        }
        var previewId = Guid.NewGuid();
        movements.Add(new InventoryValuationMovement(previewId, DateTimeOffset.UtcNow, nextSequence,
            StockMovementType.Issue, quantityMilli, 0));
        var valuation = InventoryValuationEngine.Calculate(movements, InventoryCostMethod.MovingWeightedAverage);
        var issueCost = valuation.Entries.Last(x => x.MovementId == previewId).MovementCostMinor;
        return checked((long)decimal.Round((decimal)issueCost * 1000m / quantityMilli, 0, MidpointRounding.AwayFromZero));
    }

    private static Guid InsertMovement(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, Guid wh, Guid item, string type, long qty, long cost, string key)
    {
        var movementId = Guid.NewGuid();
        using var sequence = c.CreateCommand(); sequence.Transaction = tx;
        sequence.CommandText = "SELECT COALESCE(MAX(ledger_sequence),0)+1 FROM monarch_stock_movements WHERE company_id=$co AND warehouse_id=$wh AND item_id=$item;";
        sequence.Parameters.AddWithValue("$co", co.ToString()); sequence.Parameters.AddWithValue("$wh", wh.ToString()); sequence.Parameters.AddWithValue("$item", item.ToString());
        var ledgerSequence = Convert.ToInt64(sequence.ExecuteScalar());
        using var m = c.CreateCommand(); m.Transaction = tx;
        m.CommandText = @"INSERT INTO monarch_stock_movements(id,company_id,branch_id,warehouse_id,item_id,ledger_sequence,movement_type,quantity_milli,unit_cost_minor,source_type,source_id,idempotency_key,occurred_utc,actor_user_id)
VALUES($id,$co,$br,$wh,$item,$sequence,$type,$qty,$cost,'CommercialDocument',$source,$key,$now,$user);";
        m.Parameters.AddWithValue("$id", movementId.ToString()); m.Parameters.AddWithValue("$co", co.ToString()); m.Parameters.AddWithValue("$br", br.ToString());
        m.Parameters.AddWithValue("$wh", wh.ToString()); m.Parameters.AddWithValue("$item", item.ToString()); m.Parameters.AddWithValue("$sequence", ledgerSequence); m.Parameters.AddWithValue("$type", type);
        m.Parameters.AddWithValue("$qty", qty); m.Parameters.AddWithValue("$cost", cost); m.Parameters.AddWithValue("$source", key.Substring(0, key.LastIndexOf(':')));
        m.Parameters.AddWithValue("$key", key); m.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); m.Parameters.AddWithValue("$user", user.ToString()); m.ExecuteNonQuery();
        return movementId;
    }
    private static void InsertCostAllocation(SqliteConnection c, SqliteTransaction tx, Guid co, Guid br,
        Guid documentId, Guid lineId, Guid movementId, string allocationType, long quantityMilli, long unitCostMinor, string method)
    {
        var totalCost = checked((long)decimal.Round((decimal)quantityMilli * unitCostMinor / 1000m, 0, MidpointRounding.AwayFromZero));
        using var a = c.CreateCommand(); a.Transaction = tx;
        a.CommandText = @"INSERT INTO monarch_inventory_cost_allocations(id,company_id,branch_id,document_id,document_line_id,movement_id,allocation_type,quantity_milli,unit_cost_minor,total_cost_minor,cost_method,created_utc)
VALUES($id,$co,$br,$doc,$line,$movement,$type,$qty,$unit,$total,$method,$now);";
        a.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); a.Parameters.AddWithValue("$co", co.ToString());
        a.Parameters.AddWithValue("$br", br.ToString()); a.Parameters.AddWithValue("$doc", documentId.ToString());
        a.Parameters.AddWithValue("$line", lineId.ToString()); a.Parameters.AddWithValue("$movement", movementId.ToString());
        a.Parameters.AddWithValue("$type", allocationType); a.Parameters.AddWithValue("$qty", quantityMilli);
        a.Parameters.AddWithValue("$unit", unitCostMinor); a.Parameters.AddWithValue("$total", totalCost);
        a.Parameters.AddWithValue("$method", method); a.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        a.ExecuteNonQuery();
    }
    // Permission and tenant/branch membership are checked in the same transaction
    // as document creation/posting, closing the preflight/revocation race.
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
            throw new UnauthorizedAccessException("المستخدم لا يملك صلاحية إنشاء أو ترحيل المستند التجاري ضمن المنشأة والفرع المحددين.");
    }

    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid user,
        Guid company, Guid branch, string action, Guid documentId, string details)
    {
        using var audit = c.CreateCommand(); audit.Transaction = tx;
        audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,$action,'CommercialDocument',$id,$details,$now);";
        audit.Parameters.AddWithValue("$co", company.ToString());
        audit.Parameters.AddWithValue("$br", branch.ToString());
        audit.Parameters.AddWithValue("$user", user.ToString());
        audit.Parameters.AddWithValue("$action", action);
        audit.Parameters.AddWithValue("$id", documentId.ToString());
        audit.Parameters.AddWithValue("$details", details);
        audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        audit.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON;"; p.ExecuteNonQuery(); return c; }
}
