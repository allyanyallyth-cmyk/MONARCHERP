using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Autonomy;

public sealed record ProcurementDecisionKpis(int TotalDecisions, int Approved, int Rejected,
    double? AverageDecisionHours, double? RejectionRate);

public sealed record ProcurementSupplierPerformance(Guid SupplierId, string SupplierCode, string SupplierName,
    int CompletedOrders, int OrdersWithExpectedDate, int OnTimeOrders, double? OnTimeRate,
    double? AverageCompletionLeadTimeDays);

public sealed record ProcurementConsumptionRow(Guid ItemId, string Sku, string ItemName,
    long IssuedQuantityMilli, int MovementCount);
public sealed record ProcurementForecastAccuracy(int MaturedForecasts, long ForecastDemandMilli,
    long ActualDemandMilli, double? WeightedAbsolutePercentageError);
public sealed record ProcurementSupplierItemPerformance(Guid SupplierId, string SupplierCode, string SupplierName,
    Guid ItemId, string Sku, string ItemName, string CurrencyCode, int CompletedOrders,
    long ReceivedQuantityMilli, double? AverageUnitCostMinor);
public sealed record ProcurementForecastHistoryRow(Guid ItemId, string Sku, string ItemName,
    string PolicyVersion, int HorizonDays, string WindowStartUtc, string WindowEndUtc,
    long ForecastDemandMilli, long ActualDemandMilli, long AbsoluteErrorMilli, double? AbsolutePercentageError);
public sealed record ProcurementSupplierTrend(Guid SupplierId, string SupplierCode, string SupplierName,
    int CurrentCompletedOrders, int PreviousCompletedOrders, double? CurrentOnTimeRate, double? PreviousOnTimeRate,
    double? CurrentAverageLeadTimeDays, double? PreviousAverageLeadTimeDays, bool HasReliableComparison,
    bool OnTimePerformanceDeclined, bool LeadTimeDeteriorated, string AlertLevel);

public sealed record ProcurementAnalyticsSnapshot(DateOnly FromDate, DateOnly ToDate,
    ProcurementDecisionKpis Decisions, IReadOnlyList<ProcurementSupplierPerformance> SupplierPerformance,
    IReadOnlyList<ProcurementConsumptionRow> Consumption, bool ForecastAccuracyAvailable,
    string ForecastAccuracyNote, ProcurementForecastAccuracy? ForecastAccuracy = null);

/// <summary>Read-only tenant/branch procurement analytics based on immutable decisions, posted receipts and stock movements.</summary>
public sealed class ProcurementAnalyticsStore
{
    private readonly string _connectionString;

    public ProcurementAnalyticsStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public ProcurementAnalyticsSnapshot GetSnapshot(Guid userId, Guid companyId, Guid branchId,
        DateOnly fromDate, DateOnly toDate, int topItems = 20)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty)
            throw new ArgumentException("User, company and branch are required.");
        if (toDate < fromDate) throw new ArgumentException("ToDate must not precede FromDate.");
        if (topItems < 1 || topItems > 100) throw new ArgumentOutOfRangeException(nameof(topItems));

        using var c = Open();
        using var tx = c.BeginTransaction();
        RequireReportPermission(c, tx, userId, companyId, branchId);
        var from = fromDate.ToString("yyyy-MM-dd") + "T00:00:00";
        var until = toDate.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00";

        ProcurementDecisionKpis decisions;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"SELECT COUNT(*),
