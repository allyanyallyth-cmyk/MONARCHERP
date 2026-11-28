namespace Monarch.Core.Accounting;

public enum AccountType { Asset, Liability, Equity, Revenue, Expense }
public enum NormalSide { Debit, Credit }
public enum JournalStatus { Draft, Posted, Reversed }

/// <summary>All ledger amounts are stored as integer minor units (e.g. halalas), never binary floating point.</summary>
public sealed record Account(
    Guid Id,
    string Code,
    string Name,
    AccountType Type,
    bool IsActive = true)
{
    public NormalSide NormalSide => Type is AccountType.Asset or AccountType.Expense
        ? NormalSide.Debit : NormalSide.Credit;
}

public sealed record JournalLine(Guid AccountId, long DebitMinor, long CreditMinor)
{
    public static JournalLine Debit(Guid accountId, long amountMinor) => new(accountId, amountMinor, 0);
    public static JournalLine Credit(Guid accountId, long amountMinor) => new(accountId, 0, amountMinor);
}

public sealed record JournalEntry(
    Guid Id,
    string Number,
    DateOnly Date,
    string Description,
    JournalStatus Status,
    IReadOnlyList<JournalLine> Lines,
    Guid? ReversalOfId = null,
    DateTimeOffset? PostedAt = null);

public sealed record AccountBalance(Guid AccountId, string Code, string Name, AccountType Type,
    long DebitMinor, long CreditMinor, long NetDebitMinor, long NetCreditMinor);

public sealed record TrialBalance(DateOnly From, DateOnly To, IReadOnlyList<AccountBalance> Accounts,
    long TotalDebitMinor, long TotalCreditMinor)
{
    public bool IsBalanced => TotalDebitMinor == TotalCreditMinor;
}

public sealed record IncomeStatement(DateOnly From, DateOnly To, long RevenueMinor,
    long ExpenseMinor, long NetIncomeMinor);

public sealed record BalanceSheet(DateOnly AsOf, long AssetsMinor, long LiabilitiesMinor,
    long EquityMinor, long CurrentEarningsMinor)
{
    public long LiabilitiesAndEquityMinor => checked(LiabilitiesMinor + EquityMinor + CurrentEarningsMinor);
    public bool IsBalanced => AssetsMinor == LiabilitiesAndEquityMinor;
}
