using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Monarch.Core.Autonomy;

/// <summary>Tenant/branch-scoped append-only event journal. Local-file foundation; not a central audit service.</summary>
public static class TenantAuditEventStore
{
    private static readonly object Gate = new object();
    private const string Genesis = "GENESIS";

    public sealed class AuditEvent
    {
        public DateTime OccurredAtUtc { get; set; }
        public Guid UserId { get; set; }
        public Guid CompanyId { get; set; }
        public Guid BranchId { get; set; }
        public string EventType { get; set; }
        public string EntityType { get; set; }
        public string EntityId { get; set; }
        public string Summary { get; set; }
        public string Metadata { get; set; }
        public string ChainHash { get; set; }
    }

    public static string Append(string path, Guid userId, Guid companyId, Guid branchId,
        string eventType, string entityType, string entityId, string summary, string metadata = null,
        DateTime? occurredAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Audit path is required.", nameof(path));
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("User, company and branch are required.");
        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(entityType)) throw new ArgumentException("Event and entity types are required.");
        var fields = new[] {
            (occurredAtUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            userId.ToString("D"), companyId.ToString("D"), branchId.ToString("D"),
            Encode(eventType), Encode(entityType), Encode(entityId ?? string.Empty), Encode(summary ?? string.Empty), Encode(metadata ?? string.Empty)
        };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("|", fields)));
        lock (Gate)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var previous = GetLastHashOrThrow(fullPath);
            var hash = Hash(previous + "|" + encoded);
            File.AppendAllText(fullPath, previous + "\t" + encoded + "\t" + hash + Environment.NewLine, new UTF8Encoding(false));
            return hash;
        }
    }

    public static bool Verify(string path, out int entries)
    {
        entries = 0;
        if (!File.Exists(path)) return true;
        var previous = Genesis;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length != 3 || !string.Equals(parts[0], previous, StringComparison.Ordinal)) return false;
            if (!FixedEquals(Hash(parts[0] + "|" + parts[1]), parts[2])) return false;
            try { var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])); if (decoded.Split('|').Length != 9) return false; }
            catch (FormatException) { return false; }
            previous = parts[2]; entries++;
        }
        return true;
    }

    public static IList<AuditEvent> Read(string path, Guid companyId, Guid branchId,
        DateTime? fromUtc = null, DateTime? toUtc = null, string eventType = null, int limit = 500)
    {
        if (companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("Company and branch scope are required.");
        if (limit < 1 || limit > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new List<AuditEvent>();
        if (!File.Exists(path)) return result;
        lock (Gate)
        {
            if (!Verify(path, out _)) throw new InvalidDataException("Tenant audit event chain is invalid.");
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                var f = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])).Split('|');
                var item = new AuditEvent {
                    OccurredAtUtc = DateTime.Parse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    UserId = Guid.Parse(f[1]), CompanyId = Guid.Parse(f[2]), BranchId = Guid.Parse(f[3]),
                    EventType = Decode(f[4]), EntityType = Decode(f[5]), EntityId = Decode(f[6]),
                    Summary = Decode(f[7]), Metadata = Decode(f[8]), ChainHash = parts[2]
                };
                if (item.CompanyId != companyId || item.BranchId != branchId) continue;
                if (fromUtc.HasValue && item.OccurredAtUtc < fromUtc.Value.ToUniversalTime()) continue;
                if (toUtc.HasValue && item.OccurredAtUtc > toUtc.Value.ToUniversalTime()) continue;
                if (!string.IsNullOrWhiteSpace(eventType) && !string.Equals(item.EventType, eventType, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(item);
            }
        }
        if (result.Count > limit) result.RemoveRange(0, result.Count - limit);
        return result;
    }

    private static string GetLastHashOrThrow(string path)
    {
        if (!File.Exists(path)) return Genesis;
        if (!Verify(path, out _)) throw new InvalidDataException("Tenant audit event chain is invalid; append was blocked.");
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (var i = lines.Length - 1; i >= 0; i--) if (!string.IsNullOrWhiteSpace(lines[i])) return lines[i].Split('\t')[2];
        return Genesis;
    }
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    private static string Hash(string value)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value)); var b = new StringBuilder(bytes.Length * 2);
            foreach (var x in bytes) b.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return b.ToString();
        }
    }
    private static bool FixedEquals(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        var diff = 0; for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0;
    }
}
