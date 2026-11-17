using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Persistence;
using StockLab.Persistence.Entities;
using StockLab.Services.Account;

namespace StockLab.Services.Auth;

public class GuestAuthService
{
    public const decimal InitialKrwSeed = 1_000_000m;
    public const decimal InitialUsdSeed = 1_000m;

    private const long MinUid18 = 100_000_000_000_000_000L;
    private const long MaxUid18Exclusive = 1_000_000_000_000_000_000L;

    private readonly AppDbContext _db;
    private readonly ILogger<GuestAuthService> _logger;

    public GuestAuthService(AppDbContext db, ILogger<GuestAuthService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 18자리 순수 난수 UID로 게스트 사용자, 기본 시나리오 및 KRW/USD 지갑을 생성합니다.
    /// </summary>
    public async Task<UserProfileDto> CreateGuestSessionAsync(string? nickname, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var resolvedNickname = string.IsNullOrWhiteSpace(nickname)
            ? $"게스트-{Random.Shared.Next(1000, 9999)}"
            : nickname.Trim();

        if (resolvedNickname.Length > 50)
        {
            throw new StockLabException(ErrorCode.InvalidInput, "닉네임은 50자 이하로 입력해야 합니다.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var uid = await GenerateUniqueUidAsync(ct);

        var user = new User
        {
            Uid = uid,
            Type = UserType.Guest,
            Nickname = resolvedNickname,
            CreatedAtUtc = now,
            LastActiveAtUtc = now
        };

        var defaultScenario = new Scenario
        {
            Uid = uid,
            Name = "기본 시나리오",
            CreatedAtUtc = now,
            Wallets = new List<Wallet>
            {
                new()
                {
                    Currency = "KRW",
                    AvailableCash = InitialKrwSeed,
                    PendingCash = 0m,
                    UpdatedAtUtc = now
                },
                new()
                {
                    Currency = "USD",
                    AvailableCash = InitialUsdSeed,
                    PendingCash = 0m,
                    UpdatedAtUtc = now
                }
            }
        };

        user.Scenarios.Add(defaultScenario);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("[게스트 생성] UID={Uid}, Nickname={Nickname}, ScenarioId={ScenarioId}, KRW={Krw:N0}, USD={Usd:N2}",
            user.Uid, user.Nickname, defaultScenario.Id, InitialKrwSeed, InitialUsdSeed);

        var wallets = defaultScenario.Wallets
            .OrderBy(w => w.Currency)
            .Select(w => new WalletSummaryDto(w.Id, w.Currency, w.AvailableCash, w.PendingCash))
            .ToList();

        return new UserProfileDto(
            user.Uid.ToString(),
            user.Type,
            user.Nickname,
            user.CreatedAtUtc,
            new[]
            {
                new ScenarioSummaryDto(defaultScenario.Id, defaultScenario.Name, defaultScenario.CreatedAtUtc, wallets, Array.Empty<StockHoldingDto>())
            });
    }

    public async Task<UserProfileDto> GetProfileAsync(long uid, CancellationToken ct = default)
    {
        var user = await _db.Users
            .AsNoTracking()
            .AsSplitQuery()
            .Include(u => u.Scenarios)
                .ThenInclude(s => s.Wallets)
            .Include(u => u.Scenarios)
                .ThenInclude(s => s.Holdings)
            .FirstOrDefaultAsync(u => u.Uid == uid, ct);

        if (user == null)
        {
            throw new StockLabException(ErrorCode.Unauthorized, "유효한 사용자 세션을 찾을 수 없습니다.");
        }

        return AccountService.ToUserProfileDto(user, excludeZeroHoldings: true);
    }

    private async Task<long> GenerateUniqueUidAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = GenerateCryptographicUid18();
            var exists = await _db.Users.AnyAsync(u => u.Uid == candidate, ct);
            if (!exists)
            {
                return candidate;
            }
        }

        throw new StockLabException(ErrorCode.InternalServerError, "고유 UID 생성에 실패했습니다.");
    }

    private static long GenerateCryptographicUid18()
    {
        const ulong range = (ulong)(MaxUid18Exclusive - MinUid18);
        ulong limit = ulong.MaxValue - (ulong.MaxValue % range);
        Span<byte> buffer = stackalloc byte[8];

        while (true)
        {
            RandomNumberGenerator.Fill(buffer);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            if (value < limit)
            {
                return MinUid18 + (long)(value % range);
            }
        }
    }
}
