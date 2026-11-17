using StockLab.Persistence.Entities;

namespace StockLab.Dtos;

public record GuestLoginRequest(string? Nickname);

public record WalletSummaryDto(
    long WalletId,
    string Currency,
    decimal AvailableCash,
    decimal PendingCash
);

public record StockHoldingDto(
    long HoldingId,
    string StockCode,
    long Quantity,
    decimal AverageBuyPrice
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
    IReadOnlyList<ScenarioSummaryDto> Scenarios
);

public record AdjustBalanceRequest(
    long ScenarioId,
    string Currency,
    decimal Amount
);

public record UpdateUserNicknameRequest(string Nickname);
