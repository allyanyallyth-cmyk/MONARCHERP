using Microsoft.Data.Sqlite;
using Monarch.Core.Accounting;
using System.Globalization;

namespace Monarch.Core.Commercial;

/// <summary>Idempotent bridge from posted commercial documents to the tenant general ledger.
/// Ledger commit and outbox acknowledgement are separate transactions; deterministic journal
/// numbers make retries safe if the process stops between those commits.
/// </summary>
public sealed class CommercialPostingAccounts
{
    public string ReceivablesCode { get; init; } = "1100";
    public string PayablesCode { get; init; } = "2100";
    /// <summary>Interim liability for received goods not yet matched to a supplier invoice.</summary>
    public string GoodsReceivedNotInvoicedCode { get; init; } = "2190";
    public string InventoryCode { get; init; } = "1300";
    public string PurchasesExpenseCode { get; init; } = "5100";
    public string SalesRevenueCode { get; init; } = "4100";
    public string CostOfGoodsSoldCode { get; init; } = "5000";
    public string InputTaxCode { get; init; } = "1150";
    public string OutputTaxCode { get; init; } = "2150";
}

public sealed record OutboxProcessResult(int Processed, int Failed);

public sealed class CommercialAccountingOutboxWorker
{
    private readonly string _connectionString;
    private readonly TenantLedgerStore _ledger;

    public CommercialAccountingOutboxWorker(string databasePath, TenantLedgerStore ledger)
    {
        if (ledger is null) throw new ArgumentNullException(nameof(ledger));
        var full = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
        _ledger = ledger;
    }