SUM(CASE WHEN d.decision='Approved' THEN 1 ELSE 0 END),
SUM(CASE WHEN d.decision='Rejected' THEN 1 ELSE 0 END),
AVG((julianday(d.decided_utc)-julianday(p.created_utc))*24.0)
FROM monarch_procurement_decision_history d
JOIN monarch_purchase_orders p ON p.company_id=d.company_id AND p.branch_id=d.branch_id AND p.id=d.purchase_order_id
WHERE d.company_id=$co AND d.branch_id=$br AND d.decided_utc >= $from AND d.decided_utc < $until;";
            Bind(q, companyId, branchId, from, until);
            using var r = q.ExecuteReader();
            r.Read();
            var total = r.GetInt32(0);
            var approved = r.IsDBNull(1) ? 0 : r.GetInt32(1);
            var rejected = r.IsDBNull(2) ? 0 : r.GetInt32(2);
            var avg = r.IsDBNull(3) ? (double?)null : r.GetDouble(3);
            decisions = new ProcurementDecisionKpis(total, approved, rejected, avg,
                total == 0 ? (double?)null : (double)rejected / total);
        }

        var supplierRows = new List<ProcurementSupplierPerformance>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"WITH completed AS (
 SELECT po.company_id,po.branch_id,po.id,po.supplier_id,po.expected_utc,po.created_utc,
 MAX(r.posted_utc) AS completed_utc
 FROM monarch_purchase_orders po
 JOIN monarch_purchase_receipts r ON r.company_id=po.company_id AND r.branch_id=po.branch_id AND r.purchase_order_id=po.id
 WHERE po.company_id=$co AND po.branch_id=$br AND po.status='Received'
 GROUP BY po.company_id,po.branch_id,po.id,po.supplier_id,po.expected_utc,po.created_utc
)
SELECT p.id,p.code,p.display_name,COUNT(*),
 SUM(CASE WHEN x.expected_utc IS NOT NULL THEN 1 ELSE 0 END),
 SUM(CASE WHEN x.expected_utc IS NOT NULL AND datetime(x.completed_utc)<=datetime(x.expected_utc) THEN 1 ELSE 0 END),
 AVG(CASE WHEN x.expected_utc IS NOT NULL THEN CASE WHEN datetime(x.completed_utc)<=datetime(x.expected_utc) THEN 1.0 ELSE 0.0 END ELSE NULL END),
 AVG(julianday(date(x.completed_utc))-julianday(date(x.created_utc)))
FROM completed x JOIN monarch_parties p ON p.company_id=x.company_id AND p.id=x.supplier_id
WHERE x.completed_utc >= $from AND x.completed_utc < $until
GROUP BY p.id,p.code,p.display_name ORDER BY COUNT(*) DESC,p.display_name COLLATE NOCASE LIMIT 100;";
            Bind(q, companyId, branchId, from, until);
            using var r = q.ExecuteReader();
            while (r.Read()) supplierRows.Add(new ProcurementSupplierPerformance(Guid.Parse(r.GetString(0)),
                r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                r.IsDBNull(6) ? (double?)null : r.GetDouble(6), r.IsDBNull(7) ? (double?)null : r.GetDouble(7)));
        }

        var consumptionRows = new List<ProcurementConsumptionRow>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"SELECT i.id,i.sku,i.name,SUM(m.quantity_milli),COUNT(*)
FROM monarch_stock_movements m JOIN monarch_items i ON i.company_id=m.company_id AND i.id=m.item_id
WHERE m.company_id=$co AND m.branch_id=$br AND m.occurred_utc >= $from AND m.occurred_utc < $until
AND m.movement_type IN('Issue','TransferOut','PurchaseReturn')
GROUP BY i.id,i.sku,i.name ORDER BY SUM(m.quantity_milli) DESC,i.sku LIMIT $limit;";
            Bind(q, companyId, branchId, from, until); q.Parameters.AddWithValue("$limit", topItems);
            using var r = q.ExecuteReader();
            while (r.Read()) consumptionRows.Add(new ProcurementConsumptionRow(Guid.Parse(r.GetString(0)),
                r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4)));
        }

        ProcurementForecastAccuracy? forecastAccuracy = null;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"SELECT COUNT(*),COALESCE(SUM(f.forecast_demand_milli),0),
COALESCE(SUM((SELECT COALESCE(SUM(m.quantity_milli),0) FROM monarch_stock_movements m
 WHERE m.company_id=f.company_id AND m.branch_id=f.branch_id AND m.warehouse_id=f.warehouse_id
 AND m.item_id=f.item_id AND m.occurred_utc>=f.window_start_utc AND m.occurred_utc<f.window_end_utc
 AND m.movement_type IN('Issue','TransferOut','PurchaseReturn'))),0),
COALESCE(SUM(ABS(f.forecast_demand_milli-(SELECT COALESCE(SUM(m.quantity_milli),0) FROM monarch_stock_movements m
 WHERE m.company_id=f.company_id AND m.branch_id=f.branch_id AND m.warehouse_id=f.warehouse_id
 AND m.item_id=f.item_id AND m.occurred_utc>=f.window_start_utc AND m.occurred_utc<f.window_end_utc
 AND m.movement_type IN('Issue','TransferOut','PurchaseReturn')))),0)
