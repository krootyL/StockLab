namespace StockLab.Persistence.Entities;

public class User
{
    /// <summary>
    /// 18자리 순수 숫자 고유 식별자 (100,000,000,000,000,000 ~ 999,999,999,999,999,999)
    /// </summary>
    public long Uid { get; set; }
    public UserType Type { get; set; } = UserType.Guest;
    public string? LoginId { get; set; }
    public string? PasswordHash { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastActiveAtUtc { get; set; }

    public ICollection<Scenario> Scenarios { get; set; } = new List<Scenario>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
