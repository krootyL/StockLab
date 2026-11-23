using StockLab.Persistence.Entities;

namespace StockLab.Dtos;

public record PlaceOrderRequest(
    long StrategyId,
    string StockCode,
    OrderSide Side,
    OrderType OrderType,
    long Quantity,
    decimal? OrderPrice
);

public record OrderDto(
    long OrderId,
    long StrategyId,
    string StockCode,
    OrderSide Side,
    OrderType OrderType,
    OrderStatus Status,
    decimal OrderPrice,
    long Quantity,
    long FilledQuantity,
    decimal TotalAmount,
    DateTime OrderedAtUtc
);

public record TradeDto(
    long TradeId,
    long OrderId,
    long StrategyId,
    string StockCode,
    OrderSide Side,
    decimal ExecutedPrice,
    long ExecutedQuantity,
    decimal TotalAmount,
    DateTime ExecutedAtUtc
);

public record AdminOrderDto(
    long OrderId,
    long StrategyId,
    string Uid,
    string Nickname,
    string StockCode,
    OrderSide Side,
    OrderType OrderType,
    OrderStatus Status,
    decimal OrderPrice,
    long Quantity,
    long FilledQuantity,
    decimal TotalAmount,
    DateTime OrderedAtUtc,
    bool IsMarketOpen = false,
    decimal? ReferencePrice = null,
    string? WaitReasonCode = null,
    string? WaitReason = null
);

public record BulkCancelOrdersResult(
    int CancelledCount,
    long TotalReleasedShares,
    IReadOnlyDictionary<string, decimal> RefundedCashByCurrency,
    IReadOnlyList<long> CancelledOrderIds
);

