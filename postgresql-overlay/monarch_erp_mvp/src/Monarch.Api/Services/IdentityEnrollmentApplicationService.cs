using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Monarch.Api.Security;
using Monarch.PostgreSql.Infrastructure;
using Npgsql;

namespace Monarch.Api.Services;

public sealed record IdentityEnrollmentInvitation(
    Guid EnrollmentId, string InvitationSecret, string OidcNonce, DateTimeOffset ExpiresAt);

public sealed record IdentityEnrollmentPendingItem(
    Guid EnrollmentId, Guid TargetUserId, string Issuer, string Subject,
    string Reason, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset RedeemedAt);

public sealed class IdentityEnrollmentApplicationService(PostgreSqlConnectionFactory factory)
{
    private const int InvitationLifetimeSeconds = 300;

    public async Task<IdentityEnrollmentInvitation> CreateInvitationAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, Guid targetUserId,
        string reason, CancellationToken cancellationToken = default)
    {
        var (issuer, subject) = RequireIdentity(principal);
        if (targetUserId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            throw new ArgumentException("A target user and a reason of 1-500 characters are required.");

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var nonceBytes = RandomNumberGenerator.GetBytes(32);
        var token = Base64UrlEncoder.Encode(tokenBytes);
        var nonce = Base64UrlEncoder.Encode(nonceBytes);
        var tokenHash = SHA256.HashData(tokenBytes);
        var nonceHash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        CryptographicOperations.ZeroMemory(tokenBytes);
        CryptographicOperations.ZeroMemory(nonceBytes);

        var tenant = new TenantDatabaseContext(companyId, branchId, Guid.Empty,
            issuer, subject, "security.identity.invite");
        await using var scope = await TenantPostgreSqlTransaction.BeginAsync(factory, tenant, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT monarch_security.create_identity_enrollment_claim(
                @company,@branch,@target,@issuer,@token_hash,@nonce_hash,@lifetime,@reason)
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("branch", branchId);
        command.Parameters.AddWithValue("target", targetUserId);
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("nonce_hash", nonceHash);
        command.Parameters.AddWithValue("lifetime", InvitationLifetimeSeconds);
        command.Parameters.AddWithValue("reason", reason.Trim());
        var enrollmentId = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Identity enrollment invitation was not created."));
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new IdentityEnrollmentInvitation(enrollmentId, token, nonce,
            DateTimeOffset.UtcNow.AddSeconds(InvitationLifetimeSeconds));
    }

    public async Task<Guid> RedeemInvitationAsync(
        string invitationSecret, OidcIdentityOwnershipProof proof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        byte[] secretBytes;
        try { secretBytes = Base64UrlEncoder.DecodeBytes(invitationSecret); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        { throw new UnauthorizedAccessException("Identity enrollment invitation is invalid."); }
        if (secretBytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            throw new UnauthorizedAccessException("Identity enrollment invitation is invalid.");
        }

        var tokenHash = SHA256.HashData(secretBytes);
        CryptographicOperations.ZeroMemory(secretBytes);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT enrollment_id
            FROM monarch_security.redeem_identity_enrollment_claim(@token_hash,@issuer,@subject,@nonce)
            """, connection);
        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("issuer", proof.Issuer);
        command.Parameters.AddWithValue("subject", proof.Subject);
        command.Parameters.AddWithValue("nonce", proof.Nonce);
        try
        {
            return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("Identity enrollment invitation could not be redeemed."));
        }
        catch (PostgresException exception) when (exception.SqlState is "28000" or "23505" or "40001")
        {
            throw new UnauthorizedAccessException("Identity enrollment invitation is invalid, expired, used or conflicts with an existing identity.", exception);
        }
    }

    public async Task<IReadOnlyList<IdentityEnrollmentPendingItem>> ListPendingAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId,
        CancellationToken cancellationToken = default)
    {
        var (issuer, subject) = RequireIdentity(principal);
        var tenant = new TenantDatabaseContext(companyId, branchId, Guid.Empty,
            issuer, subject, "security.identity.enrollment.approve");
        await using var scope = await TenantPostgreSqlTransaction.BeginAsync(factory, tenant, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT * FROM monarch_security.list_identity_enrollment_claims()",
            scope.Connection, scope.Transaction);
        var items = new List<IdentityEnrollmentPendingItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(new IdentityEnrollmentPendingItem(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetGuid(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        await reader.DisposeAsync().ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return items;
    }

    public async Task ApproveAsync(
        ClaimsPrincipal principal, Guid companyId, Guid branchId, Guid enrollmentId,
        string reason, CancellationToken cancellationToken = default)
    {
        var (issuer, subject) = RequireIdentity(principal);
        if (enrollmentId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            throw new ArgumentException("An enrollment ID and an approval reason of 1-500 characters are required.");
        var tenant = new TenantDatabaseContext(companyId, branchId, Guid.Empty,
            issuer, subject, "security.identity.enrollment.approve");
        await using var scope = await TenantPostgreSqlTransaction.BeginAsync(factory, tenant, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT monarch_security.approve_identity_enrollment_claim(@enrollment,@reason)",
            scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("enrollment", enrollmentId);
        command.Parameters.AddWithValue("reason", reason.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (string Issuer, string Subject) RequireIdentity(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identities = principal.Identities.Where(identity => identity.IsAuthenticated).ToArray();
        if (identities.Length != 1)
            throw new UnauthorizedAccessException("Exactly one authenticated OIDC identity is required.");
        var issuer = RequiredClaim(identities[0], "iss");
        var subject = RequiredClaim(identities[0], "sub");
        return (issuer, subject);
    }

    private static string RequiredClaim(ClaimsIdentity identity, string name)
    {
        var values = identity.Claims.Where(claim => claim.Type == name).Select(claim => claim.Value).ToArray();
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0].Length > 512)
            throw new UnauthorizedAccessException("The authenticated OIDC issuer or subject is missing or ambiguous.");
        return values[0];
    }
}