FROM monarch_procurement_forecast_snapshots f
WHERE f.company_id=$co AND f.branch_id=$br AND f.window_end_utc <= $until
AND f.window_end_utc >= $from;";
            Bind(q, companyId, branchId, from, until);
            using var r = q.ExecuteReader(); r.Read();
            var count = r.GetInt32(0); var forecast = r.GetInt64(1); var actual = r.GetInt64(2); var absoluteError = r.GetInt64(3);
            if (count > 0) forecastAccuracy = new ProcurementForecastAccuracy(count, forecast, actual,
                actual == 0 ? (double?)null : (double)absoluteError / actual);
        }
        tx.Commit();
        return new ProcurementAnalyticsSnapshot(fromDate, toDate, decisions, supplierRows, consumptionRows,
            forecastAccuracy != null, forecastAccuracy == null
                ? "Forecast accuracy is unavailable for this period: no matured, tenant-scoped immutable forecast snapshots were found."
                : "Accuracy is measured as WAPE (sum of absolute forecast errors divided by total actual demand) over matured forecast windows; zero actual demand yields no percentage.", forecastAccuracy);
    }

    /// <summary>Returns receipt-backed supplier/item performance. Unit costs remain separated by currency.</summary>
    public IReadOnlyList<ProcurementSupplierItemPerformance> GetSupplierItemPerformance(Guid userId,
        Guid companyId, Guid branchId, DateOnly fromDate, DateOnly toDate, int topRows = 100)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty)
            throw new ArgumentException("User, company and branch are required.");
        if (toDate < fromDate) throw new ArgumentException("ToDate must not precede FromDate.");
        if (topRows < 1 || topRows > 500) throw new ArgumentOutOfRangeException(nameof(topRows));
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireReportPermission(c, tx, userId, companyId, branchId);
        var from = fromDate.ToString("yyyy-MM-dd") + "T00:00:00";
        var until = toDate.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00";
        var rows = new List<ProcurementSupplierItemPerformance>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"SELECT p.id,p.code,p.display_name,i.id,i.sku,i.name,po.currency_code,
 COUNT(DISTINCT po.id),SUM(rl.quantity_milli),
 CASE WHEN SUM(rl.quantity_milli)=0 THEN NULL ELSE
  CAST(SUM(CAST(rl.quantity_milli AS REAL) * rl.unit_cost_minor) AS REAL)/SUM(rl.quantity_milli) END
FROM monarch_purchase_receipt_lines rl
JOIN monarch_purchase_receipts rr ON rr.company_id=rl.company_id AND rr.id=rl.receipt_id
JOIN monarch_purchase_orders po ON po.company_id=rr.company_id AND po.branch_id=rr.branch_id AND po.id=rr.purchase_order_id
JOIN monarch_parties p ON p.company_id=po.company_id AND p.id=po.supplier_id
JOIN monarch_items i ON i.company_id=rl.company_id AND i.id=rl.item_id
WHERE rr.company_id=$co AND rr.branch_id=$br AND rr.status='Posted'
 AND rr.posted_utc >= $from AND rr.posted_utc < $until
