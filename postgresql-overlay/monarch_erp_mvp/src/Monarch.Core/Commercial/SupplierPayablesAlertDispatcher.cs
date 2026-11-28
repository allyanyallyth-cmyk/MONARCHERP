using System.Globalization;
using Microsoft.Data.Sqlite;
using Monarch.Core.Accounting;

namespace Monarch.Core.Commercial;

public sealed record SupplierPayablesAlertDispatchClaim(
    Guid IntentId, Guid LeaseToken, Guid RecipientUserId, string AlertCode,
    string Severity, int EscalationLevel, int AttemptNumber, DateTimeOffset LeaseExpiresUtc);

public sealed record SupplierPayablesAlertDispatchResult(
    bool Accepted, string Status, int AttemptCount, DateTimeOffset? NextAttemptUtc);

/// <summary>
/// Claims tenant-scoped in-app alert intents and records immutable attempt outcomes.
/// It deliberately does not call external transports; a trusted in-app adapter must
/// consume claims and report a bounded outcome through CompleteAttempt.
/// </summary>
public sealed class SupplierPayablesAlertDispatcher
{
    private const int MaxAttempts = 8;
    private readonly string _connectionString;
    private readonly TenantLedgerStore _ledger;

    public SupplierPayablesAlertDispatcher(string databasePath, TenantLedgerStore ledger)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("مسار قاعدة البيانات مطلوب.", nameof(databasePath));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), ForeignKeys = true }.ToString();
    }

    public IReadOnlyList<SupplierPayablesAlertDispatchClaim> ClaimDue(
        Guid userId, Guid companyId, Guid branchId, int limit = 25,
        int leaseSeconds = 60, DateTimeOffset? nowUtc = null)
    {
        ValidateScope(userId, companyId, branchId);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (leaseSeconds is < 10 or > 600) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        _ledger.GetEntries(userId, companyId, branchId);
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var expiry = now.AddSeconds(leaseSeconds);
        var claims = new List<SupplierPayablesAlertDispatchClaim>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = @"SELECT s.intent_id,i.recipient_user_id,i.alert_code,i.severity,i.escalation_level,s.attempt_count
FROM monarch_supplier_ap_alert_dispatch_state s
JOIN monarch_supplier_ap_alert_delivery_intents i ON i.id=s.intent_id
WHERE i.company_id=$company AND i.branch_id=$branch AND i.recipient_user_id=$user
AND ((s.status='Pending' AND s.next_attempt_utc<=$now)
 OR (s.status='Claimed' AND s.lease_expires_utc<=$now))
ORDER BY s.next_attempt_utc,i.created_utc LIMIT $limit;";
        select.Parameters.AddWithValue("$company", companyId.ToString());
        select.Parameters.AddWithValue("$branch", branchId.ToString());
        select.Parameters.AddWithValue("$user", userId.ToString());
        select.Parameters.AddWithValue("$now", Format(now));
        select.Parameters.AddWithValue("$limit", limit);
        using var reader = select.ExecuteReader();
        var rows = new List<(Guid id, Guid recipient, string code, string severity, int level, int attempts)>();
        while (reader.Read()) rows.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5)));
        reader.Close();
        foreach (var row in rows)
        {
            var token = Guid.NewGuid();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"UPDATE monarch_supplier_ap_alert_dispatch_state
SET status='Claimed',lease_token=$token,lease_expires_utc=$expiry,updated_utc=$now
WHERE intent_id=$id AND ((status='Pending' AND next_attempt_utc<=$now)
OR (status='Claimed' AND lease_expires_utc<=$now)) AND EXISTS(
 SELECT 1 FROM monarch_supplier_ap_alert_delivery_intents i
 WHERE i.id=$id AND i.company_id=$company AND i.branch_id=$branch AND i.recipient_user_id=$user);";
            update.Parameters.AddWithValue("$token", token.ToString()); update.Parameters.AddWithValue("$expiry", Format(expiry));
            update.Parameters.AddWithValue("$now", Format(now)); update.Parameters.AddWithValue("$id", row.id.ToString());
            update.Parameters.AddWithValue("$company", companyId.ToString()); update.Parameters.AddWithValue("$branch", branchId.ToString()); update.Parameters.AddWithValue("$user", userId.ToString());
            if (update.ExecuteNonQuery() == 1)
                claims.Add(new(row.id, token, row.recipient, row.code, row.severity, row.level, row.attempts + 1, expiry));
        }
        transaction.Commit();
        return claims;
    }

    public SupplierPayablesAlertDispatchResult CompleteAttempt(
        Guid userId, Guid companyId, Guid branchId, Guid intentId, Guid leaseToken,
        bool delivered, string? errorCode = null, DateTimeOffset? nowUtc = null)
    {
        ValidateScope(userId, companyId, branchId);
        if (intentId == Guid.Empty || leaseToken == Guid.Empty) throw new ArgumentException("معرف المهمة ورمز الحجز مطلوبان.");
        if (errorCode is not null && (errorCode.Length is < 1 or > 80 || errorCode.Any(c => !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))))
            throw new ArgumentException("رمز الخطأ غير صالح.", nameof(errorCode));
        if (delivered && errorCode is not null) throw new ArgumentException("لا يقبل التسليم الناجح رمز إخفاق.", nameof(errorCode));
        if (!delivered && errorCode is null) throw new ArgumentException("رمز الإخفاق مطلوب عند عدم التسليم.", nameof(errorCode));
        _ledger.GetEntries(userId, companyId, branchId);
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = @"SELECT s.attempt_count FROM monarch_supplier_ap_alert_dispatch_state s
JOIN monarch_supplier_ap_alert_delivery_intents i ON i.id=s.intent_id
WHERE s.intent_id=$id AND i.company_id=$company AND i.branch_id=$branch AND i.recipient_user_id=$user
AND s.status='Claimed' AND s.lease_token=$token AND s.lease_expires_utc>$now;";
        read.Parameters.AddWithValue("$id", intentId.ToString()); read.Parameters.AddWithValue("$company", companyId.ToString());
        read.Parameters.AddWithValue("$branch", branchId.ToString()); read.Parameters.AddWithValue("$user", userId.ToString()); read.Parameters.AddWithValue("$token", leaseToken.ToString()); read.Parameters.AddWithValue("$now", Format(now));
        var scalar = read.ExecuteScalar();
        if (scalar is null) { transaction.Rollback(); return new(false, "LeaseNotOwned", 0, null); }
        var attempt = Convert.ToInt32(scalar, CultureInfo.InvariantCulture) + 1;
        if (attempt > MaxAttempts) { transaction.Rollback(); return new(false, "AttemptLimitReached", attempt - 1, null); }
        var dead = !delivered && attempt >= MaxAttempts;
        var status = delivered ? "Delivered" : dead ? "DeadLetter" : "Pending";
        DateTimeOffset? next = delivered || dead ? null : now.AddSeconds(Math.Min(900, 15 * (1 << Math.Min(attempt - 1, 6))));
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = @"UPDATE monarch_supplier_ap_alert_dispatch_state SET status=$status,attempt_count=$attempt,
next_attempt_utc=$next,lease_token=NULL,lease_expires_utc=NULL,last_error_code=$error,updated_utc=$now
WHERE intent_id=$id AND status='Claimed' AND lease_token=$token AND lease_expires_utc>$now AND EXISTS(
 SELECT 1 FROM monarch_supplier_ap_alert_delivery_intents i
 WHERE i.id=$id AND i.company_id=$company AND i.branch_id=$branch AND i.recipient_user_id=$user);";
            update.Parameters.AddWithValue("$status", status); update.Parameters.AddWithValue("$attempt", attempt);
            update.Parameters.AddWithValue("$next", Format(next ?? now)); update.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", Format(now)); update.Parameters.AddWithValue("$id", intentId.ToString()); update.Parameters.AddWithValue("$token", leaseToken.ToString());
            update.Parameters.AddWithValue("$company", companyId.ToString()); update.Parameters.AddWithValue("$branch", branchId.ToString()); update.Parameters.AddWithValue("$user", userId.ToString());
            if (update.ExecuteNonQuery() != 1) { transaction.Rollback(); return new(false, "LeaseNotOwned", attempt - 1, null); }
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO monarch_supplier_ap_alert_dispatch_attempts
(id,intent_id,attempt_number,outcome,error_code,attempted_utc,next_attempt_utc)
VALUES($id,$intent,(SELECT COALESCE(MAX(attempt_number),0)+1 FROM monarch_supplier_ap_alert_dispatch_attempts WHERE intent_id=$intent),$outcome,$error,$now,$next);";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); insert.Parameters.AddWithValue("$intent", intentId.ToString());
            insert.Parameters.AddWithValue("$outcome", delivered ? "Delivered" : dead ? "DeadLetter" : "RetryScheduled");
            insert.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value); insert.Parameters.AddWithValue("$now", Format(now));
            insert.Parameters.AddWithValue("$next", (object?)(next is null ? null : Format(next.Value)) ?? DBNull.Value); insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(true, status, attempt, next);
    }

    private static void ValidateScope(Guid userId, Guid companyId, Guid branchId)
    { if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("معرف المستخدم والشركة والفرع مطلوب."); }
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
