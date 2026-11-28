using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

public sealed record SupplierItemTerms(Guid SupplierId, Guid ItemId, string CurrencyCode, int CurrencyScale,
    long UnitCostMinor, int TaxRateBasisPoints, int LeadTimeDays, long MinimumOrderQuantityMilli,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string SourceReference);

public sealed record SupplierIntelligenceRow(Guid SupplierId, string SupplierCode, string SupplierName,
    Guid ItemId, string CurrencyCode, int CurrencyScale, long UnitCostMinor, int TaxRateBasisPoints,
    int ContractLeadTimeDays, int CompletedReceipts, double? AverageActualLeadTimeDays,
    double? OnTimeDeliveryRate, DateOnly EffectiveFrom);

/// <summary>Tenant-scoped supplier/item terms and evidence-based delivery scorecard.</summary>
public sealed class SupplierIntelligenceStore
{
    private readonly string _connectionString;
    public SupplierIntelligenceStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public void SaveTerms(Guid userId, Guid companyId, Guid branchId, SupplierItemTerms terms)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty || terms.SupplierId == Guid.Empty || terms.ItemId == Guid.Empty)
            throw new ArgumentException("بيانات المنشأة والمستخدم والمورد والصنف مطلوبة.");
        if (string.IsNullOrWhiteSpace(terms.CurrencyCode) || terms.CurrencyCode.Trim().Length != 3 || terms.CurrencyScale < 0 || terms.CurrencyScale > 4 ||
            terms.UnitCostMinor < 0 || terms.TaxRateBasisPoints < 0 || terms.TaxRateBasisPoints > 10000 || terms.LeadTimeDays < 0 ||
            terms.LeadTimeDays > 3650 || terms.MinimumOrderQuantityMilli <= 0 || string.IsNullOrWhiteSpace(terms.SourceReference))
            throw new ArgumentException("شروط المورد التجارية غير صالحة أو ينقصها مرجع موثق.");
        using var c = Open(); using var tx = c.BeginTransaction();
        Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        using (var supplier = c.CreateCommand())
        {
            supplier.Transaction = tx;
            supplier.CommandText = "SELECT COUNT(*) FROM monarch_parties WHERE company_id=$co AND id=$supplier AND party_type IN('Supplier','Both') AND is_active=1;";
            supplier.Parameters.AddWithValue("$co", companyId.ToString()); supplier.Parameters.AddWithValue("$supplier", terms.SupplierId.ToString());
            if (Convert.ToInt64(supplier.ExecuteScalar()) != 1) throw new InvalidOperationException("المورد غير مسجل ضمن المنشأة.");
        }
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = @"INSERT INTO monarch_supplier_item_terms
(id,company_id,supplier_id,item_id,currency_code,currency_scale,unit_cost_minor,tax_rate_bps,lead_time_days,minimum_order_quantity_milli,effective_from,effective_to,source_reference,is_active,created_by,created_utc)
VALUES($id,$co,$supplier,$item,$currency,$scale,$cost,$tax,$lead,$minimum,$from,$to,$source,1,$user,$now);";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); insert.Parameters.AddWithValue("$co", companyId.ToString());
            insert.Parameters.AddWithValue("$supplier", terms.SupplierId.ToString()); insert.Parameters.AddWithValue("$item", terms.ItemId.ToString());
            insert.Parameters.AddWithValue("$currency", terms.CurrencyCode.Trim().ToUpperInvariant()); insert.Parameters.AddWithValue("$scale", terms.CurrencyScale);
            insert.Parameters.AddWithValue("$cost", terms.UnitCostMinor); insert.Parameters.AddWithValue("$tax", terms.TaxRateBasisPoints);
            insert.Parameters.AddWithValue("$lead", terms.LeadTimeDays); insert.Parameters.AddWithValue("$minimum", terms.MinimumOrderQuantityMilli);
            insert.Parameters.AddWithValue("$from", terms.EffectiveFrom.ToString("yyyy-MM-dd"));
            insert.Parameters.AddWithValue("$to", (object?)terms.EffectiveTo?.ToString("yyyy-MM-dd") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$source", terms.SourceReference.Trim()); insert.Parameters.AddWithValue("$user", userId.ToString());
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); insert.ExecuteNonQuery();
        }
        using (var audit = c.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,'SupplierItemTermsRecorded','SupplierItemTerms',$id,$details,$now);";
            audit.Parameters.AddWithValue("$co", companyId.ToString()); audit.Parameters.AddWithValue("$br", branchId.ToString());
            audit.Parameters.AddWithValue("$user", userId.ToString()); audit.Parameters.AddWithValue("$id", terms.ItemId.ToString());
            audit.Parameters.AddWithValue("$details", "supplier=" + terms.SupplierId + ";source=" + terms.SourceReference.Trim());
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); audit.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<SupplierIntelligenceRow> GetItemOptions(Guid userId, Guid companyId, Guid branchId, Guid itemId, DateOnly asOfDate)
    {
        if (itemId == Guid.Empty) throw new ArgumentException("الصنف مطلوب.");
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT t.supplier_id,p.code,p.display_name,t.item_id,t.currency_code,t.currency_scale,t.unit_cost_minor,t.tax_rate_bps,t.lead_time_days,
COALESCE(per.completed_receipts,0),per.average_actual_days,per.on_time_rate,t.effective_from
FROM monarch_supplier_item_terms t JOIN monarch_parties p ON p.company_id=t.company_id AND p.id=t.supplier_id
LEFT JOIN (
 SELECT po.company_id,po.supplier_id,pol.item_id,COUNT(DISTINCT pr.id) completed_receipts,
 AVG(julianday(date(pr.posted_utc))-julianday(date(po.created_utc))) average_actual_days,
 AVG(CASE WHEN po.expected_utc IS NOT NULL AND datetime(pr.posted_utc)<=datetime(po.expected_utc) THEN 1.0 ELSE 0.0 END) on_time_rate
 FROM monarch_purchase_orders po JOIN monarch_purchase_order_lines pol ON pol.company_id=po.company_id AND pol.order_id=po.id
 JOIN monarch_purchase_receipts pr ON pr.company_id=po.company_id AND pr.purchase_order_id=po.id
 WHERE po.company_id=$co AND po.branch_id=$br AND po.status IN('PartiallyReceived','Received')
 GROUP BY po.company_id,po.supplier_id,pol.item_id
) per ON per.company_id=t.company_id AND per.supplier_id=t.supplier_id AND per.item_id=t.item_id
WHERE t.company_id=$co AND t.item_id=$item AND t.is_active=1 AND date(t.effective_from)<=date($asof)
AND (t.effective_to IS NULL OR date(t.effective_to)>=date($asof))
ORDER BY t.unit_cost_minor ASC,per.on_time_rate DESC,t.lead_time_days ASC;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        q.Parameters.AddWithValue("$item", itemId.ToString()); q.Parameters.AddWithValue("$asof", asOfDate.ToString("yyyy-MM-dd"));
        var rows = new List<SupplierIntelligenceRow>(); using var r = q.ExecuteReader();
        while (r.Read()) rows.Add(new SupplierIntelligenceRow(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), Guid.Parse(r.GetString(3)),
            r.GetString(4), r.GetInt32(5), r.GetInt64(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9),
            r.IsDBNull(10) ? (double?)null : r.GetDouble(10), r.IsDBNull(11) ? (double?)null : r.GetDouble(11),
            DateOnly.ParseExact(r.GetString(12), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        r.Close(); tx.Commit(); return rows;
    }

    private static void Require(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1)
AND EXISTS(SELECT 1 FROM monarch_branches b WHERE b.company_id=$co AND b.id=$br AND b.is_active=1);";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$br", br.ToString());
        q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) < 1) throw new UnauthorizedAccessException("المستخدم لا يملك الصلاحية ضمن المنشأة والفرع المحددين.");
    }
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString); c.Open();
        using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c;
    }
}
