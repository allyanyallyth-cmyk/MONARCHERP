using System.Globalization;
using Monarch.Core.Accounting;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

/// <summary>Idempotently posts customer receipts and supplier payments to the tenant ledger.
/// Ledger commit and outbox acknowledgement are separate commits; deterministic journal numbers
/// make retry safe if the process stops between them.
/// </summary>
public sealed class CommercialPaymentPostingAccounts
{
    public string ReceivablesCode { get; init; } = "1100";
    public string PayablesCode { get; init; } = "2100";
}

public sealed class CommercialPaymentAccountingOutboxWorker
{
    private readonly string _connectionString;
    private readonly TenantLedgerStore _ledger;

    public CommercialPaymentAccountingOutboxWorker(string databasePath, TenantLedgerStore ledger)
    {
        if (ledger is null) throw new ArgumentNullException(nameof(ledger));
        var full = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
        _ledger = ledger;
    }

    public OutboxProcessResult Process(Guid userId, Guid companyId, Guid branchId,
        CommercialPaymentPostingAccounts accounts, int maxItems = 25)
    {
        if (accounts is null) throw new ArgumentNullException(nameof(accounts));
        if (maxItems is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxItems));
        _ledger.GetEntries(userId, companyId, branchId); // authorization even for an empty queue
        var pending = LoadPending(companyId, branchId, maxItems);
        var processed = 0; var failed = 0;
        foreach (var item in pending)
        {
            try
            {
                ProcessOne(userId, companyId, branchId, item, accounts);
                Acknowledge(item.Id, companyId, branchId);
                processed++;
            }
            catch (Exception ex)
            {
                RecordFailure(item.Id, companyId, branchId, ex);
                failed++;
            }
        }
        return new OutboxProcessResult(processed, failed);
    }

    private void ProcessOne(Guid userId, Guid companyId, Guid branchId, PaymentOutboxItem item,
        CommercialPaymentPostingAccounts map)
    {
        var journalNumber = "AUTO-PAY-" + item.PaymentId.ToString("N").ToUpperInvariant();
        var existing = _ledger.GetEntries(userId, companyId, branchId)
            .FirstOrDefault(x => string.Equals(x.Number, journalNumber, StringComparison.OrdinalIgnoreCase));
        if (existing?.Status == JournalStatus.Posted) return;

        var payment = LoadPayment(companyId, branchId, item.PaymentId);
        var activeAccounts = _ledger.GetAccounts(userId, companyId, branchId).Where(x => x.IsActive)
            .ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        Account Require(string code, AccountType type)
        {
            if (!activeAccounts.TryGetValue(code, out var account))
                throw new InvalidOperationException($"Required posting account '{code}' is missing or inactive.");
            if (account.Type != type)
                throw new InvalidOperationException($"Posting account '{code}' must be {type}.");
            return account;
        }

        var cashOrBank = Require(payment.CashBankAccountCode, AccountType.Asset);
        var lines = new List<JournalLine>();
        void Debit(Account account, long amount) { if (amount > 0) lines.Add(JournalLine.Debit(account.Id, amount)); }
        void Credit(Account account, long amount) { if (amount > 0) lines.Add(JournalLine.Credit(account.Id, amount)); }

        if (item.EventType == "CustomerReceiptPosted")
        {
            Debit(cashOrBank, payment.AmountMinor);
            Credit(Require(map.ReceivablesCode, AccountType.Asset), payment.AmountMinor);
        }
        else if (item.EventType == "SupplierPaymentPosted")
        {
            Debit(Require(map.PayablesCode, AccountType.Liability), payment.AmountMinor);
            Credit(cashOrBank, payment.AmountMinor);
        }
        else throw new InvalidOperationException("Unsupported payment outbox event.");

        if (payment.AmountMinor <= 0 || lines.Count != 2)
            throw new InvalidOperationException("Payment must produce two positive, balanced journal lines.");

        var journal = existing ?? _ledger.CreateDraft(userId, companyId, branchId, journalNumber,
            payment.PaymentDate, $"Automatic {item.EventType}: {payment.Number}", lines);
        if (journal.Status == JournalStatus.Draft)
            _ledger.Post(userId, companyId, branchId, journal.Id);
        else if (journal.Status != JournalStatus.Posted)
            throw new InvalidOperationException("Deterministic payment journal is in a non-postable state.");
    }

    private List<PaymentOutboxItem> LoadPending(Guid companyId, Guid branchId, int limit)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"SELECT id,payment_id,event_type FROM monarch_payment_accounting_outbox
WHERE company_id=$co AND branch_id=$br AND status IN('Pending','Failed') ORDER BY created_utc,id LIMIT $limit;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        q.Parameters.AddWithValue("$limit", limit);
        using var r = q.ExecuteReader(); var result = new List<PaymentOutboxItem>();
        while (r.Read()) result.Add(new PaymentOutboxItem(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2)));
        return result;
    }

    private PaymentData LoadPayment(Guid companyId, Guid branchId, Guid paymentId)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"SELECT payment_number,payment_type,payment_method,cash_bank_account_code,amount_minor,payment_date,status
FROM monarch_commercial_payments WHERE company_id=$co AND branch_id=$br AND id=$id;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        q.Parameters.AddWithValue("$id", paymentId.ToString());
        using var r = q.ExecuteReader();
        if (!r.Read() || r.GetString(6) != "Posted") throw new InvalidOperationException("Payment is missing or not posted.");
        return new PaymentData(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
            DateOnly.ParseExact(r.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private void Acknowledge(Guid id, Guid companyId, Guid branchId)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"UPDATE monarch_payment_accounting_outbox SET status='Processed',processed_utc=$now,last_error=NULL
WHERE id=$id AND company_id=$co AND branch_id=$br AND status<>'Processed';";
        q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$id", id.ToString()); q.Parameters.AddWithValue("$co", companyId.ToString());
        q.Parameters.AddWithValue("$br", branchId.ToString()); q.ExecuteNonQuery();
    }

    private void RecordFailure(Guid id, Guid companyId, Guid branchId, Exception error)
    {
        var message = error.GetType().Name + ": " + error.Message;
        if (message.Length > 1000) message = message.Substring(0, 1000);
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"UPDATE monarch_payment_accounting_outbox SET status='Failed',attempts=attempts+1,last_error=$error
WHERE id=$id AND company_id=$co AND branch_id=$br AND status<>'Processed';";
        q.Parameters.AddWithValue("$error", message); q.Parameters.AddWithValue("$id", id.ToString());
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        q.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString); c.Open();
        using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON;"; p.ExecuteNonQuery(); return c;
    }

    private sealed record PaymentOutboxItem(Guid Id, Guid PaymentId, string EventType);
    private sealed record PaymentData(string Number, string Type, string Method, string CashBankAccountCode,
        long AmountMinor, DateOnly PaymentDate);
}
