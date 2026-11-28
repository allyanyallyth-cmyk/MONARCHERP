using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Monarch.Api.Services;

namespace Monarch.Api.Endpoints;

/// <summary>Authenticated OIDC company and branch discovery.</summary>
public static class TenantDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapTenantDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenant-context").RequireAuthorization();

        group.MapGet("/companies", async (
            ClaimsPrincipal principal,
            [FromServices] TenantDiscoveryApplicationService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.ListCompaniesAsync(principal, cancellationToken)));

        group.MapGet("/companies/{companyId:guid}/branches", async (
            Guid companyId,
            ClaimsPrincipal principal,
            [FromServices] TenantDiscoveryApplicationService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.ListBranchesAsync(principal, companyId, cancellationToken)));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action().ConfigureAwait(false));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden",
                detail: "The authenticated OIDC identity has no active access to the requested tenant context.");
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request",
                detail: exception.Message);
        }
    }
}
