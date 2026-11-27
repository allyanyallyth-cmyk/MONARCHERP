using Npgsql;

namespace Monarch.PostgreSql.Infrastructure;

public sealed class PostgreSqlOptions
{
    public const string ConnectionStringEnvironmentVariable = "MONARCH_POSTGRESQL_CONNECTION";
    public const string ContextHmacKeyEnvironmentVariable = "MONARCH_POSTGRESQL_CONTEXT_HMAC_KEY";
    public string ConnectionString { get; }
    public byte[] ContextHmacKey { get; }

    public PostgreSqlOptions(string connectionString, bool requireTls = true, string? contextHmacKey = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        contextHmacKey ??= Environment.GetEnvironmentVariable(ContextHmacKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(contextHmacKey))
            throw new InvalidOperationException($"Set {ContextHmacKeyEnvironmentVariable} in the server secret store.");
        try { ContextHmacKey = Convert.FromHexString(contextHmacKey); }
        catch (FormatException ex) { throw new ArgumentException("Tenant context HMAC key must be hexadecimal.", nameof(contextHmacKey), ex); }
        if (ContextHmacKey.Length < 32)
            throw new ArgumentException("Tenant context HMAC key must contain at least 256 bits.", nameof(contextHmacKey));

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