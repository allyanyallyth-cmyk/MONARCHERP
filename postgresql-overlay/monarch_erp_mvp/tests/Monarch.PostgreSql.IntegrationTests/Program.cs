using Monarch.Core.Abstractions;
using Monarch.PostgreSql.Infrastructure;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable(PostgreSqlOptions.ConnectionStringEnvironmentVariable)
    ?? throw new InvalidOperationException("MONARCH_POSTGRESQL_CONNECTION is required.");
await using var adminFactory = new PostgreSqlConnectionFactory(new PostgreSqlOptions(connectionString, requireTls: false));
var migrator = new PostgreSqlSchemaMigrator(adminFactory);
await migrator.ApplyAsync();
await migrator.ApplyAsync();
await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var verifyHashes = new NpgsqlCommand("SELECT count(*) FROM monarch_schema_migrations WHERE sha256 ~ '^[0-9a-f]{64}$'", admin);
    Assert(Convert.ToInt32(await verifyHashes.ExecuteScalarAsync()) >= 2, "ordered migrations recorded with SHA-256 checksums");
}
var runtimePassword = Guid.NewGuid().ToString("N");
await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var createRole = new NpgsqlCommand($"CREATE ROLE monarch_runtime_ci LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{runtimePassword}'", admin);
    await createRole.ExecuteNonQueryAsync();
    await using var grants = new NpgsqlCommand("""
        GRANT USAGE ON SCHEMA public TO monarch_runtime_ci;
        GRANT SELECT ON ALL TABLES IN SCHEMA public TO monarch_runtime_ci;
        GRANT INSERT ON monarch_outbox, monarch_gl_accounts, monarch_gl_journals, monarch_gl_journal_lines, monarch_gl_journal_sequences TO monarch_runtime_ci;
        GRANT UPDATE ON monarch_gl_journals, monarch_gl_journal_sequences TO monarch_runtime_ci;
        GRANT EXECUTE ON FUNCTION monarch_gl_next_journal_number(uuid,uuid,integer) TO monarch_runtime_ci;
        """, admin);
    await grants.ExecuteNonQueryAsync();
}
var runtimeBuilder = new NpgsqlConnectionStringBuilder(connectionString)
{
    Username = "monarch_runtime_ci", Password = runtimePassword, Pooling = true, MinPoolSize = 1, MaxPoolSize = 1
};
await using var runtimeFactory = new PostgreSqlConnectionFactory(new PostgreSqlOptions(runtimeBuilder.ConnectionString, requireTls: false));
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

// Pooled connection is deliberately reused; transaction-local settings must be cleared on commit.
await using (var connection = await runtimeFactory.OpenConnectionAsync())
await using (var command = new NpgsqlCommand("SELECT current_setting('monarch.company_id', true)", connection))
    Assert(await command.ExecuteScalarAsync() is DBNull or null, "tenant context does not leak through the connection pool");

var denied = false;
try { await using var ignored = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, new TenantDatabaseContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())); }
catch (UnauthorizedAccessException) { denied = true; }
Assert(denied, "unknown tenant membership rejected");

// Membership lifecycle is privileged and audited; the runtime database role cannot write the grant tables.
await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var revoke = new NpgsqlCommand("SELECT monarch_security.revoke_branch_access(@c,@b,@u,@actor,'integration test')", admin);
    revoke.Parameters.AddWithValue("c", first.Context.CompanyId);
    revoke.Parameters.AddWithValue("b", first.Context.BranchId);
    revoke.Parameters.AddWithValue("u", first.Context.UserId);
    revoke.Parameters.AddWithValue("actor", first.Context.UserId);
    Assert((bool)(await revoke.ExecuteScalarAsync() ?? false), "membership revocation accepted by privileged function");
}
var revokedDenied = false;
try { await using var ignored = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context); }
catch (UnauthorizedAccessException) { revokedDenied = true; }
Assert(revokedDenied, "revoked membership immediately blocks new tenant transactions");
await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var grant = new NpgsqlCommand("SELECT monarch_security.grant_branch_access(@c,@b,@u,@actor,'regrant test')", admin);
    grant.Parameters.AddWithValue("c", first.Context.CompanyId);
    grant.Parameters.AddWithValue("b", first.Context.BranchId);
    grant.Parameters.AddWithValue("u", first.Context.UserId);
    grant.Parameters.AddWithValue("actor", first.Context.UserId);
    await grant.ExecuteNonQueryAsync();
    await using var audit = new NpgsqlCommand("SELECT count(*) FROM monarch_membership_audit WHERE company_id=@c AND branch_id=@b AND user_id=@u", admin);
    audit.Parameters.AddWithValue("c", first.Context.CompanyId);
    audit.Parameters.AddWithValue("b", first.Context.BranchId);
    audit.Parameters.AddWithValue("u", first.Context.UserId);
    Assert(Convert.ToInt32(await audit.ExecuteScalarAsync()) == 2, "grant/revoke history retained");
}