GROUP BY p.id,p.code,p.display_name,i.id,i.sku,i.name,po.currency_code
ORDER BY SUM(rl.quantity_milli) DESC,p.display_name COLLATE NOCASE,i.sku LIMIT $limit;";
            Bind(q, companyId, branchId, from, until); q.Parameters.AddWithValue("$limit", topRows);
            using var r = q.ExecuteReader();
            while (r.Read()) rows.Add(new ProcurementSupplierItemPerformance(Guid.Parse(r.GetString(0)),
                r.GetString(1), r.GetString(2), Guid.Parse(r.GetString(3)), r.GetString(4), r.GetString(5),
                r.GetString(6), r.GetInt32(7), r.GetInt64(8), r.IsDBNull(9) ? (double?)null : r.GetDouble(9)));
        }
        tx.Commit(); return rows;
    }

    /// <summary>Returns matured, immutable forecast outcomes by item and forecast window.</summary>
    public IReadOnlyList<ProcurementForecastHistoryRow> GetForecastHistory(Guid userId, Guid companyId,
        Guid branchId, DateOnly fromDate, DateOnly toDate, int topRows = 500)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty)
            throw new ArgumentException("User, company and branch are required.");
        if (toDate < fromDate) throw new ArgumentException("ToDate must not precede FromDate.");
        if (topRows < 1 || topRows > 2000) throw new ArgumentOutOfRangeException(nameof(topRows));
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireReportPermission(c, tx, userId, companyId, branchId);
        var from = fromDate.ToString("yyyy-MM-dd") + "T00:00:00";
        var until = toDate.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00";
        var rows = new List<ProcurementForecastHistoryRow>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"SELECT i.id,i.sku,i.name,f.policy_version,f.horizon_days,
 f.window_start_utc,f.window_end_utc,f.forecast_demand_milli,
 (SELECT COALESCE(SUM(m.quantity_milli),0) FROM monarch_stock_movements m
  WHERE m.company_id=f.company_id AND m.branch_id=f.branch_id AND m.warehouse_id=f.warehouse_id
   AND m.item_id=f.item_id AND m.occurred_utc>=f.window_start_utc AND m.occurred_utc<f.window_end_utc
   AND m.movement_type IN('Issue','TransferOut','PurchaseReturn')) AS actual_demand,
 ABS(f.forecast_demand_milli-(SELECT COALESCE(SUM(m.quantity_milli),0) FROM monarch_stock_movements m
  WHERE m.company_id=f.company_id AND m.branch_id=f.branch_id AND m.warehouse_id=f.warehouse_id
   AND m.item_id=f.item_id AND m.occurred_utc>=f.window_start_utc AND m.occurred_utc<f.window_end_utc
   AND m.movement_type IN('Issue','TransferOut','PurchaseReturn'))) AS absolute_error
FROM monarch_procurement_forecast_snapshots f
JOIN monarch_items i ON i.company_id=f.company_id AND i.id=f.item_id
WHERE f.company_id=$co AND f.branch_id=$br AND f.window_end_utc<=$until
 AND f.window_end_utc>=$from
ORDER BY f.window_end_utc DESC,i.sku COLLATE NOCASE LIMIT $limit;";
            Bind(q, companyId, branchId, from, until); q.Parameters.AddWithValue("$limit", topRows);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var actual = r.GetInt64(8);
                rows.Add(new ProcurementForecastHistoryRow(Guid.Parse(r.GetString(0)), r.GetString(1),
                    r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6),
                    r.GetInt64(7), actual, r.GetInt64(9), actual == 0 ? (double?)null : (double)r.GetInt64(9) / actual));
            }
        }
        tx.Commit(); return rows;
    }

    /// <summary>Compares supplier delivery performance with the immediately preceding equal-length period.</summary>
    public IReadOnlyList<ProcurementSupplierTrend> GetSupplierPerformanceTrends(Guid userId, Guid companyId,
        Guid branchId, DateOnly fromDate, DateOnly toDate, int minimumOrdersPerPeriod = 3)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty)
            throw new ArgumentException("User, company and branch are required.");
        if (toDate < fromDate) throw new ArgumentException("ToDate must not precede FromDate.");
        if (minimumOrdersPerPeriod < 1 || minimumOrdersPerPeriod > 100)
            throw new ArgumentOutOfRangeException(nameof(minimumOrdersPerPeriod));
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireReportPermission(c, tx, userId, companyId, branchId);
        var days = toDate.DayNumber - fromDate.DayNumber + 1;
        var currentFrom = fromDate.ToString("yyyy-MM-dd") + "T00:00:00";
        var currentUntil = toDate.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00";
        var previousFrom = fromDate.AddDays(-days).ToString("yyyy-MM-dd") + "T00:00:00";
        var rows = new List<ProcurementSupplierTrend>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"WITH completed AS (
 SELECT po.company_id,po.branch_id,po.id,po.supplier_id,po.expected_utc,po.created_utc,
 MAX(r.posted_utc) AS completed_utc
 FROM monarch_purchase_orders po
 JOIN monarch_purchase_receipts r ON r.company_id=po.company_id AND r.branch_id=po.branch_id AND r.purchase_order_id=po.id
 WHERE po.company_id=$co AND po.branch_id=$br AND po.status='Received'
 GROUP BY po.company_id,po.branch_id,po.id,po.supplier_id,po.expected_utc,po.created_utc
), periodized AS (
 SELECT *,CASE WHEN completed_utc >= $previousFrom AND completed_utc < $currentFrom THEN 0
               WHEN completed_utc >= $currentFrom AND completed_utc < $currentUntil THEN 1 END AS period_id
 FROM completed WHERE (completed_utc >= $previousFrom AND completed_utc < $currentFrom)
                    OR (completed_utc >= $currentFrom AND completed_utc < $currentUntil)
)
SELECT p.id,p.code,p.display_name,
 SUM(CASE WHEN x.period_id=1 THEN 1 ELSE 0 END),SUM(CASE WHEN x.period_id=0 THEN 1 ELSE 0 END),
 AVG(CASE WHEN x.period_id=1 AND x.expected_utc IS NOT NULL THEN
   CASE WHEN datetime(x.completed_utc)<=datetime(x.expected_utc) THEN 1.0 ELSE 0.0 END END),
 AVG(CASE WHEN x.period_id=0 AND x.expected_utc IS NOT NULL THEN
   CASE WHEN datetime(x.completed_utc)<=datetime(x.expected_utc) THEN 1.0 ELSE 0.0 END END),
 AVG(CASE WHEN x.period_id=1 THEN julianday(date(x.completed_utc))-julianday(date(x.created_utc)) END),
 AVG(CASE WHEN x.period_id=0 THEN julianday(date(x.completed_utc))-julianday(date(x.created_utc)) END)
