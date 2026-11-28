namespace Monarch.Core.Commercial;

public sealed record SupplierPayablesOperationalAlert(
    string Code, string Severity, string Title, string Summary,
    int EventCount, DateTimeOffset LastOccurredUtc);

/// <summary>
/// Builds a read-only, tenant-scoped operational view from durable worker diagnostics.
/// It does not retry work, mutate leases, or change accounting state.
/// </summary>
public static class SupplierPayablesOperationalAlertCenter
{
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LeaseLossWindow = TimeSpan.FromHours(24);
    public const int DefaultHeartbeatCriticalThreshold = 3;

    public static IReadOnlyList<SupplierPayablesOperationalAlert> Build(
        IReadOnlyList<SupplierPayablesWorkerDiagnostic> diagnostics,
        DateTimeOffset nowUtc,
        int limit = 100,
        int heartbeatCriticalThreshold = DefaultHeartbeatCriticalThreshold)
    {
        if (diagnostics is null) throw new ArgumentNullException(nameof(diagnostics));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (heartbeatCriticalThreshold < 2) throw new ArgumentOutOfRangeException(nameof(heartbeatCriticalThreshold));

        var heartbeat = diagnostics.Where(x => x.EventCode == "heartbeat_failed" &&
            x.OccurredUtc <= nowUtc && x.OccurredUtc >= nowUtc - HeartbeatWindow).ToArray();
        var leaseLoss = diagnostics.Where(x => x.EventCode == "lease_ownership_lost" &&
            x.OccurredUtc <= nowUtc && x.OccurredUtc >= nowUtc - LeaseLossWindow).ToArray();
        var alerts = new List<SupplierPayablesOperationalAlert>();

        if (heartbeat.Length > 0)
        {
            var latest = heartbeat.Max(x => x.OccurredUtc);
            var critical = heartbeat.Length >= heartbeatCriticalThreshold;
            alerts.Add(new SupplierPayablesOperationalAlert(
                "supplier_ap_heartbeat_failures", critical ? "Critical" : "Warning",
                critical ? "تكرار تعثر نبضات عامل مستحقات الموردين" : "تعثر نبضة عامل مستحقات الموردين",
                critical ? "تكررت إخفاقات تجديد المهلة خلال آخر 15 دقيقة؛ يلزم فحص اتصال قاعدة البيانات وحالة العامل." :
                    "سُجل إخفاق في تجديد مهلة العامل؛ راقب تكراره وتحقق من سجل التشغيل.",
                heartbeat.Length, latest));
        }

        if (leaseLoss.Length > 0)
        {
            alerts.Add(new SupplierPayablesOperationalAlert(
                "supplier_ap_lease_ownership_loss", "Warning",
                "فقدان ملكية مهمة مستحقات الموردين",
                "رفض النظام تحديث مهمة لأن ملكية العامل لم تعد صالحة؛ تحقق من انتهاء المهلة أو انتقال المهمة لعامل آخر.",
                leaseLoss.Length, leaseLoss.Max(x => x.OccurredUtc)));
        }

        return alerts.OrderByDescending(x => x.Severity == "Critical")
            .ThenByDescending(x => x.LastOccurredUtc)
            .ThenBy(x => x.Code, StringComparer.Ordinal)
            .Take(limit).ToArray();
    }
}
