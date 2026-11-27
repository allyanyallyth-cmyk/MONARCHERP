using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Monarch.Api.Services;
using Monarch.Core.Abstractions;

namespace Monarch.Api.Endpoints;

/// <summary>
/// Versioned General Ledger HTTP surface. Company and branch headers are selections only;
/// PostgreSQL validates both against the authenticated OIDC subject and active RBAC grants.
/// </summary>
public static class GeneralLedgerEndpoints
{
    public static IEndpointRouteBuilder MapGeneralLedgerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/general-ledger").RequireAuthorization();

        group.MapGet("/accounts", async (HttpContext http, ClaimsPrincipal principal,
            [FromServices] GeneralLedgerApplicationService service, bool? includeInactive,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetTenantSelection(http, out var companyId, out var branchId))
                return MissingTenantSelection();
            return await ExecuteAsync(() => service.ListAccountsAsync(
                principal, companyId, branchId, includeInactive ?? false, cancellationToken));
        });

        group.MapPost("/accounts", async (HttpContext http, CreateAccountRequest request,
            ClaimsPrincipal principal, [FromServices] GeneralLedgerApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetTenantSelection(http, out var companyId, out var branchId))
                return MissingTenantSelection();
            var account = new GeneralLedgerAccount(request.AccountId, request.AccountCode, request.AccountName,
                request.AccountType, request.NormalSide);
            return await ExecuteAsync(async () =>
            {
                await service.CreateAccountAsync(principal, companyId, branchId, account, cancellationToken);
                return Results.Created($"/api/v1/general-ledger/accounts/{account.AccountId:D}",
                    new { account.AccountId });
            });
        });

        group.MapGet("/journals/{journalId:guid}", async (HttpContext http, Guid journalId,
            ClaimsPrincipal principal, [FromServices] GeneralLedgerApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetTenantSelection(http, out var companyId, out var branchId))
                return MissingTenantSelection();
            return await ExecuteAsync(() => service.GetJournalAsync(
                principal, companyId, branchId, journalId, cancellationToken));
        });

        group.MapPost("/journals", async (HttpContext http, CreateJournalDraftRequest request,
            ClaimsPrincipal principal, [FromServices] GeneralLedgerApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetTenantSelection(http, out var companyId, out var branchId))
                return MissingTenantSelection();
            if (request.Lines is null || request.Lines.Count < 2)
                return Results.BadRequest(new { error = "At least two journal lines are required." });
            return await ExecuteAsync(() => service.CreateDraftAsync(principal, companyId, branchId,
                request.JournalId, request.FiscalYear, request.JournalDate, request.Description, request.CurrencyCode,
                request.Lines.Select(line => new GeneralLedgerLineInput(
                    line.AccountId, line.DebitMinor, line.CreditMinor, line.Description)).ToArray(),
                request.ReversesJournalId, cancellationToken));
        });

        group.MapPost("/journals/{journalId:guid}/post", async (HttpContext http, Guid journalId,
            ClaimsPrincipal principal, [FromServices] GeneralLedgerApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetTenantSelection(http, out var companyId, out var branchId))
                return MissingTenantSelection();
            return await ExecuteAsync(async () =>
            {
                await service.PostAsync(principal, companyId, branchId, journalId, cancellationToken);
                return Results.NoContent();
            });
        });

        group.MapGet("/reports/trial-balance", async (HttpContext http, DateOnly asOfDate,
            string currencyCode, ClaimsPrincipal principal,
            [FromServices] GeneralLedgerApplicationService service, CancellationToken cancellationToken) =>
        {
            if (!TryGetTenantSelection(http, out var companyId, out var branchId))
                return MissingTenantSelection();
            return await ExecuteAsync(() => service.GetTrialBalanceAsync(
                principal, companyId, branchId, asOfDate, currencyCode, cancellationToken));
        });

        return endpoints;
    }

    private static bool TryGetTenantSelection(HttpContext http, out Guid companyId, out Guid branchId)
    {
        companyId = Guid.Empty;
        branchId = Guid.Empty;
        var companies = http.Request.Headers[GeneralLedgerApplicationService.CompanyHeader];
        var branches = http.Request.Headers[GeneralLedgerApplicationService.BranchHeader];
        return companies.Count == 1 && branches.Count == 1
            && Guid.TryParseExact(companies[0], "D", out companyId) && companyId != Guid.Empty
            && Guid.TryParseExact(branches[0], "D", out branchId) && branchId != Guid.Empty;
    }

    private static IResult MissingTenantSelection() =>
        Results.BadRequest(new
        {
            error = "Exactly one valid X-Monarch-Company-Id and X-Monarch-Branch-Id header is required."
        });

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var value = await action().ConfigureAwait(false);
            if (value is IResult result) return result;
            return value is null ? Results.NotFound() : Results.Ok(value);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden",
                detail: "The authenticated OIDC identity is not authorized for the selected company, branch or operation.");
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request",
                detail: exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Operation conflict",
                detail: exception.Message);
        }
    }

    public sealed record CreateAccountRequest(
        Guid AccountId, string AccountCode, string AccountName, string AccountType, string NormalSide);

    public sealed record CreateJournalDraftRequest(
        Guid JournalId, int FiscalYear, DateOnly JournalDate, string Description, string CurrencyCode,
        IReadOnlyList<JournalLineRequest> Lines, Guid? ReversesJournalId = null);

    public sealed record JournalLineRequest(
        Guid AccountId, long DebitMinor, long CreditMinor, string? Description = null);
}
