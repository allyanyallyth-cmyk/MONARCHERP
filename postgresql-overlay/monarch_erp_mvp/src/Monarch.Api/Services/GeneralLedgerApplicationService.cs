using System.Security.Claims;
using Monarch.Core.Abstractions;

namespace Monarch.Api.Services;

/// <summary>
/// Authenticated application boundary for the PostgreSQL general ledger.
/// The host must validate the token signature, issuer, audience and lifetime before this
/// service is called. Tenant scope and permissions are read only from one authenticated
/// identity; no caller-supplied tenant identifiers are accepted.
/// </summary>
public sealed class GeneralLedgerApplicationService(IGeneralLedgerStore store)
{
    public const string CompanyClaim = "monarch:company_id";
    public const string BranchClaim = "monarch:branch_id";
    public const string UserClaim = "monarch:user_id";
    public const string PermissionClaim = "monarch:permission";

    public Task CreateAccountAsync(
        ClaimsPrincipal principal, GeneralLedgerAccount account,
        CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, "gl.accounts.manage");
        ArgumentNullException.ThrowIfNull(account);
        return store.CreateAccountAsync(tenant, account, cancellationToken);
    }

    public Task<IReadOnlyList<GeneralLedgerAccount>> ListAccountsAsync(
        ClaimsPrincipal principal, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, "gl.accounts.read");
        return store.GetAccountsAsync(tenant, includeInactive, cancellationToken);
    }

    public Task<GeneralLedgerJournalDetail?> GetJournalAsync(
        ClaimsPrincipal principal, Guid journalId, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, "gl.journal.read");
        if (journalId == Guid.Empty) throw new ArgumentException("Journal ID is required.", nameof(journalId));
        return store.GetJournalAsync(tenant, journalId, cancellationToken);
    }

    public Task<GeneralLedgerJournal> CreateDraftAsync(
        ClaimsPrincipal principal, Guid journalId, int fiscalYear, DateOnly journalDate,
        string description, string currencyCode, IReadOnlyCollection<GeneralLedgerLineInput> lines,
        Guid? reversesJournalId = null, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, "gl.journal.create");
        return store.CreateDraftAsync(tenant, journalId, fiscalYear, journalDate, description,
            currencyCode, lines, reversesJournalId, cancellationToken);
    }

    public Task PostAsync(
        ClaimsPrincipal principal, Guid journalId, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, "gl.journal.post");
        if (journalId == Guid.Empty) throw new ArgumentException("Journal ID is required.", nameof(journalId));
        return store.PostAsync(tenant, journalId, cancellationToken);
    }

    public Task<IReadOnlyList<GeneralLedgerTrialBalanceRow>> GetTrialBalanceAsync(
        ClaimsPrincipal principal, DateOnly asOfDate, string currencyCode,
        CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant(principal, "gl.report.trial_balance");
        return store.GetTrialBalanceAsync(tenant, asOfDate, currencyCode, cancellationToken);
    }

    private static TenantScope RequireTenant(ClaimsPrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // ClaimsPrincipal.Claims aggregates claims from every identity. Never allow an
        // unauthenticated secondary identity to inject tenant or permission claims.
        var authenticated = principal.Identities.Where(identity => identity.IsAuthenticated).ToArray();
        if (authenticated.Length != 1)
            throw new UnauthorizedAccessException("Exactly one authenticated identity is required.");

        var identity = authenticated[0];
        var companyId = RequiredGuid(identity, CompanyClaim);
        var branchId = RequiredGuid(identity, BranchClaim);
        var userId = RequiredGuid(identity, UserClaim);
        if (!identity.Claims.Any(c => c.Type == PermissionClaim &&
            string.Equals(c.Value, permission, StringComparison.Ordinal)))
            throw new UnauthorizedAccessException("The authenticated identity lacks the required permission.");

        return new TenantScope(companyId, branchId, userId);
    }

    private static Guid RequiredGuid(ClaimsIdentity identity, string claimType)
    {
        var values = identity.Claims.Where(c => c.Type == claimType).Select(c => c.Value).ToArray();
        if (values.Length != 1 || !Guid.TryParseExact(values[0], "D", out var value) || value == Guid.Empty)
            throw new UnauthorizedAccessException("The authenticated identity has missing or ambiguous tenant claims.");
        return value;
    }
}
