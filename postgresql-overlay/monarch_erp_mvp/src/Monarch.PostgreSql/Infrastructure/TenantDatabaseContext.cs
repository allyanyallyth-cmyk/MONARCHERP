using Npgsql;

namespace Monarch.PostgreSql.Infrastructure;

public sealed record TenantDatabaseContext(
    Guid CompanyId,
    Guid BranchId,
    Guid UserId,
    string? IdentityIssuer = null,
    string? IdentitySubject = null,
    string? RequiredPermission = null)
{
    public void Validate()
    {
        if (CompanyId == Guid.Empty || BranchId == Guid.Empty)
            throw new ArgumentException("CompanyId and BranchId are mandatory.");

        var identityFields = new[] { IdentityIssuer, IdentitySubject, RequiredPermission };
        var identityFieldCount = identityFields.Count(value => !string.IsNullOrWhiteSpace(value));
        if (identityFieldCount is not (0 or 3))
            throw new ArgumentException("OIDC issuer, subject and required permission must be supplied together.");

        if (identityFieldCount == 0 && UserId == Guid.Empty)
            throw new ArgumentException("UserId is mandatory for a non-federated tenant context.");
        if (identityFieldCount == 3 && UserId == Guid.Empty)
            throw new ArgumentException("The OIDC subject must be resolved to an internal user before signing context.");
        if (identityFieldCount == 3 &&
            (IdentityIssuer!.Length > 512 || IdentitySubject!.Length > 512 || RequiredPermission!.Length > 128))
            throw new ArgumentException("OIDC authorization context exceeds supported field lengths.");
    }
}

public sealed class TenantPostgreSqlTransaction : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }
    public TenantDatabaseContext Tenant { get; }
    private bool _completed;

    private TenantPostgreSqlTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantDatabaseContext tenant)
    { Connection = connection; Transaction = transaction; Tenant = tenant; }

    public static async ValueTask<TenantPostgreSqlTransaction> BeginAsync(
        PostgreSqlConnectionFactory factory, TenantDatabaseContext tenant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(tenant);

        var federated = !string.IsNullOrWhiteSpace(tenant.IdentityIssuer);
        if (federated)
        {
            if (string.IsNullOrWhiteSpace(tenant.IdentitySubject) ||
                string.IsNullOrWhiteSpace(tenant.RequiredPermission) || tenant.UserId != Guid.Empty)
                throw new ArgumentException("Federated tenant context must contain issuer, subject and permission without a caller-supplied user ID.");

            await using var resolverConnection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var resolve = new NpgsqlCommand("""
                SELECT monarch_security.resolve_oidc_user(@company, @branch, @issuer, @subject)
                """, resolverConnection);
            resolve.Parameters.AddWithValue("company", tenant.CompanyId);
            resolve.Parameters.AddWithValue("branch", tenant.BranchId);
            resolve.Parameters.AddWithValue("issuer", tenant.IdentityIssuer!);
            resolve.Parameters.AddWithValue("subject", tenant.IdentitySubject!);
            var resolved = await resolve.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (resolved is not Guid resolvedUser || resolvedUser == Guid.Empty)
                throw new UnauthorizedAccessException("The OIDC subject is not mapped to an active MONARCH user.");
            tenant = tenant with { UserId = resolvedUser };
        }

        tenant.Validate();
        var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var (issuedAt, signature) = factory.SignTenantContext(tenant);
            if (federated)
            {
                await using var context = new NpgsqlCommand("""
                    SELECT monarch_security.set_authorized_tenant_context(
                        @company, @branch, @user, @issuer, @subject, @permission, @issued_at, @signature)
                    """, connection, transaction);
                context.Parameters.AddWithValue("company", tenant.CompanyId);
                context.Parameters.AddWithValue("branch", tenant.BranchId);
                context.Parameters.AddWithValue("user", tenant.UserId);
                context.Parameters.AddWithValue("issuer", tenant.IdentityIssuer!);
                context.Parameters.AddWithValue("subject", tenant.IdentitySubject!);
                context.Parameters.AddWithValue("permission", tenant.RequiredPermission!);
                context.Parameters.AddWithValue("issued_at", issuedAt);
                context.Parameters.AddWithValue("signature", signature);
                await context.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var context = new NpgsqlCommand("""
                    SELECT monarch_security.set_tenant_context(@company, @branch, @user, @issued_at, @signature)
                    """, connection, transaction);
                context.Parameters.AddWithValue("company", tenant.CompanyId);
                context.Parameters.AddWithValue("branch", tenant.BranchId);
                context.Parameters.AddWithValue("user", tenant.UserId);
                context.Parameters.AddWithValue("issued_at", issuedAt);
                context.Parameters.AddWithValue("signature", signature);
                await context.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            return new TenantPostgreSqlTransaction(connection, transaction, tenant);
        }
        catch (PostgresException ex) when (ex.SqlState is "P0001" or "28000")
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException("Tenant identity is invalid or branch membership is inactive.", ex);
        }
        catch
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) throw new InvalidOperationException("Transaction already completed.");
        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!_completed) await Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            await Transaction.DisposeAsync().ConfigureAwait(false);
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}