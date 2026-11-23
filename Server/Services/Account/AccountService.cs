using Microsoft.EntityFrameworkCore;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Persistence;
using StockLab.Persistence.Entities;
using StockLab.Services.Master;

namespace StockLab.Services.Account;

public class AccountService
{
    public const int MaxStrategiesPerUser = 10;
    public const int MaxWatchlistItemsPerUser = 50;

    private readonly AppDbContext _db;
    private readonly StockMasterStore _masterStore;
    private readonly ILogger<AccountService> _logger;

    public AccountService(AppDbContext db, StockMasterStore masterStore, ILogger<AccountService> logger)
    {
        _db = db;
        _masterStore = masterStore;
        _logger = logger;
    }

    /// <summary>
    /// 관리자 전용: 비관적 행 락(FOR UPDATE)으로 특정 투자 전략 지갑의 예수가능금을 증감합니다.
    /// </summary>
    public async Task<WalletSummaryDto> AdjustBalanceAsync(
        long strategyId,
        string currency,
        decimal deltaAmount,
        CancellationToken ct = default)
    {
        if (deltaAmount == 0)
        {
            throw new StockLabException(ErrorCode.InvalidInput, "증감 금액은 0이 될 수 없습니다.");
        }

        var normalizedCurrency = string.IsNullOrWhiteSpace(currency)
            ? string.Empty
            : currency.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(normalizedCurrency))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "통화 단위를 입력해야 합니다.");
        }

        _db.ChangeTracker.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var wallet = await _db.Wallets
            .FromSqlInterpolated(
                $"SELECT *, xmin FROM wallets WHERE \"StrategyId\" = {strategyId} AND \"Currency\" = {normalizedCurrency} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (wallet == null)
        {
            throw new StockLabException(ErrorCode.AccountNotFound, $"투자 전략({strategyId})의 {normalizedCurrency} 지갑을 찾을 수 없습니다.");
        }

        var nextBalance = wallet.AvailableCash + deltaAmount;
        if (nextBalance < 0)
        {
            throw new StockLabException(
                ErrorCode.InsufficientBalance,
                $"차감 후 예수가능금이 음수가 될 수 없습니다. (현재 예수가능금: {wallet.AvailableCash:N4}, 요청 증감: {deltaAmount:N4})");
        }

        wallet.AvailableCash = nextBalance;
        wallet.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("[관리자 시드머니 조정] StrategyId={StrategyId}, Currency={Currency}, Delta={Delta}, AvailableAfter={BalanceAfter}",
            strategyId, normalizedCurrency, deltaAmount, nextBalance);

        return new WalletSummaryDto(wallet.Id, wallet.Currency, wallet.AvailableCash, wallet.PendingCash);
    }

    /// <summary>
    /// 관리자 전용: 전체 또는 검색어(UID/닉네임) 기준 유저 목록과 투자 전략·지갑·보유종목을 조회합니다.
    /// </summary>
    public async Task<IReadOnlyList<UserProfileDto>> GetUsersAsync(string? query, CancellationToken ct = default)
    {
        var users = await BuildUsersQuery(query, includeDetails: true)
            .OrderByDescending(u => u.CreatedAtUtc)
            .Take(100)
            .ToListAsync(ct);

        return users.Select(ToUserProfileDto).ToList();
    }

    /// <summary>
    /// 관리자 전용: 전체 또는 검색어(UID/닉네임) 기준 유저 목록을 페이지 단위로 조회합니다.
    /// </summary>
    public async Task<PagedResult<UserProfileDto>> GetUsersPagedAsync(
        string? query,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult.Normalize(page, pageSize);
        var totalCount = await BuildUsersQuery(query, includeDetails: false).CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)normalizedSize));
        var clampedPage = Math.Clamp(normalizedPage, 1, totalPages);

        var users = await BuildUsersQuery(query, includeDetails: true)
            .OrderByDescending(u => u.CreatedAtUtc)
            .Skip((clampedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .ToListAsync(ct);

        return PagedResult.Create(users.Select(ToUserProfileDto).ToList(), clampedPage, normalizedSize, totalCount);
    }

    private IQueryable<Persistence.Entities.User> BuildUsersQuery(string? query, bool includeDetails)
    {
        IQueryable<Persistence.Entities.User> usersQuery = _db.Users.AsNoTracking();
        if (includeDetails)
        {
            usersQuery = usersQuery
                .AsSplitQuery()
                .Include(u => u.AccountWallets)
                .Include(u => u.Strategies)
                    .ThenInclude(s => s.Wallets)
                .Include(u => u.Strategies)
                    .ThenInclude(s => s.Holdings);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var trimmed = query.Trim();
            if (long.TryParse(trimmed, out var uid))
            {
                usersQuery = usersQuery.Where(u => u.Uid == uid || EF.Functions.ILike(u.Nickname, $"%{trimmed}%"));
            }
            else
            {
                usersQuery = usersQuery.Where(u => EF.Functions.ILike(u.Nickname, $"%{trimmed}%"));
            }
        }

        return usersQuery;
    }

    private static UserProfileDto ToUserProfileDto(Persistence.Entities.User user)
        => ToUserProfileDto(user, excludeZeroHoldings: false);

    public static UserProfileDto ToUserProfileDto(
        Persistence.Entities.User user,
        bool excludeZeroHoldings,
        StockMasterStore? masterStore = null,
        IReadOnlyDictionary<long, int>? activeOrderCounts = null) => new(
        user.Uid.ToString(),
        user.Type,
        user.Nickname,
        user.CreatedAtUtc,
        user.Strategies
            .OrderBy(s => s.Id)
            .Select(s => ToStrategySummaryDto(s, excludeZeroHoldings, masterStore, activeOrderCounts))
            .ToList(),
        LoginId: user.LoginId,
        IsGuest: string.IsNullOrWhiteSpace(user.LoginId),
        AccountWallets: BuildAccountWalletDtos(user.AccountWallets, user.CreatedAtUtc));

    private static IReadOnlyList<AccountWalletDto> BuildAccountWalletDtos(
        IEnumerable<UserWallet>? rows,
        DateTime fallbackTimeUtc)
    {
        var list = rows?.ToList() ?? new List<UserWallet>();
        var result = new List<AccountWalletDto>(2);
        foreach (var curr in new[] { "KRW", "USD" })
        {
            var found = list.FirstOrDefault(w => string.Equals(w.Currency, curr, StringComparison.OrdinalIgnoreCase));
            result.Add(found != null
                ? new AccountWalletDto(found.Id, curr, found.Balance, found.UpdatedAtUtc)
                : new AccountWalletDto(0, curr, 0m, fallbackTimeUtc));
        }
        return result;
    }

    private static StrategySummaryDto ToStrategySummaryDto(
        Strategy s,
        bool excludeZeroHoldings,
        StockMasterStore? masterStore = null,
        IReadOnlyDictionary<long, int>? activeOrderCounts = null) => new(
        s.Id,
        s.Name,
        s.CreatedAtUtc,
        s.Wallets
            .OrderBy(w => w.Currency)
            .Select(w => new WalletSummaryDto(w.Id, w.Currency, w.AvailableCash, w.PendingCash, w.RealizedPnl))
            .ToList(),
        s.Holdings
            .Where(h => !excludeZeroHoldings || h.Quantity > 0)
            .OrderBy(h => h.StockCode)
            .Select(h =>
            {
                var master = masterStore?.GetByCode(h.StockCode);
                var currentPrice = master is { BasePrice: > 0 } ? master.BasePrice : h.AverageBuyPrice;
                var currency = master?.Currency ?? (h.StockCode.All(char.IsDigit) ? "KRW" : "USD");
                return new StockHoldingDto(h.Id, h.StockCode, h.Quantity, h.AverageBuyPrice, currentPrice, currency);
            })
            .ToList(),
        ActiveOrderCount: activeOrderCounts != null && activeOrderCounts.TryGetValue(s.Id, out var count) ? count : 0);

    /// <summary>
    /// 관리자 전용: 특정 유저의 닉네임을 수정합니다.
    /// </summary>
    public async Task<UserProfileDto> UpdateNicknameAsync(long uid, string nickname, CancellationToken ct = default)
    {
        var trimmed = string.IsNullOrWhiteSpace(nickname)
            ? string.Empty
            : nickname.Trim();

        if (trimmed.Length is < 1 or > 50)
        {
            throw new StockLabException(ErrorCode.InvalidInput, "닉네임은 1자 이상 50자 이하로 입력해주세요.");
        }

        var user = await _db.Users
            .AsSplitQuery()
            .Include(u => u.AccountWallets)
            .Include(u => u.Strategies)
                .ThenInclude(s => s.Wallets)
            .Include(u => u.Strategies)
                .ThenInclude(s => s.Holdings)
            .FirstOrDefaultAsync(u => u.Uid == uid, ct);

        if (user == null)
        {
            throw new StockLabException(ErrorCode.AccountNotFound, $"유저(UID: {uid})를 찾을 수 없습니다.");
        }

        user.Nickname = trimmed;
        user.LastActiveAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("[관리자 유저 수정] UID={Uid}, Nickname={Nickname}", uid, trimmed);

        return ToUserProfileDto(user, excludeZeroHoldings: false, _masterStore);
    }

    private async Task EnsureRegisteredUserAsync(long uid, CancellationToken ct)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Uid == uid)
            .Select(u => new { u.Uid, u.LoginId })
            .FirstOrDefaultAsync(ct);

        if (user == null)
        {
            throw new StockLabException(ErrorCode.Unauthorized, "유효한 사용자 세션을 찾을 수 없습니다.");
        }

        if (string.IsNullOrWhiteSpace(user.LoginId))
        {
            throw new StockLabException(ErrorCode.Forbidden, "게스트 계정은 마이페이지 기능을 이용할 수 없습니다.");
        }
    }

    public async Task<IReadOnlyList<StrategySummaryDto>> GetStrategiesAsync(long uid, CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);

        var strategies = await _db.Strategies
            .AsNoTracking()
            .AsSplitQuery()
            .Where(s => s.Uid == uid)
            .Include(s => s.Wallets)
            .Include(s => s.Holdings)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        var activeCounts = await _db.Orders
            .AsNoTracking()
            .Where(o => o.Uid == uid && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled))
            .GroupBy(o => o.StrategyId)
            .Select(g => new { StrategyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StrategyId, x => x.Count, ct);

        return strategies
            .Select(s => ToStrategySummaryDto(s, excludeZeroHoldings: true, _masterStore, activeCounts))
            .ToList();
    }

    public async Task<StrategySummaryDto> CreateStrategyAsync(long uid, string name, CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);

        var trimmed = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        if (trimmed.Length is < 1 or > 50)
        {
            throw new StockLabException(ErrorCode.InvalidInput, "투자 전략 이름은 1~50자 사이로 입력해 주세요.");
        }

        var currentCount = await _db.Strategies.CountAsync(s => s.Uid == uid, ct);
        if (currentCount >= MaxStrategiesPerUser)
        {
            throw new StockLabException(ErrorCode.InvalidInput, $"투자 전략은 계정당 최대 {MaxStrategiesPerUser}개까지 생성할 수 있습니다.");
        }

        var now = DateTime.UtcNow;
        var strategy = new Strategy
        {
            Uid = uid,
            Name = trimmed,
            CreatedAtUtc = now,
            Wallets = new List<Wallet>
            {
                new()
                {
                    Currency = "KRW",
                    AvailableCash = Auth.GuestAuthService.InitialKrwSeed,
                    PendingCash = 0m,
                    UpdatedAtUtc = now
                },
                new()
                {
                    Currency = "USD",
                    AvailableCash = Auth.GuestAuthService.InitialUsdSeed,
                    PendingCash = 0m,
                    UpdatedAtUtc = now
                }
            }
        };

        _db.Strategies.Add(strategy);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("[투자 전략 생성] UID={Uid}, StrategyId={StrategyId}, Name={Name}", uid, strategy.Id, trimmed);
        return ToStrategySummaryDto(strategy, excludeZeroHoldings: true, _masterStore);
    }

    public async Task<StrategySummaryDto> RenameStrategyAsync(long uid, long strategyId, string name, CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);

        var trimmed = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        if (trimmed.Length is < 1 or > 50)
        {
            throw new StockLabException(ErrorCode.InvalidInput, "투자 전략 이름은 1~50자 사이로 입력해 주세요.");
        }

        var strategy = await _db.Strategies
            .Include(s => s.Wallets)
            .Include(s => s.Holdings)
            .FirstOrDefaultAsync(s => s.Id == strategyId && s.Uid == uid, ct);

        if (strategy == null)
        {
            throw new StockLabException(ErrorCode.Forbidden, "해당 투자 전략을 찾을 수 없거나 수정 권한이 없습니다.");
        }

        strategy.Name = trimmed;
        await _db.SaveChangesAsync(ct);

        var activeCount = await _db.Orders
            .AsNoTracking()
            .CountAsync(o => o.StrategyId == strategyId && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled), ct);

        _logger.LogInformation("[투자 전략 이름 수정] UID={Uid}, StrategyId={StrategyId}, Name={Name}", uid, strategyId, trimmed);
        return ToStrategySummaryDto(strategy, excludeZeroHoldings: true, _masterStore, new Dictionary<long, int> { [strategyId] = activeCount });
    }

    public async Task<DeleteStrategyResultDto> DeleteStrategyAsync(long uid, long strategyId, CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);

        var strategy = await _db.Strategies
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == strategyId && s.Uid == uid, ct);

        if (strategy == null)
        {
            throw new StockLabException(ErrorCode.Forbidden, "해당 투자 전략을 찾을 수 없거나 삭제 권한이 없습니다.");
        }

        var now = DateTime.UtcNow;
        _db.ChangeTracker.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // 데드락 방지: user_wallets(KRW, USD) -> wallets -> stocks 순서로 FOR UPDATE 행 잠금 획득
        var krwAccountWallet = await EnsureAndLockAccountWalletAsync(uid, "KRW", now, ct);
        var usdAccountWallet = await EnsureAndLockAccountWalletAsync(uid, "USD", now, ct);

        var strategyWallets = await _db.Wallets
            .FromSqlInterpolated(
                $"SELECT *, xmin FROM wallets WHERE \"StrategyId\" = {strategyId} ORDER BY \"Currency\" FOR UPDATE")
            .ToListAsync(ct);

        var strategyHoldings = await _db.Stocks
            .FromSqlInterpolated(
                $"SELECT *, xmin FROM stocks WHERE \"StrategyId\" = {strategyId} ORDER BY \"StockCode\" FOR UPDATE")
            .ToListAsync(ct);

        var activeOrderCount = await _db.Orders
            .CountAsync(o => o.StrategyId == strategyId && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled), ct);

        if (activeOrderCount > 0 || strategyWallets.Any(w => w.PendingCash > 0))
        {
            throw new StockLabException(
                ErrorCode.InvalidInput,
                $"체결 진행중인 주문({activeOrderCount}건)이 남아 있어 전략을 삭제할 수 없습니다. 진행중인 주문을 먼저 취소하거나 체결 완료 후 다시 시도해 주세요.");
        }

        var activeHoldingCount = strategyHoldings.Count(h => h.Quantity > 0 || h.ReservedQuantity > 0);
        if (activeHoldingCount > 0)
        {
            throw new StockLabException(
                ErrorCode.InvalidInput,
                $"보유중인 종목({activeHoldingCount}종목)이 남아 있어 전략을 삭제할 수 없습니다. 보유 종목을 모두 매도한 뒤 삭제해 주세요.");
        }

        decimal reclaimedKrw = 0m;
        decimal reclaimedUsd = 0m;

        foreach (var sw in strategyWallets)
        {
            if (sw.AvailableCash <= 0) continue;

            if (string.Equals(sw.Currency, "KRW", StringComparison.OrdinalIgnoreCase))
            {
                reclaimedKrw += sw.AvailableCash;
                krwAccountWallet.Balance += sw.AvailableCash;
                krwAccountWallet.UpdatedAtUtc = now;
            }
            else if (string.Equals(sw.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            {
                reclaimedUsd += sw.AvailableCash;
                usdAccountWallet.Balance += sw.AvailableCash;
                usdAccountWallet.UpdatedAtUtc = now;
            }

            sw.AvailableCash = 0m;
            sw.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);

        // Trades -> StrategyId FK가 Restrict이므로 해당 전략의 체결 이력 먼저 정리 후 전략 삭제 (Orders/Wallets/Stocks는 Cascade 삭제)
        await _db.Trades
            .Where(t => t.StrategyId == strategyId)
            .ExecuteDeleteAsync(ct);

        var deletedRows = await _db.Strategies
            .Where(s => s.Id == strategyId && s.Uid == uid)
            .ExecuteDeleteAsync(ct);

        if (deletedRows == 0)
        {
            throw new StockLabException(ErrorCode.Forbidden, "해당 투자 전략을 찾을 수 없거나 이미 삭제되었습니다.");
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "[투자 전략 삭제 및 예수금 자동 회수] UID={Uid}, StrategyId={StrategyId}, Name={Name}, ReclaimedKRW={ReclaimedKrw}, ReclaimedUSD={ReclaimedUsd}",
            uid, strategyId, strategy.Name, reclaimedKrw, reclaimedUsd);

        var updatedWallets = await GetAccountWalletsAsync(uid, ct);
        return new DeleteStrategyResultDto(
            strategyId,
            strategy.Name,
            reclaimedKrw,
            reclaimedUsd,
            updatedWallets);
    }

    public const decimal MaxSingleTransactionAmount = 100_000_000_000m;

    private static string NormalizeSupportedCurrency(string? currency)
    {
        var normalized = string.IsNullOrWhiteSpace(currency) ? string.Empty : currency.Trim().ToUpperInvariant();
        if (normalized is not ("KRW" or "USD"))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "지원되는 통화(KRW 또는 USD)만 사용할 수 있습니다.");
        }
        return normalized;
    }

    private static void ValidatePositiveTransactionAmount(decimal amount)
    {
        if (amount <= 0)
        {
            throw new StockLabException(ErrorCode.InvalidInput, "금액은 0보다 커야 합니다.");
        }
        if (amount > MaxSingleTransactionAmount)
        {
            throw new StockLabException(ErrorCode.InvalidInput, $"1회 최대 처리 가능 금액({MaxSingleTransactionAmount:N0})을 초과했습니다.");
        }
    }

    private async Task<UserWallet> EnsureAndLockAccountWalletAsync(long uid, string normalizedCurrency, DateTime now, CancellationToken ct)
    {
        var wallet = await _db.UserWallets
            .FromSqlInterpolated(
                $"SELECT *, xmin FROM user_wallets WHERE \"Uid\" = {uid} AND \"Currency\" = {normalizedCurrency} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (wallet != null)
        {
            return wallet;
        }

        var created = new UserWallet
        {
            Uid = uid,
            Currency = normalizedCurrency,
            Balance = 0m,
            UpdatedAtUtc = now
        };
        _db.UserWallets.Add(created);
        await _db.SaveChangesAsync(ct);

        return await _db.UserWallets
            .FromSqlInterpolated(
                $"SELECT *, xmin FROM user_wallets WHERE \"Uid\" = {uid} AND \"Currency\" = {normalizedCurrency} FOR UPDATE")
            .FirstAsync(ct);
    }

    public async Task<IReadOnlyList<AccountWalletDto>> GetAccountWalletsAsync(long uid, CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);
        var rows = await _db.UserWallets
            .AsNoTracking()
            .Where(w => w.Uid == uid)
            .ToListAsync(ct);

        return BuildAccountWalletDtos(rows, DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<AccountWalletDto>> DepositAccountWalletAsync(
        long uid,
        string currency,
        decimal amount,
        CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);
        var normalizedCurrency = NormalizeSupportedCurrency(currency);
        ValidatePositiveTransactionAmount(amount);

        var now = DateTime.UtcNow;
        _db.ChangeTracker.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var wallet = await EnsureAndLockAccountWalletAsync(uid, normalizedCurrency, now, ct);
        wallet.Balance += amount;
        wallet.UpdatedAtUtc = now;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("[통합 예수금 입금] UID={Uid}, Currency={Currency}, Amount={Amount}, BalanceAfter={BalanceAfter}",
            uid, normalizedCurrency, amount, wallet.Balance);

        return await GetAccountWalletsAsync(uid, ct);
    }

    public async Task<IReadOnlyList<AccountWalletDto>> WithdrawAccountWalletAsync(
        long uid,
        string currency,
        decimal amount,
        CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);
        var normalizedCurrency = NormalizeSupportedCurrency(currency);
        ValidatePositiveTransactionAmount(amount);

        var now = DateTime.UtcNow;
        _db.ChangeTracker.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var wallet = await EnsureAndLockAccountWalletAsync(uid, normalizedCurrency, now, ct);
        if (wallet.Balance < amount)
        {
            throw new StockLabException(
                ErrorCode.InsufficientBalance,
                $"통합 예수금 잔액이 부족합니다. (현재 잔액: {wallet.Balance:N4} {normalizedCurrency}, 출금 요청: {amount:N4} {normalizedCurrency})");
        }

        wallet.Balance -= amount;
        wallet.UpdatedAtUtc = now;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("[통합 예수금 출금] UID={Uid}, Currency={Currency}, Amount={Amount}, BalanceAfter={BalanceAfter}",
            uid, normalizedCurrency, amount, wallet.Balance);

        return await GetAccountWalletsAsync(uid, ct);
    }

    public async Task<StrategyTransferResultDto> TransferBetweenAccountAndStrategyAsync(
        long uid,
        long strategyId,
        string direction,
        string currency,
        decimal amount,
        CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);
        var normalizedCurrency = NormalizeSupportedCurrency(currency);
        ValidatePositiveTransactionAmount(amount);

        var normalizedDir = string.IsNullOrWhiteSpace(direction) ? string.Empty : direction.Trim().ToLowerInvariant();
        if (normalizedDir is not ("tostrategy" or "toaccount"))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "이체 방향은 'ToStrategy' 또는 'ToAccount'여야 합니다.");
        }

        var ownsStrategy = await _db.Strategies
            .AsNoTracking()
            .AnyAsync(s => s.Id == strategyId && s.Uid == uid, ct);
        if (!ownsStrategy)
        {
            throw new StockLabException(ErrorCode.Forbidden, "해당 투자 전략을 찾을 수 없거나 접근 권한이 없습니다.");
        }

        var now = DateTime.UtcNow;
        _db.ChangeTracker.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // 데드락 방지: 항상 user_wallets -> wallets 고정 순서로 FOR UPDATE 행 락 획득
        var accountWallet = await EnsureAndLockAccountWalletAsync(uid, normalizedCurrency, now, ct);
        var strategyWallet = await _db.Wallets
            .FromSqlInterpolated(
                $"SELECT *, xmin FROM wallets WHERE \"StrategyId\" = {strategyId} AND \"Currency\" = {normalizedCurrency} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (strategyWallet == null)
        {
            throw new StockLabException(ErrorCode.AccountNotFound, $"투자 전략({strategyId})의 {normalizedCurrency} 지갑을 찾을 수 없습니다.");
        }

        if (normalizedDir == "tostrategy")
        {
            if (accountWallet.Balance < amount)
            {
                throw new StockLabException(
                    ErrorCode.InsufficientBalance,
                    $"통합 예수금 잔액이 부족합니다. (통합 잔액: {accountWallet.Balance:N4} {normalizedCurrency}, 이체 요청: {amount:N4} {normalizedCurrency})");
            }

            accountWallet.Balance -= amount;
            accountWallet.UpdatedAtUtc = now;
            strategyWallet.AvailableCash += amount;
            strategyWallet.UpdatedAtUtc = now;
        }
        else
        {
            if (strategyWallet.AvailableCash < amount)
            {
                throw new StockLabException(
                    ErrorCode.InsufficientBalance,
                    $"전략의 주문 가능 예수금이 부족합니다. (주문 가능: {strategyWallet.AvailableCash:N4} {normalizedCurrency}, 이체 요청: {amount:N4} {normalizedCurrency})");
            }

            strategyWallet.AvailableCash -= amount;
            strategyWallet.UpdatedAtUtc = now;
            accountWallet.Balance += amount;
            accountWallet.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "[예수금 이체] UID={Uid}, StrategyId={StrategyId}, Direction={Direction}, Currency={Currency}, Amount={Amount}, AccountAfter={AccountAfter}, StrategyAfter={StrategyAfter}",
            uid, strategyId, normalizedDir, normalizedCurrency, amount, accountWallet.Balance, strategyWallet.AvailableCash);

        return new StrategyTransferResultDto(
            new AccountWalletDto(accountWallet.Id, accountWallet.Currency, accountWallet.Balance, accountWallet.UpdatedAtUtc),
            new WalletSummaryDto(strategyWallet.Id, strategyWallet.Currency, strategyWallet.AvailableCash, strategyWallet.PendingCash, strategyWallet.RealizedPnl));
    }

    public async Task<IReadOnlyList<WatchlistItemDto>> GetWatchlistAsync(long uid, CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);

        var items = await _db.WatchlistItems
            .AsNoTracking()
            .Where(w => w.Uid == uid)
            .OrderByDescending(w => w.CreatedAtUtc)
            .ToListAsync(ct);

        return items.Select(ToWatchlistItemDto).ToList();
    }

    public async Task<WatchlistItemDto> AddWatchlistAsync(long uid, string stockCode, CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);

        var trimmed = string.IsNullOrWhiteSpace(stockCode) ? string.Empty : stockCode.Trim();
        var master = _masterStore.GetByCode(trimmed)
            ?? throw new StockLabException(ErrorCode.StockNotFound, $"종목코드 '{stockCode}'를 찾을 수 없습니다.");

        var exists = await _db.WatchlistItems
            .AsNoTracking()
            .AnyAsync(w => w.Uid == uid && w.StockCode == master.ShortCode, ct);

        if (exists)
        {
            throw new StockLabException(ErrorCode.ConcurrencyConflict, $"'{master.KoreanName}'({master.ShortCode})은(는) 이미 관심종목에 등록되어 있습니다.");
        }

        var currentCount = await _db.WatchlistItems.CountAsync(w => w.Uid == uid, ct);
        if (currentCount >= MaxWatchlistItemsPerUser)
        {
            throw new StockLabException(ErrorCode.InvalidInput, $"관심종목은 최대 {MaxWatchlistItemsPerUser}개까지 등록할 수 있습니다.");
        }

        var entity = new WatchlistItem
        {
            Uid = uid,
            StockCode = master.ShortCode,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.WatchlistItems.Add(entity);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("[관심종목 추가] UID={Uid}, StockCode={StockCode}", uid, master.ShortCode);
        return ToWatchlistItemDto(entity);
    }

    public async Task RemoveWatchlistAsync(long uid, string stockCode, CancellationToken ct = default)
    {
        await EnsureRegisteredUserAsync(uid, ct);

        var trimmed = string.IsNullOrWhiteSpace(stockCode) ? string.Empty : stockCode.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "종목코드를 입력해 주세요.");
        }

        var deleted = await _db.WatchlistItems
            .Where(w => w.Uid == uid && w.StockCode.ToUpper() == trimmed)
            .ExecuteDeleteAsync(ct);

        if (deleted == 0)
        {
            throw new StockLabException(ErrorCode.StockNotFound, "관심종목에서 해당 종목을 찾을 수 없습니다.");
        }
    }

    private WatchlistItemDto ToWatchlistItemDto(WatchlistItem item)
    {
        var master = _masterStore.GetByCode(item.StockCode);
        return new WatchlistItemDto(
            item.Id,
            item.StockCode,
            master?.KoreanName ?? item.StockCode,
            master?.MarketType ?? "-",
            master?.ExchangeCode ?? "-",
            master?.Currency ?? (item.StockCode.All(char.IsDigit) ? "KRW" : "USD"),
            master?.BasePrice ?? 0m,
            item.CreatedAtUtc);
    }
}
