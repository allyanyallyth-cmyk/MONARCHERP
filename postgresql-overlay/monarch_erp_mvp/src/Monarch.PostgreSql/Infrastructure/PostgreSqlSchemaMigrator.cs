using System.Reflection;
using Npgsql;
namespace Monarch.PostgreSql.Infrastructure;
public sealed class PostgreSqlSchemaMigrator(PostgreSqlConnectionFactory factory)
{
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var bootstrap = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS monarch_schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection))
            await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(731904221)", connection, transaction))
                await migrationLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            const string version = "001_central_tenancy";
            await using (var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM monarch_schema_migrations WHERE version = @version)", connection, transaction))
            {
                check.Parameters.AddWithValue("version", version);
                if ((bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
                { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return; }
            }
            const string resource = "Monarch.PostgreSql.Schema.001_central_tenancy.sql";
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded migration '{resource}' not found.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using (var mark = new NpgsqlCommand("INSERT INTO monarch_schema_migrations(version) VALUES (@version)", connection, transaction))
            { mark.Parameters.AddWithValue("version", version); await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); throw; }
    }
}