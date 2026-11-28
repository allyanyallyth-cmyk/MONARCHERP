using System.Globalization;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Data.Sqlite;
using Monarch.Core.Accounting;

namespace Monarch.Core.Commercial;

/// <summary>
/// Idempotent GL bridge for the dedicated supplier-invoice and supplier-payment AP outboxes.
/// It deliberately rejects foreign-currency documents until a realized/unrealized FX policy
/// and currency-aware ledger are available. Journal commit and outbox acknowledgement are
/// separate transactions; deterministic journal numbers make retries safe.
/// </summary>
public sealed class SupplierPayablesPostingAccounts
{
    public string PayablesCode { get; init; } = "2100";
    public string GoodsReceivedNotInvoicedCode { get; init; } = "2190";
    public string InputTaxCode { get; init; } = "1150";
}

public sealed record SupplierPayablesWorkerHealthSnapshot(
    long InvoiceClaims, long PaymentClaims, long HeartbeatTicks, long HeartbeatFailures,
    long LeaseRenewals, long LeaseOwnershipLosses, long Acknowledgements, long FailureUpdates,
    long DiagnosticWriteFailures);

public sealed record SupplierPayablesWorkerDiagnostic(
    Guid Id, Guid CompanyId, Guid BranchId, string EventCode, string EntityKind,
    Guid? EntityId, DateTimeOffset OccurredUtc, string DetailCode);

public sealed class SupplierPayablesAccountingOutboxWorker
{
    private long _invoiceClaims, _paymentClaims, _heartbeatTicks, _heartbeatFailures;
    private long _leaseRenewals, _leaseOwnershipLosses, _acknowledgements, _failureUpdates, _diagnosticWriteFailures;

    /// <summary>Returns process-local cumulative counters; values reset when this worker instance is recreated.</summary>
    public SupplierPayablesWorkerHealthSnapshot GetHealthSnapshot() => new(
        Interlocked.Read(ref _invoiceClaims), Interlocked.Read(ref _paymentClaims),
        Interlocked.Read(ref _heartbeatTicks), Interlocked.Read(ref _heartbeatFailures),
        Interlocked.Read(ref _leaseRenewals), Interlocked.Read(ref _leaseOwnershipLosses),
        Interlocked.Read(ref _acknowledgements), Interlocked.Read(ref _failureUpdates),
        Interlocked.Read(ref _diagnosticWriteFailures));
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessingGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly TenantLedgerStore _ledger;

    public SupplierPayablesAccountingOutboxWorker(string databasePath, TenantLedgerStore ledger)
    {
        if (ledger is null) throw new ArgumentNullException(nameof(ledger));
        var full = Path.GetFullPath(databasePath);
        _databasePath = full;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
        _ledger = ledger;
    }

