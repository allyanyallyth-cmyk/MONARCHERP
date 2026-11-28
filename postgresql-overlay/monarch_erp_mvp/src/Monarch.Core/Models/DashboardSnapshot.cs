namespace Monarch.Core.Models;

public sealed record DashboardSnapshot(
    DateOnly BusinessDate,
    decimal SalesTotal,
    decimal CashReceipts,
    decimal BankReceipts,
    decimal CardReceipts,
    decimal CreditSales,
    decimal CashPayments,
    decimal CashOnHandMovement,
    int LowStockItems,
    int OverdueReceivablesCount);

public sealed record BusinessAlert(string Code, string Severity, string Title, string Message, DateTimeOffset CreatedAt);
