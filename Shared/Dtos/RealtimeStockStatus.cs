namespace StockLab.Dtos;

public record OrderBookLevel(
    decimal Price,
    long Quantity
);

public record RealtimeStockStatus(
    string StockCode,
    string MarketType,
    string ExchangeCode,
    string Currency,
    string UpdatedTime,
    IReadOnlyList<OrderBookLevel> Asks,
    IReadOnlyList<OrderBookLevel> Bids,
    long TotalAskSize,
    long TotalBidSize
);

public record RealtimeStockTick(
    string StockCode,
    string MarketType,
    string Currency,
    string Time,
    decimal Price,
    decimal PriceChange,
    decimal ChangeRate,
    long Volume,
    long TotalVolume,
    decimal Strength = 0m
);

