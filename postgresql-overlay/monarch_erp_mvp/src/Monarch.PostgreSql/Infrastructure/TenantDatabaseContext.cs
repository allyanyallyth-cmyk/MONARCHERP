using Npgsql;

namespace Monarch.PostgreSql.Infrastructure;

public sealed record TenantDatabaseContext(Guid CompanyId, Guid BranchId, Guid UserId)
{
    public void Validate()
    {
        if (CompanyId == Guid.Empty || BranchId == Guid.Empty || UserId == Guid.Empty)
            throw new ArgumentException("CompanyId, BranchId and UserId are mandatory.");
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
        tenant.Validate();
        var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var (issuedAt, signature) = factory.SignTenantContext(tenant);
            await using (var context = new NpgsqlCommand("""
                SELECT monarch_security.set_tenant_context(@company, @branch, @user, @issued_at, @signature)
                """, connection, transaction))
            {
                context.Parameters.AddWithValue("company", tenant.CompanyId);
                context.Parameters.AddWithValue("branch", tenant.BranchId);
                context.Parameters.AddWithValue("user", tenant.UserId);
                context.Parameters.AddWithValue("issued_at", issuedAt);
                context.Parameters.AddWithValue("signature", signature);
                await context.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            return new TenantPostgreSqlTransaction(connection, transaction, tenant);
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