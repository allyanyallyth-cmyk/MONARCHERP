using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Autonomy;

public sealed record ProcurementApprovalQueueItem(Guid QueueId, Guid PurchaseOrderId, Guid CompanyId,
    Guid BranchId, string Rationale, DateTimeOffset CreatedAt, string OrderNumber, string SupplierName,
    string CurrencyCode, long TotalMinor, DateTimeOffset? ExpectedAt);

/// <summary>Durable human approval inbox for autonomous purchase-order drafts.</summary>
public sealed class ProcurementApprovalQueueStore
{
    private readonly string _connectionString;
    public ProcurementApprovalQueueStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public Guid EnqueueDraft(Guid actorUserId, Guid companyId, Guid branchId, Guid purchaseOrderId, string rationale)
    {
        if (actorUserId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty || purchaseOrderId == Guid.Empty || string.IsNullOrWhiteSpace(rationale))
            throw new ArgumentException("A complete tenant-scoped draft and traceable rationale are required.");
        using var c = Open(); using var tx = c.BeginTransaction();
        Require(c, tx, actorUserId, companyId, branchId, "commercial.purchases.manage");
        using (var exists = c.CreateCommand())
        {
            exists.Transaction = tx;
            exists.CommandText = "SELECT COUNT(*) FROM monarch_purchase_orders WHERE company_id=$co AND branch_id=$br AND id=$id AND status='Draft';";
            exists.Parameters.AddWithValue("$co", companyId.ToString()); exists.Parameters.AddWithValue("$br", branchId.ToString()); exists.Parameters.AddWithValue("$id", purchaseOrderId.ToString());
            if (Convert.ToInt64(exists.ExecuteScalar()) != 1) throw new InvalidOperationException("Only an existing draft in the supplied tenant/branch can be queued.");
        }
        var queueId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"INSERT INTO monarch_procurement_approval_queue
(id,company_id,branch_id,purchase_order_id,assigned_permission_code,status,rationale,created_by,created_utc)
VALUES($id,$co,$br,$po,'commercial.purchases.approve','Pending',$why,$user,$now);";
            q.Parameters.AddWithValue("$id", queueId.ToString()); q.Parameters.AddWithValue("$co", companyId.ToString());
            q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$po", purchaseOrderId.ToString());
            q.Parameters.AddWithValue("$why", rationale.Trim()); q.Parameters.AddWithValue("$user", actorUserId.ToString()); q.Parameters.AddWithValue("$now", now.ToString("O")); q.ExecuteNonQuery();
        }
        Audit(c, tx, actorUserId, companyId, branchId, "AutonomousPurchaseOrderQueued", purchaseOrderId, "queue=" + queueId);
        NotifyApprovers(c, tx, companyId, branchId, queueId, purchaseOrderId, "ApprovalRequested",
            "طلب اعتماد مشتريات جديد", "يوجد أمر شراء جديد بانتظار مراجعتك.");
        tx.Commit(); return queueId;
    }

    public IReadOnlyList<ProcurementApprovalQueueItem> ListPending(Guid managerUserId, Guid companyId, Guid branchId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Require(c, tx, managerUserId, companyId, branchId, "commercial.purchases.approve");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT q.id,q.purchase_order_id,q.company_id,q.branch_id,q.rationale,q.created_utc,
po.order_number,p.display_name,po.currency_code,po.total_minor,po.expected_utc
FROM monarch_procurement_approval_queue q
JOIN monarch_purchase_orders po ON po.company_id=q.company_id AND po.id=q.purchase_order_id AND po.branch_id=q.branch_id
JOIN monarch_parties p ON p.company_id=po.company_id AND p.id=po.supplier_id
WHERE q.company_id=$co AND q.branch_id=$br AND q.status='Pending'
AND q.assigned_permission_code='commercial.purchases.approve' ORDER BY q.created_utc;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        var items = new List<ProcurementApprovalQueueItem>(); using var r = q.ExecuteReader();
        while (r.Read()) items.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), Guid.Parse(r.GetString(3)),
            r.GetString(4), DateTimeOffset.Parse(r.GetString(5)), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt64(9),
            r.IsDBNull(10) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(10))));
        r.Close(); tx.Commit(); return items;
    }

    public void Decide(Guid managerUserId, Guid companyId, Guid branchId, Guid queueId, bool approve, string? note)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Require(c, tx, managerUserId, companyId, branchId, "commercial.purchases.approve");
        string orderId;
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx; read.CommandText = "SELECT purchase_order_id FROM monarch_procurement_approval_queue WHERE id=$id AND company_id=$co AND branch_id=$br AND status='Pending';";
            read.Parameters.AddWithValue("$id", queueId.ToString()); read.Parameters.AddWithValue("$co", companyId.ToString()); read.Parameters.AddWithValue("$br", branchId.ToString());
            orderId = read.ExecuteScalar() as string ?? throw new InvalidOperationException("Approval item is missing or already decided.");
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx; update.CommandText = @"UPDATE monarch_procurement_approval_queue
SET status=$status,decided_by=$user,decided_utc=$now,decision_note=$note WHERE id=$id AND company_id=$co AND branch_id=$br AND status='Pending';";
            update.Parameters.AddWithValue("$status", approve ? "Approved" : "Rejected"); update.Parameters.AddWithValue("$user", managerUserId.ToString());
            update.Parameters.AddWithValue("$now", now); update.Parameters.AddWithValue("$note", (object?)note?.Trim() ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", queueId.ToString()); update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$br", branchId.ToString());
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Approval decision lost a concurrent update.");
        }
        if (approve)
        {
            using var po = c.CreateCommand(); po.Transaction = tx;
            po.CommandText = @"UPDATE monarch_purchase_orders SET status='Approved',approved_utc=$now,approved_by=$user
WHERE company_id=$co AND branch_id=$br AND id=$po AND status='Draft'
AND EXISTS(SELECT 1 FROM monarch_purchase_order_lines l WHERE l.company_id=$co AND l.order_id=$po);";
            po.Parameters.AddWithValue("$now", now); po.Parameters.AddWithValue("$user", managerUserId.ToString()); po.Parameters.AddWithValue("$co", companyId.ToString());
            po.Parameters.AddWithValue("$br", branchId.ToString()); po.Parameters.AddWithValue("$po", orderId);
            if (po.ExecuteNonQuery() != 1) throw new InvalidOperationException("Purchase order could not be approved; the queue decision will roll back.");
        }
        var decision = approve ? "Approved" : "Rejected";
        var snapshot = ReadDecisionSnapshot(c, tx, companyId, branchId, queueId, orderId);
        using (var history = c.CreateCommand())
        {
            history.Transaction = tx;
            history.CommandText = @"INSERT INTO monarch_procurement_decision_history
(id,company_id,branch_id,queue_id,purchase_order_id,decision,rationale_snapshot,decision_note,
 order_number_snapshot,supplier_id_snapshot,currency_code_snapshot,total_minor_snapshot,decided_by,decided_utc)
VALUES($id,$co,$br,$queue,$po,$decision,$rationale,$note,$number,$supplier,$currency,$total,$user,$now);";
            history.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); history.Parameters.AddWithValue("$co", companyId.ToString());
            history.Parameters.AddWithValue("$br", branchId.ToString()); history.Parameters.AddWithValue("$queue", queueId.ToString());
            history.Parameters.AddWithValue("$po", orderId); history.Parameters.AddWithValue("$decision", decision);
            history.Parameters.AddWithValue("$rationale", snapshot.Rationale); history.Parameters.AddWithValue("$note", (object?)note?.Trim() ?? DBNull.Value);
            history.Parameters.AddWithValue("$number", snapshot.OrderNumber); history.Parameters.AddWithValue("$supplier", snapshot.SupplierId);
            history.Parameters.AddWithValue("$currency", snapshot.CurrencyCode); history.Parameters.AddWithValue("$total", snapshot.TotalMinor);
            history.Parameters.AddWithValue("$user", managerUserId.ToString()); history.Parameters.AddWithValue("$now", now); history.ExecuteNonQuery();
        }
        Audit(c, tx, managerUserId, companyId, branchId, approve ? "AutonomousPurchaseOrderApproved" : "AutonomousPurchaseOrderRejected", Guid.Parse(orderId), note);
        NotifyCreator(c, tx, companyId, branchId, queueId, Guid.Parse(orderId), snapshot.CreatedBy,
            approve ? "ApprovalApproved" : "ApprovalRejected", approve ? "تم اعتماد أمر الشراء" : "تم رفض أمر الشراء",
            approve ? "تم اعتماد أمر الشراء." : "تم رفض أمر الشراء. السبب: " + (string.IsNullOrWhiteSpace(note) ? "لم يذكر سبب." : note.Trim()));
        tx.Commit();
    }


    private sealed class DecisionSnapshot
    {
        public string Rationale = ""; public string OrderNumber = ""; public string SupplierId = "";
        public string CurrencyCode = ""; public long TotalMinor; public string CreatedBy = "";
    }
    private static DecisionSnapshot ReadDecisionSnapshot(SqliteConnection c, SqliteTransaction tx, Guid company, Guid branch, Guid queue, string order)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT aq.rationale,po.order_number,po.supplier_id,po.currency_code,po.total_minor,aq.created_by
