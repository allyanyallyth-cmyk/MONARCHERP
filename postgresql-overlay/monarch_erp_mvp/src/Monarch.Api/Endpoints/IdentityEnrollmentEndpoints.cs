using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Monarch.Api.Security;
using Monarch.Api.Services;

namespace Monarch.Api.Endpoints;

public static class IdentityEnrollmentEndpoints
{
    public const string CompanyHeader = "X-Monarch-Company-Id";
    public const string BranchHeader = "X-Monarch-Branch-Id";

    public static void MapIdentityEnrollmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/security/identity-enrollments");

        group.MapPost("/invitations", async (
            HttpContext http, CreateIdentityEnrollmentRequest request,
            IdentityEnrollmentApplicationService service, CancellationToken cancellationToken) =>
        {
            if (!RequireHttps(http)) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "HTTPS is required.");
            if (!TryTenant(http, out var company, out var branch))
                return Results.BadRequest(new { error = "Valid X-Monarch-Company-Id and X-Monarch-Branch-Id headers are required." });
            try
            {
                var invitation = await service.CreateInvitationAsync(
                    http.User, company, branch, request.TargetUserId, request.Reason, cancellationToken);
                // The raw invitation secret and nonce are returned exactly once. Never log this response.
                return Results.Ok(invitation);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        group.MapPost("/redeem", async (
            HttpContext http, RedeemIdentityEnrollmentRequest request,
            IdentityEnrollmentApplicationService service, OidcEnrollmentProofValidator validator,
            CancellationToken cancellationToken) =>
        {
            if (!RequireHttps(http)) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "HTTPS is required.");
            if (string.IsNullOrWhiteSpace(request.InvitationSecret) || string.IsNullOrWhiteSpace(request.IdToken))
                return Results.BadRequest(new { error = "Invitation secret and OIDC ID token are required." });
            try
            {
                var proof = await validator.ValidateAsync(request.IdToken, cancellationToken);
                var enrollmentId = await service.RedeemInvitationAsync(request.InvitationSecret, proof, cancellationToken);
                return Results.Accepted(value: new { enrollmentId, status = "PENDING_INDEPENDENT_APPROVAL" });
            }
            catch (Microsoft.IdentityModel.Tokens.SecurityTokenException) { return Results.Unauthorized(); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        }).AllowAnonymous();

        group.MapGet("/pending", async (
            HttpContext http, IdentityEnrollmentApplicationService service, CancellationToken cancellationToken) =>
        {
            if (!TryTenant(http, out var company, out var branch)) return Results.BadRequest();
            try { return Results.Ok(await service.ListPendingAsync(http.User, company, branch, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        }).RequireAuthorization();

        group.MapPost("/{enrollmentId:guid}/approve", async (
            HttpContext http, Guid enrollmentId, ApproveIdentityEnrollmentRequest request,
            IdentityEnrollmentApplicationService service, CancellationToken cancellationToken) =>
        {
            if (!TryTenant(http, out var company, out var branch)) return Results.BadRequest();
            try
            {
                await service.ApproveAsync(http.User, company, branch, enrollmentId, request.Reason, cancellationToken);
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (Npgsql.PostgresException exception) when (exception.SqlState is "P0002" or "23505" or "40001")
            { return Results.Conflict(new { error = "Enrollment is no longer approvable or conflicts with an existing identity." }); }
        }).RequireAuthorization();
    }

    private static bool RequireHttps(HttpContext http) =>
        http.Request.IsHttps || http.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();

    private static bool TryTenant(HttpContext http, out Guid company, out Guid branch)
    {
        company = Guid.Empty;
        branch = Guid.Empty;
        var companies = http.Request.Headers[CompanyHeader];
        var branches = http.Request.Headers[BranchHeader];
        return companies.Count == 1 && branches.Count == 1
            && Guid.TryParseExact(companies[0], "D", out company) && company != Guid.Empty
            && Guid.TryParseExact(branches[0], "D", out branch) && branch != Guid.Empty;
    }
}

public sealed record CreateIdentityEnrollmentRequest(Guid TargetUserId, string Reason);
public sealed record RedeemIdentityEnrollmentRequest(string InvitationSecret, string IdToken);
public sealed record ApproveIdentityEnrollmentRequest(string Reason);
