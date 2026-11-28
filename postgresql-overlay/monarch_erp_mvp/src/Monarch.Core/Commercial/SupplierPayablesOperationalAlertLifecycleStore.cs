using System.Globalization;
using Microsoft.Data.Sqlite;
using Monarch.Core.Accounting;

namespace Monarch.Core.Commercial;

public sealed record SupplierPayablesOperationalAlertLifecycle(
    string AlertCode, DateTimeOffset LastOccurredUtc,
    bool Viewed, DateTimeOffset? ViewedUtc,
    bool Acknowledged, DateTimeOffset? AcknowledgedUtc);

/// <summary>
/// Append-only, user-specific lifecycle markers for operational alert summaries.
/// A recurrence with a newer source timestamp has a separate lifecycle identity.
/// This store never mutates diagnostics, leases, outbox records, or accounting entries.
/// </summary>
public sealed class SupplierPayablesOperationalAlertLifecycleStore
{
    private static readonly HashSet<string> AllowedCodes = new(StringComparer.Ordinal)
    {
        "supplier_ap_heartbeat_failures", "supplier_ap_lease_ownership_loss"
    };

    private readonly string _connectionString;
    private readonly TenantLedgerStore _ledger;

    public SupplierPayablesOperationalAlertLifecycleStore(string databasePath, TenantLedgerStore ledger)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("مسار قاعدة البيانات مطلوب.", nameof(databasePath));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), ForeignKeys = true
        }.ToString();
    }

    public void MarkViewed(Guid userId, Guid companyId, Guid branchId,
        SupplierPayablesOperationalAlert alert, DateTimeOffset? occurredUtc = null) =>
        Append(userId, companyId, branchId, alert, "Viewed", occurredUtc ?? DateTimeOffset.UtcNow);

    public void Acknowledge(Guid userId, Guid companyId, Guid branchId,
        SupplierPayablesOperationalAlert alert, DateTimeOffset? occurredUtc = null) =>
        Append(userId, companyId, branchId, alert, "Acknowledged", occurredUtc ?? DateTimeOffset.UtcNow);

    public IReadOnlyList<SupplierPayablesOperationalAlertLifecycle> GetStates(
        Guid userId, Guid companyId, Guid branchId,
        IReadOnlyList<SupplierPayablesOperationalAlert> alerts)
    {
        if (alerts is null) throw new ArgumentNullException(nameof(alerts));
        if (alerts.Count > 500) throw new ArgumentOutOfRangeException(nameof(alerts));
        _ledger.GetEntries(userId, companyId, branchId); // authorization before lifecycle read

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var result = new List<SupplierPayablesOperationalAlertLifecycle>(alerts.Count);
        foreach (var alert in alerts)
        {
            ValidateAlert(alert);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"SELECT action, MAX(occurred_utc)
FROM monarch_supplier_ap_alert_lifecycle
WHERE company_id=$company AND branch_id=$branch AND user_id=$user
  AND alert_code=$code AND source_last_occurred_utc=$source
GROUP BY action;";
            command.Parameters.AddWithValue("$company", companyId.ToString());
            command.Parameters.AddWithValue("$branch", branchId.ToString());
            command.Parameters.AddWithValue("$user", userId.ToString());
            command.Parameters.AddWithValue("$code", alert.Code);
            command.Parameters.AddWithValue("$source", Format(alert.LastOccurredUtc));
            var viewed = false; DateTimeOffset? viewedUtc = null;
            var acknowledged = false; DateTimeOffset? acknowledgedUtc = null;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var action = reader.GetString(0);
                    var timestamp = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                    if (action == "Viewed") { viewed = true; viewedUtc = timestamp; }
                    if (action == "Acknowledged") { acknowledged = true; acknowledgedUtc = timestamp; }
                }
            }
            result.Add(new(alert.Code, alert.LastOccurredUtc, viewed, viewedUtc, acknowledged, acknowledgedUtc));
        }
        transaction.Commit();
        return result;
    }

    private void Append(Guid userId, Guid companyId, Guid branchId,
        SupplierPayablesOperationalAlert alert, string action, DateTimeOffset occurredUtc)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty)
            throw new ArgumentException("معرف المستخدم والشركة والفرع مطلوب.");
        ValidateAlert(alert);
        _ledger.GetEntries(userId, companyId, branchId); // authorization before lifecycle write

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT OR IGNORE INTO monarch_supplier_ap_alert_lifecycle
(id,company_id,branch_id,user_id,alert_code,source_last_occurred_utc,action,occurred_utc)
VALUES($id,$company,$branch,$user,$code,$source,$action,$occurred);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$company", companyId.ToString());
        command.Parameters.AddWithValue("$branch", branchId.ToString());
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$code", alert.Code);
        command.Parameters.AddWithValue("$source", Format(alert.LastOccurredUtc));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$occurred", Format(occurredUtc));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateAlert(SupplierPayablesOperationalAlert alert)
    {
        if (alert is null) throw new ArgumentNullException(nameof(alert));
        if (!AllowedCodes.Contains(alert.Code)) throw new ArgumentException("رمز التنبيه غير معتمد.", nameof(alert));
        if (alert.LastOccurredUtc == default) throw new ArgumentException("وقت الحدث غير صالح.", nameof(alert));
    }
}
