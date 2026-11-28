using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Autonomy;

public sealed record ProcurementInboxItem(Guid NotificationId, Guid CompanyId, Guid BranchId,
    string EventType, Guid QueueId, Guid PurchaseOrderId, string Title, string Body,
    DateTimeOffset CreatedAt, bool IsRead);

public sealed record ProcurementDecisionTimelineItem(Guid DecisionId, Guid PurchaseOrderId,
    Guid QueueId, string OrderNumber, string Decision, string SupplierId, string CurrencyCode,
    long TotalMinor, string Rationale, string? DecisionNote, Guid DecidedBy, DateTimeOffset DecidedAt);

/// <summary>Tenant-scoped in-app procurement inbox and immutable decision timeline reader.</summary>
public sealed class ProcurementNotificationCenterStore
{
    private readonly string _connectionString;

    public ProcurementNotificationCenterStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public IReadOnlyList<ProcurementInboxItem> ListInbox(Guid userId, Guid companyId, Guid branchId, bool unreadOnly = false)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireTenantMember(c, tx, userId, companyId, branchId);
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT n.id,n.company_id,n.branch_id,n.event_type,n.queue_id,n.purchase_order_id,
n.title,n.body,n.created_utc,CASE WHEN r.notification_id IS NULL THEN 0 ELSE 1 END
FROM monarch_procurement_notification_outbox n
LEFT JOIN monarch_procurement_notification_reads r ON r.notification_id=n.id AND r.user_id=n.recipient_user_id
WHERE n.company_id=$co AND n.branch_id=$br AND n.recipient_user_id=$user
AND ($unread=0 OR r.notification_id IS NULL)
ORDER BY n.created_utc DESC,n.id DESC LIMIT 500;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        q.Parameters.AddWithValue("$user", userId.ToString()); q.Parameters.AddWithValue("$unread", unreadOnly ? 1 : 0);
        var rows = new List<ProcurementInboxItem>(); using var r = q.ExecuteReader();
        while (r.Read()) rows.Add(new ProcurementInboxItem(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)),
            Guid.Parse(r.GetString(2)), r.GetString(3), Guid.Parse(r.GetString(4)), Guid.Parse(r.GetString(5)),
            r.GetString(6), r.GetString(7), DateTimeOffset.Parse(r.GetString(8)), r.GetInt32(9) == 1));
        r.Close(); tx.Commit(); return rows;
    }

    public void MarkRead(Guid userId, Guid companyId, Guid branchId, Guid notificationId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireTenantMember(c, tx, userId, companyId, branchId);
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"INSERT OR IGNORE INTO monarch_procurement_notification_reads
(notification_id,user_id,read_utc)
SELECT id,$user,$now FROM monarch_procurement_notification_outbox
WHERE id=$id AND company_id=$co AND branch_id=$br AND recipient_user_id=$user;";
        q.Parameters.AddWithValue("$user", userId.ToString()); q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        q.Parameters.AddWithValue("$id", notificationId.ToString()); q.Parameters.AddWithValue("$co", companyId.ToString());
        q.Parameters.AddWithValue("$br", branchId.ToString());
        if (q.ExecuteNonQuery() != 1)
        {
            using var check = c.CreateCommand(); check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM monarch_procurement_notification_reads r JOIN monarch_procurement_notification_outbox n ON n.id=r.notification_id WHERE r.notification_id=$id AND r.user_id=$user AND n.company_id=$co AND n.branch_id=$br AND n.recipient_user_id=$user;";
            check.Parameters.AddWithValue("$id", notificationId.ToString()); check.Parameters.AddWithValue("$user", userId.ToString());
            check.Parameters.AddWithValue("$co", companyId.ToString()); check.Parameters.AddWithValue("$br", branchId.ToString());
            if (Convert.ToInt64(check.ExecuteScalar()) != 1) throw new InvalidOperationException("Notification was not found in the current user's tenant inbox.");
        }
        tx.Commit();
    }

    public int MarkAllRead(Guid userId, Guid companyId, Guid branchId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireTenantMember(c, tx, userId, companyId, branchId);
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"INSERT OR IGNORE INTO monarch_procurement_notification_reads(notification_id,user_id,read_utc)
SELECT n.id,$user,$now FROM monarch_procurement_notification_outbox n
WHERE n.company_id=$co AND n.branch_id=$br AND n.recipient_user_id=$user;";
        q.Parameters.AddWithValue("$user", userId.ToString()); q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        var count = q.ExecuteNonQuery(); tx.Commit(); return count;
    }

    public IReadOnlyList<ProcurementDecisionTimelineItem> ListDecisionTimeline(Guid userId, Guid companyId, Guid branchId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        RequirePermission(c, tx, userId, companyId, branchId, "commercial.purchases.approve");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT id,purchase_order_id,queue_id,order_number_snapshot,decision,supplier_id_snapshot,
currency_code_snapshot,total_minor_snapshot,rationale_snapshot,decision_note,decided_by,decided_utc
FROM monarch_procurement_decision_history
WHERE company_id=$co AND branch_id=$br ORDER BY decided_utc DESC,id DESC LIMIT 500;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        var rows = new List<ProcurementDecisionTimelineItem>(); using var r = q.ExecuteReader();
        while (r.Read()) rows.Add(new ProcurementDecisionTimelineItem(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)),
            Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
            r.GetInt64(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), Guid.Parse(r.GetString(10)),
            DateTimeOffset.Parse(r.GetString(11))));
        r.Close(); tx.Commit(); return rows;
    }

    private static void RequireTenantMember(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
WHERE u.id=$user AND u.is_active=1
AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1)
AND EXISTS(SELECT 1 FROM monarch_branches b WHERE b.company_id=$co AND b.id=$br AND b.is_active=1);";
        BindTenant(q, user, company, branch);
        if (Convert.ToInt64(q.ExecuteScalar()) != 1) throw new UnauthorizedAccessException("User is not an active member of this company and branch.");
    }

    private static void RequirePermission(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1
AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1)
AND EXISTS(SELECT 1 FROM monarch_branches b WHERE b.company_id=$co AND b.id=$br AND b.is_active=1);";
        BindTenant(q, user, company, branch); q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) != 1) throw new UnauthorizedAccessException("User lacks the required permission in this company and branch.");
    }

    private static void BindTenant(SqliteCommand q, Guid user, Guid company, Guid branch)
    {
        q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$br", branch.ToString()); q.Parameters.AddWithValue("$user", user.ToString());
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString); c.Open();
        using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c;
    }
}
