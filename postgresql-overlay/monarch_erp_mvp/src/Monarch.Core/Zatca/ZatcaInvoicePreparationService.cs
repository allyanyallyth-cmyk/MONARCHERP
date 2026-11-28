using System;

namespace Monarch.Core.Zatca;

/// <summary>Coordinates tenant chain lookup, invoice generation and atomic append to the immutable chain.</summary>
public sealed class ZatcaInvoicePreparationService
{
    private readonly ZatcaInvoiceChainStore _store;
    private readonly ZatcaInvoiceBuilder _builder;
    public ZatcaInvoicePreparationService(ZatcaInvoiceChainStore store, ZatcaInvoiceBuilder builder)
    { _store = store ?? throw new ArgumentNullException(nameof(store)); _builder = builder ?? throw new ArgumentNullException(nameof(builder)); }
    public ZatcaChainRecord Prepare(Guid userId, ZatcaInvoiceDraft draft)
    {
        var previous = _store.GetLatestHash(userId, draft.CompanyId, draft.BranchId, draft.EgsUnitId);
        var nextSequence = _store.GetNextSequence(userId, draft.CompanyId, draft.BranchId, draft.EgsUnitId);
        var sequencedDraft = draft with { InvoiceCounterValue = nextSequence };
        var built = _builder.Build(sequencedDraft, previous);
        return _store.Append(userId, sequencedDraft, built);
    }
}
