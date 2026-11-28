using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Monarch.Core.Commercial;

/// <summary>
/// Read-only, branch-scoped comparison between the current inventory movement subledger
/// and one active inventory asset account in the tenant general ledger. This service never
/// posts adjustments: variances require review and an explicitly approved journal.
/// </summary>
public sealed class InventoryGlReconciliationStore
{
    private readonly string _connectionString;

    public InventoryGlReconciliationStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var full = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public InventoryGlReconciliationResult Reconcile(Guid userId, Guid companyId, Guid branchId,
        string inventoryAccountCode, InventoryCostMethod costMethod, DateTimeOffset asOfUtc)
    {
        if (string.IsNullOrWhiteSpace(inventoryAccountCode)) throw new ArgumentException("Inventory account code is required.", nameof(inventoryAccountCode));
        if (asOfUtc == default) throw new ArgumentOutOfRangeException(nameof(asOfUtc), "A non-default UTC cutoff is required.");
        var cutoffUtc = asOfUtc.ToUniversalTime();
        var cutoffText = cutoffUtc.ToString("O", CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(typeof(InventoryCostMethod), costMethod)) throw new ArgumentOutOfRangeException(nameof(costMethod));
        // Commercial sales issues and warehouse transfers currently persist moving-average
        // carrying cost. FIFO reconciliation would misstate transfers until layer lineage is
        // carried across warehouses, so fail closed rather than report a misleading variance.
        if (costMethod != InventoryCostMethod.MovingWeightedAverage)
            throw new NotSupportedException("FIFO reconciliation is blocked until FIFO layer lineage is preserved across commercial issues and warehouse transfers.");

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireBranchAccess(connection, transaction, userId, companyId, branchId);
        var accountId = ResolveInventoryAccount(connection, transaction, companyId, inventoryAccountCode);
        var items = LoadStockValuations(connection, transaction, companyId, branchId, costMethod, cutoffText);
        var subledgerValue = items.Aggregate(0L, (sum, item) => checked(sum + item.ClosingValueMinor));
        var ledgerValue = LoadPostedInventoryBalance(connection, transaction, companyId, branchId, accountId, cutoffText);
        var variance = checked(ledgerValue - subledgerValue);
        var reconciledUtc = DateTimeOffset.UtcNow;
        var result = new InventoryGlReconciliationResult(companyId, branchId, inventoryAccountCode, costMethod,
            items.Count, items.Aggregate(0L, (sum, item) => checked(sum + item.ClosingQuantityMilli)),
            subledgerValue, ledgerValue, variance, variance == 0, items.AsReadOnly(), cutoffUtc, reconciledUtc);

        // The read-only comparison and its audit trail commit together. This records
        // the result without creating or modifying any general-ledger journal.
        var reconciliationId = Guid.NewGuid();
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$actor,'InventoryGlReconciled','InventoryGlReconciliation',$id,$details,$now);";
            audit.Parameters.AddWithValue("$co", companyId.ToString());
            audit.Parameters.AddWithValue("$br", branchId.ToString());
            audit.Parameters.AddWithValue("$actor", userId.ToString());
            audit.Parameters.AddWithValue("$id", reconciliationId.ToString());
            audit.Parameters.AddWithValue("$details", $"account={inventoryAccountCode.Trim()};method={costMethod};asOfUtc={cutoffText};itemWarehouseCount={result.ItemWarehouseCount};quantityMilli={result.TotalQuantityMilli};subledgerValueMinor={subledgerValue};generalLedgerValueMinor={ledgerValue};varianceMinor={variance};isBalanced={result.IsBalanced}");
            audit.Parameters.AddWithValue("$now", reconciledUtc.ToString("O", CultureInfo.InvariantCulture));
            audit.ExecuteNonQuery();
        }
        transaction.Commit();
        return result;
    }

    private static void RequireBranchAccess(SqliteConnection connection, SqliteTransaction transaction,
        Guid userId, Guid companyId, Guid branchId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT 1
FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id
JOIN monarch_user_branch_access ba ON ba.user_id=ca.user_id AND ba.company_id=ca.company_id
JOIN monarch_companies co ON co.id=ca.company_id
JOIN monarch_branches br ON br.company_id=ba.company_id AND br.id=ba.branch_id
WHERE u.id=$user AND u.is_active=1 AND ca.company_id=$company AND ca.is_active=1
AND ba.branch_id=$branch AND ba.is_active=1 AND co.is_active=1 AND br.is_active=1
AND EXISTS (SELECT 1 FROM monarch_user_roles ur
  JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id
  WHERE ur.user_id=u.id AND rp.permission_code='reports.read');";
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$company", companyId.ToString());
        command.Parameters.AddWithValue("$branch", branchId.ToString());
        if (command.ExecuteScalar() is null)
            throw new UnauthorizedAccessException("Active company and branch access plus reports.read permission are required.");
    }

    private static string ResolveInventoryAccount(SqliteConnection connection, SqliteTransaction transaction,
        Guid companyId, string code)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT id FROM monarch_tenant_accounts
