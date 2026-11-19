using Microsoft.EntityFrameworkCore;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Persistence;

namespace StockLab.Services.Account;

public class AccountService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AccountService> _logger;

    public AccountService(AppDbContext db, ILogger<AccountService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 관리자 전용: 비관적 행 락(FOR UPDATE)으로 특정 시나리오 지갑의 예수가능금을 증감합니다.
    /// </summary>
    public async Task<WalletSummaryDto> AdjustBalanceAsync(
        long scenarioId,
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
                $"SELECT *, xmin FROM wallets WHERE \"ScenarioId\" = {scenarioId} AND \"Currency\" = {normalizedCurrency} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (wallet == null)
        {
            throw new StockLabException(ErrorCode.AccountNotFound, $"시나리오({scenarioId})의 {normalizedCurrency} 지갑을 찾을 수 없습니다.");
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

        _logger.LogInformation("[관리자 시드머니 조정] ScenarioId={ScenarioId}, Currency={Currency}, Delta={Delta}, AvailableAfter={BalanceAfter}",
            scenarioId, normalizedCurrency, deltaAmount, nextBalance);

        return new WalletSummaryDto(wallet.Id, wallet.Currency, wallet.AvailableCash, wallet.PendingCash);
    }

    /// <summary>
    /// 관리자 전용: 전체 또는 검색어(UID/닉네임) 기준 유저 목록과 시나리오·지갑·보유종목을 조회합니다.
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
                .Include(u => u.Scenarios)
                    .ThenInclude(s => s.Wallets)
                .Include(u => u.Scenarios)
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

    public static UserProfileDto ToUserProfileDto(Persistence.Entities.User user, bool excludeZeroHoldings) => new(
        user.Uid.ToString(),
        user.Type,
        user.Nickname,
        user.CreatedAtUtc,
        user.Scenarios
            .OrderBy(s => s.Id)
            .Select(s => new ScenarioSummaryDto(
                s.Id,
                s.Name,
                s.CreatedAtUtc,
                s.Wallets
                    .OrderBy(w => w.Currency)
                    .Select(w => new WalletSummaryDto(w.Id, w.Currency, w.AvailableCash, w.PendingCash))
                    .ToList(),
                s.Holdings
                    .Where(h => !excludeZeroHoldings || h.Quantity > 0)
                    .OrderBy(h => h.StockCode)
                    .Select(h => new StockHoldingDto(h.Id, h.StockCode, h.Quantity, h.AverageBuyPrice))
                    .ToList()))
            .ToList());

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
            .Include(u => u.Scenarios)
                .ThenInclude(s => s.Wallets)
            .Include(u => u.Scenarios)
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

        return new UserProfileDto(
            user.Uid.ToString(),
            user.Type,
            user.Nickname,
            user.CreatedAtUtc,
            user.Scenarios
                .OrderBy(s => s.Id)
                .Select(s => new ScenarioSummaryDto(
                    s.Id,
                    s.Name,
                    s.CreatedAtUtc,
                    s.Wallets
                        .OrderBy(w => w.Currency)
                        .Select(w => new WalletSummaryDto(w.Id, w.Currency, w.AvailableCash, w.PendingCash))
                        .ToList(),
                    s.Holdings
                        .OrderBy(h => h.StockCode)
                        .Select(h => new StockHoldingDto(h.Id, h.StockCode, h.Quantity, h.AverageBuyPrice))
                        .ToList()))
                .ToList());
    }
}
