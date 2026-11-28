using System.Globalization;
using Microsoft.Data.Sqlite;
using Monarch.Core.Accounting;
using Monarch.Core.Security;

namespace Monarch.Core.Commercial;

public sealed record SupplierPayablesDeadLetterItem(
    Guid IntentId, Guid RecipientUserId, string AlertCode, string Severity,
    int AttemptCount, string? LastErrorCode, DateTimeOffset UpdatedUtc,
    int HistoricalAttempts);

/// <summary>Tenant-scoped dead-letter inspection and audited operator retry queue.</summary>
public sealed class SupplierPayablesAlertDeadLetterStore
{
    private readonly string _connectionString;
    private readonly TenantLedgerStore _ledger;

    public SupplierPayablesAlertDeadLetterStore(string databasePath, TenantLedgerStore ledger)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("مسار قاعدة البيانات مطلوب.", nameof(databasePath));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), ForeignKeys = true }.ToString();
    }

    public IReadOnlyList<SupplierPayablesDeadLetterItem> GetDeadLetters(
        UserSession actor, Guid companyId, Guid branchId, int limit = 100)
    {
        IdentityStore.Require(actor, "users.manage");
        ValidateScope(actor.UserId, companyId, branchId);
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        _ledger.GetEntries(actor.UserId, companyId, branchId);
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT i.id,i.recipient_user_id,i.alert_code,i.severity,s.attempt_count,s.last_error_code,s.updated_utc,
 (SELECT COUNT(*) FROM monarch_supplier_ap_alert_dispatch_attempts a WHERE a.intent_id=i.id)
FROM monarch_supplier_ap_alert_dispatch_state s
JOIN monarch_supplier_ap_alert_delivery_intents i ON i.id=s.intent_id
WHERE i.company_id=$company AND i.branch_id=$branch AND s.status='DeadLetter'
ORDER BY s.updated_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$company", companyId.ToString()); command.Parameters.AddWithValue("$branch", branchId.ToString());
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<SupplierPayablesDeadLetterItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), reader.GetInt32(7)));
        return rows.AsReadOnly();
    }

    public bool QueueManualRetry(UserSession actor, Guid companyId, Guid branchId,
        Guid intentId, string reasonCode, DateTimeOffset? nowUtc = null)
    {
        IdentityStore.Require(actor, "users.manage");
        ValidateScope(actor.UserId, companyId, branchId);
        if (intentId == Guid.Empty) throw new ArgumentException("معرف الإشعار مطلوب.", nameof(intentId));
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 80 ||
            reasonCode.Any(c => !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')))
            throw new ArgumentException("رمز سبب إعادة المحاولة غير صالح.", nameof(reasonCode));
        _ledger.GetEntries(actor.UserId, companyId, branchId);
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using var transaction = connection.BeginTransaction();
        int previousAttempts;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = @"SELECT s.attempt_count FROM monarch_supplier_ap_alert_dispatch_state s
JOIN monarch_supplier_ap_alert_delivery_intents i ON i.id=s.intent_id
WHERE s.intent_id=$intent AND i.company_id=$company AND i.branch_id=$branch AND s.status='DeadLetter';";
            read.Parameters.AddWithValue("$intent", intentId.ToString()); read.Parameters.AddWithValue("$company", companyId.ToString());
            read.Parameters.AddWithValue("$branch", branchId.ToString());
            var value = read.ExecuteScalar();
            if (value is null) { transaction.Rollback(); return false; }
            previousAttempts = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = @"UPDATE monarch_supplier_ap_alert_dispatch_state
SET status='Pending',attempt_count=0,next_attempt_utc=$now,last_error_code=NULL,updated_utc=$now
WHERE intent_id=$intent AND status='DeadLetter' AND EXISTS(
 SELECT 1 FROM monarch_supplier_ap_alert_delivery_intents i
 WHERE i.id=$intent AND i.company_id=$company AND i.branch_id=$branch);";
        update.Parameters.AddWithValue("$now", now); update.Parameters.AddWithValue("$intent", intentId.ToString());
        update.Parameters.AddWithValue("$company", companyId.ToString()); update.Parameters.AddWithValue("$branch", branchId.ToString());
        if (update.ExecuteNonQuery() != 1) { transaction.Rollback(); return false; }
        using var audit = connection.CreateCommand(); audit.Transaction = transaction;
        audit.CommandText = @"INSERT INTO monarch_supplier_ap_alert_dead_letter_actions
(id,intent_id,company_id,branch_id,actor_user_id,action,previous_attempt_count,occurred_utc,reason_code)
VALUES($id,$intent,$company,$branch,$actor,'ManualRetryQueued',$attempts,$now,$reason);";
        audit.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); audit.Parameters.AddWithValue("$intent", intentId.ToString());
        audit.Parameters.AddWithValue("$company", companyId.ToString()); audit.Parameters.AddWithValue("$branch", branchId.ToString());
        audit.Parameters.AddWithValue("$actor", actor.UserId.ToString()); audit.Parameters.AddWithValue("$now", now);
        audit.Parameters.AddWithValue("$attempts", previousAttempts); audit.Parameters.AddWithValue("$reason", reasonCode); audit.ExecuteNonQuery();
        transaction.Commit(); return true;
    }

    private static void ValidateScope(Guid userId, Guid companyId, Guid branchId)
    { if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("معرف المستخدم والشركة والفرع مطلوب."); }
}
