namespace StockLab.Persistence.Entities;

public class Scenario
{
    public long Id { get; set; }
    public long Uid { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    public User User { get; set; } = null!;
    public ICollection<Wallet> Wallets { get; set; } = new List<Wallet>();
    public ICollection<Stocks> Holdings { get; set; } = new List<Stocks>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<Trade> Trades { get; set; } = new List<Trade>();
}

