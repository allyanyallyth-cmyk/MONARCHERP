using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Monarch.Core.Commercial;

namespace Monarch.Core.Autonomy;

public sealed record ProcurementApprovalLineIntelligence(Guid ItemId, string Sku, string ItemName,
    long OrderedQuantityMilli, Guid SelectedSupplierId, string SelectedSupplierName,
    string SelectedCurrencyCode, long SelectedUnitCostMinor, IReadOnlyList<ProcurementSupplierOption> Alternatives);

/// <summary>Read-only supplier evidence attached to a pending manager approval. It never changes the PO.</summary>
public sealed class ProcurementApprovalIntelligenceService
{
    private readonly string _connectionString;
    private readonly ProcurementApprovalQueueStore _queue;
    private readonly ProcurementSupplierIntelligenceAdvisor _advisor;

    public ProcurementApprovalIntelligenceService(string databasePath, ProcurementApprovalQueueStore queue,
        ProcurementSupplierIntelligenceAdvisor advisor)
    {
        var full = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _advisor = advisor ?? throw new ArgumentNullException(nameof(advisor));
    }

    public IReadOnlyList<ProcurementApprovalLineIntelligence> GetPendingOrderEvidence(Guid managerUserId,
        Guid companyId, Guid branchId, Guid queueId, DateOnly asOfDate)
    {
        var pending = _queue.ListPending(managerUserId, companyId, branchId);
        ProcurementApprovalQueueItem selected = null;
        foreach (var row in pending) if (row.QueueId == queueId) { selected = row; break; }
        if (selected == null) throw new InvalidOperationException("Approval item is not pending in the current company and branch.");

        using var c = new SqliteConnection(_connectionString); c.Open();
        using var q = c.CreateCommand();
        q.CommandText = @"SELECT po.supplier_id,p.display_name,pol.item_id,pol.item_sku_snapshot,pol.item_name_snapshot,
pol.quantity_milli,pol.unit_cost_minor,po.currency_code
FROM monarch_purchase_orders po JOIN monarch_parties p ON p.company_id=po.company_id AND p.id=po.supplier_id
JOIN monarch_purchase_order_lines pol ON pol.company_id=po.company_id AND pol.order_id=po.id
WHERE po.company_id=$co AND po.branch_id=$br AND po.id=$po AND po.status='Draft' ORDER BY pol.line_number;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        q.Parameters.AddWithValue("$po", selected.PurchaseOrderId.ToString());
        var result = new List<ProcurementApprovalLineIntelligence>(); using var r = q.ExecuteReader();
        var lines = new List<Tuple<Guid,string,string,long,Guid,string,string,long>>();
        while (r.Read()) lines.Add(Tuple.Create(Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetInt64(5),
            Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(7), r.GetInt64(6)));
        r.Close();
        foreach (var line in lines)
        {
            var alternatives = _advisor.Compare(managerUserId, companyId, branchId, line.Item1, asOfDate);
            result.Add(new ProcurementApprovalLineIntelligence(line.Item1, line.Item2, line.Item3, line.Item4,
                line.Item5, line.Item6, line.Item7, line.Item8, alternatives));
        }
        return result;
    }
}
