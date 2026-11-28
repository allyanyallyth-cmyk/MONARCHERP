namespace Monarch.Core.Models;

public sealed record InventoryItem(
    Guid Id,
    string Sku,
    string Name,
    string Unit,
    decimal QuantityOnHand,
    decimal ReorderLevel,
    decimal SuggestedOrderQuantity,
    DateTimeOffset? ExpiryDate = null);
