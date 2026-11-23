namespace StockLab.Persistence.Entities;

public class Stocks
{
    public long Id { get; set; }
    public long StrategyId { get; set; }
    public string StockCode { get; set; } = string.Empty;
    public long Quantity { get; set; }
    public long ReservedQuantity { get; set; }
    public decimal AverageBuyPrice { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public uint Version { get; set; }

    public Strategy Strategy { get; set; } = null!;
}

