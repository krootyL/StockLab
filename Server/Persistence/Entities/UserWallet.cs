namespace StockLab.Persistence.Entities;

/// <summary>
/// 계정(User) 단위로 귀속되는 통화별 통합 예수금 지갑.
/// 각 투자 전략(Strategy)의 개별 지갑(Wallet)과 물리적으로 분리 보관되며,
/// 사용자가 직접 입출금하거나 특정 투자 전략과 상호 이체할 수 있다.
/// </summary>
public class UserWallet
{
    public long Id { get; set; }
    public long Uid { get; set; }
    public string Currency { get; set; } = "KRW";
    public decimal Balance { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public uint Version { get; set; }

    public User User { get; set; } = null!;
}

