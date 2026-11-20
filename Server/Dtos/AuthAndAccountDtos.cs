using StockLab.Persistence.Entities;

namespace StockLab.Dtos;

public record GuestLoginRequest(string? Nickname);

public record RegisterRequest(string LoginId, string Password, string Nickname);

public record LoginRequest(string LoginId, string Password);

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

public record ScenarioSummaryDto(
    long ScenarioId,
    string Name,
    DateTime CreatedAtUtc,
    IReadOnlyList<WalletSummaryDto> Wallets,
    IReadOnlyList<StockHoldingDto> Holdings
);

public record UserProfileDto(
    string Uid,
    UserType Type,
    string Nickname,
    DateTime CreatedAtUtc,
    IReadOnlyList<ScenarioSummaryDto> Scenarios,
    DateTimeOffset? SessionExpiresAtUtc = null
);

public record AdjustBalanceRequest(
    long ScenarioId,
    string Currency,
    decimal Amount
);

public record UpdateUserNicknameRequest(string Nickname);
