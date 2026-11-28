using Microsoft.Data.Sqlite;
using Monarch.Core.Accounting;
using System.Globalization;

namespace Monarch.Core.Commercial;

/// <summary>
/// Idempotent GL bridge for posted sales and purchase returns. The ledger commit and
/// outbox acknowledgement are separate transactions; deterministic journal numbers make retries safe.
/// </summary>
public sealed class CommercialReturnPostingAccounts
{
    public string ReceivablesCode { get; init; } = "1100";
    public string PayablesCode { get; init; } = "2100";
    /// <summary>Interim liability used to reverse a purchase return before supplier invoicing.</summary>
    public string GoodsReceivedNotInvoicedCode { get; init; } = "2190";
    public string PurchasesExpenseCode { get; init; } = "5100";
    public string SalesReturnsCode { get; init; } = "4110";
    public string InputTaxCode { get; init; } = "1150";
    public string OutputTaxCode { get; init; } = "2150";
    public string InventoryCode { get; init; } = "1300";
    public string CostOfGoodsSoldCode { get; init; } = "5000";
    public string PurchasePriceVarianceCode { get; init; } = "5190";
}

public sealed class CommercialReturnAccountingOutboxWorker
{
    private readonly string _connectionString;
    private readonly TenantLedgerStore _ledger;

    public CommercialReturnAccountingOutboxWorker(string databasePath, TenantLedgerStore ledger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        var full = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public OutboxProcessResult Process(Guid userId, Guid companyId, Guid branchId,
        CommercialReturnPostingAccounts accounts, int maxItems = 25)
    {
        if (accounts is null) throw new ArgumentNullException(nameof(accounts));
        if (maxItems is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxItems));
        _ledger.GetEntries(userId, companyId, branchId); // authorize even when queue is empty
        var pending = LoadPending(companyId, branchId, maxItems);
        var processed = 0; var failed = 0;
        foreach (var item in pending)
        {
            try
            {
                ProcessOne(userId, companyId, branchId, item, accounts);
                Acknowledge(item.Id, companyId, branchId); processed++;
            }
            catch (Exception ex) { RecordFailure(item.Id, companyId, branchId, ex); failed++; }
        }
        return new OutboxProcessResult(processed, failed);
    }

    private void ProcessOne(Guid userId, Guid companyId, Guid branchId, ReturnOutboxItem item,
        CommercialReturnPostingAccounts map)
    {
        var number = "AUTO-RET-" + item.ReturnId.ToString("N").ToUpperInvariant();
        var existing = _ledger.GetEntries(userId, companyId, branchId)
            .FirstOrDefault(x => string.Equals(x.Number, number, StringComparison.OrdinalIgnoreCase));
        if (existing?.Status == JournalStatus.Posted) return;

        var data = LoadReturn(companyId, branchId, item.ReturnId);
        var expectedEvent = data.Type == "SalesReturn" ? "SalesReturnPosted" : "PurchaseReturnPosted";
        if (!string.Equals(item.EventType, expectedEvent, StringComparison.Ordinal))
            throw new InvalidOperationException("Return event type does not match the posted return.");
        var active = _ledger.GetAccounts(userId, companyId, branchId).Where(x => x.IsActive)
            .ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        Account Require(string code, AccountType type)
        {
            if (!active.TryGetValue(code, out var account)) throw new InvalidOperationException($"Required return account '{code}' is missing or inactive.");
            if (account.Type != type) throw new InvalidOperationException($"Return account '{code}' must be {type}.");
            return account;
        }
        var lines = new List<JournalLine>();
        void Debit(Account a, long amount) { if (amount > 0) lines.Add(JournalLine.Debit(a.Id, amount)); }
        void Credit(Account a, long amount) { if (amount > 0) lines.Add(JournalLine.Credit(a.Id, amount)); }

        if (data.Type == "SalesReturn")
        {
            Debit(Require(map.SalesReturnsCode, AccountType.Expense), data.Subtotal);
            Debit(Require(map.OutputTaxCode, AccountType.Liability), data.Tax);
            Credit(Require(map.ReceivablesCode, AccountType.Asset), data.Total);
            // Restore inventory and reverse COGS at the exact historical issue allocation,
            // never at the sales price or the item's current default cost.
            if (data.StockCost > 0)
            {
                Debit(Require(map.InventoryCode, AccountType.Asset), data.StockCost);
                Credit(Require(map.CostOfGoodsSoldCode, AccountType.Expense), data.StockCost);
            }
        }
        else if (data.Type == "PurchaseReturn")
        {
            Debit(Require(map.GoodsReceivedNotInvoicedCode, AccountType.Liability), data.Total);
            if (data.StockCost > 0) Credit(Require(map.InventoryCode, AccountType.Asset), data.StockCost);
            if (data.NonStockSubtotal > 0) Credit(Require(map.PurchasesExpenseCode, AccountType.Expense), data.NonStockSubtotal);
            if (data.Tax > 0) Credit(Require(map.InputTaxCode, AccountType.Asset), data.Tax);
            // Reconcile supplier credit value against the exact original inventory carrying cost.
            var variance = checked(data.Subtotal - data.StockCost - data.NonStockSubtotal);
            if (variance != 0)
            {
                var varianceAccount = Require(map.PurchasePriceVarianceCode, AccountType.Expense);
                if (variance > 0) Credit(varianceAccount, variance);
                else Debit(varianceAccount, checked(-variance));
            }
        }
        else throw new InvalidOperationException("Unsupported return type.");

        if (data.Total <= 0 || lines.Count < 2)
            throw new InvalidOperationException("A posted return must produce positive, balanced journal lines.");
        var journal = existing ?? _ledger.CreateDraft(userId, companyId, branchId, number,
            data.ReturnDate, $"Automatic {data.Type}: {data.Number}", lines);
        if (journal.Status == JournalStatus.Draft) _ledger.Post(userId, companyId, branchId, journal.Id);
        else if (journal.Status != JournalStatus.Posted) throw new InvalidOperationException("Deterministic return journal is in a non-postable state.");
    }

