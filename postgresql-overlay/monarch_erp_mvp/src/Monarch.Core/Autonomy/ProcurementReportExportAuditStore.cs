using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Monarch.Core.Autonomy;

/// <summary>Local append-only, hash-chained audit trail for successfully saved procurement reports.</summary>
public static class ProcurementReportExportAuditStore
{
    private static readonly object Gate = new object();
    private const string Genesis = "GENESIS";

    public static string Append(string auditPath, Guid userId, Guid companyId, Guid branchId,
        string reportType, string format, DateOnly fromDate, DateOnly toDate, string outputPath, byte[] content,
        DateTime? occurredAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(auditPath)) throw new ArgumentException("Audit path is required.", nameof(auditPath));
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("User, company and branch are required.");
        if (string.IsNullOrWhiteSpace(reportType) || string.IsNullOrWhiteSpace(format)) throw new ArgumentException("Report type and format are required.");
        if (toDate < fromDate) throw new ArgumentException("To date must not precede from date.");
        if (string.IsNullOrWhiteSpace(outputPath) || content == null) throw new ArgumentException("Output path and content are required.");

        var payload = string.Join("|", new[] {
            (occurredAtUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            userId.ToString("D"), companyId.ToString("D"), branchId.ToString("D"),
            Encode(reportType), Encode(format), fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Encode(Path.GetFullPath(outputPath)),
            content.Length.ToString(CultureInfo.InvariantCulture), Hash(content)
        });
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        lock (Gate)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(auditPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var previous = GetLastHashOrThrow(auditPath);
            var hash = Hash(Encoding.UTF8.GetBytes(previous + "|" + encoded));
            File.AppendAllText(auditPath, previous + "\t" + encoded + "\t" + hash + Environment.NewLine, new UTF8Encoding(false));
            return hash;
        }
    }

    public static bool Verify(string auditPath, out int entries)
    {
        entries = 0;
        if (!File.Exists(auditPath)) return true;
        var previous = Genesis;
        foreach (var line in File.ReadAllLines(auditPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length != 3 || !string.Equals(parts[0], previous, StringComparison.Ordinal)) return false;
            var expected = Hash(Encoding.UTF8.GetBytes(parts[0] + "|" + parts[1]));
            if (!FixedEquals(expected, parts[2])) return false;
            try { Convert.FromBase64String(parts[1]); } catch (FormatException) { return false; }
            previous = parts[2]; entries++;
        }
        return true;
    }


    public sealed class AuditEntry
    {
        public DateTime OccurredAtUtc { get; set; }
        public Guid UserId { get; set; }
        public Guid CompanyId { get; set; }
        public Guid BranchId { get; set; }
        public string ReportType { get; set; }
        public string Format { get; set; }
        public DateOnly FromDate { get; set; }
        public DateOnly ToDate { get; set; }
        public string OutputPath { get; set; }
        public long ContentBytes { get; set; }
        public string ContentSha256 { get; set; }
        public string ChainHash { get; set; }
    }

    /// <summary>Reads verified audit entries and applies optional identity/format filters.</summary>
    public static System.Collections.Generic.IList<AuditEntry> ReadEntries(string auditPath,
        Guid? companyId = null, Guid? branchId = null, Guid? userId = null, string format = null)
    {
        var result = new System.Collections.Generic.List<AuditEntry>();
        if (!File.Exists(auditPath)) return result;
        lock (Gate)
        {
            if (!Verify(auditPath, out _)) throw new InvalidDataException("Procurement report export audit chain is invalid.");
            var previous = Genesis;
            foreach (var line in File.ReadAllLines(auditPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                var fields = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])).Split('|');
                if (fields.Length != 11) throw new InvalidDataException("Procurement audit payload has an unsupported format.");
                var entry = new AuditEntry
                {
                    OccurredAtUtc = DateTime.Parse(fields[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    UserId = Guid.Parse(fields[1]), CompanyId = Guid.Parse(fields[2]), BranchId = Guid.Parse(fields[3]),
                    ReportType = Decode(fields[4]), Format = Decode(fields[5]),
                    FromDate = DateOnly.ParseExact(fields[6], "yyyy-MM-dd", CultureInfo.InvariantCulture), ToDate = DateOnly.ParseExact(fields[7], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    OutputPath = Decode(fields[8]), ContentBytes = long.Parse(fields[9], CultureInfo.InvariantCulture),
                    ContentSha256 = fields[10], ChainHash = parts[2]
                };
                if ((!companyId.HasValue || entry.CompanyId == companyId.Value) &&
                    (!branchId.HasValue || entry.BranchId == branchId.Value) &&
                    (!userId.HasValue || entry.UserId == userId.Value) &&
                    (string.IsNullOrWhiteSpace(format) || string.Equals(entry.Format, format, StringComparison.OrdinalIgnoreCase)))
                    result.Add(entry);
                previous = parts[2];
            }
        }
        return result;
    }

    public static void ExportVerifiedCsv(string auditPath, string destinationPath,
        Guid? companyId = null, Guid? branchId = null, string format = null)
    {
        var entries = ReadEntries(auditPath, companyId, branchId, null, format);
        var builder = new StringBuilder("OccurredAtUtc,UserId,CompanyId,BranchId,ReportType,Format,FromDate,ToDate,OutputPath,ContentBytes,ContentSha256,ChainHash\r\n");
        foreach (var e in entries)
            builder.Append(string.Join(",", new[] { e.OccurredAtUtc.ToString("o", CultureInfo.InvariantCulture), e.UserId.ToString("D"), e.CompanyId.ToString("D"), e.BranchId.ToString("D"), Csv(e.ReportType), Csv(e.Format), e.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Csv(e.OutputPath), e.ContentBytes.ToString(CultureInfo.InvariantCulture), e.ContentSha256, e.ChainHash })).Append("\r\n");
        File.WriteAllText(destinationPath, "\uFEFF" + builder.ToString(), new UTF8Encoding(false));
    }

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    private static string Csv(string value)
    {
        var safe = value ?? string.Empty;
        if (safe.Length > 0 && "=+-@\t\r".IndexOf(safe[0]) >= 0) safe = "'" + safe;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private static string GetLastHashOrThrow(string path)
    {
        if (!File.Exists(path)) return Genesis;
        if (!Verify(path, out _)) throw new InvalidDataException("Procurement report export audit chain is invalid; append was blocked.");
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var parts = lines[i].Split('\t');
            return parts[2];
        }
        return Genesis;
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create())
        {
            var result = sha.ComputeHash(bytes); var b = new StringBuilder(result.Length * 2);
            foreach (var value in result) b.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return b.ToString();
        }
    }
    private static bool FixedEquals(string left, string right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        var diff = 0; for (var i = 0; i < left.Length; i++) diff |= left[i] ^ right[i]; return diff == 0;
    }
}
