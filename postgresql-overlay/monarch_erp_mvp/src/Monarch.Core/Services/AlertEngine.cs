using Monarch.Core.Models;

namespace Monarch.Core.Services;

/// <summary>Rule-based alerts. AI may explain and prioritize these alerts later, but must not invent source values.</summary>
public static class AlertEngine
{
    public static IReadOnlyList<BusinessAlert> Evaluate(
        IEnumerable<InventoryItem> inventory,
        DashboardSnapshot snapshot,
        DateTimeOffset now)
    {
        var alerts = new List<BusinessAlert>();
        foreach (var item in inventory.Where(x => x.QuantityOnHand <= x.ReorderLevel))
        {
            alerts.Add(new BusinessAlert(
                "INVENTORY_LOW", "Warning", "انخفاض المخزون",
                $"الصنف {item.Name} ({item.Sku}): المتبقي {item.QuantityOnHand} {item.Unit}، وحد إعادة الطلب {item.ReorderLevel}. الكمية المقترحة للطلب: {item.SuggestedOrderQuantity}.", now));
        }

        if (snapshot.OverdueReceivablesCount > 0)
            alerts.Add(new BusinessAlert("RECEIVABLE_OVERDUE", "Warning", "مستحقات متأخرة",
                $"يوجد {snapshot.OverdueReceivablesCount} فاتورة مستحقة تجاوزت تاريخ الاستحقاق.", now));

        return alerts;
    }
}
