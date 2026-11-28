using System.Globalization;
using Microsoft.Data.Sqlite;
using Monarch.Core.Accounting;

namespace Monarch.Core.Commercial;

/// <summary>
/// Persists idempotent in-app delivery intents after tenant authorization.
/// A separate dispatcher is intentionally required to perform actual delivery.
/// </summary>
public sealed class SupplierPayablesAlertDeliveryStore
{
    private readonly string _connectionString;
    private readonly TenantLedgerStore _ledger;

    public SupplierPayablesAlertDeliveryStore(string databasePath, TenantLedgerStore ledger)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("مسار قاعدة البيانات مطلوب.", nameof(databasePath));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), ForeignKeys = true
        }.ToString();
    }

    public int CreateIntents(Guid companyId, Guid branchId,
        IReadOnlyList<Guid> recipientUserIds,
        IReadOnlyList<SupplierPayablesOperationalAlert> alerts,
        DateTimeOffset? nowUtc = null)
    {
        if (companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("معرف الشركة والفرع مطلوب.");
        if (recipientUserIds is null || recipientUserIds.Count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(recipientUserIds));
        if (alerts is null || alerts.Count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(alerts));
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var decisions = alerts.Select(x => SupplierPayablesAlertDeliveryPolicy.Assess(x, now)).ToArray();
        var recipients = recipientUserIds.Distinct().ToArray();
        if (recipients.Any(x => x == Guid.Empty)) throw new ArgumentException("معرف المستلم غير صالح.", nameof(recipientUserIds));

        // Authorize every recipient before opening the write transaction.
        foreach (var recipient in recipients) _ledger.GetEntries(recipient, companyId, branchId);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var inserted = 0;
        foreach (var recipient in recipients)
        foreach (var pair in alerts.Zip(decisions))
        {
            var alert = pair.First;
            var decision = pair.Second;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT OR IGNORE INTO monarch_supplier_ap_alert_delivery_intents
(id,company_id,branch_id,recipient_user_id,alert_code,source_last_occurred_utc,severity,
escalation_level,channel,reason_code,due_utc,created_utc)
VALUES($id,$company,$branch,$recipient,$code,$source,$severity,$level,$channel,$reason,$due,$created);";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$company", companyId.ToString());
            command.Parameters.AddWithValue("$branch", branchId.ToString());
            command.Parameters.AddWithValue("$recipient", recipient.ToString());
            command.Parameters.AddWithValue("$code", decision.AlertCode);
            command.Parameters.AddWithValue("$source", Format(alert.LastOccurredUtc));
            command.Parameters.AddWithValue("$severity", decision.Severity);
            command.Parameters.AddWithValue("$level", decision.EscalationLevel);
            command.Parameters.AddWithValue("$channel", decision.Channel);
            command.Parameters.AddWithValue("$reason", decision.ReasonCode);
            command.Parameters.AddWithValue("$due", Format(decision.DueUtc));
            command.Parameters.AddWithValue("$created", Format(now));
            inserted += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return inserted;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
