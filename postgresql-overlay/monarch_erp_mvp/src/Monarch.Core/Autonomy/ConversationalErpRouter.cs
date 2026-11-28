using System;
using System.Collections.Generic;

namespace Monarch.Core.Autonomy;

public sealed record ErpUserScope(Guid UserId, Guid CompanyId, Guid BranchId);
public sealed record ErpQueryRequest(string IntentCode, IReadOnlyList<string> Parameters, Guid CompanyId, Guid BranchId, string RequiredPermission);
public sealed record ErpQueryResult(string IntentCode, string Status, string DisplayTitle, IReadOnlyList<IReadOnlyDictionary<string, object>> Rows, string? Message);

/// <summary>Implementations must re-check permissions in the same DB transaction as the tenant-filtered query.</summary>
public interface IAuthorizedErpQueryExecutor
{
    ErpQueryResult Execute(ErpUserScope scope, ErpQueryRequest request);
}

public sealed class ConversationalErpRouter
{
    private readonly AutonomousAgentEngine _parser;
    private readonly IAuthorizedErpQueryExecutor _executor;
    public ConversationalErpRouter(AutonomousAgentEngine parser, IAuthorizedErpQueryExecutor executor)
    { _parser = parser ?? throw new ArgumentNullException(nameof(parser)); _executor = executor ?? throw new ArgumentNullException(nameof(executor)); }

    public ErpQueryResult Route(string utterance, ErpUserScope scope)
    {
        if (scope == null || scope.UserId == Guid.Empty || scope.CompanyId == Guid.Empty || scope.BranchId == Guid.Empty)
            throw new UnauthorizedAccessException("An authenticated user, company and branch scope are required.");
        var intent = _parser.ParseIntent(utterance);
        var permission = intent.IntentCode switch
        {
            "SalesReport" => "reports.read",
            "ZatcaPendingInvoices" => "reports.read",
            "InventoryReorderPreview" => "reports.read",
            "MonthCloseAssessment" => "reports.read",
            _ => throw new NotSupportedException("The requested natural-language intent is not supported.")
        };
        // No SQL is generated from user text. Only allowlisted intent codes and parsed parameters reach the executor.
        var request = new ErpQueryRequest(intent.IntentCode, intent.Parameters, scope.CompanyId, scope.BranchId, permission);
        return _executor.Execute(scope, request);
    }
}
