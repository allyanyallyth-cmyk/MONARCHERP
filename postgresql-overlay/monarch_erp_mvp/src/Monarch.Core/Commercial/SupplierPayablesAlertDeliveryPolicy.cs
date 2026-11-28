namespace Monarch.Core.Commercial;

public sealed record SupplierPayablesAlertDeliveryDecision(
    string AlertCode, string Severity, int EscalationLevel,
    string Channel, string ReasonCode, DateTimeOffset DueUtc);

/// <summary>
/// Deterministic delivery and escalation planning only. It creates no network traffic,
/// sends no messages, and does not alter worker leases, diagnostics, or accounting data.
/// </summary>
public static class SupplierPayablesAlertDeliveryPolicy
{
    private static readonly HashSet<string> AllowedCodes = new(StringComparer.Ordinal)
    {
        "supplier_ap_heartbeat_failures", "supplier_ap_lease_ownership_loss"
    };

    public static SupplierPayablesAlertDeliveryDecision Assess(
        SupplierPayablesOperationalAlert alert, DateTimeOffset nowUtc)
    {
        if (alert is null) throw new ArgumentNullException(nameof(alert));
        if (!AllowedCodes.Contains(alert.Code)) throw new ArgumentException("رمز التنبيه غير معتمد.", nameof(alert));
        if (alert.Severity is not ("Warning" or "Critical")) throw new ArgumentException("درجة الخطورة غير معتمدة.", nameof(alert));
        if (alert.LastOccurredUtc == default || alert.LastOccurredUtc > nowUtc)
            throw new ArgumentException("وقت التنبيه غير صالح.", nameof(alert));

        var age = nowUtc - alert.LastOccurredUtc;
        var level = alert.Severity == "Critical" ? 2 : age >= TimeSpan.FromMinutes(30) ? 2 : 1;
        var reason = alert.Severity == "Critical" ? "critical_immediate_escalation" :
            level == 2 ? "warning_overdue_escalation" : "warning_initial_delivery";
        return new(alert.Code, alert.Severity, level, "InApp", reason, nowUtc);
    }
}
