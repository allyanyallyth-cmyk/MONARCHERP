using Monarch.PostgreSql.Infrastructure;
using Npgsql;
var connectionString = Environment.GetEnvironmentVariable(PostgreSqlOptions.ConnectionStringEnvironmentVariable)
    ?? throw new InvalidOperationException("MONARCH_POSTGRESQL_CONNECTION is required.");
await using var adminFactory = new PostgreSqlConnectionFactory(new PostgreSqlOptions(connectionString));
await new PostgreSqlSchemaMigrator(adminFactory).ApplyAsync();
await new PostgreSqlSchemaMigrator(adminFactory).ApplyAsync();
var runtimePassword = Guid.NewGuid().ToString("N"); // ephemeral CI-only credential
await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using (var createRole = new NpgsqlCommand($"CREATE ROLE monarch_runtime_ci LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{runtimePassword}'", admin))
        await createRole.ExecuteNonQueryAsync();
    await using (var grants = new NpgsqlCommand("GRANT USAGE ON SCHEMA public TO monarch_runtime_ci; GRANT SELECT ON ALL TABLES IN SCHEMA public TO monarch_runtime_ci; GRANT INSERT ON monarch_outbox TO monarch_runtime_ci", admin))
        await grants.ExecuteNonQueryAsync();
}
var runtimeBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Username = "monarch_runtime_ci", Password = runtimePassword, Pooling = false };
await using var runtimeFactory = new PostgreSqlConnectionFactory(new PostgreSqlOptions(runtimeBuilder.ConnectionString));
var first = await SeedTenantAsync(adminFactory, "FIRST");
var second = await SeedTenantAsync(adminFactory, "SECOND");
var writer = new PostgreSqlOutboxWriter();
await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context))
{
    Assert(await writer.EnqueueAsync(scope, Guid.NewGuid(), "Smoke", "aggregate-1", "Smoke.Created", new { tenant = "first" }, DateTimeOffset.UtcNow), "first tenant event inserted");
    var eventId = Guid.NewGuid();
    Assert(await writer.EnqueueAsync(scope, eventId, "Smoke", "aggregate-2", "Smoke.Created", new { value = 2 }, DateTimeOffset.UtcNow), "idempotency event inserted");
    Assert(!await writer.EnqueueAsync(scope, eventId, "Smoke", "aggregate-2", "Smoke.Created", new { value = 2 }, DateTimeOffset.UtcNow), "duplicate event ignored");
    await scope.CommitAsync();
}
await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, second.Context))
{
    Assert(await writer.EnqueueAsync(scope, Guid.NewGuid(), "Smoke", "aggregate-3", "Smoke.Created", new { tenant = "second" }, DateTimeOffset.UtcNow), "second tenant event inserted");
    await scope.CommitAsync();
}
await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context))
await using (var command = new NpgsqlCommand("SELECT count(*) FROM monarch_outbox", scope.Connection, scope.Transaction))
    Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 2, "RLS hides the other tenant's outbox rows");
var denied = false;
try { await using var ignored = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, new TenantDatabaseContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())); }
catch (UnauthorizedAccessException) { denied = true; }
Assert(denied, "unknown tenant membership rejected");
Console.WriteLine("PASS: PostgreSQL migration, non-superuser RLS, membership and outbox integration.");
static async Task<TenantSeed> SeedTenantAsync(PostgreSqlConnectionFactory factory, string suffix)
{
    var company = Guid.NewGuid(); var branch = Guid.NewGuid(); var user = Guid.NewGuid();
    await using var connection = await factory.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction, "INSERT INTO monarch_companies(company_id,company_code,legal_name) VALUES (@c,@code,@name)", ("c",company),("code",suffix+"-"+company.ToString("N")),("name","MONARCH "+suffix));
    await ExecuteAsync(connection, transaction, "INSERT INTO monarch_branches(company_id,branch_id,branch_code,branch_name) VALUES (@c,@b,@code,@name)", ("c",company),("b",branch),("code","BR-"+branch.ToString("N")),("name",suffix+" Branch"));
    await ExecuteAsync(connection, transaction, "INSERT INTO monarch_tenant_users(company_id,user_id,normalized_login,display_name) VALUES (@c,@u,@login,@name)", ("c",company),("u",user),("login","ci-"+user.ToString("N")),("name",suffix+" User"));
    await ExecuteAsync(connection, transaction, "INSERT INTO monarch_user_branch_grants(company_id,branch_id,user_id) VALUES (@c,@b,@u)", ("c",company),("b",branch),("u",user));
    await transaction.CommitAsync();
    return new TenantSeed(new TenantDatabaseContext(company,branch,user));
}
static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value)[] parameters)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    foreach (var (name,value) in parameters) command.Parameters.AddWithValue(name,value);
    await command.ExecuteNonQueryAsync();
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: "+message);
    Console.WriteLine("PASS: "+message);
}
sealed record TenantSeed(TenantDatabaseContext Context);