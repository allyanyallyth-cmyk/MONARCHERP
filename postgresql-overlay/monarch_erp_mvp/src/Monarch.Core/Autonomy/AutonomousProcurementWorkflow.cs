using System;
using System.Collections.Generic;

namespace Monarch.Core.Autonomy;

public sealed record ProcurementDraftRequest(Guid UserId, Guid CompanyId, Guid BranchId, Guid SupplierId,
    Guid WarehouseId, string CurrencyCode, int CurrencyScale, string OrderNumber,
    IReadOnlyList<ProcurementDraftLine> Lines, DateTimeOffset? ExpectedAt, string Rationale, string? RecommendationKey = null, ProcurementForecastSnapshot? ForecastSnapshot = null);
public sealed record ProcurementForecastSnapshot(long ForecastDemandMilli, int HorizonDays, DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc, string PolicyVersion);
public sealed record ProcurementDraftLine(Guid ItemId, long QuantityMilli, long UnitCostMinor, int TaxRateBasisPoints);
public sealed record ProcurementDraftResult(Guid PurchaseOrderId, string Status, Guid ApprovalQueueId, string Message);

/// <summary>Adapter must persist a Draft PO and an approval-queue assignment atomically, without approval or GL posting.</summary>
public interface IProcurementDraftGateway
{
    ProcurementDraftResult CreateDraftForPurchasingManager(ProcurementDraftRequest request);
}

/// <summary>Autonomous procurement is draft-only. This engine has no approve/post capability by design.</summary>
public sealed class AutonomousProcurementWorkflow
{
    private readonly IProcurementDraftGateway _gateway;
    public AutonomousProcurementWorkflow(IProcurementDraftGateway gateway) => _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    public ProcurementDraftResult CreateDraft(Guid userId, Guid companyId, Guid branchId, Guid supplierId,
        Guid warehouseId, string currencyCode, int currencyScale, string orderNumber,
        IReadOnlyList<ProcurementDraftLine> lines, DateTimeOffset? expectedAt, string rationale, string? recommendationKey = null, ProcurementForecastSnapshot? forecastSnapshot = null)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty || supplierId == Guid.Empty || warehouseId == Guid.Empty)
            throw new ArgumentException("User, tenant, branch, supplier and warehouse scopes are required.");
        if (lines == null || lines.Count == 0) throw new ArgumentException("At least one purchase line is required.");
        if (string.IsNullOrWhiteSpace(rationale)) throw new ArgumentException("A traceable procurement rationale is required.");
        var result = _gateway.CreateDraftForPurchasingManager(new ProcurementDraftRequest(userId, companyId, branchId,
            supplierId, warehouseId, currencyCode, currencyScale, orderNumber, lines, expectedAt, rationale, recommendationKey, forecastSnapshot));
        if (result == null || result.PurchaseOrderId == Guid.Empty || result.ApprovalQueueId == Guid.Empty || result.Status != "Draft")
            throw new InvalidOperationException("Procurement gateway did not return a manager-queued Draft purchase order.");
        return result;
    }
}
