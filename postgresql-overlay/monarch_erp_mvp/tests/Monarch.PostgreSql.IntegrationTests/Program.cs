using Monarch.Core.Abstractions;
using Monarch.PostgreSql.Infrastructure;
using Npgsql;
using System.Security.Cryptography;

var connectionString = Environment.GetEnvironmentVariable(PostgreSqlOptions.ConnectionStringEnvironmentVariable)
    ?? throw new InvalidOperationException("MONARCH_POSTGRESQL_CONNECTION is required.");
var contextKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
await using var adminFactory = new PostgreSqlConnectionFactory(
    new PostgreSqlOptions(connectionString, requireTls: false, contextHmacKey: contextKey));
var migrator = new PostgreSqlSchemaMigrator(adminFactory);
await migrator.ApplyAsync();
await migrator.ApplyAsync();
await Task.WhenAll(migrator.ApplyAsync(), migrator.ApplyAsync());

await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var provisionSecret = new NpgsqlCommand("""
        INSERT INTO monarch_security.context_secrets(secret_id,secret)
        VALUES(1,decode(@key,'hex'))
        ON CONFLICT(secret_id) DO UPDATE SET secret=EXCLUDED.secret
        """, admin);
    provisionSecret.Parameters.AddWithValue("key", contextKey);
    await provisionSecret.ExecuteNonQueryAsync();

    await using var verifyHashes = new NpgsqlCommand(
        "SELECT count(*) FROM monarch_schema_migrations WHERE length(btrim(sha256))=64", admin);
    Assert(Convert.ToInt32(await verifyHashes.ExecuteScalarAsync()) >= 2,
        "ordered migrations recorded with SHA-256 checksums");

    await using var readHash = new NpgsqlCommand(
        "SELECT btrim(sha256) FROM monarch_schema_migrations WHERE version='001_central_tenancy'", admin);
    var originalHash = (string)(await readHash.ExecuteScalarAsync()
        ?? throw new InvalidOperationException("Missing migration hash."));
    await using (var corruptHash = new NpgsqlCommand(
        "UPDATE monarch_schema_migrations SET sha256=repeat('0',64) WHERE version='001_central_tenancy'", admin))
        await corruptHash.ExecuteNonQueryAsync();

    var checksumMismatchRejected = false;
    try { await migrator.ApplyAsync(); }
    catch (InvalidOperationException) { checksumMismatchRejected = true; }

    await using (var restoreHash = new NpgsqlCommand(
        "UPDATE monarch_schema_migrations SET sha256=@hash WHERE version='001_central_tenancy'", admin))
    {
        restoreHash.Parameters.AddWithValue("hash", originalHash);
        await restoreHash.ExecuteNonQueryAsync();
    }
    Assert(checksumMismatchRejected, "applied migration checksum tampering is rejected");
}
await migrator.ApplyAsync();

var runtimePassword = Guid.NewGuid().ToString("N");
await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var createRole = new NpgsqlCommand(
        $"CREATE ROLE monarch_runtime_ci LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{runtimePassword}'", admin);
    await createRole.ExecuteNonQueryAsync();

    await using var grants = new NpgsqlCommand("""
        GRANT USAGE ON SCHEMA public, monarch_security TO monarch_runtime_ci;
        GRANT SELECT ON ALL TABLES IN SCHEMA public TO monarch_runtime_ci;
        GRANT INSERT ON monarch_outbox, monarch_gl_accounts, monarch_gl_journals,
          monarch_gl_journal_lines, monarch_gl_journal_sequences TO monarch_runtime_ci;
        GRANT UPDATE ON monarch_gl_journals, monarch_gl_journal_sequences TO monarch_runtime_ci;
        GRANT EXECUTE ON FUNCTION monarch_gl_next_journal_number(uuid,uuid,integer) TO monarch_runtime_ci;
        GRANT EXECUTE ON FUNCTION monarch_security.set_tenant_context(uuid,uuid,uuid,bigint,text) TO monarch_runtime_ci;
        GRANT EXECUTE ON FUNCTION monarch_security.context_is_valid() TO monarch_runtime_ci;
        """, admin);
    await grants.ExecuteNonQueryAsync();
}