// General Ledger repository: company chart, balanced draft, atomic posting + outbox and trial balance.
var tenant = new TenantScope(first.Context.CompanyId, first.Context.BranchId, first.Context.UserId);
var gl = new PostgreSqlGeneralLedgerStore(runtimeFactory);
var cash = Guid.NewGuid();
var equity = Guid.NewGuid();
await gl.CreateAccountAsync(tenant, new GeneralLedgerAccount(cash, "1000", "Cash", "ASSET", "D"));
await gl.CreateAccountAsync(tenant, new GeneralLedgerAccount(equity, "3000", "Owner Equity", "EQUITY", "C"));
var journalId = Guid.NewGuid();
var journal = await gl.CreateDraftAsync(tenant, journalId, DateTime.UtcNow.Year, DateOnly.FromDateTime(DateTime.UtcNow),
    "Opening cash balance", "SAR", new[]
    {
        new GeneralLedgerLineInput(cash, 12500, 0, "Cash debit"),
        new GeneralLedgerLineInput(equity, 0, 12500, "Equity credit")
    });
Assert(journal.Status == "DRAFT" && journal.JournalNumber > 0, "GL draft and atomic sequence allocation");
await gl.PostAsync(tenant, journalId);
var trialBalance = await gl.GetTrialBalanceAsync(tenant, DateOnly.FromDateTime(DateTime.UtcNow));
Assert(trialBalance.Sum(x => x.DebitMinor) == 12500 && trialBalance.Sum(x => x.CreditMinor) == 12500,
    "GL trial balance reconciles in integer minor units");

// A direct SQL attempt to post an unbalanced journal must fail at the database boundary.
var unbalancedId = Guid.NewGuid();
var unbalanced = await gl.CreateDraftAsync(tenant, unbalancedId, DateTime.UtcNow.Year, DateOnly.FromDateTime(DateTime.UtcNow),
    "Database balance guard", "SAR", new[]
    {
        new GeneralLedgerLineInput(cash, 1000, 0),
        new GeneralLedgerLineInput(equity, 0, 1000)
    });
await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context))
{
    await using var corrupt = new NpgsqlCommand("""
        UPDATE monarch_gl_journal_lines SET credit_minor=999
        WHERE company_id=@c AND branch_id=@b AND journal_id=@j AND line_number=2
        """, scope.Connection, scope.Transaction);
    corrupt.Parameters.AddWithValue("c", first.Context.CompanyId);
    corrupt.Parameters.AddWithValue("b", first.Context.BranchId);
    corrupt.Parameters.AddWithValue("j", unbalancedId);
    await corrupt.ExecuteNonQueryAsync();
    await scope.CommitAsync();
}
var balanceGuard = false;
try { await gl.PostAsync(tenant, unbalancedId); }
catch (PostgresException) { balanceGuard = true; }
Assert(balanceGuard, "database trigger rejects unbalanced journal posting");

// Posted journal rows are immutable.
var immutable = false;
try
{
    await using var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context);
    await using var change = new NpgsqlCommand("UPDATE monarch_gl_journal_lines SET debit_minor=12499 WHERE company_id=@c AND branch_id=@b AND journal_id=@j AND line_number=1", scope.Connection, scope.Transaction);
    change.Parameters.AddWithValue("c", first.Context.CompanyId);
    change.Parameters.AddWithValue("b", first.Context.BranchId);
    change.Parameters.AddWithValue("j", journalId);
    await change.ExecuteNonQueryAsync();
}
catch (PostgresException) { immutable = true; }
Assert(immutable, "posted GL journal lines are immutable");

Console.WriteLine("PASS: PostgreSQL 16 migrations, RLS, pooled context, membership lifecycle, GL and outbox integration.");

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