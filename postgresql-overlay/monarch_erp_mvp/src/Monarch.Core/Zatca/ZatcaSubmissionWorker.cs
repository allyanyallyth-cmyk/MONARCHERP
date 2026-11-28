using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Monarch.Core.Zatca;

/// <summary>Processes a bounded batch of tenant-scoped ZATCA submissions; no automatic background loop is started.</summary>
public sealed class ZatcaSubmissionWorker
{
    private readonly ZatcaInvoiceChainStore _store;
    private readonly ZatcaApiClient _client;
    private readonly ZatcaEgsRegistry _egsRegistry;
    private readonly Monarch.Core.Autonomy.CircuitBreaker? _submissionCircuit;
    private readonly Action<string>? _resilienceAlert;
    public ZatcaSubmissionWorker(ZatcaInvoiceChainStore store, ZatcaApiClient client, ZatcaEgsRegistry egsRegistry,
        Monarch.Core.Autonomy.CircuitBreaker? submissionCircuit = null, Action<string>? resilienceAlert = null)
    { _store = store ?? throw new ArgumentNullException(nameof(store)); _client = client ?? throw new ArgumentNullException(nameof(client)); _egsRegistry = egsRegistry ?? throw new ArgumentNullException(nameof(egsRegistry)); _submissionCircuit = submissionCircuit; _resilienceAlert = resilienceAlert; }

    public Task<(int Succeeded, int Rejected, int Failed)> ProcessAsync(Guid userId, Guid companyId,
        Guid branchId, int maxItems = 10, CancellationToken cancellationToken = default) =>
        ProcessWithRetryLimitAsync(userId, companyId, branchId, maxItems, 8, cancellationToken);

    public async Task<(int Succeeded, int Rejected, int Failed)> ProcessWithRetryLimitAsync(Guid userId, Guid companyId,
        Guid branchId, int maxItems, int maxAttempts, CancellationToken cancellationToken = default)
    {
        // Do not claim local outbox work while the remote ZATCA circuit is open.
        // Local invoice creation/accounting remains available; only external submission is isolated.
        if (_submissionCircuit != null && !_submissionCircuit.TryEnter())
        {
            _resilienceAlert?.Invoke("ZATCA submission circuit is open; outbound submissions are paused. Local operations remain available.");
            return (0, 0, 0);
        }
        // Evaluate readiness before claiming work; every blocked invoice receives an auditable reason.
        // SQL readiness predicates remain mandatory defense-in-depth.
        foreach (var candidate in _store.ListPending(userId, companyId, branchId, Math.Min(maxItems, 200)))
        {
            try
            {
                var readiness = _egsRegistry.EvaluateReadiness(userId, companyId, branchId, candidate.EgsUnitId);
                if (!readiness.CanSubmitInvoices)
                    _store.AuditEgsReadinessBlocked(userId, companyId, branchId, candidate.EgsUnitId, candidate.Id,
                        string.Join(" | ", readiness.BlockingReasons));
            }
            catch (KeyNotFoundException)
            {
                _store.AuditEgsReadinessBlocked(userId, companyId, branchId, candidate.EgsUnitId, candidate.Id,
                    "EGS registry record is missing for this invoice.");
            }
        }
        var pending = _store.ClaimPending(userId, companyId, branchId, maxItems, maxAttempts);
        var succeeded = 0; var rejected = 0; var failed = 0;
        foreach (var row in pending)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = _store.GetSubmissionPayload(userId, companyId, branchId, row.Id);
                var result = await _client.SubmitAsync(companyId, payload.EgsUnitId, Enum.Parse<ZatcaInvoiceKind>(payload.Kind), row.Uuid.ToString(), payload.Hash, payload.Xml, cancellationToken).ConfigureAwait(false);
                _store.CompleteSubmission(userId, companyId, branchId, row.Id, result.Status,
                    result.ValidationErrors ?? result.RawResponse, result.ClearedInvoiceXml, result.QrPayloadBase64);
                if (_submissionCircuit != null)
                {
                    if (result.Accepted || result.Status == "Rejected") _submissionCircuit.RecordSuccess();
                    else _submissionCircuit.RecordFailure();
                }
                if (result.Accepted) succeeded++; else if (result.Status == "Rejected") rejected++; else failed++;
            }
            catch (OperationCanceledException ex)
            {
                // Cancellation may occur after request bytes have left the process. Never convert
                // that ambiguous outcome into a retryable Failed state.
                try { _store.MarkSubmissionOutcomeUnknown(userId, companyId, branchId, row.Id, "Cancellation/timeout may have occurred after transmission began: " + ex.Message); } catch { }
                _submissionCircuit?.RecordFailure();
                _resilienceAlert?.Invoke("ZATCA submission outcome is unknown; invoice quarantined from automatic retry.");
                throw;
            }
            catch (Exception ex)
            {
                // A transport exception does not prove ZATCA did not receive the invoice.
                // Keep outbox in Processing; ClaimPending will not reclaim stale Processing rows.
                try { _store.MarkSubmissionOutcomeUnknown(userId, companyId, branchId, row.Id, ex.GetType().Name + ": " + ex.Message); } catch { }
                _submissionCircuit?.RecordFailure();
                _resilienceAlert?.Invoke("ZATCA transport failure; ambiguous invoice outcome quarantined from automatic retry.");
                failed++;
            }
        }
        return (succeeded, rejected, failed);
    }
}
