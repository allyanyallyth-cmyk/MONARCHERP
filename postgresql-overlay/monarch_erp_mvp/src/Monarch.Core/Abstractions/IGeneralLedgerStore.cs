namespace Monarch.Core.Abstractions;

public sealed record TenantScope(
    Guid CompanyId,
    Guid BranchId,
    Guid UserId,
    string? IdentityIssuer = null,
    string? IdentitySubject = null,
    string? RequiredPermission = null);
public sealed record GeneralLedgerAccount(Guid AccountId, string AccountCode, string AccountName, string AccountType, string NormalSide);
public sealed record GeneralLedgerLineInput(Guid AccountId, long DebitMinor, long CreditMinor, string? Description = null);
public sealed record GeneralLedgerJournal(Guid JournalId, long JournalNumber, int FiscalYear, DateOnly JournalDate, string CurrencyCode, string Status);
public sealed record GeneralLedgerTrialBalanceRow(Guid AccountId, string AccountCode, string AccountName, long DebitMinor, long CreditMinor);
public sealed record GeneralLedgerJournalLine(int LineNumber, Guid AccountId, string? Description, long DebitMinor, long CreditMinor);
public sealed record GeneralLedgerJournalDetail(
    GeneralLedgerJournal Journal,
    string Description,
    Guid CreatedBy,
    Guid? PostedBy,
    Guid? ReversesJournalId,
    IReadOnlyList<GeneralLedgerJournalLine> Lines);

public interface IGeneralLedgerStore
{
    Task CreateAccountAsync(TenantScope tenant, GeneralLedgerAccount account, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GeneralLedgerAccount>> GetAccountsAsync(TenantScope tenant, bool includeInactive = false,
        CancellationToken cancellationToken = default);
    Task<GeneralLedgerJournal> CreateDraftAsync(TenantScope tenant, Guid journalId, int fiscalYear,
        DateOnly journalDate, string description, string currencyCode,
        IReadOnlyCollection<GeneralLedgerLineInput> lines, Guid? reversesJournalId = null,
        CancellationToken cancellationToken = default);
    Task<GeneralLedgerJournalDetail?> GetJournalAsync(TenantScope tenant, Guid journalId,
        CancellationToken cancellationToken = default);
    Task PostAsync(TenantScope tenant, Guid journalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GeneralLedgerTrialBalanceRow>> GetTrialBalanceAsync(TenantScope tenant,
        DateOnly asOfDate, string currencyCode, CancellationToken cancellationToken = default);
}
