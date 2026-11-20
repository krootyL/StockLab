using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Persistence;
using StockLab.Persistence.Entities;
using StockLab.Services.Account;
using StockLab.Services.Master;

namespace StockLab.Services.Auth;

public class GuestAuthService
{
    public const decimal InitialKrwSeed = 1_000_000m;
    public const decimal InitialUsdSeed = 1_000m;

    private const long MinUid18 = 100_000_000_000_000_000L;
    private const long MaxUid18Exclusive = 1_000_000_000_000_000_000L;

    private static readonly PasswordHasher<User> PasswordHasher = new();

    private readonly AppDbContext _db;
    private readonly StockMasterStore _masterStore;
    private readonly ILogger<GuestAuthService> _logger;

    public GuestAuthService(AppDbContext db, StockMasterStore masterStore, ILogger<GuestAuthService> logger)
    {
        _db = db;
        _masterStore = masterStore;
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
            Type = UserType.Local,
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

    public async Task<bool> IsLoginIdAvailableAsync(string loginId, CancellationToken ct = default)
    {
        var trimmedId = loginId?.Trim() ?? string.Empty;
        if (trimmedId.Length < 3 || trimmedId.Length > 50 || trimmedId.Any(char.IsWhiteSpace))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "아이디는 공백 없이 3~50자 사이로 입력해야 합니다.");
        }

        return !await _db.Users.AnyAsync(u => u.LoginId == trimmedId, ct);
    }

    public async Task<UserProfileDto> RegisterLocalUserAsync(
        string loginId,
        string password,
        string nickname,
        CancellationToken ct = default)
    {
        var trimmedId = loginId?.Trim() ?? string.Empty;
        var trimmedNick = nickname?.Trim() ?? string.Empty;

        if (trimmedId.Length < 3 || trimmedId.Length > 50 || trimmedId.Any(char.IsWhiteSpace))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "아이디는 공백 없이 3~50자 사이로 입력해야 합니다.");
        }

        if (!IsValidPassword(password))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "비밀번호는 공백 없이 8~64자이며 영문, 숫자, 특수문자를 모두 포함해야 합니다.");
        }

        if (string.IsNullOrWhiteSpace(trimmedNick) || trimmedNick.Length > 50)
        {
            throw new StockLabException(ErrorCode.InvalidInput, "닉네임은 1~50자 사이로 입력해야 합니다.");
        }

        var exists = await _db.Users.AnyAsync(u => u.LoginId == trimmedId, ct);
        if (exists)
        {
            throw new StockLabException(ErrorCode.ConcurrencyConflict, "이미 사용 중인 아이디입니다.");
        }

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var uid = await GenerateUniqueUidAsync(ct);
        var user = new User
        {
            Uid = uid,
            Type = UserType.Local,
            LoginId = trimmedId,
            Nickname = trimmedNick,
            CreatedAtUtc = now,
            LastActiveAtUtc = now
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, password);

        var defaultScenario = new Scenario
        {
            Uid = uid,
            Name = "기본 시나리오",
            CreatedAtUtc = now,
            Wallets = new List<Wallet>
            {
                new() { Currency = "KRW", AvailableCash = InitialKrwSeed, PendingCash = 0m, UpdatedAtUtc = now },
                new() { Currency = "USD", AvailableCash = InitialUsdSeed, PendingCash = 0m, UpdatedAtUtc = now }
            }
        };

        user.Scenarios.Add(defaultScenario);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("[회원가입] UID={Uid}, LoginId={LoginId}, Nickname={Nickname}",
            user.Uid, user.LoginId, user.Nickname);

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

    public async Task<UserProfileDto> LoginAsync(string loginId, string password, CancellationToken ct = default)
    {
        var trimmedId = loginId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedId) || string.IsNullOrWhiteSpace(password))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "아이디와 비밀번호를 모두 입력해 주세요.");
        }

        var user = await _db.Users
            .Include(u => u.Scenarios)
                .ThenInclude(s => s.Wallets)
            .Include(u => u.Scenarios)
                .ThenInclude(s => s.Holdings)
            .FirstOrDefaultAsync(u => u.LoginId == trimmedId, ct);

        if (user == null || string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            throw new StockLabException(ErrorCode.Unauthorized, "아이디 또는 비밀번호가 올바르지 않습니다.");
        }

        var verifyResult = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verifyResult == PasswordVerificationResult.Failed)
        {
            throw new StockLabException(ErrorCode.Unauthorized, "아이디 또는 비밀번호가 올바르지 않습니다.");
        }

        user.LastActiveAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("[로그인] UID={Uid}, LoginId={LoginId}, Type={Type}",
            user.Uid, user.LoginId, user.Type);

        return AccountService.ToUserProfileDto(user, excludeZeroHoldings: true, _masterStore);
    }

    private static bool IsValidPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 64)
        {
            return false;
        }

        if (password.Any(char.IsWhiteSpace))
        {
            return false;
        }

        bool hasLetter = password.Any(char.IsLetter);
        bool hasDigit = password.Any(char.IsDigit);
        bool hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));
        return hasLetter && hasDigit && hasSpecial;
    }

    public async Task EnsureAdminAccountAsync(string adminLoginId, string adminPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adminLoginId) || string.IsNullOrWhiteSpace(adminPassword)) return;

        var trimmedId = adminLoginId.Trim();
        var existingAdmin = await _db.Users.FirstOrDefaultAsync(u => u.LoginId == trimmedId, ct);
        if (existingAdmin != null)
        {
            existingAdmin.Type = UserType.Admin;
            existingAdmin.PasswordHash = PasswordHasher.HashPassword(existingAdmin, adminPassword);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var now = DateTime.UtcNow;
        var uid = await GenerateUniqueUidAsync(ct);
        var admin = new User
        {
            Uid = uid,
            Type = UserType.Admin,
            LoginId = trimmedId,
            Nickname = "관리자",
            CreatedAtUtc = now,
            LastActiveAtUtc = now
        };
        admin.PasswordHash = PasswordHasher.HashPassword(admin, adminPassword);
        admin.Scenarios.Add(new Scenario
        {
            Uid = uid,
            Name = "관리자 시나리오",
            CreatedAtUtc = now,
            Wallets = new List<Wallet>
            {
                new() { Currency = "KRW", AvailableCash = InitialKrwSeed, PendingCash = 0m, UpdatedAtUtc = now },
                new() { Currency = "USD", AvailableCash = InitialUsdSeed, PendingCash = 0m, UpdatedAtUtc = now }
            }
        });

        _db.Users.Add(admin);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[관리자 계정 시딩] 관리자 계정(LoginId={LoginId}, Type={Type})이 준비되었습니다.", trimmedId, (int)UserType.Admin);
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

        return AccountService.ToUserProfileDto(user, excludeZeroHoldings: true, _masterStore);
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
