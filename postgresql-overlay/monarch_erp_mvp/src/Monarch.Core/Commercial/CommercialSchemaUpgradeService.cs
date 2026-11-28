using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Monarch.Core.Commercial;

/// <summary>
/// Explicit, operator-approved commercial schema upgrade. Inspect is read-only;
/// Apply requires a new backup path and an explicit confirmation flag.
/// Stop all MONARCH processes before applying an upgrade.
/// </summary>
public sealed class CommercialSchemaUpgradeService
{
    private readonly string _databasePath;
    public CommercialSchemaUpgradeService(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
    }

    public CommercialSchemaUpgradePlan Inspect(string schemaPath)
    {
        if (!File.Exists(_databasePath)) throw new FileNotFoundException("MONARCH database was not found.", _databasePath);
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("Commercial schema was not found.", schemaPath);
        var sql = File.ReadAllText(schemaPath);
        var expected = Regex.Matches(sql, @"CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var c = Open(_databasePath, true))
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using var r = q.ExecuteReader(); while (r.Read()) present.Add(r.GetString(0));
        }
        var missing = expected.Where(x => !present.Contains(x)).ToArray();
        bool sequenceColumn = false;
        if (present.Contains("monarch_stock_movements"))
        {
            using var c = Open(_databasePath, true); using var q = c.CreateCommand();
            q.CommandText = "PRAGMA table_info(monarch_stock_movements);";
            using var r = q.ExecuteReader(); while (r.Read()) if (string.Equals(r.GetString(1), "ledger_sequence", StringComparison.OrdinalIgnoreCase)) sequenceColumn = true;
        }
        var integrity = Integrity(_databasePath);
        return new CommercialSchemaUpgradePlan(_databasePath, Path.GetFullPath(schemaPath), expected.Length,
            expected.Length - missing.Length, missing, present.Contains("monarch_stock_movements") && !sequenceColumn,
            integrity, missing.Length > 0 || (present.Contains("monarch_stock_movements") && !sequenceColumn));
    }

    public CommercialSchemaUpgradeResult Apply(string schemaPath, string backupPath, bool confirmApply)
    {
        if (!confirmApply) throw new InvalidOperationException("Explicit confirmation is required. Run Inspect first, then call Apply with confirmApply=true.");
        if (string.IsNullOrWhiteSpace(backupPath)) throw new ArgumentException("A safety backup path is required.", nameof(backupPath));
        var plan = Inspect(schemaPath);
        if (!string.Equals(plan.Integrity, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Refusing upgrade because current database integrity is not OK: " + plan.Integrity);
        var backup = Path.GetFullPath(backupPath);
        if (string.Equals(backup, _databasePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Backup path must differ from database path.", nameof(backupPath));
        if (File.Exists(backup)) throw new IOException("Refusing to overwrite an existing safety backup: " + backup);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        CopyDatabase(_databasePath, backup);
        if (!string.Equals(Integrity(backup), "ok", StringComparison.OrdinalIgnoreCase))
        { TryDelete(backup); throw new InvalidDataException("Safety backup integrity check failed."); }

        try
        {
            new CommercialStore(_databasePath).Initialize(schemaPath);
            var after = Integrity(_databasePath);
            if (!string.Equals(after, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Upgraded database integrity check failed: " + after);
            var verified = Inspect(schemaPath);
            if (verified.MissingTables.Length != 0 || verified.NeedsMovementSequenceUpgrade)
                throw new InvalidDataException("Upgrade completed but required commercial schema objects remain missing.");
            return new CommercialSchemaUpgradeResult(_databasePath, backup, after, true,
                "Commercial schema upgrade completed. Safety backup retained.");
        }
        catch
        {
            RestoreDatabase(backup, _databasePath);
            throw;
        }
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var b = new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var c = new SqliteConnection(b.ToString()); c.Open(); return c;
    }
    private static string Integrity(string path)
    {
        using var c = Open(path, true); using var q = c.CreateCommand(); q.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(q.ExecuteScalar()) ?? "no result";
    }
    private static void CopyDatabase(string sourcePath, string destinationPath)
    {
        using var source = Open(sourcePath, true); using var destination = Open(destinationPath, false);
        source.BackupDatabase(destination);
    }
    private static void RestoreDatabase(string backupPath, string destinationPath)
    {
        using var source = Open(backupPath, true); using var destination = Open(destinationPath, false);
        source.BackupDatabase(destination);
        if (!string.Equals(Integrity(destinationPath), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Automatic rollback copy did not pass SQLite integrity_check; preserve both database and backup for recovery.");
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

public sealed class CommercialSchemaUpgradePlan
{
    public CommercialSchemaUpgradePlan(string databasePath, string schemaPath, int expectedTableCount, int presentTableCount,
        string[] missingTables, bool needsMovementSequenceUpgrade, string integrity, bool upgradeRecommended)
    { DatabasePath=databasePath; SchemaPath=schemaPath; ExpectedTableCount=expectedTableCount; PresentTableCount=presentTableCount;
      MissingTables=missingTables; NeedsMovementSequenceUpgrade=needsMovementSequenceUpgrade; Integrity=integrity; UpgradeRecommended=upgradeRecommended; }
    public string DatabasePath { get; } public string SchemaPath { get; } public int ExpectedTableCount { get; }
    public int PresentTableCount { get; } public string[] MissingTables { get; }
    public bool NeedsMovementSequenceUpgrade { get; } public string Integrity { get; } public bool UpgradeRecommended { get; }
}
public sealed class CommercialSchemaUpgradeResult
{
    public CommercialSchemaUpgradeResult(string databasePath, string backupPath, string integrity, bool succeeded, string message)
    { DatabasePath=databasePath; BackupPath=backupPath; Integrity=integrity; Succeeded=succeeded; Message=message; }
    public string DatabasePath { get; } public string BackupPath { get; } public string Integrity { get; }
    public bool Succeeded { get; } public string Message { get; }
}
