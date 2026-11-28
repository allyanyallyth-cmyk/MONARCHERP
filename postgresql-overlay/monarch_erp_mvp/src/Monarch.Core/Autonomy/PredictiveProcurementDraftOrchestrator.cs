using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Monarch.Core.Autonomy;

/// <summary>Explicitly bridges read-only demand analysis to manager-queued purchase drafts.
/// Supplier, price, tax and numbering must come from approved commercial master data supplied by the caller.
/// This component cannot approve, post, or silently invent commercial terms.</summary>
public sealed record PredictiveProcurementCommercialTerms(Guid SupplierId, string CurrencyCode, int CurrencyScale,
    long UnitCostMinor, int TaxRateBasisPoints, string OrderNumber, DateTimeOffset? ExpectedAt);
public sealed record PredictiveProcurementDraftOutcome(Guid ItemId, string Sku, bool Created,
    Guid? PurchaseOrderId, Guid? ApprovalQueueId, string Status, string Message);

public sealed class PredictiveProcurementDraftOrchestrator
{
    private readonly SqlitePredictiveProcurementAnalyzer _analyzer;
    private readonly AutonomousProcurementWorkflow _workflow;

    public PredictiveProcurementDraftOrchestrator(SqlitePredictiveProcurementAnalyzer analyzer, AutonomousProcurementWorkflow workflow)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    public IReadOnlyList<PredictiveProcurementDraftOutcome> AnalyzeAndCreateDrafts(Guid userId, Guid companyId,
        Guid branchId, Guid warehouseId, DateTimeOffset asOf, PredictiveProcurementPolicy policy,
        IReadOnlyDictionary<Guid, PredictiveProcurementCommercialTerms> approvedTerms,
        Func<PredictiveProcurementCandidate, string> rationaleFactory)
    {
        if (userId == Guid.Empty || approvedTerms == null || rationaleFactory == null)
            throw new ArgumentException("User, approved commercial terms and rationale provider are required.");

        var candidates = _analyzer.Analyze(companyId, branchId, warehouseId, asOf, policy);
        var outcomes = new List<PredictiveProcurementDraftOutcome>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!approvedTerms.TryGetValue(candidate.ItemId, out var terms) || terms == null || terms.SupplierId == Guid.Empty ||
                string.IsNullOrWhiteSpace(terms.OrderNumber) || terms.UnitCostMinor < 0 || terms.TaxRateBasisPoints < 0 || terms.TaxRateBasisPoints > 10000)
            {
                outcomes.Add(new PredictiveProcurementDraftOutcome(candidate.ItemId, candidate.Sku, false, null, null,
                    "CommercialTermsRequired", "No valid approved supplier, price, tax and order-number terms were supplied. No draft was created."));
                continue;
            }

            try
            {
                var rationale = rationaleFactory(candidate);
                var result = _workflow.CreateDraft(userId, companyId, branchId, terms.SupplierId, warehouseId,
                    terms.CurrencyCode, terms.CurrencyScale, terms.OrderNumber,
                    new[] { new ProcurementDraftLine(candidate.ItemId, candidate.SuggestedQuantityMilli,
                        terms.UnitCostMinor, terms.TaxRateBasisPoints) }, terms.ExpectedAt,
                    string.IsNullOrWhiteSpace(rationale) ? candidate.Rationale : rationale,
                    BuildRecommendationKey(candidate, asOf, policy),
                    new ProcurementForecastSnapshot(checked((long)decimal.Round(candidate.AverageDailyDemandMilli * policy.TargetCoverageDays, 0, MidpointRounding.AwayFromZero)),
                        policy.TargetCoverageDays, asOf.ToUniversalTime(), asOf.ToUniversalTime().AddDays(policy.TargetCoverageDays),
                        "demand-average-v1"));
                outcomes.Add(new PredictiveProcurementDraftOutcome(candidate.ItemId, candidate.Sku, true,
                    result.PurchaseOrderId, result.ApprovalQueueId, "Draft", result.Message));
            }
            catch (Exception ex)
            {
                // One supplier/item failure must not erase successful drafts for other candidates.
                outcomes.Add(new PredictiveProcurementDraftOutcome(candidate.ItemId, candidate.Sku, false,
                    null, null, "DraftCreationFailed", ex.GetType().Name + ": " + ex.Message));
            }
        }
        return outcomes;
    }
    private static string BuildRecommendationKey(PredictiveProcurementCandidate candidate, DateTimeOffset asOf, PredictiveProcurementPolicy policy)
    {
        // One stable recommendation identity per tenant/branch/warehouse/item and UTC analysis-day/policy window.
        var day = asOf.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var material = string.Join("|", candidate.CompanyId.ToString("D"), candidate.BranchId.ToString("D"),
            candidate.WarehouseId.ToString("D"), candidate.ItemId.ToString("D"), day, policy.LookbackDays.ToString(CultureInfo.InvariantCulture),
            policy.SupplierLeadTimeDays.ToString(CultureInfo.InvariantCulture), policy.TargetCoverageDays.ToString(CultureInfo.InvariantCulture),
            policy.MinimumDailyDemandMilli.ToString(CultureInfo.InvariantCulture));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(material))).Replace("-", string.Empty).ToLowerInvariant();
    }

}
