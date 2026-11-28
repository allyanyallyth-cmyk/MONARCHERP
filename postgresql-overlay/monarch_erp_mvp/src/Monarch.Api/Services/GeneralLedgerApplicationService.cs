using System.Security.Claims;
using Monarch.Core.Abstractions;

namespace Monarch.Api.Services;

/// <summary>
/// Authenticated application boundary for the PostgreSQL general ledger.
/// OIDC issuer and subject are taken from the single validated identity.
/// Company and branch are untrusted selections; PostgreSQL verifies their membership
/// and the required permission in the same transaction as the protected operation.
/// </summary>
public sealed class GeneralLedgerApplicationService(IGeneralLedgerStore store)
{
    public const string CompanyHeader = "X-Monarch-Company-Id";
    public const string BranchHeader = "X-Monarch-Branch-Id";
    public const string IssuerClaim = "iss";
    public const string SubjectClaim = "sub";

    public Task CreateAccountAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, GeneralLedgerAccount account,
        CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, companyId, branchId, "gl.accounts.manage");
        ArgumentNullException.ThrowIfNull(account);
        return store.CreateAccountAsync(tenant, account, cancellationToken);
    }

    public Task<IReadOnlyList<GeneralLedgerAccount>> ListAccountsAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, companyId, branchId, "gl.accounts.read");
        return store.GetAccountsAsync(tenant, includeInactive, cancellationToken);
    }

    public Task<IReadOnlyList<GeneralLedgerJournalSummary>> ListJournalsAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, int limit = 250,
        CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, companyId, branchId, "gl.journal.read");
        return store.GetJournalsAsync(tenant, limit, cancellationToken);
    }

    public Task<GeneralLedgerJournalDetail?> GetJournalAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, Guid journalId,
        CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, companyId, branchId, "gl.journal.read");
        if (journalId == Guid.Empty) throw new ArgumentException("Journal ID is required.", nameof(journalId));
        return store.GetJournalAsync(tenant, journalId, cancellationToken);
    }

    public Task<GeneralLedgerJournal> CreateDraftAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, Guid journalId, int fiscalYear,
        DateOnly journalDate, string description, string currencyCode,
        IReadOnlyCollection<GeneralLedgerLineInput> lines, Guid? reversesJournalId = null,
        CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, companyId, branchId, "gl.journal.create");
        return store.CreateDraftAsync(tenant, journalId, fiscalYear, journalDate, description,
            currencyCode, lines, reversesJournalId, cancellationToken);
    }

    public Task PostAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, Guid journalId,
        CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, companyId, branchId, "gl.journal.post");
        if (journalId == Guid.Empty) throw new ArgumentException("Journal ID is required.", nameof(journalId));
        return store.PostAsync(tenant, journalId, cancellationToken);
    }

    public Task<IReadOnlyList<GeneralLedgerTrialBalanceRow>> GetTrialBalanceAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, DateOnly asOfDate,
        string currencyCode, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, companyId, branchId, "gl.report.trial_balance");
        return store.GetTrialBalanceAsync(tenant, asOfDate, currencyCode, cancellationToken);
    }

    private static TenantScope RequireTenant(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (companyId == Guid.Empty || branchId == Guid.Empty)
            throw new UnauthorizedAccessException("A valid company and branch selection is required.");

        var authenticated = principal.Identities.Where(identity => identity.IsAuthenticated).ToArray();
        if (authenticated.Length != 1)
            throw new UnauthorizedAccessException("Exactly one authenticated identity is required.");

        var identity = authenticated[0];
        var issuer = RequiredSingleClaim(identity, IssuerClaim);
        var subject = RequiredSingleClaim(identity, SubjectClaim);

        // No tenant, internal user or permission claim is trusted from the access token.
        // PostgreSQL resolves the internal user and evaluates current RBAC inside the
        // same transaction as the requested ledger operation.
        return new TenantScope(companyId, branchId, Guid.Empty, issuer, subject, permission);
    }

    private static string RequiredSingleClaim(ClaimsIdentity identity, string claimType)
    {
        var values = identity.Claims.Where(claim => claim.Type == claimType)
            .Select(claim => claim.Value).ToArray();
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0].Length > 512)
            throw new UnauthorizedAccessException("The validated OIDC identity has missing or ambiguous issuer/subject claims.");
        return values[0];
    }
}
