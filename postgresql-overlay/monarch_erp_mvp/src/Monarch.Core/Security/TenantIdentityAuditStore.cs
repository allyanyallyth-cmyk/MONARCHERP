using Microsoft.Data.Sqlite;

namespace Monarch.Core.Security;

/// <summary>Tenant-scoped identity-administration audit backed by the transactional company audit table.</summary>
public sealed class TenantIdentityAuditStore
{
    private readonly string _connectionString;

    public TenantIdentityAuditStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, ForeignKeys = true }.ToString();
    }

    /// <summary>Records a user/role administration event for an explicitly selected company and branch.</summary>
    public void Record(UserSession actor, Guid companyId, Guid branchId, string action, Guid subjectUserId, string details = null)
    {
        IdentityStore.Require(actor, "users.manage");
        if (companyId == Guid.Empty || branchId == Guid.Empty || subjectUserId == Guid.Empty)
            throw new ArgumentException("Company, branch and subject user are required.");
        if (string.IsNullOrWhiteSpace(action) || action.Length > 80) throw new ArgumentException("A valid audit action is required.");
        if (details != null && details.Length > 2000) throw new ArgumentException("Audit details exceed 2000 characters.");

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireCompanyBranchAccess(connection, transaction, actor.UserId, companyId, branchId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($company,$branch,$actor,$action,'User',$subject,$details,$now);";
        command.Parameters.AddWithValue("$company", companyId.ToString("D"));
        command.Parameters.AddWithValue("$branch", branchId.ToString("D"));
        command.Parameters.AddWithValue("$actor", actor.UserId.ToString("D"));
        command.Parameters.AddWithValue("$action", action.Trim());
        command.Parameters.AddWithValue("$subject", subjectUserId.ToString("D"));
        command.Parameters.AddWithValue("$details", (object)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Reads only identity events visible to the authorized actor in the requested tenant branch.</summary>
    public IReadOnlyList<TenantIdentityAuditEntry> Read(UserSession actor, Guid companyId, Guid branchId,
        DateTime? fromUtc = null, DateTime? toUtc = null, string action = null, int limit = 500)
    {
        IdentityStore.Require(actor, "users.manage");
        if (companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("Company and branch scope are required.");
        if (limit < 1 || limit > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value) throw new ArgumentException("From date must not exceed to date.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireCompanyBranchAccess(connection, transaction, actor.UserId, companyId, branchId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT actor_user_id,action,entity_id,details,occurred_utc
FROM monarch_company_audit_log
WHERE company_id=$company AND branch_id=$branch AND entity_type='User'
AND ($from IS NULL OR occurred_utc >= $from) AND ($to IS NULL OR occurred_utc <= $to)
AND ($action IS NULL OR action=$action COLLATE NOCASE)
ORDER BY occurred_utc DESC,id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$company", companyId.ToString("D"));
        command.Parameters.AddWithValue("$branch", branchId.ToString("D"));
        command.Parameters.AddWithValue("$from", (object?)fromUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)toUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", (object)(string.IsNullOrWhiteSpace(action) ? null : action.Trim()) ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var entries = new List<TenantIdentityAuditEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            entries.Add(new TenantIdentityAuditEntry(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                Guid.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)).UtcDateTime));
        transaction.Commit();
        return entries.AsReadOnly();
    }

    private static void RequireCompanyBranchAccess(SqliteConnection connection, SqliteTransaction transaction,
        Guid actor, Guid company, Guid branch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT EXISTS(
SELECT 1 FROM monarch_user_company_access ca
JOIN monarch_user_branch_access ba ON ba.user_id=ca.user_id AND ba.company_id=ca.company_id
JOIN monarch_branches b ON b.id=ba.branch_id AND b.company_id=ba.company_id
WHERE ca.user_id=$user AND ca.company_id=$company AND ba.branch_id=$branch
AND ca.is_active=1 AND ba.is_active=1 AND b.is_active=1);";
        command.Parameters.AddWithValue("$user", actor.ToString("D"));
        command.Parameters.AddWithValue("$company", company.ToString("D"));
        command.Parameters.AddWithValue("$branch", branch.ToString("D"));
        if (Convert.ToInt64(command.ExecuteScalar()) != 1)
            throw new UnauthorizedAccessException("المستخدم لا يملك وصولاً نشطاً إلى المنشأة والفرع المحددين.");
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

public sealed class TenantIdentityAuditEntry
{
    public Guid ActorUserId { get; }
    public string Action { get; }
    public Guid SubjectUserId { get; }
    public string Details { get; }
    public DateTime OccurredUtc { get; }
    public TenantIdentityAuditEntry(Guid actorUserId, string action, Guid subjectUserId, string details, DateTime occurredUtc)
    { ActorUserId = actorUserId; Action = action; SubjectUserId = subjectUserId; Details = details; OccurredUtc = occurredUtc; }
}
