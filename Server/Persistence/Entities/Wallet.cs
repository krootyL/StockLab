namespace StockLab.Persistence.Entities;

public class Wallet
{
    public long Id { get; set; }
    public long ScenarioId { get; set; }
    public string Currency { get; set; } = "KRW";
    public decimal AvailableCash { get; set; }
    public decimal PendingCash { get; set; }
    public decimal RealizedPnl { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public uint Version { get; set; }

    public Scenario Scenario { get; set; } = null!;
}

