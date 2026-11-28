using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Monarch.Core.Accounting;

/// <summary>
/// Company- and branch-scoped SQLite ledger. Every public operation requires an authenticated
/// user plus an explicit company and branch. Authorization and the corresponding read/write run
/// in the same SQLite transaction so a permission revocation cannot race the ledger operation.
/// Accounts are company-level records, but their access is gated by the selected active branch.
/// </summary>
public sealed class TenantLedgerStore
{
    private readonly string _connectionString;

    public TenantLedgerStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, ForeignKeys = true }.ToString();
    }

    /// <summary>Run after ledger-storage.sql, security.sql and tenancy.sql have initialized the shared database.</summary>
    public void Initialize(string? schemaPath = null)
    {
        schemaPath ??= Path.Combine(AppContext.BaseDirectory, "database", "tenant-accounting.sql");
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("MONARCH tenant accounting schema was not found.", schemaPath);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(schemaPath);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the company chart only when the user is authorized for the selected branch.</summary>
    public IReadOnlyCollection<Account> GetAccounts(Guid userId, Guid companyId, Guid branchId)
        => ReadAuthorized(userId, companyId, branchId, (connection, transaction) =>
            LoadAccounts(connection, transaction, companyId));

    public IReadOnlyCollection<JournalEntry> GetEntries(Guid userId, Guid companyId, Guid branchId)
        => ReadAuthorized(userId, companyId, branchId, (connection, transaction) =>
            LoadLedger(connection, transaction, companyId, branchId).Entries);

    public TrialBalance GetTrialBalance(Guid userId, Guid companyId, Guid branchId, DateOnly from, DateOnly to)
        => ReadAuthorized(userId, companyId, branchId, (connection, transaction) =>
            LoadLedger(connection, transaction, companyId, branchId).GetTrialBalance(from, to));

    public IncomeStatement GetIncomeStatement(Guid userId, Guid companyId, Guid branchId, DateOnly from, DateOnly to)
        => ReadAuthorized(userId, companyId, branchId, (connection, transaction) =>
            LoadLedger(connection, transaction, companyId, branchId).GetIncomeStatement(from, to));

    public BalanceSheet GetBalanceSheet(Guid userId, Guid companyId, Guid branchId, DateOnly asOf)
        => ReadAuthorized(userId, companyId, branchId, (connection, transaction) =>
            LoadLedger(connection, transaction, companyId, branchId).GetBalanceSheet(asOf));

    public void AddAccount(Guid userId, Guid companyId, Guid branchId, Account account)
    {
        if (account is null) throw new ArgumentNullException(nameof(account));
        Mutate(userId, companyId, branchId, book => { book.AddAccount(account); return true; },
            "TenantAccountCreated", "TenantAccount", _ => account.Id.ToString("D"));
    }

    public JournalEntry CreateDraft(Guid userId, Guid companyId, Guid branchId, string number, DateOnly date,
        string description, IEnumerable<JournalLine> lines)
        => Mutate(userId, companyId, branchId, book => book.CreateDraft(number, date, description, lines),
            "TenantJournalDraftCreated", "TenantJournal", entry => ((JournalEntry)(object)entry).Id.ToString("D"));

    /// <summary>Posts a journal only if it belongs to the exact selected company/branch context.</summary>
    public JournalEntry Post(Guid userId, Guid companyId, Guid selectedBranchId, Guid entryId, DateTimeOffset? postedAt = null)
        => MutateJournal(userId, companyId, selectedBranchId, entryId,
            book => book.Post(entryId, postedAt), "TenantJournalPosted");

    /// <summary>Reverses a journal only if it belongs to the exact selected company/branch context.</summary>
    public JournalEntry Reverse(Guid userId, Guid companyId, Guid selectedBranchId, Guid originalEntryId,
        string reversalNumber, DateOnly reversalDate, string reason, DateTimeOffset? postedAt = null)
        => MutateJournal(userId, companyId, selectedBranchId, originalEntryId,
            book => book.Reverse(originalEntryId, reversalNumber, reversalDate, reason, postedAt), "TenantJournalReversed");

    private T ReadAuthorized<T>(Guid userId, Guid companyId, Guid branchId,
        Func<SqliteConnection, SqliteTransaction, T> read)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireBranchAccess(connection, transaction, userId, companyId, branchId, write: false);
        var result = read(connection, transaction);
        transaction.Commit();
        return result;
    }

    private T Mutate<T>(Guid userId, Guid companyId, Guid branchId, Func<LedgerBook, T> operation,
        string auditAction, string entityType, Func<T, string> entityId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireBranchAccess(connection, transaction, userId, companyId, branchId, write: true);
        var book = LoadLedger(connection, transaction, companyId, branchId);
        var result = operation(book);
        PersistChanges(connection, transaction, companyId, branchId, book);
        InsertAudit(connection, transaction, userId, companyId, branchId, auditAction, entityType, entityId(result));
        transaction.Commit();
        return result;
    }

    private JournalEntry MutateJournal(Guid userId, Guid companyId, Guid selectedBranchId, Guid journalId,
        Func<LedgerBook, JournalEntry> operation, string auditAction)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        // Authorize the selected branch before resolving the journal ID, then keep both checks
        // and the mutation in the same transaction to avoid cross-branch access and TOCTOU races.
        RequireBranchAccess(connection, transaction, userId, companyId, selectedBranchId, write: true);
        var actualBranch = GetEntryBranch(connection, transaction, companyId, journalId);
        if (actualBranch != selectedBranchId)
            throw new UnauthorizedAccessException("Journal is outside the selected branch context.");
        var book = LoadLedger(connection, transaction, companyId, selectedBranchId);
        var result = operation(book);
        PersistChanges(connection, transaction, companyId, selectedBranchId, book);
        InsertAudit(connection, transaction, userId, companyId, selectedBranchId, auditAction, "TenantJournal", result.Id.ToString("D"));
        transaction.Commit();
        return result;
    }

    private static IReadOnlyCollection<Account> LoadAccounts(SqliteConnection connection, SqliteTransaction transaction, Guid companyId)
    {
        var accounts = new List<Account>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT id,code,name,account_type,is_active FROM monarch_tenant_accounts
WHERE company_id=$company ORDER BY code;";
        command.Parameters.AddWithValue("$company", companyId.ToString());
        using var reader = command.ExecuteReader();
        while (reader.Read()) accounts.Add(new Account(Guid.Parse(reader.GetString(0)), reader.GetString(1),
            reader.GetString(2), Enum.Parse<AccountType>(reader.GetString(3)), reader.GetInt64(4) == 1));
        return accounts.AsReadOnly();
    }

    private static LedgerBook LoadLedger(SqliteConnection connection, SqliteTransaction transaction, Guid companyId, Guid branchId)
    {
        var accounts = LoadAccounts(connection, transaction, companyId);
        var company = companyId.ToString();
        var rows = new List<(Guid Id, string Number, string Date, string Description, string Status, string? Reversal, string? Posted)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"SELECT id,number,business_date,description,status,reversal_of_id,posted_at_utc
FROM monarch_tenant_journals WHERE company_id=$company AND branch_id=$branch ORDER BY business_date,number;";
            command.Parameters.AddWithValue("$company", company);
            command.Parameters.AddWithValue("$branch", branchId.ToString());
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        var entries = rows.Select(row => new JournalEntry(row.Id, row.Number,
            DateOnly.ParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), row.Description,
            Enum.Parse<JournalStatus>(row.Status), LoadLines(connection, transaction, company, row.Id),
            row.Reversal is null ? null : Guid.Parse(row.Reversal),
            row.Posted is null ? null : DateTimeOffset.Parse(row.Posted, CultureInfo.InvariantCulture))).ToList();
        return LedgerBook.Restore(accounts, entries);
    }

    private static IReadOnlyList<JournalLine> LoadLines(SqliteConnection connection, SqliteTransaction transaction,
        string companyId, Guid journalId)
    {
        var lines = new List<JournalLine>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT account_id,debit_minor,credit_minor FROM monarch_tenant_journal_lines
WHERE company_id=$company AND journal_id=$journal ORDER BY id;";
        command.Parameters.AddWithValue("$company", companyId);
        command.Parameters.AddWithValue("$journal", journalId.ToString());
        using var reader = command.ExecuteReader();
        while (reader.Read()) lines.Add(new JournalLine(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetInt64(2)));
        return lines.AsReadOnly();
    }

    private static void PersistChanges(SqliteConnection connection, SqliteTransaction transaction,
        Guid companyId, Guid branchId, LedgerBook book)
    {
        var company = companyId.ToString();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO monarch_tenant_accounts(id,company_id,code,name,account_type,is_active,created_utc)
VALUES($id,$company,$code,$name,$type,$active,$created)
ON CONFLICT(id) DO UPDATE SET name=excluded.name,is_active=excluded.is_active
WHERE monarch_tenant_accounts.company_id=excluded.company_id;";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var code = command.Parameters.Add("$code", SqliteType.Text);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var type = command.Parameters.Add("$type", SqliteType.Text);
            var active = command.Parameters.Add("$active", SqliteType.Integer);
            var companyParameter = command.Parameters.Add("$company", SqliteType.Text);
            var created = command.Parameters.Add("$created", SqliteType.Text);
            foreach (var account in book.Accounts)
            {
                id.Value = account.Id.ToString(); code.Value = account.Code; name.Value = account.Name;
                type.Value = account.Type.ToString(); active.Value = account.IsActive ? 1 : 0;
                companyParameter.Value = company;
                created.Value = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }
        }

        var existing = new HashSet<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM monarch_tenant_journals WHERE company_id=$company;";
            command.Parameters.AddWithValue("$company", company);
            using var reader = command.ExecuteReader(); while (reader.Read()) existing.Add(Guid.Parse(reader.GetString(0)));
        }
        foreach (var entry in book.Entries.Where(e => !existing.Contains(e.Id)))
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO monarch_tenant_journals
(id,company_id,branch_id,number,business_date,description,status,reversal_of_id,posted_at_utc,created_utc)
VALUES($id,$company,$branch,$number,$date,$description,'Draft',$reversal,NULL,$created);";
                command.Parameters.AddWithValue("$id", entry.Id.ToString()); command.Parameters.AddWithValue("$company", company);
                command.Parameters.AddWithValue("$branch", branchId.ToString()); command.Parameters.AddWithValue("$number", entry.Number);
                command.Parameters.AddWithValue("$date", entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$description", entry.Description);
                command.Parameters.AddWithValue("$reversal", entry.ReversalOfId?.ToString() ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
            foreach (var line in entry.Lines)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = @"INSERT INTO monarch_tenant_journal_lines(company_id,journal_id,account_id,debit_minor,credit_minor)
VALUES($company,$journal,$account,$debit,$credit);";
                command.Parameters.AddWithValue("$company", company); command.Parameters.AddWithValue("$journal", entry.Id.ToString());
                command.Parameters.AddWithValue("$account", line.AccountId.ToString()); command.Parameters.AddWithValue("$debit", line.DebitMinor);
                command.Parameters.AddWithValue("$credit", line.CreditMinor); command.ExecuteNonQuery();
            }
        }
        foreach (var entry in book.Entries.Where(e => e.Status != JournalStatus.Draft).OrderBy(e => e.Status == JournalStatus.Reversed ? 1 : 0))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"UPDATE monarch_tenant_journals SET status=$status,posted_at_utc=$posted
