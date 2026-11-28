using Monarch.Core.Models;

namespace Monarch.Core.Services;

/// <summary>Calculates dashboard values from posted source records. This is deterministic business logic, not generative AI.</summary>
public static class DashboardCalculator
{
    public static DashboardSnapshot Calculate(
        DateOnly date,
        IEnumerable<SalesInvoice> invoices,
        IEnumerable<CashMovement> movements,
        IEnumerable<InventoryItem> inventory,
        int overdueReceivablesCount = 0)
    {
        var dayInvoices = invoices.Where(x => x.BusinessDate == date && !x.IsVoided).ToArray();
        var dayMovements = movements.Where(x => x.BusinessDate == date).ToArray();
        var receipts = dayMovements.Where(x => x.Type == CashMovementType.Receipt).ToArray();
        var payments = dayMovements.Where(x => x.Type == CashMovementType.Payment).ToArray();
        decimal ByMethod(PaymentMethod method) => receipts.Where(x => x.Method == method).Sum(x => x.Amount);

        return new DashboardSnapshot(
            date,
            dayInvoices.Sum(x => x.NetTotal),
            ByMethod(PaymentMethod.Cash),
            ByMethod(PaymentMethod.Bank),
            ByMethod(PaymentMethod.Card),
            dayInvoices.Sum(x => Math.Max(0, x.NetTotal - x.PaidAmount)),
            payments.Where(x => x.Method == PaymentMethod.Cash).Sum(x => x.Amount),
            ByMethod(PaymentMethod.Cash) - payments.Where(x => x.Method == PaymentMethod.Cash).Sum(x => x.Amount),
            inventory.Count(x => x.QuantityOnHand <= x.ReorderLevel),
            Math.Max(0, overdueReceivablesCount));
    }
}