var runtimeBuilder = new NpgsqlConnectionStringBuilder(connectionString)
{
    Username = "monarch_runtime_ci",
    Password = runtimePassword,
    Pooling = true,
    MinPoolSize = 1,
    MaxPoolSize = 1
};
await using var runtimeFactory = new PostgreSqlConnectionFactory(
    new PostgreSqlOptions(runtimeBuilder.ConnectionString, requireTls: false, contextHmacKey: contextKey));

var first = await SeedTenantAsync(adminFactory, "FIRST");
var second = await SeedTenantAsync(adminFactory, "SECOND");
var writer = new PostgreSqlOutboxWriter();

await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context))
{
    Assert(await writer.EnqueueAsync(scope, Guid.NewGuid(), "Smoke", "aggregate-1",
        "Smoke.Created", new { tenant = "first" }, DateTimeOffset.UtcNow), "first tenant event inserted");
    var eventId = Guid.NewGuid();
    Assert(await writer.EnqueueAsync(scope, eventId, "Smoke", "aggregate-2",
        "Smoke.Created", new { value = 2 }, DateTimeOffset.UtcNow), "idempotency event inserted");
    Assert(!await writer.EnqueueAsync(scope, eventId, "Smoke", "aggregate-2",
        "Smoke.Created", new { value = 2 }, DateTimeOffset.UtcNow), "duplicate event ignored");
    await scope.CommitAsync();
}
await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, second.Context))
{
    Assert(await writer.EnqueueAsync(scope, Guid.NewGuid(), "Smoke", "aggregate-3",
        "Smoke.Created", new { tenant = "second" }, DateTimeOffset.UtcNow), "second tenant event inserted");
    await scope.CommitAsync();
}
await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context))
await using (var command = new NpgsqlCommand("SELECT count(*) FROM monarch_outbox", scope.Connection, scope.Transaction))
    Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 2, "RLS hides the other tenant's outbox rows");

// Reuse the same pooled connection; transaction-local tenant settings must be cleared on commit.
await using (var connection = await runtimeFactory.OpenConnectionAsync())
await using (var command = new NpgsqlCommand("""
    SELECT COALESCE(current_setting('monarch.company_id', true),''),
           COALESCE(current_setting('monarch.branch_id', true),''),
           COALESCE(current_setting('monarch.user_id', true),''),
           COALESCE(current_setting('monarch.context_issued_at', true),''),
           COALESCE(current_setting('monarch.context_signature', true),'')
    """, connection))
await using (var reader = await command.ExecuteReaderAsync())
{
    await reader.ReadAsync();
    Assert(Enumerable.Range(0, 5).All(i => string.IsNullOrEmpty(reader.GetString(i))),
        "tenant context does not leak through the connection pool");
}

// Custom GUCs are forgeable; the signed HMAC context required by RLS is not.
await using (var connection = await runtimeFactory.OpenConnectionAsync())
await using (var transaction = await connection.BeginTransactionAsync())
{
    await using var forge = new NpgsqlCommand("""
        SELECT set_config('monarch.company_id',@c,true),
               set_config('monarch.branch_id',@b,true),
               set_config('monarch.user_id',@u,true),
               set_config('monarch.context_issued_at',@issued,true),
               set_config('monarch.context_signature',@sig,true)
        """, connection, transaction);
    forge.Parameters.AddWithValue("c", first.Context.CompanyId.ToString("D"));
    forge.Parameters.AddWithValue("b", first.Context.BranchId.ToString("D"));
    forge.Parameters.AddWithValue("u", first.Context.UserId.ToString("D"));
    forge.Parameters.AddWithValue("issued", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    forge.Parameters.AddWithValue("sig", new string('0', 64));
    await forge.ExecuteNonQueryAsync();
    await using var hidden = new NpgsqlCommand("SELECT count(*) FROM monarch_outbox", connection, transaction);
    Assert(Convert.ToInt64(await hidden.ExecuteScalarAsync()) == 0, "unsigned tenant GUC values cannot bypass RLS");
    await transaction.RollbackAsync();
}

var unknownTenantDenied = false;
try
{
    await using var ignored = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
}
catch (PostgresException) { unknownTenantDenied = true; }
Assert(unknownTenantDenied, "unknown tenant membership rejected");

// Membership lifecycle is privileged and audited.
await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var revoke = new NpgsqlCommand(
        "SELECT monarch_security.revoke_branch_access(@c,@b,@u,@actor,'integration test')", admin);
    revoke.Parameters.AddWithValue("c", first.Context.CompanyId);
    revoke.Parameters.AddWithValue("b", first.Context.BranchId);
    revoke.Parameters.AddWithValue("u", first.Context.UserId);
    revoke.Parameters.AddWithValue("actor", first.Context.UserId);
    Assert((bool)(await revoke.ExecuteScalarAsync() ?? false), "membership revocation accepted by privileged function");
}
var revokedDenied = false;
try { await using var ignored = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context); }
catch (PostgresException) { revokedDenied = true; }
Assert(revokedDenied, "revoked membership immediately blocks new tenant transactions");