WHERE company_id=$company AND branch_id=$branch AND id=$id AND status<>$status;";
            command.Parameters.AddWithValue("$status", entry.Status.ToString());
            command.Parameters.AddWithValue("$posted", entry.PostedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$company", company); command.Parameters.AddWithValue("$branch", branchId.ToString());
            command.Parameters.AddWithValue("$id", entry.Id.ToString());
            command.ExecuteNonQuery();
        }
    }

    private static Guid GetEntryBranch(SqliteConnection connection, SqliteTransaction transaction, Guid companyId, Guid journalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT branch_id FROM monarch_tenant_journals WHERE company_id=$company AND id=$journal;";
        command.Parameters.AddWithValue("$company", companyId.ToString());
        command.Parameters.AddWithValue("$journal", journalId.ToString());
        var value = command.ExecuteScalar();
        return value is string branch ? Guid.Parse(branch) : throw new KeyNotFoundException("Journal was not found in the selected company.");
    }

    private static void InsertAudit(SqliteConnection connection, SqliteTransaction transaction,
        Guid userId, Guid companyId, Guid branchId, string action, string entityType, string entityId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($company,$branch,$actor,$action,$entity,$id,$details,$occurred);";
        command.Parameters.AddWithValue("$company", companyId.ToString("D"));
        command.Parameters.AddWithValue("$branch", branchId.ToString("D"));
        command.Parameters.AddWithValue("$actor", userId.ToString("D"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$entity", entityType);
        command.Parameters.AddWithValue("$id", entityId);
        command.Parameters.AddWithValue("$details", "Recorded atomically with tenant ledger mutation.");
        command.Parameters.AddWithValue("$occurred", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void RequireBranchAccess(SqliteConnection connection, SqliteTransaction transaction,
        Guid userId, Guid companyId, Guid branchId, bool write)
    {
        if (userId == Guid.Empty) throw new UnauthorizedAccessException("Authenticated user is required.");
        if (companyId == Guid.Empty) throw new ArgumentException("Company ID is required.", nameof(companyId));
        if (branchId == Guid.Empty) throw new ArgumentException("Branch ID is required.", nameof(branchId));

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT a.role_code,
CASE WHEN a.role_code='Administrator' COLLATE NOCASE OR EXISTS(
 SELECT 1 FROM monarch_user_branch_access ba
 WHERE ba.user_id=a.user_id AND ba.company_id=a.company_id AND ba.branch_id=b.id AND ba.is_active=1)
THEN 1 ELSE 0 END AS has_branch_access
FROM monarch_user_company_access a
JOIN monarch_users u ON u.id=a.user_id AND u.is_active=1
JOIN monarch_companies c ON c.id=a.company_id AND c.is_active=1
JOIN monarch_branches b ON b.id=$branch AND b.company_id=c.id AND b.is_active=1
WHERE a.user_id=$user AND a.company_id=$company AND a.is_active=1;";
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$company", companyId.ToString());
        command.Parameters.AddWithValue("$branch", branchId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new UnauthorizedAccessException("User has no active access to the selected company and branch.");
        var role = reader.GetString(0);
        var hasBranchAccess = reader.GetInt64(1) == 1;
        if (!hasBranchAccess) throw new UnauthorizedAccessException("User has no active access to the selected branch.");
        if (write && !string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(role, "Accountant", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Company role does not permit accounting changes.");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery(); return connection;
    }
}
