namespace Monarch.Core.Commercial;

public enum PartyType { Customer, Supplier, Both }
public enum ItemType { Stock, Service, NonStock }
public enum StockMovementType { Opening, Receipt, Issue, TransferIn, TransferOut, AdjustmentIn, AdjustmentOut, SalesReturn, PurchaseReturn }

public sealed record Party(Guid Id, string Code, PartyType Type, string LegalName, string DisplayName,
    string? TaxNumber = null, string? Phone = null, string? Email = null, string? Address = null,
    long CreditLimitMinor = 0, int PaymentTermsDays = 0, bool IsActive = true);
public sealed record UnitOfMeasure(Guid Id, string Code, string Name, int DecimalPlaces = 0);
public sealed record ItemGroup(Guid Id, Guid? ParentId, string Code, string Name, bool IsActive = true);
public sealed record StockItem(Guid Id, Guid UnitId, string Sku, string Name, ItemType Type,
    long CostMinor = 0, long SalePriceMinor = 0, long ReorderLevelMilli = 0,
    Guid? GroupId = null, string? Barcode = null, bool AllowNegative = false, bool IsActive = true);
public sealed record Warehouse(Guid Id, Guid BranchId, string Code, string Name, bool IsActive = true);
public sealed record StockMovement(Guid Id, Guid WarehouseId, Guid ItemId, StockMovementType Type,
    long QuantityMilli, long UnitCostMinor, string IdempotencyKey, string? SourceType = null, string? SourceId = null);
public sealed record StockBalance(Guid WarehouseId, Guid ItemId, long QuantityMilli);
