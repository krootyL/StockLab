namespace StockLab.Persistence.Entities;

public class WatchlistItem
{
    public long Id { get; set; }
    public long Uid { get; set; }
    public string StockCode { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    public User User { get; set; } = null!;
}

