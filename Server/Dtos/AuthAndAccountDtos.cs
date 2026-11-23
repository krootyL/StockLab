using StockLab.Persistence.Entities;

namespace StockLab.Dtos;

public record GuestLoginRequest(string? Nickname);

public record RegisterRequest(string LoginId, string Password, string Nickname);

public record LoginRequest(string LoginId, string Password);

public record AccountWalletDto(
    long WalletId,
    string Currency,
    decimal Balance,
    DateTime UpdatedAtUtc
);

public record WalletSummaryDto(
    long WalletId,
    string Currency,
    decimal AvailableCash,
    decimal PendingCash,
    decimal RealizedPnl = 0m
);

public record StockHoldingDto(
    long HoldingId,
    string StockCode,
    long Quantity,
    decimal AverageBuyPrice,
    decimal CurrentPrice = 0m,
    string Currency = "KRW"
);

public record StrategySummaryDto(
    long StrategyId,
    string Name,
    DateTime CreatedAtUtc,
    IReadOnlyList<WalletSummaryDto> Wallets,
    IReadOnlyList<StockHoldingDto> Holdings,
    int ActiveOrderCount = 0
);

public record UserProfileDto(
    string Uid,
    UserType Type,
    string Nickname,
    DateTime CreatedAtUtc,
    IReadOnlyList<StrategySummaryDto> Strategies,
    DateTimeOffset? SessionExpiresAtUtc = null,
    string? LoginId = null,
    bool IsGuest = false,
    IReadOnlyList<AccountWalletDto>? AccountWallets = null
);

public record AdjustBalanceRequest(
    long StrategyId,
    string Currency,
    decimal Amount
);

public record AccountWalletTransactionRequest(
    string Currency,
    decimal Amount
);

public record StrategyTransferRequest(
    string Direction,
    string Currency,
    decimal Amount
);

public record StrategyTransferResultDto(
    AccountWalletDto AccountWallet,
    WalletSummaryDto StrategyWallet
);

public record DeleteStrategyResultDto(
    long DeletedStrategyId,
    string DeletedStrategyName,
    decimal ReclaimedKrw,
    decimal ReclaimedUsd,
    IReadOnlyList<AccountWalletDto> AccountWallets
);

public record UpdateUserNicknameRequest(string Nickname);

public record UpdatePasswordRequest(string CurrentPassword, string NewPassword);

public record CreateStrategyRequest(string Name);

public record UpdateStrategyRequest(string Name);

public record AddWatchlistRequest(string StockCode);

public record WatchlistItemDto(
    long Id,
    string StockCode,
    string StockName,
    string MarketType,
    string ExchangeCode,
    string Currency,
    decimal BasePrice,
    DateTime CreatedAtUtc
);
