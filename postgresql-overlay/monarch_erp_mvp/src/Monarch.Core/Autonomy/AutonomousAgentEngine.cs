using System;
using System.Collections.Generic;
using System.Linq;

namespace Monarch.Core.Autonomy;

/// <summary>Deterministic decision-support primitives. This engine proposes actions; it never posts journals or places orders.</summary>
public sealed class AutonomousAgentEngine
{
    public CashForecastResult ForecastCash(Guid companyId, Guid branchId, decimal openingCash,
        IEnumerable<CashFlowEvent> events, DateTimeOffset asOfUtc, int horizonDays = 30)
    {
        if (companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("Company and branch are required.");
        if (horizonDays < 1 || horizonDays > 90) throw new ArgumentOutOfRangeException(nameof(horizonDays));
        var daily = events.Where(x => x.CompanyId == companyId && x.BranchId == branchId && x.DueUtc > asOfUtc && x.DueUtc <= asOfUtc.AddDays(horizonDays))
            .GroupBy(x => x.DueUtc.UtcDateTime.Date).ToDictionary(g => g.Key, g => g.Sum(x => x.Direction == CashFlowDirection.Inflow ? x.Amount : -x.Amount));
        var balance = openingCash; var min = openingCash; DateTimeOffset? breach = null; var points = new List<CashForecastPoint>();
        for (var day = 1; day <= horizonDays; day++)
        {
            var date = asOfUtc.AddDays(day).UtcDateTime.Date;
            if (daily.TryGetValue(date, out var movement)) balance += movement;
            if (balance < min) min = balance;
            if (balance < 0 && breach == null) breach = new DateTimeOffset(date, TimeSpan.Zero);
            points.Add(new CashForecastPoint(new DateTimeOffset(date, TimeSpan.Zero), balance));
        }
        return new CashForecastResult(companyId, branchId, openingCash, min, breach, points);
    }

    public IReadOnlyList<ReorderRecommendation> RecommendReorders(Guid companyId, Guid branchId, IEnumerable<InventoryDemandSnapshot> items)
    {
        return items.Where(x => x.CompanyId == companyId && x.BranchId == branchId).Select(x =>
        {
            if (x.AverageDailyUsage < 0 || x.LeadTimeDays < 0 || x.SafetyStock < 0 || x.OnHand < 0 || x.OnOrder < 0)
                throw new ArgumentException("Inventory demand inputs cannot be negative.");
            var reorderPoint = x.AverageDailyUsage * x.LeadTimeDays + x.SafetyStock;
            var target = x.AverageDailyUsage * (x.LeadTimeDays + x.ReviewPeriodDays) + x.SafetyStock;
            var shortage = Math.Max(0m, target - x.OnHand - x.OnOrder);
            return new ReorderRecommendation(x.ItemId, x.OnHand + x.OnOrder <= reorderPoint, shortage, reorderPoint,
                "Recommendation only; purchase order requires authorized approval.");
        }).Where(x => x.IsCritical || x.SuggestedQuantity > 0).ToArray();
    }

    public IReadOnlyList<FinancialAnomaly> DetectAnomalies(IEnumerable<FinancialObservation> observations, decimal absoluteFloor = 1000m, decimal sigmaThreshold = 3m)
    {
        if (absoluteFloor < 0 || sigmaThreshold <= 0) throw new ArgumentOutOfRangeException();
        return observations.GroupBy(x => new { x.CompanyId, x.BranchId, x.Category }).SelectMany(group =>
        {
            var values = group.Select(x => x.Amount).ToArray(); if (values.Length < 5) return Array.Empty<FinancialAnomaly>();
            var mean = values.Average(); var variance = values.Average(v => (v - mean) * (v - mean)); var sd = (decimal)Math.Sqrt((double)variance);
            return group.Where(x => Math.Abs(x.Amount - mean) >= absoluteFloor && sd > 0 && Math.Abs(x.Amount - mean) / sd >= sigmaThreshold)
                .Select(x => new FinancialAnomaly(x.TransactionId, x.CompanyId, x.BranchId, x.Amount, mean, sd, "Statistical outlier; investigation required, not a fraud determination."));
        }).ToArray();
    }

    public MonthCloseAssessment AssessMonthClose(Guid companyId, Guid branchId, IEnumerable<MonthCloseControl> controls)
    {
        var missing = controls.Where(x => x.CompanyId == companyId && x.BranchId == branchId && !x.IsComplete).Select(x => x.ControlName).Distinct().ToArray();
        return new MonthCloseAssessment(companyId, branchId, missing.Length == 0, missing,
            missing.Length == 0 ? "Controls complete; authorized controller may approve close." : "Close blocked until all listed controls are resolved.");
    }

    public NaturalLanguageIntent ParseIntent(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return new NaturalLanguageIntent("Unknown", Array.Empty<string>(), false, "Empty request.");
        var s = utterance.Trim().ToLowerInvariant();
        if (ContainsAny(s, "مبيعات", "sales")) return new NaturalLanguageIntent("SalesReport", ExtractTopN(s), false, "Read-only report intent; resolve date and tenant scope before execution.");
        if (ContainsAny(s, "zatca", "الزكاة", "الفواتير المعلقة")) return new NaturalLanguageIntent("ZatcaPendingInvoices", Array.Empty<string>(), false, "Read-only query; tenant authorization is required.");
        if (ContainsAny(s, "مخزون", "اطلب", "شراء", "reorder", "inventory")) return new NaturalLanguageIntent("InventoryReorderPreview", Array.Empty<string>(), false, "Preview only; purchase order creation requires explicit approval.");
        if (ContainsAny(s, "اقفل الشهر", "إغلاق الشهر", "month close")) return new NaturalLanguageIntent("MonthCloseAssessment", Array.Empty<string>(), false, "Assessment only; final close requires authorized approval.");
        return new NaturalLanguageIntent("Unknown", Array.Empty<string>(), false, "No supported intent matched. No action was executed.");
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);
    private static string[] ExtractTopN(string s)
    {
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? new[] { "top=" + Math.Min(100, Math.Max(1, int.Parse(digits))) } : Array.Empty<string>();
    }
}

public enum CashFlowDirection { Inflow, Outflow }
public sealed record CashFlowEvent(Guid CompanyId, Guid BranchId, DateTimeOffset DueUtc, decimal Amount, CashFlowDirection Direction, string SourceReference);
public sealed record CashForecastPoint(DateTimeOffset DateUtc, decimal ClosingBalance);
public sealed record CashForecastResult(Guid CompanyId, Guid BranchId, decimal OpeningCash, decimal MinimumProjectedCash, DateTimeOffset? FirstNegativeDateUtc, IReadOnlyList<CashForecastPoint> Points);
public sealed record InventoryDemandSnapshot(Guid CompanyId, Guid BranchId, string ItemId, decimal OnHand, decimal OnOrder, decimal AverageDailyUsage, int LeadTimeDays, int ReviewPeriodDays, decimal SafetyStock);
public sealed record ReorderRecommendation(string ItemId, bool IsCritical, decimal SuggestedQuantity, decimal ReorderPoint, string ControlNote);
public sealed record FinancialObservation(Guid CompanyId, Guid BranchId, string Category, string TransactionId, decimal Amount);
public sealed record FinancialAnomaly(string TransactionId, Guid CompanyId, Guid BranchId, decimal Amount, decimal Mean, decimal StandardDeviation, string Explanation);
public sealed record MonthCloseControl(Guid CompanyId, Guid BranchId, string ControlName, bool IsComplete);
public sealed record MonthCloseAssessment(Guid CompanyId, Guid BranchId, bool CanClose, IReadOnlyList<string> MissingControls, string Explanation);
public sealed record NaturalLanguageIntent(string IntentCode, IReadOnlyList<string> Parameters, bool IsExecutable, string SafetyNote);
