using Microsoft.Data.Sqlite;

namespace Monarch.Core.Security;

/// <summary>Read-only unified tenant audit reader. Every query is constrained to the actor's active company and branch membership.</summary>
public sealed class TenantAuditLogReader
{
    private readonly string _connectionString;

    public TenantAuditLogReader(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, ForeignKeys = true }.ToString();
    }

    public IReadOnlyList<TenantAuditLogEntry> Read(UserSession actor, Guid companyId, Guid branchId,
        DateTime? fromUtc = null, DateTime? toUtc = null, string action = null,
        string entityType = null, Guid? actorUserId = null, int limit = 500)
    {
        IdentityStore.Require(actor, "users.manage");
        if (companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("Company and branch scope are required.");
        if (actorUserId == Guid.Empty) throw new ArgumentException("Actor filter must be a valid user id.", nameof(actorUserId));
        if (limit < 1 || limit > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value) throw new ArgumentException("From date must not exceed to date.");
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var access = connection.CreateCommand())
        {
            access.Transaction = transaction;
            access.CommandText = @"SELECT EXISTS(
SELECT 1 FROM monarch_user_company_access ca
JOIN monarch_user_branch_access ba ON ba.user_id=ca.user_id AND ba.company_id=ca.company_id
JOIN monarch_branches b ON b.id=ba.branch_id AND b.company_id=ba.company_id
WHERE ca.user_id=$user AND ca.company_id=$company AND ba.branch_id=$branch
AND ca.is_active=1 AND ba.is_active=1 AND b.is_active=1);";
            access.Parameters.AddWithValue("$user", actor.UserId.ToString("D"));
            access.Parameters.AddWithValue("$company", companyId.ToString("D"));
            access.Parameters.AddWithValue("$branch", branchId.ToString("D"));
            if (Convert.ToInt64(access.ExecuteScalar()) != 1)
                throw new UnauthorizedAccessException("لا يملك المستخدم وصولاً نشطاً إلى نطاق سجل التدقيق المطلوب.");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT a.id,a.actor_user_id,COALESCE(u.display_name,u.username,'—'),a.action,
 a.entity_type,a.entity_id,a.details,a.occurred_utc
FROM monarch_company_audit_log a
LEFT JOIN monarch_users u ON u.id=a.actor_user_id
WHERE a.company_id=$company AND a.branch_id=$branch
AND ($from IS NULL OR a.occurred_utc >= $from)
AND ($to IS NULL OR a.occurred_utc <= $to)
AND ($action IS NULL OR a.action COLLATE NOCASE LIKE $action ESCAPE '\')
AND ($entity IS NULL OR a.entity_type=$entity COLLATE NOCASE)
AND ($actor IS NULL OR a.actor_user_id=$actor)
ORDER BY a.occurred_utc DESC,a.id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$company", companyId.ToString("D"));
        command.Parameters.AddWithValue("$branch", branchId.ToString("D"));
        command.Parameters.AddWithValue("$from", (object?)fromUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)toUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", (object?)EscapeLike(action) ?? DBNull.Value);
        command.Parameters.AddWithValue("$entity", (object?)(string.IsNullOrWhiteSpace(entityType) ? null : entityType.Trim()) ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", (object?)actorUserId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<TenantAuditLogEntry>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                Guid? actorId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
                Guid? entityId = Guid.TryParse(reader.IsDBNull(5) ? null : reader.GetString(5), out var parsed) ? parsed : (Guid?)null;
                rows.Add(new TenantAuditLogEntry(reader.GetInt64(0), actorId, reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), entityId, reader.IsDBNull(6) ? null : reader.GetString(6),
                    DateTimeOffset.Parse(reader.GetString(7)).UtcDateTime));
            }
        }
        transaction.Commit();
        return rows.AsReadOnly();
    }

    private static string? EscapeLike(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return "%" + value.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }
}

public sealed class TenantAuditLogEntry
{
    public long Id { get; }
    public Guid? ActorUserId { get; }
    public string ActorName { get; }
    public string Action { get; }
    public string EntityType { get; }
    public Guid? EntityId { get; }
    public string Details { get; }
    public DateTime OccurredUtc { get; }

    public TenantAuditLogEntry(long id, Guid? actorUserId, string actorName, string action,
        string entityType, Guid? entityId, string details, DateTime occurredUtc)
    { Id = id; ActorUserId = actorUserId; ActorName = actorName; Action = action; EntityType = entityType; EntityId = entityId; Details = details; OccurredUtc = occurredUtc; }
}
