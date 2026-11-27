using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Monarch.PostgreSql.Infrastructure;

public sealed class PostgreSqlSchemaMigrator(PostgreSqlConnectionFactory factory)
{
    private const long AdvisoryLockKey = 731904221;

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var bootstrap = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS monarch_schema_migrations (
                version text PRIMARY KEY,
                sha256 char(64) NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );
            ALTER TABLE monarch_schema_migrations ADD COLUMN IF NOT EXISTS sha256 char(64) NULL;
            """, connection))
            await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var assembly = typeof(PostgreSqlSchemaMigrator).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(x => x.Contains(".Schema.", StringComparison.Ordinal) && x.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (migrations.Length == 0) throw new InvalidOperationException("No embedded PostgreSQL migrations were found.");

        foreach (var resource in migrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded migration '{resource}' not found.");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
            var version = resource[(resource.LastIndexOf(".Schema.", StringComparison.Ordinal) + 8)..^4];

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
                {
                    migrationLock.Parameters.AddWithValue("key", AdvisoryLockKey);
                    await migrationLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var check = new NpgsqlCommand(
                    "SELECT sha256 FROM monarch_schema_migrations WHERE version = @version", connection, transaction))
                {
                    check.Parameters.AddWithValue("version", version);
                    var existing = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (existing is not null && existing is not DBNull)
                    {
                        var appliedHash = Convert.ToString(existing)?.Trim();
                        if (!string.Equals(appliedHash, hash, StringComparison.Ordinal))
                            throw new InvalidOperationException($"Applied migration '{version}' checksum mismatch. Refusing to modify database history.");
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (existing is not null && existing is DBNull)
                        throw new InvalidOperationException($"Migration '{version}' exists without a SHA-256 baseline. Explicitly baseline it after verifying the live schema.");
                }

                await using (var command = new NpgsqlCommand(sql, connection, transaction))
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await using (var mark = new NpgsqlCommand(
                    "INSERT INTO monarch_schema_migrations(version, sha256) VALUES (@version, @sha256)", connection, transaction))
                {
                    mark.Parameters.AddWithValue("version", version);
                    mark.Parameters.AddWithValue("sha256", hash);
                    await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }
}