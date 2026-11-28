namespace Monarch.Core.Accounting;

/// <summary>
/// Deterministic in-memory double-entry ledger domain service.
/// Persistence, authorization, numbering policy and database transactions are supplied by infrastructure layers.
/// </summary>
public sealed class LedgerBook
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Account> _accounts = new();
    private readonly Dictionary<Guid, JournalEntry> _entries = new();

    public IReadOnlyCollection<Account> Accounts { get { lock (_gate) return _accounts.Values.ToArray(); } }

    /// <summary>Rehydrates a ledger from trusted persisted records. Used by the persistence adapter.</summary>
    public static LedgerBook Restore(IEnumerable<Account> accounts, IEnumerable<JournalEntry> entries)
    {
        if (accounts is null) throw new ArgumentNullException(nameof(accounts));
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        var book = new LedgerBook();
        foreach (var account in accounts) book.AddAccount(account);
        foreach (var entry in entries)
        {
            if (entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.Number) || string.IsNullOrWhiteSpace(entry.Description))
                throw new InvalidOperationException("Persisted journal metadata is invalid.");
            if (book._entries.Values.Any(e => string.Equals(e.Number, entry.Number, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Persisted journal number is duplicated.");
            book.ValidateLines(entry.Lines, requireBalanced: entry.Status is JournalStatus.Posted or JournalStatus.Reversed);
            book._entries.Add(entry.Id, entry with { Lines = Array.AsReadOnly(entry.Lines.ToArray()) });
        }
        foreach (var entry in book._entries.Values.Where(e => e.ReversalOfId is not null))
        {
            var original = book.GetEntry(entry.ReversalOfId!.Value);
            if (entry.Status != JournalStatus.Draft) ValidateExactReversal(entry.Lines, original.Lines);
        }
        return book;
    }
    public IReadOnlyCollection<JournalEntry> Entries { get { lock (_gate) return _entries.Values.ToArray(); } }

    public void AddAccount(Account account)
    {
        if (account is null) throw new ArgumentNullException(nameof(account));
        if (account.Id == Guid.Empty) throw new ArgumentException("Account ID is required.");
        if (string.IsNullOrWhiteSpace(account.Code) || string.IsNullOrWhiteSpace(account.Name))
            throw new ArgumentException("Account code and name are required.");
        lock (_gate)
        {
            if (_accounts.ContainsKey(account.Id)) throw new InvalidOperationException("Duplicate account ID.");
            if (_accounts.Values.Any(a => string.Equals(a.Code, account.Code, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Duplicate account code.");
            _accounts.Add(account.Id, account);
        }
    }

    public JournalEntry CreateDraft(string number, DateOnly date, string description, IEnumerable<JournalLine> lines)
    {
        if (string.IsNullOrWhiteSpace(number)) throw new ArgumentException("Entry number is required.");
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Description is required.");
        if (lines is null) throw new ArgumentNullException(nameof(lines));
        var copiedLines = lines.ToArray();
        lock (_gate)
        {
            if (_entries.Values.Any(e => string.Equals(e.Number, number, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Duplicate journal number.");
            ValidateLines(copiedLines, requireBalanced: false);
            var entry = new JournalEntry(Guid.NewGuid(), number.Trim(), date, description.Trim(),
                JournalStatus.Draft, Array.AsReadOnly(copiedLines));
            _entries.Add(entry.Id, entry);
            return entry;
        }
    }

    public JournalEntry Post(Guid entryId, DateTimeOffset? postedAt = null)
    {
        lock (_gate)
        {
            var entry = GetEntry(entryId);
            if (entry.Status != JournalStatus.Draft) throw new InvalidOperationException("Only a draft can be posted.");
            ValidateLines(entry.Lines, requireBalanced: true);
            if (entry.ReversalOfId is Guid originalId)
            {
                var original = GetEntry(originalId);
                if (original.Status != JournalStatus.Posted)
                    throw new InvalidOperationException("The original entry must still be posted.");
                ValidateExactReversal(entry.Lines, original.Lines);
            }
            var posted = entry with { Status = JournalStatus.Posted, PostedAt = postedAt ?? DateTimeOffset.UtcNow };
            _entries[entryId] = posted;
            if (entry.ReversalOfId is Guid reversedId)
                _entries[reversedId] = GetEntry(reversedId) with { Status = JournalStatus.Reversed };
            return posted;
        }
    }

    public JournalEntry Reverse(Guid originalEntryId, string reversalNumber, DateOnly reversalDate,
        string reason, DateTimeOffset? postedAt = null)
    {
        lock (_gate)
        {
            var original = GetEntry(originalEntryId);
            if (original.Status != JournalStatus.Posted) throw new InvalidOperationException("Only a posted entry can be reversed.");
            if (_entries.Values.Any(e => e.ReversalOfId == originalEntryId))
                throw new InvalidOperationException("This entry already has a reversal.");
            if (string.IsNullOrWhiteSpace(reversalNumber) || string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Reversal number and reason are required.");
            var inverseLines = original.Lines.Select(l => new JournalLine(l.AccountId, l.CreditMinor, l.DebitMinor)).ToArray();
            var reversal = new JournalEntry(Guid.NewGuid(), reversalNumber.Trim(), reversalDate,
                $"Reversal of {original.Number}: {reason.Trim()}", JournalStatus.Draft,
                Array.AsReadOnly(inverseLines), originalEntryId);
            if (_entries.Values.Any(e => string.Equals(e.Number, reversal.Number, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Duplicate journal number.");
            _entries.Add(reversal.Id, reversal);
            return Post(reversal.Id, postedAt);
        }
    }

    public TrialBalance GetTrialBalance(DateOnly from, DateOnly to)
    {
        if (from > to) throw new ArgumentException("Start date must not be after end date.");
        lock (_gate)
        {
            var posted = _entries.Values.Where(e => e.Status is JournalStatus.Posted or JournalStatus.Reversed)
                .Where(e => e.Date >= from && e.Date <= to).ToArray();
            var rows = new List<AccountBalance>();
            foreach (var account in _accounts.Values.OrderBy(a => a.Code, StringComparer.Ordinal))
            {
                var debit = Sum(posted.SelectMany(e => e.Lines).Where(l => l.AccountId == account.Id).Select(l => l.DebitMinor));
                var credit = Sum(posted.SelectMany(e => e.Lines).Where(l => l.AccountId == account.Id).Select(l => l.CreditMinor));
                if (debit == 0 && credit == 0) continue;
                var net = checked(debit - credit);
                rows.Add(new AccountBalance(account.Id, account.Code, account.Name, account.Type,
                    debit, credit, Math.Max(net, 0), Math.Max(-net, 0)));
            }
            return new TrialBalance(from, to, rows, Sum(rows.Select(r => r.DebitMinor)), Sum(rows.Select(r => r.CreditMinor)));
        }
    }

    public IncomeStatement GetIncomeStatement(DateOnly from, DateOnly to)
    {
        var trial = GetTrialBalance(from, to);
        var revenue = Sum(trial.Accounts.Where(a => a.Type == AccountType.Revenue).Select(a => a.NetCreditMinor - a.NetDebitMinor));
        var expenses = Sum(trial.Accounts.Where(a => a.Type == AccountType.Expense).Select(a => a.NetDebitMinor - a.NetCreditMinor));
        return new IncomeStatement(from, to, revenue, expenses, checked(revenue - expenses));
    }

    public BalanceSheet GetBalanceSheet(DateOnly asOf)
    {
        var trial = GetTrialBalance(DateOnly.MinValue, asOf);
        var assets = Sum(trial.Accounts.Where(a => a.Type == AccountType.Asset).Select(a => a.NetDebitMinor - a.NetCreditMinor));
        var liabilities = Sum(trial.Accounts.Where(a => a.Type == AccountType.Liability).Select(a => a.NetCreditMinor - a.NetDebitMinor));
        var equity = Sum(trial.Accounts.Where(a => a.Type == AccountType.Equity).Select(a => a.NetCreditMinor - a.NetDebitMinor));
        var earnings = GetIncomeStatement(DateOnly.MinValue, asOf).NetIncomeMinor;
        return new BalanceSheet(asOf, assets, liabilities, equity, earnings);
    }

    private void ValidateLines(IReadOnlyList<JournalLine> lines, bool requireBalanced)
    {
        if (lines.Count < 2) throw new InvalidOperationException("A journal requires at least two lines.");
        foreach (var line in lines)
        {
            if (!_accounts.TryGetValue(line.AccountId, out var account)) throw new InvalidOperationException("Unknown account.");
            if (!account.IsActive) throw new InvalidOperationException($"Account {account.Code} is inactive.");
            if (line.DebitMinor < 0 || line.CreditMinor < 0 || (line.DebitMinor > 0 && line.CreditMinor > 0)
                || (line.DebitMinor == 0 && line.CreditMinor == 0))
                throw new InvalidOperationException("Each line must contain one positive debit or credit amount.");
        }
        if (requireBalanced && Sum(lines.Select(l => l.DebitMinor)) != Sum(lines.Select(l => l.CreditMinor)))
            throw new InvalidOperationException("Journal is not balanced.");
    }

    private static void ValidateExactReversal(IReadOnlyList<JournalLine> reversal, IReadOnlyList<JournalLine> original)
    {
        static Dictionary<Guid, (long Debit, long Credit)> Aggregate(IEnumerable<JournalLine> lines)
        {
            var result = new Dictionary<Guid, (long Debit, long Credit)>();
            foreach (var line in lines)
            {
                result.TryGetValue(line.AccountId, out var old);
                result[line.AccountId] = (checked(old.Debit + line.DebitMinor), checked(old.Credit + line.CreditMinor));
            }
            return result;
        }
        var expected = Aggregate(original).ToDictionary(p => p.Key, p => (Debit: p.Value.Credit, Credit: p.Value.Debit));
        var actual = Aggregate(reversal);
        if (expected.Count != actual.Count || expected.Any(p => !actual.TryGetValue(p.Key, out var a)
            || a.Debit != p.Value.Debit || a.Credit != p.Value.Credit))
            throw new InvalidOperationException("Reversal must exactly invert the original by account.");
    }

    private JournalEntry GetEntry(Guid id) => _entries.TryGetValue(id, out var entry)
        ? entry : throw new KeyNotFoundException("Journal entry not found.");

    private static long Sum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (var value in values) total = checked(total + value);
        return total;
    }
}