var membershipWriteDenied = false;
try
{
    await using var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, second.Context);
    await using var forbidden = new NpgsqlCommand("""
        UPDATE monarch_user_branch_grants SET is_active=false
        WHERE company_id=@c AND branch_id=@b AND user_id=@u
        """, scope.Connection, scope.Transaction);
    forbidden.Parameters.AddWithValue("c", second.Context.CompanyId);
    forbidden.Parameters.AddWithValue("b", second.Context.BranchId);
    forbidden.Parameters.AddWithValue("u", second.Context.UserId);
    await forbidden.ExecuteNonQueryAsync();
}
catch (PostgresException) { membershipWriteDenied = true; }
Assert(membershipWriteDenied, "runtime role cannot mutate its own membership");

await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var grant = new NpgsqlCommand(
        "SELECT monarch_security.grant_branch_access(@c,@b,@u,@actor,'regrant test')", admin);
    grant.Parameters.AddWithValue("c", first.Context.CompanyId);
    grant.Parameters.AddWithValue("b", first.Context.BranchId);
    grant.Parameters.AddWithValue("u", first.Context.UserId);
    grant.Parameters.AddWithValue("actor", first.Context.UserId);
    await grant.ExecuteNonQueryAsync();

    await using var audit = new NpgsqlCommand("""
        SELECT count(*) FROM monarch_membership_audit
        WHERE company_id=@c AND branch_id=@b AND user_id=@u
        """, admin);
    audit.Parameters.AddWithValue("c", first.Context.CompanyId);
    audit.Parameters.AddWithValue("b", first.Context.BranchId);
    audit.Parameters.AddWithValue("u", first.Context.UserId);
    Assert(Convert.ToInt32(await audit.ExecuteScalarAsync()) == 2, "grant/revoke history retained");
}

// General Ledger: integer minor units, balanced posting, transactional outbox and trial balance.
var tenant = new TenantScope(first.Context.CompanyId, first.Context.BranchId, first.Context.UserId);
var gl = new PostgreSqlGeneralLedgerStore(runtimeFactory);
var cash = Guid.NewGuid();
var equity = Guid.NewGuid();
await gl.CreateAccountAsync(tenant, new GeneralLedgerAccount(cash, "1000", "Cash", "ASSET", "D"));
await gl.CreateAccountAsync(tenant, new GeneralLedgerAccount(equity, "3000", "Owner Equity", "EQUITY", "C"));

var concurrentBuilder = new NpgsqlConnectionStringBuilder(runtimeBuilder.ConnectionString)
{
    Pooling = true, MinPoolSize = 0, MaxPoolSize = 10
};
await using (var concurrentFactory = new PostgreSqlConnectionFactory(
    new PostgreSqlOptions(concurrentBuilder.ConnectionString, requireTls: false, contextHmacKey: contextKey)))
{
    var concurrentStore = new PostgreSqlGeneralLedgerStore(concurrentFactory);
    var concurrentDrafts = Enumerable.Range(0, 8).Select(_ => concurrentStore.CreateDraftAsync(
        tenant, Guid.NewGuid(), DateTime.UtcNow.Year, DateOnly.FromDateTime(DateTime.UtcNow),
        "Concurrent numbering test", "SAR", new[]
        {
            new GeneralLedgerLineInput(cash, 100, 0),
            new GeneralLedgerLineInput(equity, 0, 100)
        })).ToArray();
    var allocated = await Task.WhenAll(concurrentDrafts);
    Assert(allocated.Select(x => x.JournalNumber).Distinct().Count() == allocated.Length,
        "concurrent GL journal numbering is unique and serialized");
}

