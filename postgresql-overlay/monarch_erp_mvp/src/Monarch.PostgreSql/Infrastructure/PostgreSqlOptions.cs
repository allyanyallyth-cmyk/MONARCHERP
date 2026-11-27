namespace Monarch.PostgreSql.Infrastructure;
public sealed class PostgreSqlOptions
{
    public const string ConnectionStringEnvironmentVariable = "MONARCH_POSTGRESQL_CONNECTION";
    public string ConnectionString { get; }
    public PostgreSqlOptions(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        ConnectionString = connectionString;
    }
    public static PostgreSqlOptions FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException($"Set {ConnectionStringEnvironmentVariable} using a secret store; never commit credentials."));
}