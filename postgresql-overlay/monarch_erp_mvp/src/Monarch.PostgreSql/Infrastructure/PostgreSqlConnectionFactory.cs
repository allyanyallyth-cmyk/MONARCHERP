using Npgsql;
namespace Monarch.PostgreSql.Infrastructure;
public sealed class PostgreSqlConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    public PostgreSqlConnectionFactory(PostgreSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        _dataSource.OpenConnectionAsync(cancellationToken);
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}