    public OutboxProcessResult Process(Guid userId, Guid companyId, Guid branchId,
        CommercialPostingAccounts accounts, int maxItems = 25)
    {
        if (accounts is null) throw new ArgumentNullException(nameof(accounts));
        if (maxItems is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxItems));
        // Authorize even when the queue is empty; never use an empty result as an access oracle.
        _ledger.GetEntries(userId, companyId, branchId);
        var pending = LoadPending(companyId, branchId, maxItems);
        var processed = 0; var failed = 0;
        foreach (var item in pending)
        {
            try
            {
                ProcessOne(userId, companyId, branchId, item, accounts);
                // Re-check authorization immediately before the direct outbox write. Ledger
                // posting and outbox acknowledgement are separate transactions, so a prior
                // authorization check must not be treated as permission for this mutation.
                _ledger.GetEntries(userId, companyId, branchId);
                Acknowledge(userId, item.Id, companyId, branchId);
                processed++;
            }
            catch (UnauthorizedAccessException)
            {
                // Authorization failures are not processing failures. In particular, do not
                // mutate the outbox after a user's branch/company access has been revoked.
                // Let the caller observe the denial and leave the event untouched for an
                // authorized worker to retry later.
                throw;
            }
            catch (Exception ex)
            {
                // Failure bookkeeping is also a write. If access was revoked while the
                // operation was running, propagate the denial rather than mutating the queue.
                _ledger.GetEntries(userId, companyId, branchId);
                RecordFailure(userId, item.Id, companyId, branchId, ex);
                failed++;
            }
        }
        return new OutboxProcessResult(processed, failed);
    }

    private void ProcessOne(Guid userId, Guid companyId, Guid branchId, OutboxItem item, CommercialPostingAccounts map)
    {
        var existing = _ledger.GetEntries(userId, companyId, branchId)
            .FirstOrDefault(x => string.Equals(x.Number, JournalNumber(item.DocumentId), StringComparison.OrdinalIgnoreCase));
        if (existing?.Status == JournalStatus.Posted) return;
        var document = LoadDocument(companyId, branchId, item.DocumentId);
        var ledgerAccounts = _ledger.GetAccounts(userId, companyId, branchId).Where(x => x.IsActive).ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        Account Require(string code, AccountType expectedType)
        {
            if (!ledgerAccounts.TryGetValue(code, out var account))
                throw new InvalidOperationException($"Required posting account code '{code}' is not configured or active.");
            if (account.Type != expectedType)
                throw new InvalidOperationException($"Posting account '{code}' must be of type {expectedType}.");
            return account;
        }

        var lines = new List<JournalLine>();
        if (item.EventType == "SalePosted")
        {
            AddDebit(Require(map.ReceivablesCode, AccountType.Asset), document.Total);
            AddCredit(Require(map.SalesRevenueCode, AccountType.Revenue), document.Subtotal);
            AddCredit(Require(map.OutputTaxCode, AccountType.Liability), document.Tax);
            // Recognize perpetual inventory cost from immutable per-line issue allocations,
            // never from the sales price or the item's current default cost.
            if (document.StockSubtotal > 0 && document.StockAllocationCount == 0)
                throw new InvalidOperationException("Posted stock sale has no persisted inventory cost allocation; COGS posting is blocked.");
            if (document.StockCost > 0)
            {
                AddDebit(Require(map.CostOfGoodsSoldCode, AccountType.Expense), document.StockCost);
                AddCredit(Require(map.InventoryCode, AccountType.Asset), document.StockCost);
            }
        }
        else if (item.EventType == "PurchasePosted")
        {
            AddDebit(Require(map.InventoryCode, AccountType.Asset), document.StockSubtotal);
            AddDebit(Require(map.PurchasesExpenseCode, AccountType.Expense), document.NonStockSubtotal);
            AddDebit(Require(map.InputTaxCode, AccountType.Asset), document.Tax);
            AddCredit(Require(map.GoodsReceivedNotInvoicedCode, AccountType.Liability), document.Total);
        }
        else throw new InvalidOperationException("Unsupported commercial outbox event.");

        void AddDebit(Account account, long amount) { if (amount > 0) lines.Add(JournalLine.Debit(account.Id, amount)); }
        void AddCredit(Account account, long amount) { if (amount > 0) lines.Add(JournalLine.Credit(account.Id, amount)); }
        if (document.Total <= 0 || lines.Count < 2) throw new InvalidOperationException("A posted commercial document must have a positive, balanced total.");

        JournalEntry journal;
        if (existing is null)
        {
            journal = _ledger.CreateDraft(userId, companyId, branchId, JournalNumber(item.DocumentId),
                new DateOnly(document.CreatedUtc.UtcDateTime.Year, document.CreatedUtc.UtcDateTime.Month, document.CreatedUtc.UtcDateTime.Day), $"Automatic {item.EventType}: {document.Number}", lines);
        }
        else journal = existing;
        if (journal.Status == JournalStatus.Draft) _ledger.Post(userId, companyId, branchId, journal.Id);
        else if (journal.Status != JournalStatus.Posted) throw new InvalidOperationException("The deterministic journal exists in a non-postable state.");
    }

    private static string JournalNumber(Guid id) => "AUTO-COM-" + id.ToString("N").ToUpperInvariant();

    private List<OutboxItem> LoadPending(Guid companyId, Guid branchId, int limit)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"SELECT id,document_id,event_type FROM monarch_accounting_posting_outbox
WHERE company_id=$co AND branch_id=$br AND status IN('Pending','Failed') ORDER BY created_utc,id LIMIT $limit;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$limit", limit);
        using var r = q.ExecuteReader(); var rows = new List<OutboxItem>();
        while (r.Read()) rows.Add(new OutboxItem(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2)));
        return rows;
    }

    private DocumentData LoadDocument(Guid companyId, Guid branchId, Guid documentId)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"SELECT d.document_number,d.subtotal_minor,d.tax_minor,d.total_minor,d.created_utc,d.status,
COALESCE(SUM(CASE WHEN i.item_type='Stock' THEN l.net_minor ELSE 0 END),0),
COALESCE(SUM(CASE WHEN i.item_type<>'Stock' THEN l.net_minor ELSE 0 END),0),
COALESCE((SELECT SUM(a.total_cost_minor) FROM monarch_inventory_cost_allocations a
 WHERE a.company_id=d.company_id AND a.branch_id=d.branch_id AND a.document_id=d.id AND a.allocation_type='SalesIssue'),0),
COALESCE((SELECT COUNT(*) FROM monarch_inventory_cost_allocations a
 WHERE a.company_id=d.company_id AND a.branch_id=d.branch_id AND a.document_id=d.id AND a.allocation_type='SalesIssue'),0)
