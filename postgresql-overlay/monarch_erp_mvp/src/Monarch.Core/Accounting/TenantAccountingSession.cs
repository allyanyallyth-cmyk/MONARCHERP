using Monarch.Core.Organizations;
using Monarch.Core.Security;

namespace Monarch.Core.Accounting;

/// <summary>
/// Explicit, authenticated company/branch context for desktop workflows. It deliberately exposes
/// only tenant-scoped ledger operations; callers cannot fall back to the legacy single-company store.
/// </summary>
public sealed class TenantAccountingSession
{
    private readonly TenantLedgerStore _ledger;
    private readonly Guid _userId;

    public Guid CompanyId { get; }
    public Guid BranchId { get; }
    public string CompanyName { get; }
    public string BranchName { get; }
    public string BaseCurrencyCode { get; }
    public int CurrencyExponent { get; }
    public string CompanyRoleCode { get; }

    private TenantAccountingSession(TenantLedgerStore ledger, Guid userId, CompanySummary company, BranchSummary branch, int currencyExponent, string companyRoleCode)
    {
        _ledger = ledger;
        _userId = userId;
        CompanyId = company.Id;
        BranchId = branch.Id;
        CompanyName = company.DisplayName;
        BranchName = branch.Name;
        BaseCurrencyCode = company.BaseCurrencyCode;
        CurrencyExponent = currencyExponent;
        CompanyRoleCode = companyRoleCode;
    }

    public static IReadOnlyList<CompanyAccessSummary> GetAvailableCompanies(CompanyDirectoryStore directory, UserSession session)
    {
        if (directory == null) throw new ArgumentNullException(nameof(directory));
        if (session == null) throw new UnauthorizedAccessException("An authenticated session is required.");
        return directory.GetUserCompanies(session.UserId)
            .Where(x => x.IsActive)
            .ToList().AsReadOnly();
    }

    public static IReadOnlyList<BranchSummary> GetAvailableBranches(CompanyDirectoryStore directory, UserSession session, Guid companyId)
    {
        if (directory == null) throw new ArgumentNullException(nameof(directory));
        if (session == null) throw new UnauthorizedAccessException("An authenticated session is required.");
        return directory.GetAccessibleBranches(session.UserId, companyId);
    }

    public static TenantAccountingSession Open(CompanyDirectoryStore directory, TenantLedgerStore ledger,
        UserSession session, Guid companyId, Guid branchId)
    {
        if (directory == null) throw new ArgumentNullException(nameof(directory));
        if (ledger == null) throw new ArgumentNullException(nameof(ledger));
        if (session == null) throw new UnauthorizedAccessException("An authenticated session is required.");

        var companyAccess = directory.GetUserCompanies(session.UserId)
            .FirstOrDefault(x => x.CompanyId == companyId && x.IsActive);
        if (companyAccess == null)
            throw new UnauthorizedAccessException("The user has no active access to the selected company.");

        var company = directory.GetCompanies().FirstOrDefault(x => x.Id == companyId && x.IsActive);
        if (company == null) throw new KeyNotFoundException("The selected active company was not found.");

        var branch = directory.GetAccessibleBranches(session.UserId, companyId)
            .FirstOrDefault(x => x.Id == branchId && x.IsActive);
        if (branch == null)
            throw new UnauthorizedAccessException("The user has no active access to the selected branch.");

        // Force an authorization-checked read before returning a usable context.
        ledger.GetEntries(session.UserId, companyId, branchId);
        var currency = directory.GetCompanyBaseCurrency(companyId);
        var exponent = directory.GetCurrencyExponent(currency);
        return new TenantAccountingSession(ledger, session.UserId, company, branch, exponent, companyAccess.RoleCode);
    }

    public IReadOnlyCollection<Account> GetAccounts() => _ledger.GetAccounts(_userId, CompanyId, BranchId);
    public IReadOnlyCollection<JournalEntry> GetEntries() => _ledger.GetEntries(_userId, CompanyId, BranchId);
    public TrialBalance GetTrialBalance(DateOnly from, DateOnly to) => _ledger.GetTrialBalance(_userId, CompanyId, BranchId, from, to);
    public IncomeStatement GetIncomeStatement(DateOnly from, DateOnly to) => _ledger.GetIncomeStatement(_userId, CompanyId, BranchId, from, to);
    public BalanceSheet GetBalanceSheet(DateOnly asOf) => _ledger.GetBalanceSheet(_userId, CompanyId, BranchId, asOf);
    public void AddAccount(Account account) => _ledger.AddAccount(_userId, CompanyId, BranchId, account);
    public JournalEntry CreateDraft(string number, DateOnly date, string description, IEnumerable<JournalLine> lines)
        => _ledger.CreateDraft(_userId, CompanyId, BranchId, number, date, description, lines);
    public JournalEntry Post(Guid journalId) => _ledger.Post(_userId, CompanyId, BranchId, journalId);
    public JournalEntry Reverse(Guid journalId, string reversalNumber, DateOnly date, string reason)
        => _ledger.Reverse(_userId, CompanyId, BranchId, journalId, reversalNumber, date, reason);
}
