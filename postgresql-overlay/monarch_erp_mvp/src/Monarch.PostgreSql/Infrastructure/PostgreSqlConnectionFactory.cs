using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Monarch.PostgreSql.Infrastructure;

public sealed class PostgreSqlConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly byte[] _contextHmacKey;

    public PostgreSqlConnectionFactory(PostgreSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _contextHmacKey = options.ContextHmacKey.ToArray();
        _dataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        _dataSource.OpenConnectionAsync(cancellationToken);

    internal (long IssuedAt, string Signature) SignTenantContext(TenantDatabaseContext tenant)
    {
        tenant.Validate();
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var message = tenant.IdentityIssuer is null
            ? $"{tenant.CompanyId:D}:{tenant.BranchId:D}:{tenant.UserId:D}:{issuedAt}"
            : $"{tenant.CompanyId:D}:{tenant.BranchId:D}:{tenant.UserId:D}:{issuedAt}:{tenant.IdentityIssuer}:{tenant.IdentitySubject}:{tenant.RequiredPermission}";
        var signature = Convert.ToHexString(HMACSHA256.HashData(_contextHmacKey, Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
        return (issuedAt, signature);
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}