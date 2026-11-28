namespace Monarch.Core.Models;

public sealed record SalesInvoice(
    Guid Id,
    DateOnly BusinessDate,
    string InvoiceNumber,
    decimal NetTotal,
    decimal PaidAmount,
    bool IsVoided = false);