FROM monarch_procurement_approval_queue aq JOIN monarch_purchase_orders po
ON po.company_id=aq.company_id AND po.branch_id=aq.branch_id AND po.id=aq.purchase_order_id
WHERE aq.id=$queue AND aq.company_id=$co AND aq.branch_id=$br AND po.id=$po;";
        q.Parameters.AddWithValue("$queue", queue.ToString()); q.Parameters.AddWithValue("$co", company.ToString());
        q.Parameters.AddWithValue("$br", branch.ToString()); q.Parameters.AddWithValue("$po", order);
        using var r = q.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("Decision snapshot source is unavailable.");
        return new DecisionSnapshot { Rationale=r.GetString(0), OrderNumber=r.GetString(1), SupplierId=r.GetString(2),
            CurrencyCode=r.GetString(3), TotalMinor=r.GetInt64(4), CreatedBy=r.GetString(5) };
    }
    private static void NotifyApprovers(SqliteConnection c, SqliteTransaction tx, Guid company, Guid branch, Guid queue, Guid order, string eventType, string title, string body)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT DISTINCT u.id FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id
WHERE u.is_active=1 AND rp.permission_code='commercial.purchases.approve';";
        q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$br", branch.ToString());
        var recipients = new List<string>(); using (var r=q.ExecuteReader()) while(r.Read()) recipients.Add(r.GetString(0));
        foreach (var recipient in recipients) InsertNotification(c,tx,company,branch,recipient,eventType,queue,order,title,body);
    }
    private static void NotifyCreator(SqliteConnection c, SqliteTransaction tx, Guid company, Guid branch, Guid queue, Guid order, string recipient, string eventType, string title, string body)
        => InsertNotification(c,tx,company,branch,recipient,eventType,queue,order,title,body);
    private static void InsertNotification(SqliteConnection c, SqliteTransaction tx, Guid company, Guid branch, string recipient, string eventType, Guid queue, Guid order, string title, string body)
    {
        using var q=c.CreateCommand(); q.Transaction=tx;
        q.CommandText=@"INSERT OR IGNORE INTO monarch_procurement_notification_outbox
(id,company_id,branch_id,recipient_user_id,event_type,queue_id,purchase_order_id,title,body,created_utc,status,attempts)
VALUES($id,$co,$br,$recipient,$event,$queue,$po,$title,$body,$now,'Pending',0);";
        q.Parameters.AddWithValue("$id",Guid.NewGuid().ToString()); q.Parameters.AddWithValue("$co",company.ToString());
        q.Parameters.AddWithValue("$br",branch.ToString()); q.Parameters.AddWithValue("$recipient",recipient);
        q.Parameters.AddWithValue("$event",eventType); q.Parameters.AddWithValue("$queue",queue.ToString());
        q.Parameters.AddWithValue("$po",order.ToString()); q.Parameters.AddWithValue("$title",title); q.Parameters.AddWithValue("$body",body);
        q.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); q.ExecuteNonQuery();
    }

    private static void Require(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1)
AND EXISTS(SELECT 1 FROM monarch_branches b WHERE b.company_id=$co AND b.id=$br AND b.is_active=1);";
        q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$br", branch.ToString()); q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) < 1) throw new UnauthorizedAccessException("User lacks the required permission in this company and branch.");
    }
    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, string action, Guid order, string? details)
    {
        using var a = c.CreateCommand(); a.Transaction = tx;
        a.CommandText = @"INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,$action,'PurchaseOrder',$id,$details,$now);";
        a.Parameters.AddWithValue("$co", co.ToString()); a.Parameters.AddWithValue("$br", br.ToString()); a.Parameters.AddWithValue("$user", user.ToString());
        a.Parameters.AddWithValue("$action", action); a.Parameters.AddWithValue("$id", order.ToString()); a.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value); a.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); a.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c; }
}