    /// <summary>Reads recent diagnostics only after the ledger store confirms tenant/branch access.</summary>
    public IReadOnlyList<SupplierPayablesWorkerDiagnostic> GetRecentDiagnostics(
        Guid userId, Guid companyId, Guid branchId, int limit = 100)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        _ledger.GetEntries(userId, companyId, branchId);
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT id,company_id,branch_id,event_code,entity_kind,entity_id,occurred_utc,detail_code FROM monarch_supplier_ap_worker_diagnostics WHERE company_id=$co AND branch_id=$br ORDER BY occurred_utc DESC,id DESC LIMIT $limit;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$limit", limit);
        var rows = new List<SupplierPayablesWorkerDiagnostic>();
        using var r = q.ExecuteReader();
        while (r.Read()) rows.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)),
            Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5)),
            DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), r.GetString(7)));
        return rows;
    }

    /// <summary>Returns tenant/branch-authorized operational alerts derived from durable diagnostics.</summary>
    public IReadOnlyList<SupplierPayablesOperationalAlert> GetOperationalAlerts(
        Guid userId, Guid companyId, Guid branchId, int limit = 100,
        int heartbeatCriticalThreshold = SupplierPayablesOperationalAlertCenter.DefaultHeartbeatCriticalThreshold,
        DateTimeOffset? nowUtc = null)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (heartbeatCriticalThreshold < 2) throw new ArgumentOutOfRangeException(nameof(heartbeatCriticalThreshold));
        var diagnostics = GetRecentDiagnostics(userId, companyId, branchId, 500);
        return SupplierPayablesOperationalAlertCenter.Build(
            diagnostics, nowUtc ?? DateTimeOffset.UtcNow, limit, heartbeatCriticalThreshold);
    }

    private void RecordDiagnostic(Guid companyId, Guid branchId, string eventCode,
        string entityKind, Guid? entityId, string detailCode)
    {
        try
        {
            using var c = Open(); using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO monarch_supplier_ap_worker_diagnostics (id,company_id,branch_id,event_code,entity_kind,entity_id,occurred_utc,detail_code) VALUES ($id,$co,$br,$event,$kind,$entity,$utc,$detail);";
            q.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
            q.Parameters.AddWithValue("$event", eventCode); q.Parameters.AddWithValue("$kind", entityKind);
            q.Parameters.AddWithValue("$entity", entityId.HasValue ? entityId.Value.ToString() : DBNull.Value);
            q.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            q.Parameters.AddWithValue("$detail", detailCode.Length <= 80 ? detailCode : detailCode.Substring(0, 80));
            q.ExecuteNonQuery();
        }
        catch { Interlocked.Increment(ref _diagnosticWriteFailures); }
    }

    public OutboxProcessResult Process(Guid userId, Guid companyId, Guid branchId,
        SupplierPayablesPostingAccounts accounts, int maxItems = 25)
    {
        if (accounts is null) throw new ArgumentNullException(nameof(accounts));
        if (maxItems is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxItems));

        // Serialize workers in this process for the same database/company/branch. The
        // durable SQLite leases coordinate separate processes; deterministic journal identity
        // remains the final duplicate-posting guard if a lease expires mid-operation.
        var gateKey = string.Join("|", _databasePath, companyId.ToString("N"), branchId.ToString("N"));
        var gate = ProcessingGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            // Check tenant/branch authorization even when both queues are empty.
            _ledger.GetEntries(userId, companyId, branchId);
            var leaseOwner = Guid.NewGuid().ToString("N");
            var invoices = ClaimPendingInvoices(companyId, branchId, maxItems, leaseOwner);
            var payments = ClaimPendingPayments(companyId, branchId, maxItems, leaseOwner);
            // Keep long-running batches alive. Renewal is token-fenced, so a worker whose
            // lease has been taken over cannot extend the new owner's lease.
            using var heartbeat = new Timer(_ =>
            {
                Interlocked.Increment(ref _heartbeatTicks);
                try { RenewLeases(companyId, branchId, invoices, payments); }
                catch (Exception ex)
                {
                    // Diagnostics are best-effort; they never grant ownership or block accounting.
                    Interlocked.Increment(ref _heartbeatFailures);
                    RecordDiagnostic(companyId, branchId, "heartbeat_failed", "batch", null, ex.GetType().Name);
                }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            var processed = 0; var failed = 0;
            foreach (var item in invoices)
            {
                try { PostInvoice(userId, companyId, branchId, item, accounts); AcknowledgeInvoice(item.Id, companyId, branchId, item.LeaseToken); processed++; }
                catch (Exception ex) { FailInvoice(item.Id, companyId, branchId, item.LeaseToken, ex); failed++; }
            }
            foreach (var item in payments)
            {
                try { PostPayment(userId, companyId, branchId, item, accounts); AcknowledgePayment(item.Id, companyId, branchId, item.LeaseToken); processed++; }
                catch (Exception ex) { FailPayment(item.Id, companyId, branchId, item.LeaseToken, ex); failed++; }
            }
            return new OutboxProcessResult(processed, failed);
        }
        finally { gate.Release(); }
    }

    private void PostInvoice(Guid userId, Guid companyId, Guid branchId, InvoiceOutboxItem item,
        SupplierPayablesPostingAccounts map)
    {
        var number = "AUTO-SUP-INV-" + item.InvoiceId.ToString("N").ToUpperInvariant();
        var existing = FindJournal(userId, companyId, branchId, number);
        var invoice = LoadInvoice(companyId, branchId, item.InvoiceId);
        RequireBaseCurrency(companyId, invoice.CurrencyCode, invoice.CurrencyScale);
        var active = _ledger.GetAccounts(userId, companyId, branchId).Where(x => x.IsActive)
            .ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        Account Require(string code, AccountType type)
        {
            if (!active.TryGetValue(code, out var account)) throw new InvalidOperationException($"Required posting account '{code}' is missing or inactive.");
            if (account.Type != type) throw new InvalidOperationException($"Posting account '{code}' must be {type}.");
            return account;
        }
        var lines = new List<JournalLine>();
        void Debit(Account account, long amount) { if (amount > 0) lines.Add(JournalLine.Debit(account.Id, amount)); }
        void Credit(Account account, long amount) { if (amount > 0) lines.Add(JournalLine.Credit(account.Id, amount)); }
        Debit(Require(map.GoodsReceivedNotInvoicedCode, AccountType.Liability), invoice.Subtotal);
        Debit(Require(map.InputTaxCode, AccountType.Asset), invoice.Tax);
        Credit(Require(map.PayablesCode, AccountType.Liability), invoice.Total);
        if (invoice.Total <= 0 || lines.Count < 2 || checked(invoice.Subtotal + invoice.Tax) != invoice.Total)
            throw new InvalidOperationException("Supplier invoice totals do not reconcile to its immutable lines.");
        if (existing is not null)
        {
            EnsureJournalMatches(existing, invoice.InvoiceDate, lines);
            if (existing.Status == JournalStatus.Posted) return;
        }
        var journal = existing ?? _ledger.CreateDraft(userId, companyId, branchId, number, invoice.InvoiceDate,
            $"Supplier invoice {invoice.InvoiceNumber}", lines);
        if (journal.Status == JournalStatus.Draft) _ledger.Post(userId, companyId, branchId, journal.Id);
        else if (journal.Status != JournalStatus.Posted) throw new InvalidOperationException("Deterministic supplier invoice journal is not postable.");
    }

    private void PostPayment(Guid userId, Guid companyId, Guid branchId, PaymentOutboxItem item,
        SupplierPayablesPostingAccounts map)
    {
        var number = "AUTO-SUP-PAY-" + item.PaymentId.ToString("N").ToUpperInvariant();
        var existing = FindJournal(userId, companyId, branchId, number);
        var payment = LoadPayment(companyId, branchId, item.PaymentId);
        RequireBaseCurrency(companyId, payment.CurrencyCode, payment.CurrencyScale);
        var active = _ledger.GetAccounts(userId, companyId, branchId).Where(x => x.IsActive)
            .ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        Account Require(string code, AccountType type)
        {
            if (!active.TryGetValue(code, out var account)) throw new InvalidOperationException($"Required posting account '{code}' is missing or inactive.");
            if (account.Type != type) throw new InvalidOperationException($"Posting account '{code}' must be {type}.");
            return account;
        }
        var lines = new List<JournalLine>();
        lines.Add(JournalLine.Debit(Require(map.PayablesCode, AccountType.Liability).Id, payment.AmountMinor));
        lines.Add(JournalLine.Credit(Require(payment.CashBankAccountCode, AccountType.Asset).Id, payment.AmountMinor));
        if (existing is not null)
        {
            EnsureJournalMatches(existing, payment.PaymentDate, lines);
            if (existing.Status == JournalStatus.Posted) return;
        }
        var journal = existing ?? _ledger.CreateDraft(userId, companyId, branchId, number, payment.PaymentDate,
            $"Supplier payment {payment.PaymentNumber}", lines);
        if (journal.Status == JournalStatus.Draft) _ledger.Post(userId, companyId, branchId, journal.Id);
        else if (journal.Status != JournalStatus.Posted) throw new InvalidOperationException("Deterministic supplier payment journal is not postable.");
    }

    private JournalEntry? FindJournal(Guid userId, Guid companyId, Guid branchId, string number) =>
        _ledger.GetEntries(userId, companyId, branchId).FirstOrDefault(x => string.Equals(x.Number, number, StringComparison.OrdinalIgnoreCase));

    private static void EnsureJournalMatches(JournalEntry existing, DateOnly expectedDate, IReadOnlyList<JournalLine> expectedLines)
    {
        if (existing.Status is not (JournalStatus.Draft or JournalStatus.Posted) || existing.Date != expectedDate ||
            !existing.Lines.SequenceEqual(expectedLines))
            throw new InvalidOperationException("A deterministic supplier journal already exists but its date, status, or lines differ from the source document; automatic posting was blocked.");
    }

    private InvoiceData LoadInvoice(Guid companyId, Guid branchId, Guid invoiceId)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"SELECT i.invoice_number,i.supplier_invoice_date,i.currency_code,i.currency_scale,i.subtotal_minor,i.tax_minor,i.total_minor,
