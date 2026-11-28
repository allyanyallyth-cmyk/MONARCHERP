using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Monarch.Core.Abstractions;

public sealed record TenantCompany(Guid CompanyId, string CompanyCode, string LegalName);
public sealed record TenantBranch(Guid BranchId, string BranchCode, string BranchName);

/// <summary>
/// Reads tenant choices from the authenticated central API. Results are discovery hints;
/// the server revalidates membership and permissions on every protected business operation.
/// </summary>
public interface ITenantDiscoveryClient
{
    Task<IReadOnlyList<TenantCompany>> ListCompaniesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TenantBranch>> ListBranchesAsync(Guid companyId, CancellationToken cancellationToken = default);
}