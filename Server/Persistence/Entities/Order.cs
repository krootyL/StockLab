namespace StockLab.Persistence.Entities;

public class Order
{
    public long OrderId { get; set; }
    public long StrategyId { get; set; }
    public long Uid { get; set; }
    public string StockCode { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public OrderType OrderType { get; set; }
    public OrderStatus Status { get; set; }
    public decimal OrderPrice { get; set; }
    public long Quantity { get; set; }
    public long FilledQuantity { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime OrderedAtUtc { get; set; }

    public Strategy Strategy { get; set; } = null!;
    public User User { get; set; } = null!;
    public ICollection<Trade> Trades { get; set; } = new List<Trade>();
}
