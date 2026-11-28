namespace Monarch.Core.Backup;

public sealed class BackupResult
{
    public BackupResult(string path, DateTime createdUtc, long encryptedBytes, string integrityResult)
    { Path = path; CreatedUtc = createdUtc; EncryptedBytes = encryptedBytes; IntegrityResult = integrityResult; }
    public string Path { get; }
    public DateTime CreatedUtc { get; }
    public long EncryptedBytes { get; }
    public string IntegrityResult { get; }
}

public sealed class BackupVerificationResult
{
    public BackupVerificationResult(bool isValid, string message)
    { IsValid = isValid; Message = message; }
    public bool IsValid { get; }
    public string Message { get; }
}

public sealed class RestoreResult
{
    public RestoreResult(string databasePath, string safetyBackupPath, string integrityResult)
    { DatabasePath = databasePath; SafetyBackupPath = safetyBackupPath; IntegrityResult = integrityResult; }
    public string DatabasePath { get; }
    public string SafetyBackupPath { get; }
    public string IntegrityResult { get; }
}
