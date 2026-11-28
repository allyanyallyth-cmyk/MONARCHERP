namespace Monarch.Core.Models;

public enum CashMovementType { Receipt, Payment }
public enum PaymentMethod { Cash, Bank, Card, Credit, Other }

public sealed record CashMovement(
    Guid Id,
    DateOnly BusinessDate,
    CashMovementType Type,
    PaymentMethod Method,
    decimal Amount,
    string Description);
