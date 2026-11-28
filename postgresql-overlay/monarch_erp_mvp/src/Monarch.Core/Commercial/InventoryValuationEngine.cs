namespace Monarch.Core.Commercial;

public enum InventoryCostMethod { Fifo, MovingWeightedAverage }

/// <summary>Immutable, deterministic valuation over a single warehouse/item posting-sequence stream.
/// Quantities are milli-units and costs are currency minor-units per whole unit.
/// Transfers must be represented by a paired outbound/inbound movement carrying the same cost.
/// </summary>
public sealed record InventoryValuationMovement(
    Guid Id, DateTimeOffset OccurredUtc, long Sequence, StockMovementType Type,
    long QuantityMilli, long UnitCostMinor);

public sealed record InventoryCostLayer(long RemainingQuantityMilli, long UnitCostMinor);
public sealed record InventoryValuationEntry(
    Guid MovementId, long BalanceQuantityMilli, long BalanceValueMinor,
    long MovementCostMinor, long AverageUnitCostMinor);
public sealed record InventoryValuationResult(
    long ClosingQuantityMilli, long ClosingValueMinor, long AverageUnitCostMinor,
    IReadOnlyList<InventoryCostLayer> RemainingFifoLayers,
    IReadOnlyList<InventoryValuationEntry> Entries);

public static class InventoryValuationEngine
{
    public static InventoryValuationResult Calculate(
        IEnumerable<InventoryValuationMovement> movements, InventoryCostMethod method)
    {
        if (movements is null) throw new ArgumentNullException(nameof(movements));
        if (!Enum.IsDefined(typeof(InventoryCostMethod), method)) throw new ArgumentOutOfRangeException(nameof(method));
        var ordered = movements.OrderBy(x => x.Sequence).ThenBy(x => x.Id).ToList();
        var layers = new LinkedList<InventoryCostLayer>();
        var entries = new List<InventoryValuationEntry>();
        long quantity = 0, value = 0;
        foreach (var m in ordered)
        {
            if (m.QuantityMilli <= 0 || m.UnitCostMinor < 0)
                throw new ArgumentOutOfRangeException(nameof(movements), "Movement quantity and unit cost are invalid.");
            bool inbound = IsInbound(m.Type);
            bool outbound = IsOutbound(m.Type);
            if (!inbound && !outbound) throw new InvalidOperationException($"Unsupported movement type: {m.Type}.");
            long movementCost;
            if (inbound)
            {
                movementCost = Cost(m.QuantityMilli, m.UnitCostMinor);
                quantity = checked(quantity + m.QuantityMilli);
                value = checked(value + movementCost);
                if (method == InventoryCostMethod.Fifo)
                    layers.AddLast(new InventoryCostLayer(m.QuantityMilli, m.UnitCostMinor));
                else
                {
                    layers.Clear();
                    if (quantity > 0) layers.AddLast(new InventoryCostLayer(quantity, Average(value, quantity)));
                }
            }
            else
            {
                if (m.QuantityMilli > quantity)
                    throw new InvalidOperationException("Inventory valuation cannot issue more than the available quantity.");
                if (method == InventoryCostMethod.Fifo)
                {
                    movementCost = ConsumeFifo(layers, m.QuantityMilli);
                }
                else
                {
                    var avg = Average(value, quantity);
                    movementCost = Cost(m.QuantityMilli, avg);
                    var nextQty = quantity - m.QuantityMilli;
                    value = nextQty == 0 ? 0 : Math.Max(0, value - movementCost);
                    layers.Clear();
                    if (nextQty > 0) layers.AddLast(new InventoryCostLayer(nextQty, Average(value, nextQty)));
                }
                quantity -= m.QuantityMilli;
                if (method == InventoryCostMethod.Fifo)
                    value = checked(value - movementCost);
            }
            if (quantity == 0) value = 0;
            entries.Add(new InventoryValuationEntry(m.Id, quantity, value, movementCost,
                quantity == 0 ? 0 : Average(value, quantity)));
        }
        return new InventoryValuationResult(quantity, value, quantity == 0 ? 0 : Average(value, quantity),
            layers.ToArray(), entries);
    }

    private static long ConsumeFifo(LinkedList<InventoryCostLayer> layers, long requested)
    {
        long remaining = requested, total = 0;
        while (remaining > 0)
        {
            if (layers.First is null) throw new InvalidOperationException("FIFO cost layers are insufficient.");
            var layer = layers.First.Value;
            var take = Math.Min(remaining, layer.RemainingQuantityMilli);
            total = checked(total + Cost(take, layer.UnitCostMinor));
            remaining -= take;
            if (take == layer.RemainingQuantityMilli) layers.RemoveFirst();
            else layers.First.Value = layer with { RemainingQuantityMilli = layer.RemainingQuantityMilli - take };
        }
        return total;
    }

    private static long Cost(long quantityMilli, long unitCostMinor) =>
        checked((long)decimal.Round((decimal)quantityMilli * unitCostMinor / 1000m, 0, MidpointRounding.AwayFromZero));
    private static long Average(long valueMinor, long quantityMilli) => quantityMilli == 0 ? 0 :
        checked((long)decimal.Round((decimal)valueMinor * 1000m / quantityMilli, 0, MidpointRounding.AwayFromZero));

    private static bool IsInbound(StockMovementType t) => t is StockMovementType.Opening or StockMovementType.Receipt
        or StockMovementType.TransferIn or StockMovementType.AdjustmentIn or StockMovementType.SalesReturn;
    private static bool IsOutbound(StockMovementType t) => t is StockMovementType.Issue or StockMovementType.TransferOut
        or StockMovementType.AdjustmentOut or StockMovementType.PurchaseReturn;
}
