using System.Globalization;
using Microsoft.Data.Sqlite;
using Monarch.Core.Accounting;

namespace Monarch.Core.Commercial;

public sealed record SupplierPayablesAlertInboxItem(
    Guid IntentId, string AlertCode, string Severity, int EscalationLevel,
    DateTimeOffset SourceOccurredUtc, DateTimeOffset DeliveredUtc,
    bool Viewed, DateTimeOffset? ViewedUtc, bool Acknowledged, DateTimeOffset? AcknowledgedUtc);

/// <summary>Persistent recipient inbox projection and append-only read markers.
/// Delivery state remains owned by the dispatcher; this store never claims or retries work.</summary>
public sealed class SupplierPayablesAlertInboxStore
{
    private readonly string _connectionString;
    private readonly TenantLedgerStore _ledger;

    public SupplierPayablesAlertInboxStore(string databasePath, TenantLedgerStore ledger)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("مسار قاعدة البيانات مطلوب.", nameof(databasePath));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), ForeignKeys = true }.ToString();
    }

    public IReadOnlyList<SupplierPayablesAlertInboxItem> GetDelivered(Guid userId, Guid companyId, Guid branchId, int limit = 100)
    {
        ValidateScope(userId, companyId, branchId);
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        _ledger.GetEntries(userId, companyId, branchId);
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT i.id,i.alert_code,i.severity,i.escalation_level,i.source_last_occurred_utc,s.updated_utc,
 MAX(CASE WHEN e.action='Viewed' THEN 1 ELSE 0 END),MAX(CASE WHEN e.action='Viewed' THEN e.occurred_utc END),
 MAX(CASE WHEN e.action='Acknowledged' THEN 1 ELSE 0 END),MAX(CASE WHEN e.action='Acknowledged' THEN e.occurred_utc END)
FROM monarch_supplier_ap_alert_delivery_intents i
JOIN monarch_supplier_ap_alert_dispatch_state s ON s.intent_id=i.id
LEFT JOIN monarch_supplier_ap_alert_inbox_events e ON e.intent_id=i.id AND e.company_id=i.company_id AND e.branch_id=i.branch_id AND e.recipient_user_id=i.recipient_user_id
WHERE i.company_id=$company AND i.branch_id=$branch AND i.recipient_user_id=$user AND s.status='Delivered'
GROUP BY i.id ORDER BY s.updated_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$company", companyId.ToString()); command.Parameters.AddWithValue("$branch", branchId.ToString());
        command.Parameters.AddWithValue("$user", userId.ToString()); command.Parameters.AddWithValue("$limit", limit);
        var items = new List<SupplierPayablesAlertInboxItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.GetInt32(6) == 1, reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            reader.GetInt32(8) == 1, reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        return items;
    }

    public void MarkViewed(Guid userId, Guid companyId, Guid branchId, Guid intentId, DateTimeOffset? occurredUtc = null) =>
        Append(userId, companyId, branchId, intentId, "Viewed", occurredUtc ?? DateTimeOffset.UtcNow);

    public void Acknowledge(Guid userId, Guid companyId, Guid branchId, Guid intentId, DateTimeOffset? occurredUtc = null) =>
        Append(userId, companyId, branchId, intentId, "Acknowledged", occurredUtc ?? DateTimeOffset.UtcNow);

    private void Append(Guid userId, Guid companyId, Guid branchId, Guid intentId, string action, DateTimeOffset occurredUtc)
    {
        ValidateScope(userId, companyId, branchId);
        if (intentId == Guid.Empty) throw new ArgumentException("معرف الإشعار مطلوب.", nameof(intentId));
        _ledger.GetEntries(userId, companyId, branchId);
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"INSERT OR IGNORE INTO monarch_supplier_ap_alert_inbox_events
(id,intent_id,company_id,branch_id,recipient_user_id,action,occurred_utc)
SELECT $id,i.id,i.company_id,i.branch_id,i.recipient_user_id,$action,$occurred
FROM monarch_supplier_ap_alert_delivery_intents i
JOIN monarch_supplier_ap_alert_dispatch_state s ON s.intent_id=i.id
WHERE i.id=$intent AND i.company_id=$company AND i.branch_id=$branch AND i.recipient_user_id=$user AND s.status='Delivered';";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); command.Parameters.AddWithValue("$intent", intentId.ToString());
        command.Parameters.AddWithValue("$company", companyId.ToString()); command.Parameters.AddWithValue("$branch", branchId.ToString());
        command.Parameters.AddWithValue("$user", userId.ToString()); command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$occurred", occurredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() == 0)
        {
            using var verify = connection.CreateCommand();
            verify.CommandText = @"SELECT EXISTS(SELECT 1 FROM monarch_supplier_ap_alert_delivery_intents i
JOIN monarch_supplier_ap_alert_dispatch_state s ON s.intent_id=i.id
WHERE i.id=$intent AND i.company_id=$company AND i.branch_id=$branch
AND i.recipient_user_id=$user AND s.status='Delivered');";
            verify.Parameters.AddWithValue("$intent", intentId.ToString()); verify.Parameters.AddWithValue("$company", companyId.ToString());
            verify.Parameters.AddWithValue("$branch", branchId.ToString()); verify.Parameters.AddWithValue("$user", userId.ToString());
            if (Convert.ToInt32(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("الإشعار غير موجود أو لم يُسلّم للمستخدم الحالي.");
        }
    }

    private static void ValidateScope(Guid userId, Guid companyId, Guid branchId)
    { if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("معرف المستخدم والشركة والفرع مطلوب."); }
}
