using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Monarch.Core.Accounting;

/// <summary>Transactional SQLite persistence for the accounting domain. Each public mutation is atomic.</summary>
public sealed class SqliteLedgerStore
{
    private readonly string _connectionString;

    public SqliteLedgerStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, ForeignKeys = true }.ToString();
    }

    public void Initialize(string? schemaPath = null)
    {
        schemaPath ??= Path.Combine(AppContext.BaseDirectory, "database", "ledger-storage.sql");
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("MONARCH ledger schema was not found.", schemaPath);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(schemaPath);
        command.ExecuteNonQuery();
    }

    public string GetBaseCurrency()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT base_currency_code FROM monarch_company_settings WHERE id=1;";
        return (string?)command.ExecuteScalar() ?? throw new InvalidOperationException("Company currency is not configured.");
    }

    public int GetCurrencyExponent(string currencyCode)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT minor_unit_exponent FROM monarch_currencies WHERE code=$code AND is_active=1;";
        command.Parameters.AddWithValue("$code", currencyCode.ToUpperInvariant());
        var value = command.ExecuteScalar();
        return value is null ? throw new KeyNotFoundException($"Currency {currencyCode} is not configured.") : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public void SetBaseCurrency(string currencyCode)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE monarch_company_settings SET base_currency_code=$code WHERE id=1 AND EXISTS(SELECT 1 FROM monarch_currencies WHERE code=$code AND is_active=1);";
        command.Parameters.AddWithValue("$code", currencyCode.ToUpperInvariant());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The selected active currency does not exist.");
        transaction.Commit();
    }

    public IReadOnlyCollection<Account> GetAccounts() => LoadLedger().Accounts;
    public IReadOnlyCollection<JournalEntry> GetEntries() => LoadLedger().Entries;
    public TrialBalance GetTrialBalance(DateOnly from, DateOnly to) => LoadLedger().GetTrialBalance(from, to);
    public IncomeStatement GetIncomeStatement(DateOnly from, DateOnly to) => LoadLedger().GetIncomeStatement(from, to);
    public BalanceSheet GetBalanceSheet(DateOnly asOf) => LoadLedger().GetBalanceSheet(asOf);

    public void AddAccount(Account account) => Mutate(book => book.AddAccount(account));

    public JournalEntry CreateDraft(string number, DateOnly date, string description, IEnumerable<JournalLine> lines)
    {
        JournalEntry? created = null;
        Mutate(book => created = book.CreateDraft(number, date, description, lines));
        return created!;
    }

    public JournalEntry Post(Guid entryId, DateTimeOffset? postedAt = null)
    {
        JournalEntry? posted = null;
        Mutate(book => posted = book.Post(entryId, postedAt));
        return posted!;
    }

    public JournalEntry Reverse(Guid originalEntryId, string reversalNumber, DateOnly reversalDate,
        string reason, DateTimeOffset? postedAt = null)
    {
        JournalEntry? reversal = null;
        Mutate(book => reversal = book.Reverse(originalEntryId, reversalNumber, reversalDate, reason, postedAt));
        return reversal!;
    }

    private void Mutate(Action<LedgerBook> operation)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var before = LoadLedger(connection, transaction);
        operation(before);
        PersistChanges(connection, transaction, before);
        transaction.Commit();
    }

    private LedgerBook LoadLedger()
    {
        using var connection = Open();
        return LoadLedger(connection, null);
    }

    private static LedgerBook LoadLedger(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var accounts = new List<Account>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,code,name,account_type,is_active FROM monarch_accounts ORDER BY code;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                accounts.Add(new Account(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    Enum.Parse<AccountType>(reader.GetString(3)), reader.GetInt64(4) == 1));
        }

        var journalRows = new List<(Guid Id, string Number, string Date, string Description, string Status, string? Reversal, string? Posted)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,number,business_date,description,status,reversal_of_id,posted_at_utc FROM monarch_journals ORDER BY business_date,number;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) journalRows.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        var entries = journalRows.Select(row => new JournalEntry(row.Id, row.Number,
            DateOnly.ParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), row.Description,
            Enum.Parse<JournalStatus>(row.Status), LoadLines(connection, transaction, row.Id),
            row.Reversal is null ? null : Guid.Parse(row.Reversal),
            row.Posted is null ? null : DateTimeOffset.Parse(row.Posted, CultureInfo.InvariantCulture))).ToList();
        return LedgerBook.Restore(accounts, entries);
    }

    private static IReadOnlyList<JournalLine> LoadLines(SqliteConnection connection, SqliteTransaction? transaction, Guid journalId)
    {
        var lines = new List<JournalLine>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT account_id,debit_minor,credit_minor FROM monarch_journal_lines WHERE journal_id=$id ORDER BY id;";
        command.Parameters.AddWithValue("$id", journalId.ToString());
        using var reader = command.ExecuteReader();
        while (reader.Read()) lines.Add(new JournalLine(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetInt64(2)));
        return lines.AsReadOnly();
    }

    private static void PersistChanges(SqliteConnection connection, SqliteTransaction transaction, LedgerBook book)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO monarch_accounts(id,code,name,account_type,is_active) VALUES($id,$code,$name,$type,$active) ON CONFLICT(id) DO UPDATE SET name=excluded.name,is_active=excluded.is_active;";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var code = command.Parameters.Add("$code", SqliteType.Text);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var type = command.Parameters.Add("$type", SqliteType.Text);
            var active = command.Parameters.Add("$active", SqliteType.Integer);
            foreach (var account in book.Accounts)
            {
                id.Value = account.Id.ToString(); code.Value = account.Code; name.Value = account.Name;
                type.Value = account.Type.ToString(); active.Value = account.IsActive ? 1 : 0;
                command.ExecuteNonQuery();
            }
        }

        var existingIds = new HashSet<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM monarch_journals;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) existingIds.Add(Guid.Parse(reader.GetString(0)));
        }

        foreach (var entry in book.Entries.Where(e => !existingIds.Contains(e.Id)))
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO monarch_journals(id,number,business_date,description,status,reversal_of_id,posted_at_utc,created_utc) VALUES($id,$number,$date,$description,'Draft',$reversal,NULL,$created);";
                command.Parameters.AddWithValue("$id", entry.Id.ToString());
                command.Parameters.AddWithValue("$number", entry.Number);
                command.Parameters.AddWithValue("$date", entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$description", entry.Description);
                command.Parameters.AddWithValue("$reversal", entry.ReversalOfId?.ToString() ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
            foreach (var line in entry.Lines)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO monarch_journal_lines(journal_id,account_id,debit_minor,credit_minor) VALUES($journal,$account,$debit,$credit);";
                command.Parameters.AddWithValue("$journal", entry.Id.ToString());
                command.Parameters.AddWithValue("$account", line.AccountId.ToString());
                command.Parameters.AddWithValue("$debit", line.DebitMinor);
                command.Parameters.AddWithValue("$credit", line.CreditMinor);
                command.ExecuteNonQuery();
            }
        }

        foreach (var entry in book.Entries.Where(e => e.Status != JournalStatus.Draft)
                     .OrderBy(e => e.Status == JournalStatus.Reversed ? 1 : 0))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE monarch_journals SET status=$status,posted_at_utc=$posted WHERE id=$id AND status<>$status;";
            command.Parameters.AddWithValue("$status", entry.Status.ToString());
            command.Parameters.AddWithValue("$posted", entry.PostedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$id", entry.Id.ToString());
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }
}