COALESCE(SUM(CASE WHEN it.item_type='Stock' THEN l.net_minor ELSE 0 END),0),
COALESCE(SUM(CASE WHEN it.item_type<>'Stock' THEN l.net_minor ELSE 0 END),0),
COALESCE(SUM(l.tax_minor),0),COALESCE(SUM(l.total_minor),0),i.status
FROM monarch_supplier_invoices i JOIN monarch_supplier_invoice_lines l ON l.company_id=i.company_id AND l.invoice_id=i.id
JOIN monarch_items it ON it.company_id=l.company_id AND it.id=l.item_id
WHERE i.company_id=$co AND i.branch_id=$br AND i.id=$id
GROUP BY i.id,i.invoice_number,i.supplier_invoice_date,i.currency_code,i.currency_scale,i.subtotal_minor,i.tax_minor,i.total_minor,i.status;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$id", invoiceId.ToString());
        using var r = q.ExecuteReader();
        if (!r.Read() || r.GetString(11) != "Posted") throw new InvalidOperationException("Supplier invoice is missing or not posted.");
        if (r.GetInt64(4) != checked(r.GetInt64(7) + r.GetInt64(8)) || r.GetInt64(5) != r.GetInt64(9) || r.GetInt64(6) != r.GetInt64(10))
            throw new InvalidOperationException("Supplier invoice header does not match its lines.");
        return new InvoiceData(r.GetString(0), DateOnly.ParseExact(r.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.GetString(2), r.GetInt32(3), r.GetInt64(4), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(6));
    }

    private PaymentData LoadPayment(Guid companyId, Guid branchId, Guid paymentId)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"SELECT p.payment_number,p.payment_date,p.currency_code,p.currency_scale,p.amount_minor,p.cash_bank_account_code,p.status,