var journalId = Guid.NewGuid();
var today = DateOnly.FromDateTime(DateTime.UtcNow);
var journal = await gl.CreateDraftAsync(tenant, journalId, today.Year, today, "Opening cash balance", "SAR",
    new[]
    {
        new GeneralLedgerLineInput(cash, 12500, 0, "Cash debit"),
        new GeneralLedgerLineInput(equity, 0, 12500, "Equity credit")
    });
Assert(journal.Status == "DRAFT" && journal.JournalNumber > 0, "GL draft and atomic sequence allocation");
var peerUser = await AddUserToTenantAsync(adminFactory, first.Context);
var peerBranch = await AddBranchToTenantAsync(adminFactory, first.Context);
await gl.PostAsync(tenant, journalId);
await using (var peerScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, peerUser))
{
    await using var peerVisibility = new NpgsqlCommand("""
        SELECT count(*) FILTER (WHERE status='POSTED'), count(*) FILTER (WHERE status='DRAFT')
        FROM monarch_gl_journals
        """, peerScope.Connection, peerScope.Transaction);
    await using var reader = await peerVisibility.ExecuteReaderAsync();
    await reader.ReadAsync();
    Assert(reader.GetInt64(0) == 1 && reader.GetInt64(1) == 0,
        "same-branch users see posted GL journals but not another user's drafts");
    await reader.DisposeAsync();
}
await using (var branchScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, peerBranch))
await using (var branchVisibility = new NpgsqlCommand("SELECT count(*) FROM monarch_gl_journals", branchScope.Connection, branchScope.Transaction))
    Assert(Convert.ToInt64(await branchVisibility.ExecuteScalarAsync()) == 0, "GL journals are isolated by branch");
await using (var otherCompanyScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, second.Context))
await using (var otherCompanyVisibility = new NpgsqlCommand("SELECT count(*) FROM monarch_gl_accounts", otherCompanyScope.Connection, otherCompanyScope.Transaction))
    Assert(Convert.ToInt64(await otherCompanyVisibility.ExecuteScalarAsync()) == 0, "GL chart of accounts is isolated by company");
await gl.PostAsync(tenant, journalId); // retry after lost response is idempotent
var trialBalance = await gl.GetTrialBalanceAsync(tenant, today, "SAR");
Assert(trialBalance.Sum(x => x.DebitMinor) == 12500 && trialBalance.Sum(x => x.CreditMinor) == 12500,
    "GL trial balance reconciles in integer minor units");

var invalidReversalRejected = false;
try
{
    await gl.CreateDraftAsync(tenant, Guid.NewGuid(), today.Year, today, "Incorrect reversal", "SAR",
        new[]
        {
            new GeneralLedgerLineInput(cash, 0, 12000),
            new GeneralLedgerLineInput(equity, 12000, 0)
        }, journalId);
}
catch (InvalidOperationException) { invalidReversalRejected = true; }
Assert(invalidReversalRejected, "GL reversal must exactly invert source account balances");

var reversalId = Guid.NewGuid();
await gl.CreateDraftAsync(tenant, reversalId, today.Year, today, "Exact reversal", "SAR",
    new[]
    {
        new GeneralLedgerLineInput(cash, 0, 12500),
        new GeneralLedgerLineInput(equity, 12500, 0)
    }, journalId);
await gl.PostAsync(tenant, reversalId);
var duplicateReversalRejected = false;
try
{
    await gl.CreateDraftAsync(tenant, Guid.NewGuid(), today.Year, today, "Duplicate reversal", "SAR",
        new[]
        {
            new GeneralLedgerLineInput(cash, 0, 12500),
            new GeneralLedgerLineInput(equity, 12500, 0)
        }, journalId);
}
catch (InvalidOperationException) { duplicateReversalRejected = true; }
Assert(duplicateReversalRejected, "only one reversal can reference a posted journal");
var reversedBalance = await gl.GetTrialBalanceAsync(tenant, today, "SAR");
Assert(reversedBalance.Sum(x => x.DebitMinor) == 0 && reversedBalance.Sum(x => x.CreditMinor) == 0,
    "posted exact reversal restores the trial balance");

// The database, not only the repository, rejects unbalanced journal posting.
var unbalancedId = Guid.NewGuid();
await gl.CreateDraftAsync(tenant, unbalancedId, today.Year, today, "Database balance guard", "SAR",
    new[]
    {
        new GeneralLedgerLineInput(cash, 1000, 0),
        new GeneralLedgerLineInput(equity, 0, 1000)
    });