FROM monarch_commercial_documents d
LEFT JOIN monarch_commercial_document_lines l ON l.company_id=d.company_id AND l.document_id=d.id
LEFT JOIN monarch_items i ON i.company_id=l.company_id AND i.id=l.item_id
WHERE d.company_id=$co AND d.branch_id=$br AND d.id=$id
GROUP BY d.id,d.document_number,d.subtotal_minor,d.tax_minor,d.total_minor,d.created_utc,d.status;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$id", documentId.ToString());
        using var r = q.ExecuteReader();
        if (!r.Read() || r.GetString(5) != "Posted") throw new InvalidOperationException("Outbox document is missing or not posted.");
        return new DocumentData(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3),
            r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9),
            DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture));
    }

    private void Acknowledge(Guid userId, Guid id, Guid companyId, Guid branchId)
    {
        ExecuteAuthorizedOutboxMutation(userId, companyId, branchId, q =>
        {
            q.CommandText = @"UPDATE monarch_accounting_posting_outbox SET status='Processed',processed_utc=$now,last_error=NULL
WHERE id=$id AND company_id=$co AND branch_id=$br AND status<>'Processed';";
            q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            q.Parameters.AddWithValue("$id", id.ToString()); q.Parameters.AddWithValue("$co", companyId.ToString());
            q.Parameters.AddWithValue("$br", branchId.ToString());
        });
    }

    private void RecordFailure(Guid userId, Guid id, Guid companyId, Guid branchId, Exception error)
    {
        var message = error.GetType().Name + ": " + error.Message;
        if (message.Length > 1000) message = message.Substring(0, 1000);
        ExecuteAuthorizedOutboxMutation(userId, companyId, branchId, q =>
        {
            q.CommandText = @"UPDATE monarch_accounting_posting_outbox SET status='Failed',attempts=attempts+1,last_error=$error
WHERE id=$id AND company_id=$co AND branch_id=$br AND status<>'Processed';";
            q.Parameters.AddWithValue("$error", message); q.Parameters.AddWithValue("$id", id.ToString());
            q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        });
    }

    // Authorization and the outbox mutation share one IMMEDIATE SQLite transaction. This makes
    // revocation and acknowledgement/failure bookkeeping serializable: either the authorized
    // mutation commits first, or revocation commits first and this operation is denied.
    private void ExecuteAuthorizedOutboxMutation(Guid userId, Guid companyId, Guid branchId,
        Action<SqliteCommand> configureMutation)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction(deferred: false);
        using (var authorization = c.CreateCommand())
        {
            authorization.Transaction = transaction;
            authorization.CommandText = @"SELECT a.role_code,
CASE WHEN a.role_code='Administrator' COLLATE NOCASE OR EXISTS(
 SELECT 1 FROM monarch_user_branch_access ba
 WHERE ba.user_id=a.user_id AND ba.company_id=a.company_id AND ba.branch_id=b.id AND ba.is_active=1)
THEN 1 ELSE 0 END AS has_branch_access
FROM monarch_user_company_access a
JOIN monarch_users u ON u.id=a.user_id AND u.is_active=1
JOIN monarch_companies co ON co.id=a.company_id AND co.is_active=1
JOIN monarch_branches b ON b.id=$branch AND b.company_id=co.id AND b.is_active=1
WHERE a.user_id=$user AND a.company_id=$company AND a.is_active=1;";
            authorization.Parameters.AddWithValue("$user", userId.ToString());
            authorization.Parameters.AddWithValue("$company", companyId.ToString());
            authorization.Parameters.AddWithValue("$branch", branchId.ToString());
            using var reader = authorization.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(1) != 1)
                throw new UnauthorizedAccessException("User has no active access to the selected company and branch.");
            var role = reader.GetString(0);
            if (!string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(role, "Accountant", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Company role does not permit accounting changes.");
        }

        using (var mutation = c.CreateCommand())
        {
            mutation.Transaction = transaction;
            configureMutation(mutation);
            mutation.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString); c.Open();
        using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON;"; p.ExecuteNonQuery(); return c;
    }

    private sealed record OutboxItem(Guid Id, Guid DocumentId, string EventType);
    private sealed record DocumentData(string Number, long Subtotal, long Tax, long Total,
        long StockSubtotal, long NonStockSubtotal, long StockCost, long StockAllocationCount, DateTimeOffset CreatedUtc);
}
