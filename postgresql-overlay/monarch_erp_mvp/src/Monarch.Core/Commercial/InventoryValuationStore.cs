using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

/// <summary>Persists reproducible, append-only valuation snapshots for one company/branch/warehouse/item.</summary>
public sealed class InventoryValuationStore
{
    private readonly string _connectionString;
    public InventoryValuationStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public void Initialize(string? schemaPath = null)
    {
        schemaPath ??= Path.Combine(AppContext.BaseDirectory, "database", "inventory-valuation.sql");
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("Inventory valuation schema not found.", schemaPath);
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = File.ReadAllText(schemaPath); cmd.ExecuteNonQuery();
    }

    public InventoryValuationResult CalculateAndPersist(Guid userId, Guid companyId, Guid branchId,
        Guid warehouseId, Guid itemId, InventoryCostMethod method)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        // Authorization is evaluated on the same connection/transaction as the valuation
        // read and snapshot writes, avoiding a permission-change TOCTOU window.
        RequireBranch(c, tx, userId, companyId, branchId, true);
        using (var scope = c.CreateCommand())
        {
            scope.Transaction = tx;
            scope.CommandText = @"SELECT 1 FROM monarch_warehouses w JOIN monarch_items i ON i.company_id=w.company_id
AND i.id=$item AND i.item_type='Stock' AND i.is_active=1
WHERE w.company_id=$co AND w.id=$wh AND w.branch_id=$br AND w.is_active=1;";
            scope.Parameters.AddWithValue("$item", itemId.ToString()); scope.Parameters.AddWithValue("$co", companyId.ToString());
            scope.Parameters.AddWithValue("$wh", warehouseId.ToString()); scope.Parameters.AddWithValue("$br", branchId.ToString());
            if (scope.ExecuteScalar() is null) throw new UnauthorizedAccessException("Warehouse/item is not active in the selected company and branch.");
        }
        var movements = new List<InventoryValuationMovement>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"SELECT id,occurred_utc,ledger_sequence,movement_type,quantity_milli,unit_cost_minor
FROM monarch_stock_movements WHERE company_id=$co AND branch_id=$br AND warehouse_id=$wh AND item_id=$item
ORDER BY ledger_sequence;";
            cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$br", branchId.ToString());
            cmd.Parameters.AddWithValue("$wh", warehouseId.ToString()); cmd.Parameters.AddWithValue("$item", itemId.ToString());
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) movements.Add(new InventoryValuationMovement(Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1)), reader.GetInt64(2),
                Enum.Parse<StockMovementType>(reader.GetString(3), false), reader.GetInt64(4), reader.GetInt64(5)));
        }
        var result = InventoryValuationEngine.Calculate(movements, method);
        var runId = Guid.NewGuid();
        using (var run = c.CreateCommand())
        {
            run.Transaction = tx;
            run.CommandText = @"INSERT INTO monarch_inventory_valuation_runs
(id,company_id,branch_id,warehouse_id,item_id,cost_method,closing_quantity_milli,closing_value_minor,average_unit_cost_minor,movement_count,calculated_utc,calculated_by)
VALUES($id,$co,$br,$wh,$item,$method,$qty,$value,$avg,$count,$now,$user);";
            run.Parameters.AddWithValue("$id", runId.ToString()); run.Parameters.AddWithValue("$co", companyId.ToString());
            run.Parameters.AddWithValue("$br", branchId.ToString()); run.Parameters.AddWithValue("$wh", warehouseId.ToString());
            run.Parameters.AddWithValue("$item", itemId.ToString()); run.Parameters.AddWithValue("$method", method.ToString());
            run.Parameters.AddWithValue("$qty", result.ClosingQuantityMilli); run.Parameters.AddWithValue("$value", result.ClosingValueMinor);
            run.Parameters.AddWithValue("$avg", result.AverageUnitCostMinor); run.Parameters.AddWithValue("$count", result.Entries.Count);
            run.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); run.Parameters.AddWithValue("$user", userId.ToString()); run.ExecuteNonQuery();
        }
        for (var index = 0; index < result.Entries.Count; index++)
        {
            var e = result.Entries[index]; using var entry = c.CreateCommand(); entry.Transaction = tx;
            entry.CommandText = @"INSERT INTO monarch_inventory_valuation_entries
(run_id,line_number,movement_id,balance_quantity_milli,balance_value_minor,movement_cost_minor,average_unit_cost_minor)
VALUES($run,$line,$movement,$qty,$value,$cost,$avg);";
            entry.Parameters.AddWithValue("$run", runId.ToString()); entry.Parameters.AddWithValue("$line", index + 1);
            entry.Parameters.AddWithValue("$movement", e.MovementId.ToString()); entry.Parameters.AddWithValue("$qty", e.BalanceQuantityMilli);
            entry.Parameters.AddWithValue("$value", e.BalanceValueMinor); entry.Parameters.AddWithValue("$cost", e.MovementCostMinor);
            entry.Parameters.AddWithValue("$avg", e.AverageUnitCostMinor); entry.ExecuteNonQuery();
        }
        for (var index = 0; index < result.RemainingFifoLayers.Count; index++)
        {
            var layer = result.RemainingFifoLayers[index]; using var row = c.CreateCommand(); row.Transaction = tx;
            row.CommandText = "INSERT INTO monarch_inventory_valuation_layers(run_id,layer_number,remaining_quantity_milli,unit_cost_minor) VALUES($run,$line,$qty,$cost);";
            row.Parameters.AddWithValue("$run", runId.ToString()); row.Parameters.AddWithValue("$line", index + 1);
            row.Parameters.AddWithValue("$qty", layer.RemainingQuantityMilli); row.Parameters.AddWithValue("$cost", layer.UnitCostMinor); row.ExecuteNonQuery();
        }
        // Keep the valuation snapshot and its audit event atomic: an audit failure
        // must not leave an apparently successful, unaudited valuation run.
        using (var audit = c.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$actor,'InventoryValuationCalculated','InventoryValuationRun',$id,$details,$now);";
            audit.Parameters.AddWithValue("$co", companyId.ToString());
            audit.Parameters.AddWithValue("$br", branchId.ToString());
            audit.Parameters.AddWithValue("$actor", userId.ToString());
            audit.Parameters.AddWithValue("$id", runId.ToString());
            audit.Parameters.AddWithValue("$details", $"warehouse={warehouseId};item={itemId};method={method};quantityMilli={result.ClosingQuantityMilli};valueMinor={result.ClosingValueMinor};movements={result.Entries.Count}");
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }
        tx.Commit();
        return result;
    }

    private static void RequireBranch(SqliteConnection c, SqliteTransaction tx,
        Guid user, Guid company, Guid branch, bool write)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"SELECT ca.role_code
FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id
JOIN monarch_user_branch_access ba ON ba.user_id=ca.user_id AND ba.company_id=ca.company_id
WHERE u.id=$u AND u.is_active=1 AND ca.company_id=$co AND ba.branch_id=$br
AND ca.is_active=1 AND ba.is_active=1;";
        cmd.Parameters.AddWithValue("$u", user.ToString());
        cmd.Parameters.AddWithValue("$co", company.ToString());
        cmd.Parameters.AddWithValue("$br", branch.ToString());
        var role = cmd.ExecuteScalar() as string;
        if (role is null) throw new UnauthorizedAccessException("No active user access to selected company and branch.");
        if (write && role is not ("Administrator" or "Accountant"))
            throw new UnauthorizedAccessException("The assigned role cannot calculate or persist inventory valuation.");
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON;"; p.ExecuteNonQuery(); return c; }
}