FROM periodized x JOIN monarch_parties p ON p.company_id=x.company_id AND p.id=x.supplier_id
GROUP BY p.id,p.code,p.display_name ORDER BY p.display_name COLLATE NOCASE LIMIT 500;";
            Bind(q, companyId, branchId, currentFrom, currentUntil);
            q.Parameters.AddWithValue("$previousFrom", previousFrom);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var currentCount = r.GetInt32(3); var previousCount = r.GetInt32(4);
                double? currentOnTime = r.IsDBNull(5) ? (double?)null : r.GetDouble(5);
                double? previousOnTime = r.IsDBNull(6) ? (double?)null : r.GetDouble(6);
                double? currentLead = r.IsDBNull(7) ? (double?)null : r.GetDouble(7);
                double? previousLead = r.IsDBNull(8) ? (double?)null : r.GetDouble(8);
                var reliable = currentCount >= minimumOrdersPerPeriod && previousCount >= minimumOrdersPerPeriod;
                var onTimeDeclined = reliable && currentOnTime.HasValue && previousOnTime.HasValue
                    && previousOnTime.Value - currentOnTime.Value >= 0.15;
                var leadDeteriorated = reliable && currentLead.HasValue && previousLead.HasValue
                    && previousLead.Value > 0 && currentLead.Value >= previousLead.Value * 1.25;
                var alert = !reliable ? "InsufficientData" : onTimeDeclined && leadDeteriorated ? "Critical"
                    : onTimeDeclined || leadDeteriorated ? "Warning" : "Stable";
                rows.Add(new ProcurementSupplierTrend(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2),
                    currentCount, previousCount, currentOnTime, previousOnTime, currentLead, previousLead,
                    reliable, onTimeDeclined, leadDeteriorated, alert));
            }
        }
        tx.Commit(); return rows;
    }

    private static void RequireReportPermission(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code='reports.read'
WHERE u.id=$user AND u.is_active=1
AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1)
AND EXISTS(SELECT 1 FROM monarch_branches b WHERE b.company_id=$co AND b.id=$br AND b.is_active=1);";
        q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$br", branch.ToString());
        q.Parameters.AddWithValue("$user", user.ToString());
        if (Convert.ToInt64(q.ExecuteScalar()) != 1)
            throw new UnauthorizedAccessException("reports.read is required in the selected company and branch.");
    }

    private static void Bind(SqliteCommand q, Guid company, Guid branch, string from, string until)
    {
        q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$br", branch.ToString());
        q.Parameters.AddWithValue("$from", from); q.Parameters.AddWithValue("$until", until);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString); c.Open();
        using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c;
    }
}