COALESCE(SUM(a.amount_minor),0),COUNT(a.id),
COUNT(CASE WHEN ap.invoice_id IS NOT NULL AND ap.branch_id=p.branch_id AND i.branch_id=p.branch_id
 AND ap.supplier_id=p.supplier_id AND ap.currency_code=p.currency_code AND ap.currency_scale=p.currency_scale
 AND i.status='Posted' THEN 1 END)
FROM monarch_supplier_payments p
LEFT JOIN monarch_supplier_payment_allocations a ON a.company_id=p.company_id AND a.payment_id=p.id
LEFT JOIN monarch_supplier_payables ap ON ap.company_id=a.company_id AND ap.invoice_id=a.invoice_id
LEFT JOIN monarch_supplier_invoices i ON i.company_id=ap.company_id AND i.id=ap.invoice_id
WHERE p.company_id=$co AND p.branch_id=$br AND p.id=$id
GROUP BY p.id,p.payment_number,p.payment_date,p.currency_code,p.currency_scale,p.amount_minor,p.cash_bank_account_code,p.status;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$id", paymentId.ToString());
        using var r = q.ExecuteReader();
        if (!r.Read() || r.GetString(6) != "Posted") throw new InvalidOperationException("Supplier payment is missing or not posted.");
        if (r.GetInt64(4) <= 0 || r.GetInt64(7) != r.GetInt64(4) || r.GetInt64(8) < 1)
            throw new InvalidOperationException("Supplier payment allocations do not reconcile to its amount.");
        if (r.GetInt64(8) != r.GetInt64(9))
            throw new InvalidOperationException("Supplier payment contains an allocation outside its supplier, branch, currency, or posted-invoice scope.");
        return new PaymentData(r.GetString(0), DateOnly.ParseExact(r.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.GetString(2), r.GetInt32(3), r.GetInt64(4), r.GetString(5));
    }

    private void RequireBaseCurrency(Guid companyId, string currencyCode, int scale)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"SELECT co.base_currency_code,cu.minor_unit_exponent FROM monarch_companies co
