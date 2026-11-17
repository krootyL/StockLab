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