WHERE company_id=$company AND code=$code COLLATE NOCASE AND is_active=1 AND account_type='Asset';";
        command.Parameters.AddWithValue("$company", companyId.ToString());
        command.Parameters.AddWithValue("$code", code.Trim());
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException("The configured inventory account is missing, inactive, or not an Asset account.");
    }

    private static List<InventoryGlItemValuation> LoadStockValuations(SqliteConnection connection,
        SqliteTransaction transaction, Guid companyId, Guid branchId, InventoryCostMethod method, string cutoffUtc)
    {
        var groups = new List<(Guid WarehouseId, string WarehouseCode, Guid ItemId, string Sku, string Name)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"SELECT DISTINCT w.id,w.code,i.id,i.sku,i.name
FROM monarch_stock_movements m
JOIN monarch_warehouses w ON w.company_id=m.company_id AND w.id=m.warehouse_id
JOIN monarch_items i ON i.company_id=m.company_id AND i.id=m.item_id
WHERE m.company_id=$company AND m.branch_id=$branch AND w.branch_id=$branch
AND i.item_type='Stock' AND m.occurred_utc <= $cutoff
ORDER BY w.code,i.sku;";
            command.Parameters.AddWithValue("$company", companyId.ToString());
            command.Parameters.AddWithValue("$branch", branchId.ToString());
            command.Parameters.AddWithValue("$cutoff", cutoffUtc);
            using var reader = command.ExecuteReader();
            while (reader.Read()) groups.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1),
                Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4)));
        }

        var result = new List<InventoryGlItemValuation>(groups.Count);
        foreach (var group in groups)
        {
            var movements = new List<InventoryValuationMovement>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT id,occurred_utc,ledger_sequence,movement_type,quantity_milli,unit_cost_minor
FROM monarch_stock_movements
WHERE company_id=$company AND branch_id=$branch AND warehouse_id=$warehouse AND item_id=$item
AND occurred_utc <= $cutoff ORDER BY ledger_sequence,id;";
                command.Parameters.AddWithValue("$company", companyId.ToString());
                command.Parameters.AddWithValue("$branch", branchId.ToString());
                command.Parameters.AddWithValue("$warehouse", group.WarehouseId.ToString());
                command.Parameters.AddWithValue("$item", group.ItemId.ToString());
                command.Parameters.AddWithValue("$cutoff", cutoffUtc);
                using var reader = command.ExecuteReader();
                while (reader.Read()) movements.Add(new InventoryValuationMovement(Guid.Parse(reader.GetString(0)),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetInt64(2),
                    Enum.Parse<StockMovementType>(reader.GetString(3), false), reader.GetInt64(4), reader.GetInt64(5)));
            }
            var valuation = InventoryValuationEngine.Calculate(movements, method);
            result.Add(new InventoryGlItemValuation(group.WarehouseId, group.WarehouseCode, group.ItemId,
                group.Sku, group.Name, valuation.ClosingQuantityMilli, valuation.ClosingValueMinor));
        }
        return result;
    }

    private static long LoadPostedInventoryBalance(SqliteConnection connection, SqliteTransaction transaction,
        Guid companyId, Guid branchId, string accountId, string cutoffUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT COALESCE(SUM(l.debit_minor-l.credit_minor),0)
FROM monarch_tenant_journal_lines l
JOIN monarch_tenant_journals j ON j.company_id=l.company_id AND j.id=l.journal_id
WHERE l.company_id=$company AND j.branch_id=$branch AND j.status IN ('Posted','Reversed')
AND j.posted_at_utc IS NOT NULL AND j.posted_at_utc <= $cutoff AND l.account_id=$account;";
        command.Parameters.AddWithValue("$company", companyId.ToString());
        command.Parameters.AddWithValue("$branch", branchId.ToString());
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$cutoff", cutoffUtc);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}

public sealed record InventoryGlItemValuation(Guid WarehouseId, string WarehouseCode, Guid ItemId,
    string Sku, string ItemName, long ClosingQuantityMilli, long ClosingValueMinor);

public sealed record InventoryGlReconciliationResult(Guid CompanyId, Guid BranchId, string InventoryAccountCode,
    InventoryCostMethod CostMethod, int ItemWarehouseCount, long TotalQuantityMilli,
    long InventorySubledgerValueMinor, long GeneralLedgerValueMinor, long VarianceMinor,
    bool IsBalanced, IReadOnlyList<InventoryGlItemValuation> Items, DateTimeOffset AsOfUtc, DateTimeOffset ReconciledUtc);