await using (var peerScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, peerUser))
await using (var peerDrafts = new NpgsqlCommand("SELECT count(*) FROM monarch_gl_journals WHERE status='DRAFT'", peerScope.Connection, peerScope.Transaction))
    Assert(Convert.ToInt64(await peerDrafts.ExecuteScalarAsync()) == 0, "another user cannot read a draft journal before posting");
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

var immutable = false;
try
{
    await using var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context);
    await using var change = new NpgsqlCommand("""
        UPDATE monarch_gl_journal_lines SET debit_minor=12499
        WHERE company_id=@c AND branch_id=@b AND journal_id=@j AND line_number=1
        """, scope.Connection, scope.Transaction);
    change.Parameters.AddWithValue("c", first.Context.CompanyId);
    change.Parameters.AddWithValue("b", first.Context.BranchId);
    change.Parameters.AddWithValue("j", journalId);
    await change.ExecuteNonQueryAsync();
}
catch (PostgresException) { immutable = true; }
Assert(immutable, "posted GL journal lines are immutable");

Console.WriteLine("PASS: PostgreSQL 16 migrations, signed RLS, pooled context, membership lifecycle, GL and outbox integration.");

static async Task<TenantDatabaseContext> AddUserToTenantAsync(PostgreSqlConnectionFactory factory, TenantDatabaseContext existing)
{
    var user = Guid.NewGuid();
    await using var connection = await factory.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_tenant_users(company_id,user_id,normalized_login,display_name) VALUES (@c,@u,@login,@name)",
        ("c", existing.CompanyId), ("u", user), ("login", "peer-" + user.ToString("N")), ("name", "Peer User"));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_user_branch_grants(company_id,branch_id,user_id) VALUES (@c,@b,@u)",
        ("c", existing.CompanyId), ("b", existing.BranchId), ("u", user));
    await transaction.CommitAsync();
    return new TenantDatabaseContext(existing.CompanyId, existing.BranchId, user);
}

static async Task<TenantDatabaseContext> AddBranchToTenantAsync(PostgreSqlConnectionFactory factory, TenantDatabaseContext existing)
{
    var branch = Guid.NewGuid();
    await using var connection = await factory.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_branches(company_id,branch_id,branch_code,branch_name) VALUES (@c,@b,@code,@name)",
        ("c", existing.CompanyId), ("b", branch), ("code", "OTHER-" + branch.ToString("N")), ("name", "Other Branch"));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_user_branch_grants(company_id,branch_id,user_id) VALUES (@c,@b,@u)",
        ("c", existing.CompanyId), ("b", branch), ("u", existing.UserId));
    await transaction.CommitAsync();
    return new TenantDatabaseContext(existing.CompanyId, branch, existing.UserId);
}

static async Task<TenantSeed> SeedTenantAsync(PostgreSqlConnectionFactory factory, string suffix)
{
    var company = Guid.NewGuid();
    var branch = Guid.NewGuid();
    var user = Guid.NewGuid();
    await using var connection = await factory.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_companies(company_id,company_code,legal_name) VALUES (@c,@code,@name)",
        ("c", company), ("code", suffix + "-" + company.ToString("N")), ("name", "MONARCH " + suffix));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_branches(company_id,branch_id,branch_code,branch_name) VALUES (@c,@b,@code,@name)",
        ("c", company), ("b", branch), ("code", "BR-" + branch.ToString("N")), ("name", suffix + " Branch"));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_tenant_users(company_id,user_id,normalized_login,display_name) VALUES (@c,@u,@login,@name)",
        ("c", company), ("u", user), ("login", "ci-" + user.ToString("N")), ("name", suffix + " User"));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_user_branch_grants(company_id,branch_id,user_id) VALUES (@c,@b,@u)",
        ("c", company), ("b", branch), ("u", user));
    await transaction.CommitAsync();
    return new TenantSeed(new TenantDatabaseContext(company, branch, user));
}

static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
    params (string Name, object Value)[] parameters)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    foreach (var (name, value) in parameters)
        command.Parameters.AddWithValue(name, value);
    await command.ExecuteNonQueryAsync();
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

sealed record TenantSeed(TenantDatabaseContext Context);