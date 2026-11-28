using Npgsql;
namespace Monarch.PostgreSql.Infrastructure;
public sealed class PostgreSqlHealthProbe(PostgreSqlConnectionFactory factory)
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
        }
        catch (NpgsqlException) { return false; }
        catch (TimeoutException) { return false; }
    }
}