    private List<ReturnOutboxItem> LoadPending(Guid companyId, Guid branchId, int limit)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"SELECT id,return_id,event_type FROM monarch_return_accounting_outbox
WHERE company_id=$co AND branch_id=$br AND status IN('Pending','Failed') ORDER BY created_utc,id LIMIT $limit;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$limit", limit);
        using var r = q.ExecuteReader(); var rows = new List<ReturnOutboxItem>();
        while (r.Read()) rows.Add(new ReturnOutboxItem(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2)));
        return rows;
    }

    private ReturnData LoadReturn(Guid companyId, Guid branchId, Guid returnId)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"SELECT r.return_number,r.return_type,r.subtotal_minor,r.tax_minor,r.total_minor,r.created_utc,r.status,
COALESCE(SUM(CASE WHEN i.item_type='Stock' THEN rl.net_minor ELSE 0 END),0),
COALESCE(SUM(CASE WHEN i.item_type<>'Stock' THEN rl.net_minor ELSE 0 END),0),
COALESCE((SELECT SUM(a.total_cost_minor) FROM monarch_inventory_return_cost_allocations a
 WHERE a.company_id=r.company_id AND a.return_id=r.id),0)
FROM monarch_commercial_returns r
LEFT JOIN monarch_commercial_return_lines rl ON rl.company_id=r.company_id AND rl.return_id=r.id
LEFT JOIN monarch_items i ON i.company_id=rl.company_id AND i.id=rl.item_id
WHERE r.company_id=$co AND r.branch_id=$br AND r.id=$id
GROUP BY r.id,r.return_number,r.return_type,r.subtotal_minor,r.tax_minor,r.total_minor,r.created_utc,r.status;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$id", returnId.ToString());
        using var r = q.ExecuteReader();
        if (!r.Read() || r.GetString(6) != "Posted") throw new InvalidOperationException("Return is missing or not posted.");
        var created = DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture);
        return new ReturnData(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
            r.GetInt64(7), r.GetInt64(8), r.GetInt64(9),
            new DateOnly(created.UtcDateTime.Year, created.UtcDateTime.Month, created.UtcDateTime.Day));
    }

    private void Acknowledge(Guid id, Guid companyId, Guid branchId)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"UPDATE monarch_return_accounting_outbox SET status='Processed',processed_utc=$now,last_error=NULL
WHERE id=$id AND company_id=$co AND branch_id=$br AND status<>'Processed';";
        q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$id", id.ToString()); q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.ExecuteNonQuery();
    }

    private void RecordFailure(Guid id, Guid companyId, Guid branchId, Exception error)
    {
        var message = error.GetType().Name + ": " + error.Message; if (message.Length > 1000) message = message.Substring(0, 1000);
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = @"UPDATE monarch_return_accounting_outbox SET status='Failed',attempts=attempts+1,last_error=$error
WHERE id=$id AND company_id=$co AND branch_id=$br AND status<>'Processed';";
        q.Parameters.AddWithValue("$error", message); q.Parameters.AddWithValue("$id", id.ToString());
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString); c.Open();
        using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON;"; p.ExecuteNonQuery(); return c;
    }

    private sealed record ReturnOutboxItem(Guid Id, Guid ReturnId, string EventType);
    private sealed record ReturnData(string Number, string Type, long Subtotal, long Tax, long Total,
        long StockSubtotal, long NonStockSubtotal, long StockCost, DateOnly ReturnDate);
}
