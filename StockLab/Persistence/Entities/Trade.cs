namespace StockLab.Persistence.Entities;

public class Trade
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public long ScenarioId { get; set; }
    public string StockCode { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public decimal ExecutedPrice { get; set; }
    public long ExecutedQuantity { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime ExecutedAtUtc { get; set; }

    public Order Order { get; set; } = null!;
    public Scenario Scenario { get; set; } = null!;
}