JOIN monarch_currencies cu ON cu.code=co.base_currency_code AND cu.is_active=1 WHERE co.id=$co AND co.is_active=1;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); using var r = q.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Company base currency is not configured.");
        if (!string.Equals(r.GetString(0), currencyCode, StringComparison.OrdinalIgnoreCase) || r.GetInt32(1) != scale)
            throw new InvalidOperationException("Supplier document currency differs from the company base currency; FX accounting is not enabled, so GL posting is blocked.");
    }

    private List<InvoiceOutboxItem> ClaimPendingInvoices(Guid companyId, Guid branchId, int limit, string owner)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT id,invoice_id FROM monarch_supplier_invoice_accounting_outbox WHERE company_id=$co AND branch_id=$br AND processed_utc IS NULL AND (lease_expires_utc IS NULL OR lease_expires_utc<=$now) ORDER BY created_utc,id LIMIT $limit;";
        var now = DateTimeOffset.UtcNow; q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture)); q.Parameters.AddWithValue("$limit", limit);
        var candidates = new List<(Guid Id, Guid InvoiceId)>(); using (var r = q.ExecuteReader()) while (r.Read()) candidates.Add((Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1))));
        var claimed = new List<InvoiceOutboxItem>();
        foreach (var row in candidates) { var token = owner + Guid.NewGuid().ToString("N"); using var claim = c.CreateCommand(); claim.CommandText = "UPDATE monarch_supplier_invoice_accounting_outbox SET lease_token=$token,lease_expires_utc=$until WHERE id=$id AND company_id=$co AND branch_id=$br AND processed_utc IS NULL AND (lease_expires_utc IS NULL OR lease_expires_utc<=$now);"; claim.Parameters.AddWithValue("$token", token); claim.Parameters.AddWithValue("$until", now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture)); claim.Parameters.AddWithValue("$id", row.Id.ToString()); claim.Parameters.AddWithValue("$co", companyId.ToString()); claim.Parameters.AddWithValue("$br", branchId.ToString()); claim.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture)); if (claim.ExecuteNonQuery()==1) { claimed.Add(new(row.Id,row.InvoiceId,token)); Interlocked.Increment(ref _invoiceClaims); } }
        return claimed;
    }

    private List<PaymentOutboxItem> ClaimPendingPayments(Guid companyId, Guid branchId, int limit, string owner)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT id,payment_id FROM monarch_supplier_payment_accounting_outbox WHERE company_id=$co AND branch_id=$br AND processed_utc IS NULL AND (lease_expires_utc IS NULL OR lease_expires_utc<=$now) ORDER BY created_utc,id LIMIT $limit;";
        var now = DateTimeOffset.UtcNow; q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture)); q.Parameters.AddWithValue("$limit", limit);
        var candidates = new List<(Guid Id, Guid PaymentId)>(); using (var r = q.ExecuteReader()) while (r.Read()) candidates.Add((Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1))));
        var claimed = new List<PaymentOutboxItem>();
        foreach (var row in candidates) { var token = owner + Guid.NewGuid().ToString("N"); using var claim = c.CreateCommand(); claim.CommandText = "UPDATE monarch_supplier_payment_accounting_outbox SET lease_token=$token,lease_expires_utc=$until WHERE id=$id AND company_id=$co AND branch_id=$br AND processed_utc IS NULL AND (lease_expires_utc IS NULL OR lease_expires_utc<=$now);"; claim.Parameters.AddWithValue("$token", token); claim.Parameters.AddWithValue("$until", now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture)); claim.Parameters.AddWithValue("$id", row.Id.ToString()); claim.Parameters.AddWithValue("$co", companyId.ToString()); claim.Parameters.AddWithValue("$br", branchId.ToString()); claim.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture)); if (claim.ExecuteNonQuery()==1) { claimed.Add(new(row.Id,row.PaymentId,token)); Interlocked.Increment(ref _paymentClaims); } }
        return claimed;
    }

    private void RenewLeases(Guid companyId, Guid branchId, IReadOnlyList<InvoiceOutboxItem> invoices,
        IReadOnlyList<PaymentOutboxItem> payments)
    {
        var now = DateTimeOffset.UtcNow;
        var until = now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture);
        using var c = Open();
        foreach (var item in invoices)
        {
            using var q = c.CreateCommand();
            q.CommandText = "UPDATE monarch_supplier_invoice_accounting_outbox SET lease_expires_utc=CASE WHEN lease_expires_utc<$until THEN $until ELSE lease_expires_utc END WHERE id=$id AND company_id=$co AND branch_id=$br AND processed_utc IS NULL AND lease_token=$token AND lease_expires_utc>$now;";
            q.Parameters.AddWithValue("$until", until); q.Parameters.AddWithValue("$id", item.Id.ToString());
            q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
            q.Parameters.AddWithValue("$token", item.LeaseToken); q.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            if (q.ExecuteNonQuery() == 1) Interlocked.Increment(ref _leaseRenewals);
        }
        foreach (var item in payments)
        {
            using var q = c.CreateCommand();
            q.CommandText = "UPDATE monarch_supplier_payment_accounting_outbox SET lease_expires_utc=CASE WHEN lease_expires_utc<$until THEN $until ELSE lease_expires_utc END WHERE id=$id AND company_id=$co AND branch_id=$br AND processed_utc IS NULL AND lease_token=$token AND lease_expires_utc>$now;";
            q.Parameters.AddWithValue("$until", until); q.Parameters.AddWithValue("$id", item.Id.ToString());
            q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
            q.Parameters.AddWithValue("$token", item.LeaseToken); q.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            if (q.ExecuteNonQuery() == 1) Interlocked.Increment(ref _leaseRenewals);
        }
    }

    private void AcknowledgeInvoice(Guid id, Guid companyId, Guid branchId, string token) => UpdateInvoiceOutbox(id, companyId, branchId, token, true, null);
    private void FailInvoice(Guid id, Guid companyId, Guid branchId, string token, Exception ex) => UpdateInvoiceOutbox(id, companyId, branchId, token, false, ex);
    private void UpdateInvoiceOutbox(Guid id, Guid companyId, Guid branchId, string token, bool success, Exception? ex)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = success
            ? "UPDATE monarch_supplier_invoice_accounting_outbox SET processed_utc=$now,last_error=NULL,lease_token=NULL,lease_expires_utc=NULL WHERE id=$id AND company_id=$co AND branch_id=$br AND processed_utc IS NULL AND lease_token=$token AND lease_expires_utc>$now;"
            : "UPDATE monarch_supplier_invoice_accounting_outbox SET attempts=attempts+1,last_error=$error,lease_token=NULL,lease_expires_utc=NULL WHERE id=$id AND company_id=$co AND branch_id=$br AND processed_utc IS NULL AND lease_token=$token AND lease_expires_utc>$now;";
        q.Parameters.AddWithValue("$id", id.ToString()); q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$token", token); q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (!success) q.Parameters.AddWithValue("$error", ErrorText(ex!));
        var changed = q.ExecuteNonQuery();
        if (changed != 1) { Interlocked.Increment(ref _leaseOwnershipLosses); RecordDiagnostic(companyId, branchId, "lease_ownership_lost", "invoice", id, success ? "ack_rejected" : "failure_update_rejected"); if (success) throw new InvalidOperationException("Supplier invoice outbox lease was lost or already acknowledged; stale worker update was rejected."); }
        else if (success) Interlocked.Increment(ref _acknowledgements); else Interlocked.Increment(ref _failureUpdates);
    }

    private void AcknowledgePayment(Guid id, Guid companyId, Guid branchId, string token) => UpdatePaymentOutbox(id, companyId, branchId, token, true, null);
    private void FailPayment(Guid id, Guid companyId, Guid branchId, string token, Exception ex) => UpdatePaymentOutbox(id, companyId, branchId, token, false, ex);
    private void UpdatePaymentOutbox(Guid id, Guid companyId, Guid branchId, string token, bool success, Exception? ex)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = success
            ? "UPDATE monarch_supplier_payment_accounting_outbox SET processed_utc=$now,last_error=NULL,lease_token=NULL,lease_expires_utc=NULL WHERE id=$id AND company_id=$co AND branch_id=$br AND processed_utc IS NULL AND lease_token=$token AND lease_expires_utc>$now;"
            : "UPDATE monarch_supplier_payment_accounting_outbox SET attempts=attempts+1,last_error=$error,lease_token=NULL,lease_expires_utc=NULL WHERE id=$id AND company_id=$co AND branch_id=$br AND processed_utc IS NULL AND lease_token=$token AND lease_expires_utc>$now;";
        q.Parameters.AddWithValue("$id", id.ToString()); q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$token", token); q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (!success) q.Parameters.AddWithValue("$error", ErrorText(ex!));
        var changed = q.ExecuteNonQuery();
        if (changed != 1) { Interlocked.Increment(ref _leaseOwnershipLosses); RecordDiagnostic(companyId, branchId, "lease_ownership_lost", "payment", id, success ? "ack_rejected" : "failure_update_rejected"); if (success) throw new InvalidOperationException("Supplier payment outbox lease was lost or already acknowledged; stale worker update was rejected."); }
        else if (success) Interlocked.Increment(ref _acknowledgements); else Interlocked.Increment(ref _failureUpdates);
    }

    private static string ErrorText(Exception ex)
    {
        var text = ex.GetType().Name + ": " + ex.Message;
        return text.Length <= 1000 ? text : text.Substring(0, 1000);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString); c.Open();
        using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c;
    }

    private sealed record InvoiceOutboxItem(Guid Id, Guid InvoiceId, string LeaseToken);
    private sealed record PaymentOutboxItem(Guid Id, Guid PaymentId, string LeaseToken);
    private sealed record InvoiceData(string InvoiceNumber, DateOnly InvoiceDate, string CurrencyCode, int CurrencyScale,
        long Subtotal, long StockSubtotal, long NonStockSubtotal, long Tax, long Total);
    private sealed record PaymentData(string PaymentNumber, DateOnly PaymentDate, string CurrencyCode, int CurrencyScale,
        long AmountMinor, string CashBankAccountCode);
}
