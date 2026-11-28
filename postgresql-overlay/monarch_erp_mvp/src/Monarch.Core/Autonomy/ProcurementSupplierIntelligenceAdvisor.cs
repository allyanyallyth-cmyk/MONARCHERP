using System;
using System.Collections.Generic;
using System.Linq;
using Monarch.Core.Commercial;

namespace Monarch.Core.Autonomy;

public sealed record ProcurementSupplierOption(Guid SupplierId, string SupplierCode, string SupplierName,
    string CurrencyCode, int CurrencyScale, long UnitCostMinor, int TaxRateBasisPoints, int LeadTimeDays,
    int CompletedReceipts, double? AverageActualLeadTimeDays, double? OnTimeDeliveryRate,
    double Score, string RiskLevel, IReadOnlyList<string> RiskFlags, bool ComparableWithinCurrency);

/// <summary>Builds evidence-based supplier comparisons for procurement review. It never creates or approves orders.</summary>
public sealed class ProcurementSupplierIntelligenceAdvisor
{
    private readonly SupplierIntelligenceStore _store;
    public ProcurementSupplierIntelligenceAdvisor(SupplierIntelligenceStore store)
    { _store = store ?? throw new ArgumentNullException(nameof(store)); }

    public IReadOnlyList<ProcurementSupplierOption> Compare(Guid userId, Guid companyId, Guid branchId,
        Guid itemId, DateOnly asOfDate)
    {
        var rows = _store.GetItemOptions(userId, companyId, branchId, itemId, asOfDate);
        if (rows.Count == 0) return Array.Empty<ProcurementSupplierOption>();
        var groups = rows.GroupBy(x => x.CurrencyCode + ":" + x.CurrencyScale).ToList();
        var result = new List<ProcurementSupplierOption>(rows.Count);
        foreach (var row in rows)
        {
            var sameCurrency = groups.First(g => g.Key == row.CurrencyCode + ":" + row.CurrencyScale).ToList();
            var minCost = sameCurrency.Min(x => x.UnitCostMinor);
            var maxCost = sameCurrency.Max(x => x.UnitCostMinor);
            var minLead = sameCurrency.Min(x => x.ContractLeadTimeDays);
            var maxLead = sameCurrency.Max(x => x.ContractLeadTimeDays);
            var priceScore = maxCost == minCost ? 100d : 100d * (maxCost - row.UnitCostMinor) / (maxCost - (double)minCost);
            var leadScore = maxLead == minLead ? 100d : 100d * (maxLead - row.ContractLeadTimeDays) / (maxLead - (double)minLead);
            // No delivery history is explicitly neutral, not treated as perfect performance.
            var reliabilityScore = row.OnTimeDeliveryRate.HasValue ? Math.Max(0d, Math.Min(100d, row.OnTimeDeliveryRate.Value * 100d)) : 50d;
            var score = Math.Round(priceScore * 0.40d + leadScore * 0.25d + reliabilityScore * 0.35d, 2);
            var flags = new List<string>();
            if (row.CompletedReceipts == 0) flags.Add("NoVerifiedDeliveryHistory");
            else if (row.OnTimeDeliveryRate.HasValue && row.OnTimeDeliveryRate.Value < 0.80d) flags.Add("LowOnTimeDeliveryRate");
            if (row.AverageActualLeadTimeDays.HasValue && row.AverageActualLeadTimeDays.Value > row.ContractLeadTimeDays) flags.Add("ActualLeadTimeExceedsContract");
            if (groups.Count > 1) flags.Add("CurrencyNotComparableAcrossSuppliers");
            var risk = flags.Contains("LowOnTimeDeliveryRate") || flags.Contains("ActualLeadTimeExceedsContract") ? "High" :
                flags.Count > 0 ? "Moderate" : "Low";
            result.Add(new ProcurementSupplierOption(row.SupplierId, row.SupplierCode, row.SupplierName,
                row.CurrencyCode, row.CurrencyScale, row.UnitCostMinor, row.TaxRateBasisPoints,
                row.ContractLeadTimeDays, row.CompletedReceipts, row.AverageActualLeadTimeDays,
                row.OnTimeDeliveryRate, score, risk, flags, groups.Count == 1));
        }
        return result.OrderByDescending(x => x.Score).ThenBy(x => x.UnitCostMinor).ToList();
    }
}
