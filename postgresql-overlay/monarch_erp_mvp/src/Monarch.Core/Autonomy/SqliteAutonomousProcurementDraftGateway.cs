using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Autonomy;

/// <summary>
/// Creates the purchase-order draft, its lines, approval inbox assignment and audit event
/// in one SQLite transaction. This gateway deliberately has no approval or posting API.
/// </summary>
public sealed class SqliteAutonomousProcurementDraftGateway : IProcurementDraftGateway
{
    private readonly string _connectionString;

    public SqliteAutonomousProcurementDraftGateway(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public ProcurementDraftResult CreateDraftForPurchasingManager(ProcurementDraftRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (request.UserId == Guid.Empty || request.CompanyId == Guid.Empty || request.BranchId == Guid.Empty ||
            request.SupplierId == Guid.Empty || request.WarehouseId == Guid.Empty)
            throw new ArgumentException("User, company, branch, supplier and warehouse are required.");
        if (string.IsNullOrWhiteSpace(request.OrderNumber) || request.OrderNumber.Trim().Length > 40)
            throw new ArgumentException("A valid purchase-order number is required.");
        if (string.IsNullOrWhiteSpace(request.CurrencyCode) || request.CurrencyCode.Trim().Length != 3 || request.CurrencyScale < 0 || request.CurrencyScale > 4)
            throw new ArgumentException("Currency configuration is invalid.");
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500)
            throw new ArgumentException("Between 1 and 500 purchase lines are required.");
        if (string.IsNullOrWhiteSpace(request.Rationale) || request.Rationale.Trim().Length > 2000)
            throw new ArgumentException("A traceable procurement rationale is required.");

        var orderId = Guid.NewGuid();
        var queueId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using (var pragma = c.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=8000;"; pragma.ExecuteNonQuery(); }
        using var tx = c.BeginTransaction();
        Require(c, tx, request.UserId, request.CompanyId, request.BranchId, "commercial.purchases.manage");
        ValidateSupplierAndWarehouse(c, tx, request);

        if (!string.IsNullOrWhiteSpace(request.RecommendationKey))
        {
            if (request.RecommendationKey.Length != 64 || request.Lines.Count != 1)
                throw new ArgumentException("A predictive recommendation requires a 64-character SHA-256 key and exactly one item line.");
            using (var existing = c.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = @"SELECT item_id,purchase_order_id,approval_queue_id FROM monarch_procurement_recommendation_runs
WHERE company_id=$co AND branch_id=$br AND warehouse_id=$wh AND recommendation_key=$key;";
                existing.Parameters.AddWithValue("$co", request.CompanyId.ToString()); existing.Parameters.AddWithValue("$br", request.BranchId.ToString());
                existing.Parameters.AddWithValue("$wh", request.WarehouseId.ToString()); existing.Parameters.AddWithValue("$key", request.RecommendationKey);
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    var existingItem = reader.GetString(0); var existingOrder = Guid.Parse(reader.GetString(1)); var existingQueue = Guid.Parse(reader.GetString(2));
                    reader.Close();
                    if (!string.Equals(existingItem, request.Lines[0].ItemId.ToString(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Recommendation key is already bound to a different item in this scope.");
                    using var status = c.CreateCommand(); status.Transaction = tx;
                    status.CommandText = "SELECT status FROM monarch_purchase_orders WHERE company_id=$co AND id=$po;";
                    status.Parameters.AddWithValue("$co", request.CompanyId.ToString()); status.Parameters.AddWithValue("$po", existingOrder.ToString());
                    var existingStatus = status.ExecuteScalar() as string;
                    if (string.IsNullOrEmpty(existingStatus)) throw new InvalidOperationException("Idempotency record references a missing purchase order.");
                    tx.Commit();
                    return new ProcurementDraftResult(existingOrder, existingStatus, existingQueue, "Existing recommendation draft returned; no duplicate purchase order was created.");
                }
            }
            using (var claim = c.CreateCommand())
            {
                claim.Transaction = tx;
                claim.CommandText = @"INSERT INTO monarch_procurement_recommendation_runs
(id,company_id,branch_id,warehouse_id,item_id,recommendation_key,purchase_order_id,approval_queue_id,created_by,created_utc)
VALUES($id,$co,$br,$wh,$item,$key,$po,$queue,$user,$now);";
                claim.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); claim.Parameters.AddWithValue("$co", request.CompanyId.ToString());
                claim.Parameters.AddWithValue("$br", request.BranchId.ToString()); claim.Parameters.AddWithValue("$wh", request.WarehouseId.ToString());
                claim.Parameters.AddWithValue("$item", request.Lines[0].ItemId.ToString()); claim.Parameters.AddWithValue("$key", request.RecommendationKey);
                claim.Parameters.AddWithValue("$po", orderId.ToString()); claim.Parameters.AddWithValue("$queue", queueId.ToString());
                claim.Parameters.AddWithValue("$user", request.UserId.ToString()); claim.Parameters.AddWithValue("$now", now.ToString("O"));
                claim.ExecuteNonQuery();
            }
        }

        using (var header = c.CreateCommand())
        {
            header.Transaction = tx;
            header.CommandText = @"INSERT INTO monarch_purchase_orders
(id,company_id,branch_id,order_number,supplier_id,warehouse_id,status,currency_code,currency_scale,expected_utc,notes,created_utc,created_by)
VALUES($id,$co,$br,$number,$supplier,$warehouse,'Draft',$currency,$scale,$expected,$notes,$now,$user);";
            header.Parameters.AddWithValue("$id", orderId.ToString()); header.Parameters.AddWithValue("$co", request.CompanyId.ToString());
            header.Parameters.AddWithValue("$br", request.BranchId.ToString()); header.Parameters.AddWithValue("$number", request.OrderNumber.Trim());
            header.Parameters.AddWithValue("$supplier", request.SupplierId.ToString()); header.Parameters.AddWithValue("$warehouse", request.WarehouseId.ToString());
            header.Parameters.AddWithValue("$currency", request.CurrencyCode.Trim().ToUpperInvariant()); header.Parameters.AddWithValue("$scale", request.CurrencyScale);
            header.Parameters.AddWithValue("$expected", (object?)request.ExpectedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            header.Parameters.AddWithValue("$notes", "Autonomous recommendation: " + request.Rationale.Trim());
            header.Parameters.AddWithValue("$now", now.ToString("O")); header.Parameters.AddWithValue("$user", request.UserId.ToString());
            header.ExecuteNonQuery();
        }

        long subtotal = 0, tax = 0;
        var lineNo = 0;
        foreach (var line in request.Lines)
        {
            if (line.ItemId == Guid.Empty || line.QuantityMilli <= 0 || line.UnitCostMinor < 0 || line.TaxRateBasisPoints < 0 || line.TaxRateBasisPoints > 10000)
                throw new ArgumentOutOfRangeException(nameof(request), "A procurement line contains invalid quantity, cost, item or tax rate.");
            string sku, name;
            using (var item = c.CreateCommand())
            {
                item.Transaction = tx; item.CommandText = "SELECT sku,name FROM monarch_items WHERE company_id=$co AND id=$item AND is_active=1;";
                item.Parameters.AddWithValue("$co", request.CompanyId.ToString()); item.Parameters.AddWithValue("$item", line.ItemId.ToString());
                using var reader = item.ExecuteReader();
                if (!reader.Read()) throw new InvalidOperationException("A requested item is missing or inactive in this company.");
                sku = reader.GetString(0); name = reader.GetString(1);
            }
            var net = checked((long)decimal.Round((decimal)line.QuantityMilli * line.UnitCostMinor / 1000m, 0, MidpointRounding.AwayFromZero));
            var lineTax = checked((long)decimal.Round((decimal)net * line.TaxRateBasisPoints / 10000m, 0, MidpointRounding.AwayFromZero));
            subtotal = checked(subtotal + net); tax = checked(tax + lineTax);
            using var insert = c.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = @"INSERT INTO monarch_purchase_order_lines
(id,company_id,order_id,line_number,item_id,item_sku_snapshot,item_name_snapshot,quantity_milli,unit_cost_minor,tax_rate_bps,net_minor,tax_minor,total_minor)
VALUES($id,$co,$order,$line,$item,$sku,$name,$qty,$cost,$rate,$net,$tax,$total);";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); insert.Parameters.AddWithValue("$co", request.CompanyId.ToString());
            insert.Parameters.AddWithValue("$order", orderId.ToString()); insert.Parameters.AddWithValue("$line", ++lineNo);
            insert.Parameters.AddWithValue("$item", line.ItemId.ToString()); insert.Parameters.AddWithValue("$sku", sku); insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$qty", line.QuantityMilli); insert.Parameters.AddWithValue("$cost", line.UnitCostMinor);
            insert.Parameters.AddWithValue("$rate", line.TaxRateBasisPoints); insert.Parameters.AddWithValue("$net", net);
            insert.Parameters.AddWithValue("$tax", lineTax); insert.Parameters.AddWithValue("$total", checked(net + lineTax)); insert.ExecuteNonQuery();
        }
        using (var totals = c.CreateCommand())
        {
            totals.Transaction = tx; totals.CommandText = "UPDATE monarch_purchase_orders SET subtotal_minor=$subtotal,tax_minor=$tax,total_minor=$total WHERE company_id=$co AND id=$id AND status='Draft';";
            totals.Parameters.AddWithValue("$subtotal", subtotal); totals.Parameters.AddWithValue("$tax", tax); totals.Parameters.AddWithValue("$total", checked(subtotal + tax));
            totals.Parameters.AddWithValue("$co", request.CompanyId.ToString()); totals.Parameters.AddWithValue("$id", orderId.ToString());
            if (totals.ExecuteNonQuery() != 1) throw new InvalidOperationException("Could not finalize purchase-order draft totals.");
        }
        using (var queue = c.CreateCommand())
        {
            queue.Transaction = tx;
            queue.CommandText = @"INSERT INTO monarch_procurement_approval_queue
(id,company_id,branch_id,purchase_order_id,assigned_permission_code,status,rationale,created_by,created_utc)
VALUES($id,$co,$br,$po,'commercial.purchases.approve','Pending',$rationale,$user,$now);";
            queue.Parameters.AddWithValue("$id", queueId.ToString()); queue.Parameters.AddWithValue("$co", request.CompanyId.ToString());
            queue.Parameters.AddWithValue("$br", request.BranchId.ToString()); queue.Parameters.AddWithValue("$po", orderId.ToString());
            queue.Parameters.AddWithValue("$rationale", request.Rationale.Trim()); queue.Parameters.AddWithValue("$user", request.UserId.ToString());
            queue.Parameters.AddWithValue("$now", now.ToString("O")); queue.ExecuteNonQuery();
        }
        Audit(c, tx, request.UserId, request.CompanyId, request.BranchId, orderId, queueId, request.Rationale.Trim(), now);
        if (request.ForecastSnapshot != null)
        {
            if (string.IsNullOrWhiteSpace(request.RecommendationKey) || request.ForecastSnapshot.ForecastDemandMilli < 0 ||
                request.ForecastSnapshot.HorizonDays < 1 || request.ForecastSnapshot.HorizonDays > 180 ||
                request.ForecastSnapshot.WindowEndUtc <= request.ForecastSnapshot.WindowStartUtc ||
                (request.ForecastSnapshot.WindowEndUtc - request.ForecastSnapshot.WindowStartUtc).TotalDays > 180 ||
                string.IsNullOrWhiteSpace(request.ForecastSnapshot.PolicyVersion))
                throw new ArgumentException("A valid forecast snapshot requires a durable recommendation key, bounded horizon and policy version.");
            using var forecast = c.CreateCommand(); forecast.Transaction = tx;
            forecast.CommandText = @"INSERT INTO monarch_procurement_forecast_snapshots
(company_id,branch_id,warehouse_id,item_id,recommendation_key,purchase_order_id,forecast_demand_milli,horizon_days,window_start_utc,window_end_utc,policy_version,created_utc)
VALUES($co,$br,$wh,$item,$key,$po,$demand,$days,$start,$end,$policy,$created);";
            forecast.Parameters.AddWithValue("$co", request.CompanyId.ToString()); forecast.Parameters.AddWithValue("$br", request.BranchId.ToString());
            forecast.Parameters.AddWithValue("$wh", request.WarehouseId.ToString()); forecast.Parameters.AddWithValue("$item", request.Lines[0].ItemId.ToString());
            forecast.Parameters.AddWithValue("$key", request.RecommendationKey!); forecast.Parameters.AddWithValue("$po", orderId.ToString());
            forecast.Parameters.AddWithValue("$demand", request.ForecastSnapshot.ForecastDemandMilli); forecast.Parameters.AddWithValue("$days", request.ForecastSnapshot.HorizonDays);
            forecast.Parameters.AddWithValue("$start", request.ForecastSnapshot.WindowStartUtc.ToUniversalTime().ToString("O"));
            forecast.Parameters.AddWithValue("$end", request.ForecastSnapshot.WindowEndUtc.ToUniversalTime().ToString("O"));
            forecast.Parameters.AddWithValue("$policy", request.ForecastSnapshot.PolicyVersion.Trim()); forecast.Parameters.AddWithValue("$created", now.ToString("O"));
            forecast.ExecuteNonQuery();
        }
        tx.Commit();
        return new ProcurementDraftResult(orderId, "Draft", queueId, "Draft created and assigned to the purchasing-manager approval inbox.");
    }

    private static void ValidateSupplierAndWarehouse(SqliteConnection c, SqliteTransaction tx, ProcurementDraftRequest r)
    {
        using (var supplier = c.CreateCommand())
        {
            supplier.Transaction = tx; supplier.CommandText = "SELECT party_type FROM monarch_parties WHERE company_id=$co AND id=$id AND is_active=1;";
            supplier.Parameters.AddWithValue("$co", r.CompanyId.ToString()); supplier.Parameters.AddWithValue("$id", r.SupplierId.ToString());
            var type = supplier.ExecuteScalar() as string;
            if (type != "Supplier" && type != "Both") throw new InvalidOperationException("Supplier is missing, inactive or not a supplier party.");
        }
        using (var warehouse = c.CreateCommand())
        {
            warehouse.Transaction = tx; warehouse.CommandText = "SELECT 1 FROM monarch_warehouses WHERE company_id=$co AND branch_id=$br AND id=$id AND is_active=1;";
            warehouse.Parameters.AddWithValue("$co", r.CompanyId.ToString()); warehouse.Parameters.AddWithValue("$br", r.BranchId.ToString()); warehouse.Parameters.AddWithValue("$id", r.WarehouseId.ToString());
            if (warehouse.ExecuteScalar() is null) throw new InvalidOperationException("Warehouse is missing, inactive or outside the selected branch.");
        }
    }

    private static void Require(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1)
AND EXISTS(SELECT 1 FROM monarch_branches b WHERE b.company_id=$co AND b.id=$br AND b.is_active=1);";
        q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$br", branch.ToString());
        q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) < 1) throw new UnauthorizedAccessException("User lacks procurement-management permission in this tenant and branch.");
    }

    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch, Guid order, Guid queue, string rationale, DateTimeOffset now)
    {
        using var a = c.CreateCommand(); a.Transaction = tx;
        a.CommandText = @"INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,'AutonomousPurchaseOrderDraftCreated','PurchaseOrder',$order,$details,$now);";
        a.Parameters.AddWithValue("$co", company.ToString()); a.Parameters.AddWithValue("$br", branch.ToString()); a.Parameters.AddWithValue("$user", user.ToString());
        a.Parameters.AddWithValue("$order", order.ToString()); a.Parameters.AddWithValue("$details", "queue=" + queue + "; rationale=" + rationale);
        a.Parameters.AddWithValue("$now", now.ToString("O")); a.ExecuteNonQuery();
    }
}
