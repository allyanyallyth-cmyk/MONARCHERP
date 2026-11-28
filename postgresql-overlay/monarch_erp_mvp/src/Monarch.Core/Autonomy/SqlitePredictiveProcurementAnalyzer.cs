using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Autonomy;

public sealed record PredictiveProcurementPolicy(int LookbackDays, int SupplierLeadTimeDays, int TargetCoverageDays, long MinimumDailyDemandMilli = 1);
public sealed record PredictiveProcurementCandidate(Guid CompanyId, Guid BranchId, Guid WarehouseId, Guid ItemId,
    string Sku, string ItemName, long OnHandMilli, long OpenPurchaseOrderMilli, decimal AverageDailyDemandMilli,
    long ReorderPointMilli, long SuggestedQuantityMilli, int LookbackDays, int LeadTimeDays, string Rationale);

/// <summary>Read-only, tenant/branch/warehouse-scoped demand analyzer. It never creates or approves orders.</summary>
public sealed class SqlitePredictiveProcurementAnalyzer
{
    private readonly string _connectionString;
    public SqlitePredictiveProcurementAnalyzer(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var full = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true, Mode = SqliteOpenMode.ReadWrite }.ToString();
    }

    public IReadOnlyList<PredictiveProcurementCandidate> Analyze(Guid companyId, Guid branchId, Guid warehouseId,
        DateTimeOffset asOf, PredictiveProcurementPolicy policy)
    {
        if (companyId == Guid.Empty || branchId == Guid.Empty || warehouseId == Guid.Empty) throw new ArgumentException("Company, branch and warehouse scope are required.");
        if (policy == null || policy.LookbackDays < 7 || policy.LookbackDays > 365 || policy.SupplierLeadTimeDays < 0 || policy.SupplierLeadTimeDays > 365 || policy.TargetCoverageDays < 1 || policy.TargetCoverageDays > 180 || policy.MinimumDailyDemandMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "Procurement policy values are outside supported bounds.");
        var from = asOf.AddDays(-policy.LookbackDays).ToUniversalTime().ToString("O");
        var until = asOf.ToUniversalTime().ToString("O");
        var results = new List<PredictiveProcurementCandidate>();
        using var c = new SqliteConnection(_connectionString); c.Open();
        using (var pragma = c.CreateCommand()) { pragma.CommandText = "PRAGMA busy_timeout=8000;"; pragma.ExecuteNonQuery(); }
        using var tx = c.BeginTransaction(deferred: true);
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"
SELECT i.id,i.sku,i.name,i.reorder_level_milli,
 COALESCE(b.quantity_milli,0) AS on_hand,
 COALESCE(d.consumed_milli,0) AS consumed,
 COALESCE(po.open_qty_milli,0) AS open_po
FROM monarch_items i
JOIN monarch_warehouses w ON w.company_id=i.company_id AND w.id=$wh AND w.branch_id=$br AND w.is_active=1
LEFT JOIN monarch_stock_balances b ON b.company_id=i.company_id AND b.warehouse_id=w.id AND b.item_id=i.id
LEFT JOIN (
 SELECT item_id,SUM(quantity_milli) AS consumed FROM monarch_stock_movements
 WHERE company_id=$co AND branch_id=$br AND warehouse_id=$wh AND occurred_utc>$from AND occurred_utc<=$until
 AND movement_type IN('Issue','TransferOut','PurchaseReturn') GROUP BY item_id
) d ON d.item_id=i.id
LEFT JOIN (
 SELECT l.item_id,SUM(l.quantity_milli-l.received_quantity_milli) AS open_qty_milli
 FROM monarch_purchase_order_lines l JOIN monarch_purchase_orders p ON p.company_id=l.company_id AND p.id=l.order_id
 WHERE l.company_id=$co AND p.branch_id=$br AND p.warehouse_id=$wh AND p.status IN('Approved','PartiallyReceived')
 GROUP BY l.item_id
) po ON po.item_id=i.id
WHERE i.company_id=$co AND i.is_active=1 AND i.item_type='Stock'
ORDER BY i.sku;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$br", branchId.ToString());
        cmd.Parameters.AddWithValue("$wh", warehouseId.ToString()); cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$until", until);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var itemId = Guid.Parse(reader.GetString(0)); var sku = reader.GetString(1); var name = reader.GetString(2);
            var safety = reader.GetInt64(3); var onHand = reader.GetInt64(4); var consumed = reader.GetInt64(5); var openPo = reader.GetInt64(6);
            var avg = (decimal)consumed / policy.LookbackDays;
            if (avg < policy.MinimumDailyDemandMilli || avg <= 0) continue;
            var reorderPoint = checked((long)decimal.Ceiling(avg * policy.SupplierLeadTimeDays) + safety);
            var inventoryPosition = checked(onHand + openPo);
            if (inventoryPosition > reorderPoint) continue;
            var target = checked((long)decimal.Ceiling(avg * (policy.SupplierLeadTimeDays + policy.TargetCoverageDays)) + safety);
            var suggested = Math.Max(0, target - inventoryPosition);
            if (suggested == 0) continue;
            var rationale = $"Average daily demand {avg:0.###} milli-units over {policy.LookbackDays} days; lead time {policy.SupplierLeadTimeDays} days; on hand {onHand}; open PO {openPo}; reorder point {reorderPoint}.";
            results.Add(new PredictiveProcurementCandidate(companyId, branchId, warehouseId, itemId, sku, name, onHand, openPo, avg, reorderPoint, suggested, policy.LookbackDays, policy.SupplierLeadTimeDays, rationale));
        }
        reader.Close(); tx.Commit(); return results;
    }
}
