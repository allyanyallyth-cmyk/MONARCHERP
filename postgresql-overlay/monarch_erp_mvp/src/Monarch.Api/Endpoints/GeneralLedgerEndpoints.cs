using System.Security.Claims;
using Monarch.Api.Services;
using Monarch.Core.Abstractions;

namespace Monarch.Api.Endpoints;

/// <summary>
/// Versioned, authenticated HTTP surface for the PostgreSQL general ledger.
/// Tenant scope and operation permissions are revalidated by the application service.
/// </summary>
public static class GeneralLedgerEndpoints
{
    public static IEndpointRouteBuilder MapGeneralLedgerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/general-ledger").RequireAuthorization();

        group.MapGet("/accounts", async (ClaimsPrincipal principal, GeneralLedgerApplicationService service,
            bool includeInactive, CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.ListAccountsAsync(principal, includeInactive, cancellationToken)));

        group.MapPost("/accounts", async (CreateAccountRequest request, ClaimsPrincipal principal,
            GeneralLedgerApplicationService service, CancellationToken cancellationToken) =>
        {
            var account = new GeneralLedgerAccount(request.AccountId, request.AccountCode, request.AccountName,
                request.AccountType, request.NormalSide);
            return await ExecuteAsync(async () =>
            {
                await service.CreateAccountAsync(principal, account, cancellationToken);
                return Results.Created($"/api/v1/general-ledger/accounts/{account.AccountId:D}",
                    new { account.AccountId });
            });
        });

        group.MapGet("/journals/{journalId:guid}", async (Guid journalId, ClaimsPrincipal principal,
            GeneralLedgerApplicationService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.GetJournalAsync(principal, journalId, cancellationToken)));

        group.MapPost("/journals", async (CreateJournalDraftRequest request, ClaimsPrincipal principal,
            GeneralLedgerApplicationService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.CreateDraftAsync(principal, request.JournalId, request.FiscalYear,
                request.JournalDate, request.Description, request.CurrencyCode,
                request.Lines.Select(line => new GeneralLedgerLineInput(
                    line.AccountId, line.DebitMinor, line.CreditMinor, line.Description)).ToArray(),
                request.ReversesJournalId, cancellationToken)));

        group.MapPost("/journals/{journalId:guid}/post", async (Guid journalId, ClaimsPrincipal principal,
            GeneralLedgerApplicationService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                await service.PostAsync(principal, journalId, cancellationToken);
                return Results.NoContent();
            }));

        group.MapGet("/reports/trial-balance", async (DateOnly asOfDate, string currencyCode,
            ClaimsPrincipal principal, GeneralLedgerApplicationService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.GetTrialBalanceAsync(principal, asOfDate, currencyCode, cancellationToken)));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var value = await action().ConfigureAwait(false);
            return value is null ? Results.NotFound() : Results.Ok(value);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden",
                detail: "The authenticated identity does not have permission for this operation.");
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
