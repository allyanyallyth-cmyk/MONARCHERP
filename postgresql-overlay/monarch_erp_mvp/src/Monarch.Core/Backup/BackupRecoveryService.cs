using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Monarch.Core.Backup;

/// <summary>
/// Encrypted, whole-database backup/restore for a local MONARCH SQLite database.
/// Restore must be run while MONARCH and every other process using this database are closed.
/// </summary>
public sealed class BackupRecoveryService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MONARCHB");
    private const byte FormatVersion = 1;
    private const int Pbkdf2Iterations = 310000;
    private const int SaltLength = 16;
    private const int IvLength = 16;
    private const int MacLength = 32;
    private const int MinimumPasswordLength = 12;
    private const int DatabaseOperationLockTimeoutMilliseconds = 30000;
    private readonly string _databasePath;

    public BackupRecoveryService(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
    }

    public BackupResult CreateEncryptedBackup(string destinationPath, string password, int retentionCount = 10)
    {
        return ExecuteWithDatabaseMutex(() => CreateEncryptedBackupCore(destinationPath, password, retentionCount));
    }

    private BackupResult CreateEncryptedBackupCore(string destinationPath, string password, int retentionCount)
    {
        ValidatePassword(password);
        if (retentionCount < 1) throw new ArgumentOutOfRangeException(nameof(retentionCount));
        if (!File.Exists(_databasePath)) throw new FileNotFoundException("MONARCH database was not found.", _databasePath);

        var target = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tempDatabase = Path.Combine(Path.GetDirectoryName(target)!, ".monarch-backup-" + Guid.NewGuid().ToString("N") + ".db");
        var tempEncrypted = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            CreateConsistentSqliteCopy(_databasePath, tempDatabase);
            var integrity = CheckIntegrity(tempDatabase);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite backup integrity check failed: " + integrity);

            var plaintext = File.ReadAllBytes(tempDatabase);
            var encrypted = Encrypt(plaintext, password);
            using (var output = new FileStream(tempEncrypted, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(encrypted, 0, encrypted.Length);
                output.Flush(true);
            }
            if (File.Exists(target)) File.Replace(tempEncrypted, target, null);
            else File.Move(tempEncrypted, target);
            ApplyRetention(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target), retentionCount);
            return new BackupResult(target, DateTime.UtcNow, new FileInfo(target).Length, integrity);
        }
        finally
        {
            TryDelete(tempDatabase);
            TryDelete(tempDatabase + "-wal");
            TryDelete(tempDatabase + "-shm");
            TryDelete(tempEncrypted);
        }
    }

    public BackupVerificationResult VerifyEncryptedBackup(string backupPath, string password)
    {
        try
        {
            ValidatePassword(password);
            var plain = Decrypt(File.ReadAllBytes(Path.GetFullPath(backupPath)), password);
            var temp = Path.Combine(Path.GetTempPath(), "monarch-verify-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                File.WriteAllBytes(temp, plain);
                var result = CheckIntegrity(temp);
                return new BackupVerificationResult(string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase),
                    string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? "Backup is authenticated and SQLite integrity is OK." : "SQLite integrity check failed: " + result);
            }
            finally { TryDelete(temp); TryDelete(temp + "-wal"); TryDelete(temp + "-shm"); }
        }
        catch (CryptographicException) { return new BackupVerificationResult(false, "Backup authentication failed: incorrect password or damaged file."); }
        catch (Exception ex) { return new BackupVerificationResult(false, ex.Message); }
    }

    /// <summary>Creates an encrypted safety copy of the current DB before replacing it with a verified backup.</summary>
    public RestoreResult RestoreEncryptedBackup(string backupPath, string password, string safetyBackupPath)
    {
        return ExecuteWithDatabaseMutex(() => RestoreEncryptedBackupCore(backupPath, password, safetyBackupPath));
    }

    private RestoreResult RestoreEncryptedBackupCore(string backupPath, string password, string safetyBackupPath)
    {
        ValidatePassword(password);
        var encrypted = File.ReadAllBytes(Path.GetFullPath(backupPath));
        var plaintext = Decrypt(encrypted, password); // authenticate before touching the live database
        var restoreTemp = Path.Combine(Path.GetDirectoryName(_databasePath)!, ".monarch-restore-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            File.WriteAllBytes(restoreTemp, plaintext);
            var integrity = CheckIntegrity(restoreTemp);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Refusing to restore: SQLite integrity check failed: " + integrity);

            BackupResult? safety = null;
            if (File.Exists(_databasePath))
                safety = CreateEncryptedBackup(safetyBackupPath, password, 1000);

            // Caller must close all MONARCH processes before restore. Keep a rollback file until post-replacement verification passes.
            var previous = _databasePath + ".pre-restore-" + Guid.NewGuid().ToString("N");
            var previousWal = previous + "-wal";
            var previousShm = previous + "-shm";
            var liveWal = _databasePath + "-wal";
            var liveShm = _databasePath + "-shm";
            var hadPrevious = File.Exists(_databasePath);
            var movedWal = false;
            var movedShm = false;
            var restoreJournal = _databasePath + ".restore-journal";
            WriteRestoreJournal(restoreJournal, previous, hadPrevious, "prepared");
            var restoreCommitted = false;
            try
            {
                // Close idle pooled SQLite handles before replacing the database file.
                // Active connections are not forcibly terminated; the caller must still
                // ensure every MONARCH process has released the database.
                SqliteConnection.ClearAllPools();

                // Quarantine existing SQLite sidecars instead of deleting them before
                // replacement. If restore fails, the old database and its WAL state
                // must be put back together as one rollback set.
                if (File.Exists(liveWal)) { File.Move(liveWal, previousWal); movedWal = true; }
                if (File.Exists(liveShm)) { File.Move(liveShm, previousShm); movedShm = true; }
                if (hadPrevious) File.Replace(restoreTemp, _databasePath, previous);
                else File.Move(restoreTemp, _databasePath);

                var restoredIntegrity = CheckIntegrity(_databasePath);
                if (!string.Equals(restoredIntegrity, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Restored database failed integrity check: " + restoredIntegrity + ". Safety backup: " + (safety?.Path ?? "none"));
                // Persist commit before deleting rollback artifacts. Recovery can
                // then finish cleanup without attaching old sidecars to the new DB.
                WriteRestoreJournal(restoreJournal, previous, hadPrevious, "committed");
                restoreCommitted = true;
                // Once committed is durable, rollback is no longer valid. Cleanup
                // must be strict and leave the journal for startup retry on failure.
                DeleteIfExistsStrict(previous);
                DeleteIfExistsStrict(previousWal);
                DeleteIfExistsStrict(previousShm);
                DeleteIfExistsStrict(restoreJournal);
                return new RestoreResult(_databasePath, safety?.Path ?? string.Empty, restoredIntegrity);
            }
            catch
            {
                // A cleanup failure after the durable commit must not roll back the
                // verified live database or remove the journal needed for retry.
                if (restoreCommitted) throw;

                if (hadPrevious && File.Exists(previous))
                {
                    // Restore the exact pre-operation database file. Keep the rollback
                    // copy until moving it back to the live path has succeeded.
                    TryDelete(_databasePath);
                    File.Move(previous, _databasePath);
                }
                else if (!hadPrevious)
                {
                    // A failed first-time restore must not leave an unverified database
                    // behind when there was no prior live file to roll back to. If the
                    // file is locked or access is denied, preserve the prepared journal
                    // so startup can retry instead of recording a false completion.
                    DeleteIfExistsStrict(_databasePath);
                }
                RestoreSidecar(previousWal, liveWal, movedWal);
                RestoreSidecar(previousShm, liveShm, movedShm);
                TryDelete(restoreJournal);
                throw;
            }
        }
        finally { TryDelete(restoreTemp); TryDelete(restoreTemp + "-wal"); TryDelete(restoreTemp + "-shm"); }
    }

    /// <summary>
    /// Serializes MONARCH backup and restore operations across processes that use
    /// this service for the same database path. All participating MONARCH builds
    /// must use this lock; unrelated SQLite clients are not controlled by it.
    /// </summary>
    private T ExecuteWithDatabaseMutex<T>(Func<T> operation)
    {
        var identity = Encoding.UTF8.GetBytes(_databasePath.ToUpperInvariant());
        byte[] digest;
        using (var sha = SHA256.Create()) digest = sha.ComputeHash(identity);
        var mutexName = "MONARCH_DB_OPERATION_" + BitConverter.ToString(digest).Replace("-", string.Empty);
        using (var mutex = new Mutex(false, mutexName))
        {
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(DatabaseOperationLockTimeoutMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner terminated unexpectedly. The OS grants
                    // ownership to this process; continue under the recovered lock.
                    ownsMutex = true;
                }

                if (!ownsMutex)
                    throw new TimeoutException("Another MONARCH process is performing a backup or restore for this database. Retry after it finishes.");

                RecoverInterruptedRestore();
                return operation();
            }
            finally
            {
                if (ownsMutex) mutex.ReleaseMutex();
            }
        }
    }

    private void RecoverInterruptedRestore()
    {
        var journal = _databasePath + ".restore-journal";
        // A process can terminate after flushing the temporary journal but before
        // atomically publishing it. The temp file is never authoritative: all
        // database/sidecar mutations begin only after the prepared journal is
        // published. Remove only our exact GUID-N temp naming pattern, under the
        // database-operation mutex, and fail closed if cleanup cannot complete.
        CleanupAbandonedRestoreJournalTemps(journal);
        if (!File.Exists(journal)) return;

        var lines = File.ReadAllLines(journal, new UTF8Encoding(false, true));
        string previousPath;
        string phase;
        bool hadPrevious;

        // Version 1 journals are retained for compatibility with packages that
        // may have been interrupted before this upgrade. New journals include a
        // SHA-256 framing checksum so truncation or accidental edits fail closed.
        if (lines.Length == 3)
        {
            previousPath = lines[0];
            if (!bool.TryParse(lines[1], out hadPrevious))
                throw new InvalidDataException("Restore recovery journal is invalid. Database operations are blocked for manual recovery.");
            phase = lines[2];
        }
        else if (lines.Length == 5 && lines[0] == "MONARCH-RESTORE-JOURNAL-V2")
        {
            previousPath = lines[1];
            if (!bool.TryParse(lines[2], out hadPrevious) || !IsValidJournalChecksum(lines))
                throw new InvalidDataException("Restore recovery journal checksum or fields are invalid. Database operations are blocked for manual recovery.");
            phase = lines[3];
        }
        else
        {
            throw new InvalidDataException("Restore recovery journal format is invalid. Database operations are blocked for manual recovery.");
        }

        if (string.IsNullOrWhiteSpace(previousPath) || (phase != "prepared" && phase != "committed"))
            throw new InvalidDataException("Restore recovery journal fields are invalid. Database operations are blocked for manual recovery.");

        var previous = Path.GetFullPath(previousPath);
        var expectedRollbackPrefix = Path.GetFileName(_databasePath) + ".pre-restore-";
        var previousName = Path.GetFileName(previous);
        var rollbackToken = previousName.StartsWith(expectedRollbackPrefix, StringComparison.OrdinalIgnoreCase)
            ? previousName.Substring(expectedRollbackPrefix.Length)
            : string.Empty;
        Guid rollbackId;
        if (!string.Equals(Path.GetDirectoryName(previous), Path.GetDirectoryName(_databasePath), StringComparison.OrdinalIgnoreCase)
            || rollbackToken.Length != 32
            || !Guid.TryParseExact(rollbackToken, "N", out rollbackId))
            throw new InvalidDataException("Restore recovery journal references an unexpected rollback path. Database operations are blocked for manual recovery.");

        var previousWal = previous + "-wal";
        var previousShm = previous + "-shm";
        var liveWal = _databasePath + "-wal";
        var liveShm = _databasePath + "-shm";

        var previousExists = File.Exists(previous);
        var liveExists = File.Exists(_databasePath);
        if (phase == "committed")
        {
            // Use the normalized phase parsed from either V1 or V2. In V2,
            // lines[2] is hadPrevious, not the phase field.
            // A committed journal is only safe to finalize while the verified live
            // database still exists. Preserve rollback artifacts and block startup
            // if the main file disappeared unexpectedly.
            if (!liveExists)
                throw new InvalidDataException("Committed restore journal has no live database. Recovery artifacts were preserved for manual recovery.");
            var committedIntegrity = CheckIntegrity(_databasePath);
            if (!string.Equals(committedIntegrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Committed restore database failed recovery integrity validation: " + committedIntegrity + ". Rollback artifacts were preserved for manual recovery.");
            // Do not discard the journal when rollback-artifact cleanup fails.
            // A later startup must be able to retry cleanup rather than leave
            // untracked recovery files behind.
            DeleteIfExistsStrict(previous);
            DeleteIfExistsStrict(previousWal);
            DeleteIfExistsStrict(previousShm);
            DeleteIfExistsStrict(journal);
            return;
        }

        // Validate the transaction's main-file state before changing anything.
        // With a prior database, either its rollback file or the untouched/restored
        // live file must exist. A first-time restore must never have a rollback file.
        if (hadPrevious && !previousExists && !liveExists)
            throw new InvalidDataException("Prepared restore journal has neither the prior nor a live database. Recovery artifacts were preserved for manual recovery.");
        if (!hadPrevious && previousExists)
            throw new InvalidDataException("First-time restore journal unexpectedly references a prior database. Recovery artifacts were preserved for manual recovery.");

        // A previous main database file proves File.Replace completed. Otherwise
        // the old main file is still live and only sidecars may need restoration.
        if (previousExists)
        {
            TryDelete(_databasePath);
            File.Move(previous, _databasePath);
        }
        else if (!hadPrevious)
        {
            // A first-time restore has no rollback main file. If a live file exists,
            // it can only be the uncommitted replacement and must not be trusted.
            // Deletion is strict: on access/sharing failure keep the journal so a
            // later startup can retry rather than silently accepting the bad file.
            DeleteIfExistsStrict(_databasePath);
        }

        RestoreSidecar(previousWal, liveWal, File.Exists(previousWal));
        RestoreSidecar(previousShm, liveShm, File.Exists(previousShm));
        TryDelete(journal);
    }

    private static void CleanupAbandonedRestoreJournalTemps(string journalPath)
    {
        var directory = Path.GetDirectoryName(journalPath)!;
        var pattern = Path.GetFileName(journalPath) + ".tmp-*";
        foreach (var candidate in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(candidate);
            var prefix = Path.GetFileName(journalPath) + ".tmp-";
            var token = name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : string.Empty;
            Guid temporaryId;
            if (token.Length != 32 || !Guid.TryParseExact(token, "N", out temporaryId)) continue;
            DeleteIfExistsStrict(candidate);
        }
    }

    private static void WriteRestoreJournal(string journalPath, string previousPath, bool hadPrevious, string phase)
    {
        var temporary = journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var fields = new[] { "MONARCH-RESTORE-JOURNAL-V2", previousPath, hadPrevious ? "true" : "false", phase };
        var checksum = ComputeJournalChecksum(fields);
        var createdTemporary = false;
        var publishedTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdTemporary = true;
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    foreach (var field in fields) writer.WriteLine(field);
                    writer.WriteLine(checksum);
                    writer.Flush();
                    stream.Flush(true);
                }
            }
            if (File.Exists(journalPath)) File.Replace(temporary, journalPath, null);
            else File.Move(temporary, journalPath);
            publishedTemporary = true;
        }
        finally
        {
            // Never remove a colliding entry that this invocation did not create.
            if (createdTemporary && !publishedTemporary) TryDelete(temporary);
        }
    }

    private static bool IsValidJournalChecksum(string[] lines)
    {
        if (lines.Length != 5 || lines[4].Length != 64) return false;
        for (var i = 0; i < lines[4].Length; i++)
            if (!Uri.IsHexDigit(lines[4][i])) return false;
        var fields = new[] { lines[0], lines[1], lines[2], lines[3] };
        var expected = ComputeJournalChecksum(fields);
        var difference = 0;
        for (var i = 0; i < expected.Length; i++)
            difference |= char.ToUpperInvariant(expected[i]) ^ char.ToUpperInvariant(lines[4][i]);
        return difference == 0;
    }

    private static string ComputeJournalChecksum(string[] fields)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join("\n", fields));
        using (var sha = SHA256.Create())
        {
            var digest = sha.ComputeHash(payload);
            return BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    private static void RestoreSidecar(string quarantinedPath, string livePath, bool wasMoved)
    {
        if (!wasMoved || !File.Exists(quarantinedPath)) return;
        if (File.Exists(livePath)) File.Delete(livePath);
        File.Move(quarantinedPath, livePath);
    }

    private static void CreateConsistentSqliteCopy(string sourcePath, string destinationPath)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        var destinationBuilder = new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using (var source = new SqliteConnection(sourceBuilder.ToString()))
        using (var destination = new SqliteConnection(destinationBuilder.ToString()))
        {
            source.Open(); destination.Open();
            source.BackupDatabase(destination);
        }
    }

    private static string CheckIntegrity(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            var integrityIssues = new List<string>();
            var integrityResultCount = 0;
            var integrityOkResultCount = 0;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        integrityResultCount++;
                        var result = Convert.ToString(reader.GetValue(0)) ?? "no result";
                        if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                            integrityOkResultCount++;
                        else
                            integrityIssues.Add(result);
                    }
            }
            // An empty result set is not proof of integrity. Fail closed if SQLite
            // does not return its required integrity_check result row.
            if (integrityResultCount == 0)
                integrityIssues.Add("integrity_check returned no result rows");
            else if (integrityResultCount != 1 || integrityOkResultCount != 1)
                integrityIssues.Add("integrity_check did not return exactly one successful result row");

            // Physical page integrity alone does not detect orphaned rows. Validate all
            // declared foreign keys as part of create, verify, and restore as well.
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_key_check;";
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        integrityIssues.Add("foreign_key_check: table=" + Convert.ToString(reader.GetValue(0))
                            + ", rowid=" + Convert.ToString(reader.GetValue(1))
                            + ", parent=" + Convert.ToString(reader.GetValue(2))
                            + ", fk=" + Convert.ToString(reader.GetValue(3)));
            }
            return integrityIssues.Count == 0 ? "ok" : string.Join(" | ", integrityIssues);
        }
    }

    private static byte[] Encrypt(byte[] plaintext, string password)
    {
        var salt = RandomBytes(SaltLength); var iv = RandomBytes(IvLength);
        byte[] keys = DeriveKeys(password, salt);
        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.KeySize = 256; aes.BlockSize = 128; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            aes.Key = Slice(keys, 0, 32); aes.IV = iv;
            using (var transform = aes.CreateEncryptor()) ciphertext = transform.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Magic); writer.Write(FormatVersion); writer.Write(Pbkdf2Iterations);
            writer.Write(salt); writer.Write(iv); writer.Write((long)ciphertext.Length); writer.Write(ciphertext);
            writer.Flush();
            var authenticated = stream.ToArray();
            byte[] mac;
            using (var hmac = new HMACSHA256(Slice(keys, 32, 32))) mac = hmac.ComputeHash(authenticated);
            writer.Write(mac); writer.Flush();
            Array.Clear(keys, 0, keys.Length);
            return stream.ToArray();
        }
    }

    private static byte[] Decrypt(byte[] package, string password)
    {
        const int fixedHeader = 8 + 1 + 4 + SaltLength + IvLength + 8;
        if (package.Length < fixedHeader + 16 + MacLength) throw new InvalidDataException("Backup file is too short.");
        using (var stream = new MemoryStream(package, false))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
        {
            var magic = reader.ReadBytes(Magic.Length);
            if (!FixedEquals(magic, Magic)) throw new InvalidDataException("Not a MONARCH backup file.");
            if (reader.ReadByte() != FormatVersion) throw new InvalidDataException("Unsupported backup format version.");
            var iterations = reader.ReadInt32();
            if (iterations < 100000 || iterations > 2000000) throw new InvalidDataException("Invalid key-derivation parameters.");
            var salt = reader.ReadBytes(SaltLength); var iv = reader.ReadBytes(IvLength);
            var length = reader.ReadInt64();
            if (length < 16 || length > int.MaxValue || stream.Length - stream.Position != length + MacLength)
                throw new InvalidDataException("Invalid backup payload length.");
            var ciphertext = reader.ReadBytes((int)length); var suppliedMac = reader.ReadBytes(MacLength);
            var keys = DeriveKeys(password, salt, iterations);
            try
            {
                var authenticatedLength = package.Length - MacLength;
                var authenticated = new byte[authenticatedLength]; Buffer.BlockCopy(package, 0, authenticated, 0, authenticatedLength);
                byte[] expected;
                using (var hmac = new HMACSHA256(Slice(keys, 32, 32))) expected = hmac.ComputeHash(authenticated);
                if (!FixedEquals(expected, suppliedMac)) throw new CryptographicException("Backup authentication failed.");
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256; aes.BlockSize = 128; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    aes.Key = Slice(keys, 0, 32); aes.IV = iv;
                    using (var transform = aes.CreateDecryptor()) return transform.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                }
            }
            finally { Array.Clear(keys, 0, keys.Length); }
        }
    }

    private static byte[] DeriveKeys(string password, byte[] salt, int iterations = Pbkdf2Iterations)
    {
        // PBKDF2-HMAC-SHA1 is used for compatibility with .NET Framework 4.6.1; encrypt-then-MAC uses HMAC-SHA256.
        using (var derive = new Rfc2898DeriveBytes(password, salt, iterations)) return derive.GetBytes(64);
    }
    private static byte[] RandomBytes(int count) { var bytes = new byte[count]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return bytes; }
    private static byte[] Slice(byte[] source, int offset, int count) { var result = new byte[count]; Buffer.BlockCopy(source, offset, result, 0, count); return result; }
    private static bool FixedEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        var difference = 0; for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i]; return difference == 0;
    }
    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
            throw new ArgumentException("Backup password must contain at least 12 characters.", nameof(password));
    }
    private static void ApplyRetention(string directory, string backupFamily, int retentionCount)
    {
        // Retention is scoped to this backup family; it must never delete another database's backups.
        var files = new DirectoryInfo(directory).GetFiles("*.mbackup")
            .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f.Name), backupFamily, StringComparison.OrdinalIgnoreCase)
                || f.Name.StartsWith(backupFamily + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var old in files.Skip(retentionCount)) TryDelete(old.FullName);
    }
    private static void DeleteIfExistsStrict(string path)
    {
        // File.Delete is already idempotent for a missing file. Avoid File.Exists:
        // it can return false when metadata access fails (for example, access denied),
        // which would incorrectly allow the recovery journal to be discarded.
        // Let real I/O, sharing, and authorization failures propagate to the caller.
        File.Delete(path);
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup; never mask primary operation */ } }
}
