using Npgsql;

namespace Monarch.PostgreSql.Infrastructure;

public sealed class PostgreSqlOptions
{
    public const string ConnectionStringEnvironmentVariable = "MONARCH_POSTGRESQL_CONNECTION";
    public string ConnectionString { get; }

    public PostgreSqlOptions(string connectionString, bool requireTls = true)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 100,
            Timeout = 15,
            CommandTimeout = 30,
            KeepAlive = 30
        };
        if (requireTls)
        {
            if (builder.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
                builder.SslMode = SslMode.VerifyFull;
            if (builder.SslMode != SslMode.VerifyFull)
                throw new ArgumentException("Production PostgreSQL connections must use SSL Mode=VerifyFull.", nameof(connectionString));
        }
        ConnectionString = builder.ConnectionString;
    }

    public static PostgreSqlOptions FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException($"Set {ConnectionStringEnvironmentVariable} using a secret store; never commit credentials."));
}