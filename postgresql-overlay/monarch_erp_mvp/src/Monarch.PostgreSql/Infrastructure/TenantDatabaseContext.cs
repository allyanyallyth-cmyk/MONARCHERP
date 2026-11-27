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
            await using (var settings = new NpgsqlCommand("""
                SELECT set_config('monarch.company_id', @company, true),
                       set_config('monarch.branch_id', @branch, true),
                       set_config('monarch.user_id', @user, true)
                """, connection, transaction))
            {
                settings.Parameters.AddWithValue("company", tenant.CompanyId.ToString("D"));
                settings.Parameters.AddWithValue("branch", tenant.BranchId.ToString("D"));
                settings.Parameters.AddWithValue("user", tenant.UserId.ToString("D"));
                await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Membership is checked inside the same transaction after transaction-local context is set.
            await using (var membership = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM monarch_user_branch_grants g
                    JOIN monarch_tenant_users u ON u.company_id = g.company_id AND u.user_id = g.user_id AND u.is_active
                    JOIN monarch_branches b ON b.company_id = g.company_id AND b.branch_id = g.branch_id AND b.is_active
                    WHERE g.company_id = @company AND g.branch_id = @branch AND g.user_id = @user)
                """, connection, transaction))
            {
                membership.Parameters.AddWithValue("company", tenant.CompanyId);
                membership.Parameters.AddWithValue("branch", tenant.BranchId);
                membership.Parameters.AddWithValue("user", tenant.UserId);
                if ((bool)(await membership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false) is false)
                    throw new UnauthorizedAccessException("User is inactive or lacks access to this company branch.");
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