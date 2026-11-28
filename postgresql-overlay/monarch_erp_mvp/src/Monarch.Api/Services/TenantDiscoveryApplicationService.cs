using System.Security.Claims;
using Monarch.PostgreSql.Infrastructure;
using Npgsql;

namespace Monarch.Api.Services;

public sealed record TenantCompanySummary(Guid CompanyId, string CompanyCode, string LegalName);
public sealed record TenantBranchSummary(Guid BranchId, string BranchCode, string BranchName);

/// <summary>
/// Discovers only companies and branches linked to the single validated OIDC identity.
/// The database security-definer functions enforce active identity and membership;
/// no caller-supplied user ID or permission claim is accepted.
/// </summary>
public sealed class TenantDiscoveryApplicationService(PostgreSqlConnectionFactory factory)
{
    public async Task<IReadOnlyList<TenantCompanySummary>> ListCompaniesAsync(
        ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var (issuer, subject) = RequireIdentity(principal);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT company_id, company_code, legal_name FROM monarch_security.list_oidc_companies(@issuer,@subject)",
            connection);
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("subject", subject);
        var result = new List<TenantCompanySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new TenantCompanySummary(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public async Task<IReadOnlyList<TenantBranchSummary>> ListBranchesAsync(
        ClaimsPrincipal principal, Guid companyId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty)
            throw new ArgumentException("A valid company selection is required.", nameof(companyId));
        var (issuer, subject) = RequireIdentity(principal);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT branch_id, branch_code, branch_name FROM monarch_security.list_oidc_company_branches(@company,@issuer,@subject)",
            connection);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("subject", subject);
        var result = new List<TenantBranchSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new TenantBranchSummary(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    private static (string Issuer, string Subject) RequireIdentity(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identities = principal.Identities.Where(identity => identity.IsAuthenticated).ToArray();
        if (identities.Length != 1)
            throw new UnauthorizedAccessException("Exactly one authenticated OIDC identity is required.");
        return (RequiredSingleClaim(identities[0], "iss"), RequiredSingleClaim(identities[0], "sub"));
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
