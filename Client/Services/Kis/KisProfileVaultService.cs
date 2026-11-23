using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using StockLab.Common;
using StockLab.Services.Kis;
using StockLab.Settings;

namespace StockLab.Client.Services.Kis;

public sealed record KisApiProfile(
    string Id,
    string Name,
    string AppKey,
    string AppSecret,
    List<string> AllowedUserIds,
    string? AccessToken = null,
    DateTime? ExpiresAtUtc = null
);

public sealed record KisProfileSummary(
    string Id,
    string Name,
    IReadOnlyList<string> AllowedUserIds,
    bool HasValidToken,
    DateTime? ExpiresAtUtc
);

public sealed record KisVaultStatus(
    bool IsConnected,
    string? ActiveProfileId,
    string? ActiveProfileName,
    string Source,
    IReadOnlyList<KisProfileSummary> Profiles
);

/// <summary>
/// Windows DPAPI 기반 키 링 + AES-256 인증 암호화(DataProtection)로 다중 KIS API 프로필을 로컬 저장하는 금고 서비스.
/// N:1 소유자 매핑(AllowedUserIds)으로 허용된 계정에 한해 동일 KIS 프로필(토큰 포함)을 공유하며,
/// 프로필 이름(Name)은 고유 Id와 분리되어 중복 및 언제든 수정이 가능하다.
/// </summary>
public class KisProfileVaultService
{
    private sealed record VaultStoreData(string? ActiveProfileId, List<KisApiProfile> Profiles);

    private static readonly TimeSpan TokenSafeBuffer = TimeSpan.FromMinutes(10);
    private readonly IDataProtector _protector;
    private readonly KisSettings _settings;
    private readonly KisSessionService _sessionService;
    private readonly ILogger<KisProfileVaultService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _vaultFilePath;

    private string? _activeProfileId;
    private string? _activeProfileName;
    private string _source = "locked";

    public KisProfileVaultService(
        IOptions<KisSettings> settings,
        KisSessionService sessionService,
        ILogger<KisProfileVaultService> logger)
    {
        _settings = settings.Value;
        _sessionService = sessionService;
        _logger = logger;

        var vaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StockLab",
            "Vault");
        var keyRingDir = Directory.CreateDirectory(Path.Combine(vaultRoot, "keys"));
        _vaultFilePath = Path.Combine(vaultRoot, "kis-profiles.vault");

        var dpProvider = DataProtectionProvider.Create(keyRingDir, cfg =>
        {
            cfg.SetApplicationName("StockLab.Client");
            if (OperatingSystem.IsWindows())
            {
                cfg.ProtectKeysWithDpapi(protectToLocalMachine: false);
            }
        });
        _protector = dpProvider.CreateProtector("StockLab.KisProfileVault.v1");
    }

    private static bool IsOwnerAllowed(KisApiProfile profile, string? ownerUid)
    {
        if (string.IsNullOrWhiteSpace(ownerUid) || ownerUid == "*") return true;
        if (profile.AllowedUserIds == null || profile.AllowedUserIds.Count == 0) return true;
        return profile.AllowedUserIds.Any(id => string.Equals(id, ownerUid, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> NormalizeAllowedIds(string ownerUid, IEnumerable<string>? extraIds)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(ownerUid) && ownerUid != "*")
        {
            list.Add(ownerUid.Trim());
        }
        if (extraIds != null)
        {
            foreach (var raw in extraIds)
            {
                var t = raw?.Trim();
                if (!string.IsNullOrWhiteSpace(t) && !list.Contains(t, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(t);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// 로그인된 계정(ownerUid)이 접근 가능한 KIS 프로필을 로컬 DPAPI 금고 파일에서 잠금 해제한다.
    /// </summary>
    public KisVaultStatus UnlockFromVault(string ownerUid)
    {
        var store = LoadVaultUnsafe();
        var allowed = store.Profiles.Where(p => IsOwnerAllowed(p, ownerUid)).ToList();
        if (allowed.Count == 0)
        {
            Lock();
            return GetStatus(ownerUid);
        }

        var active = !string.IsNullOrWhiteSpace(store.ActiveProfileId)
            ? allowed.FirstOrDefault(p => p.Id.Equals(store.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            : null;
        active ??= allowed[0];

        ApplyProfileToRuntime(active);
        _activeProfileId = active.Id;
        _activeProfileName = active.Name;
        _source = "vault";
        _logger.LogInformation("[KIS 잠금 해제] 소유자({Owner}) 금고 프로필 '{ProfileName}' 적재 완료",
            ownerUid, active.Name);
        return GetStatus(ownerUid);
    }

    public void Lock()
    {
        _settings.AppKey = string.Empty;
        _settings.AppSecret = string.Empty;
        _activeProfileId = null;
        _activeProfileName = null;
        _source = "locked";
    }

    public KisVaultStatus GetStatus(string? ownerUid = null)
    {
        var store = LoadVaultUnsafe();
        var visible = string.IsNullOrWhiteSpace(ownerUid)
            ? new List<KisApiProfile>()
            : store.Profiles.Where(p => IsOwnerAllowed(p, ownerUid)).ToList();

        var now = DateTime.UtcNow;
        var summaries = visible.Select(p => new KisProfileSummary(
            p.Id,
            p.Name,
            p.AllowedUserIds,
            !string.IsNullOrWhiteSpace(p.AccessToken) && p.ExpiresAtUtc.HasValue && (p.ExpiresAtUtc.Value - TokenSafeBuffer) > now,
            p.ExpiresAtUtc
        )).ToList();

        var isConnected = !string.IsNullOrWhiteSpace(_settings.AppKey) && !string.IsNullOrWhiteSpace(_settings.AppSecret);
        return new KisVaultStatus(
            isConnected,
            _activeProfileId,
            _activeProfileName,
            _source,
            summaries
        );
    }

    public async Task<KisVaultStatus> CreateAndActivateAsync(
        string ownerUid,
        string name,
        string appKey,
        string appSecret,
        IEnumerable<string>? sharedUserIds = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = string.IsNullOrWhiteSpace(name) ? "기본 계정" : name.Trim();
        var trimmedKey = (appKey ?? string.Empty).Trim();
        var trimmedSecret = (appSecret ?? string.Empty).Trim();

        if (trimmedKey.Length != 36)
        {
            throw new StockLabException(ErrorCode.InvalidInput, $"KIS AppKey는 36자리여야 합니다. (입력: {trimmedKey.Length}자리)");
        }
        if (trimmedSecret.Length != 180)
        {
            throw new StockLabException(ErrorCode.InvalidInput, $"KIS AppSecret은 180자리여야 합니다. (입력: {trimmedSecret.Length}자리)");
        }

        await _lock.WaitAsync(cancellationToken);
        var prevKey = _settings.AppKey;
        var prevSecret = _settings.AppSecret;
        var prevId = _activeProfileId;
        var prevProfile = _activeProfileName;
        var prevSource = _source;

        try
        {
            _settings.AppKey = trimmedKey;
            _settings.AppSecret = trimmedSecret;
            _sessionService.ClearCachedToken(trimmedKey);

            var (accessToken, expiresAtUtc) = await _sessionService.GetAccessTokenWithExpiryAsync(cancellationToken);

            var store = LoadVaultUnsafe();
            var owners = NormalizeAllowedIds(ownerUid, sharedUserIds);
            var newProfile = new KisApiProfile(
                Guid.NewGuid().ToString("N"),
                trimmedName,
                trimmedKey,
                trimmedSecret,
                owners,
                accessToken,
                expiresAtUtc
            );
            store.Profiles.Add(newProfile);

            SaveVaultUnsafe(new VaultStoreData(newProfile.Id, store.Profiles));

            _activeProfileId = newProfile.Id;
            _activeProfileName = newProfile.Name;
            _source = "vault";
            _logger.LogInformation("[KIS DPAPI 금고] 프로필 '{ProfileName}' (Id={Id}, Owners={Owners}) 암호화 저장 완료",
                newProfile.Name, newProfile.Id, string.Join(',', owners));

            return GetStatus(ownerUid);
        }
        catch
        {
            _settings.AppKey = prevKey;
            _settings.AppSecret = prevSecret;
            _activeProfileId = prevId;
            _activeProfileName = prevProfile;
            _source = prevSource;
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KisVaultStatus> UpdateProfileMetaAsync(
        string id,
        string ownerUid,
        string newName,
        IEnumerable<string>? sharedUserIds = null,
        string? newAppKey = null,
        string? newAppSecret = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "프로필 이름을 입력해주세요.");
        }

        var hasNewKeys = !string.IsNullOrWhiteSpace(newAppKey) || !string.IsNullOrWhiteSpace(newAppSecret);
        var trimmedKey = (newAppKey ?? string.Empty).Trim();
        var trimmedSecret = (newAppSecret ?? string.Empty).Trim();

        if (hasNewKeys)
        {
            if (trimmedKey.Length != 36)
            {
                throw new StockLabException(ErrorCode.InvalidInput, $"KIS AppKey는 36자리여야 합니다. (입력: {trimmedKey.Length}자리)");
            }
            if (trimmedSecret.Length != 180)
            {
                throw new StockLabException(ErrorCode.InvalidInput, $"KIS AppSecret은 180자리여야 합니다. (입력: {trimmedSecret.Length}자리)");
            }
        }

        await _lock.WaitAsync(cancellationToken);
        var prevKey = _settings.AppKey;
        var prevSecret = _settings.AppSecret;
        try
        {
            var store = LoadVaultUnsafe();
            var idx = store.Profiles.FindIndex(p => p.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || !IsOwnerAllowed(store.Profiles[idx], ownerUid))
            {
                throw new StockLabException(ErrorCode.AccountNotFound, "수정할 KIS 프로필을 찾을 수 없습니다.");
            }

            var existing = store.Profiles[idx];
            var owners = sharedUserIds != null
                ? NormalizeAllowedIds(ownerUid, sharedUserIds)
                : existing.AllowedUserIds;

            var updated = existing with { Name = trimmedName, AllowedUserIds = owners };

            if (hasNewKeys)
            {
                _settings.AppKey = trimmedKey;
                _settings.AppSecret = trimmedSecret;
                _sessionService.ClearCachedToken(trimmedKey);

                var (accessToken, expiresAtUtc) = await _sessionService.GetAccessTokenWithExpiryAsync(cancellationToken);
                updated = updated with
                {
                    AppKey = trimmedKey,
                    AppSecret = trimmedSecret,
                    AccessToken = accessToken,
                    ExpiresAtUtc = expiresAtUtc
                };

                // 현재 활성 프로필이 아니면 원래 런타임 키 유지, 활성 프로필이면 새 키 유지
                if (!string.Equals(_activeProfileId, updated.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.AppKey = prevKey;
                    _settings.AppSecret = prevSecret;
                }
            }

            store.Profiles[idx] = updated;
            SaveVaultUnsafe(new VaultStoreData(store.ActiveProfileId, store.Profiles));

            if (string.Equals(_activeProfileId, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                _activeProfileName = updated.Name;
            }

            return GetStatus(ownerUid);
        }
        catch
        {
            _settings.AppKey = prevKey;
            _settings.AppSecret = prevSecret;
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KisVaultStatus> SelectProfileAsync(string id, string ownerUid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "전환할 프로필을 선택해주세요.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = LoadVaultUnsafe();
            var target = store.Profiles.FirstOrDefault(p =>
                p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase) && IsOwnerAllowed(p, ownerUid))
                ?? throw new StockLabException(ErrorCode.AccountNotFound, "해당 KIS 프로필을 찾을 수 없거나 접근 권한이 없습니다.");

            ApplyProfileToRuntime(target);
            var (token, expiresAtUtc) = await _sessionService.GetAccessTokenWithExpiryAsync(cancellationToken);

            var refreshed = target with { AccessToken = token, ExpiresAtUtc = expiresAtUtc };
            var idx = store.Profiles.FindIndex(p => p.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) store.Profiles[idx] = refreshed;

            SaveVaultUnsafe(new VaultStoreData(refreshed.Id, store.Profiles));
            _activeProfileId = refreshed.Id;
            _activeProfileName = refreshed.Name;
            _source = "vault";
            return GetStatus(ownerUid);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KisVaultStatus> DeleteProfileAsync(string id, string ownerUid, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = LoadVaultUnsafe();
            var target = store.Profiles.FirstOrDefault(p =>
                p.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase) && IsOwnerAllowed(p, ownerUid))
                ?? throw new StockLabException(ErrorCode.AccountNotFound, "삭제할 KIS 프로필을 찾을 수 없습니다.");

            store.Profiles.Remove(target);

            string? nextActiveId = store.ActiveProfileId;
            if (string.Equals(_activeProfileId, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                var fallback = store.Profiles.FirstOrDefault(p => IsOwnerAllowed(p, ownerUid));
                if (fallback != null)
                {
                    ApplyProfileToRuntime(fallback);
                    nextActiveId = fallback.Id;
                    _activeProfileId = fallback.Id;
                    _activeProfileName = fallback.Name;
                    _source = "vault";
                }
                else
                {
                    Lock();
                    nextActiveId = null;
                }
            }

            SaveVaultUnsafe(new VaultStoreData(nextActiveId, store.Profiles));
            return GetStatus(ownerUid);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ApplyProfileToRuntime(KisApiProfile profile)
    {
        _settings.AppKey = profile.AppKey;
        _settings.AppSecret = profile.AppSecret;
        if (!string.IsNullOrWhiteSpace(profile.AccessToken)
            && profile.ExpiresAtUtc.HasValue
            && (profile.ExpiresAtUtc.Value - TokenSafeBuffer) > DateTime.UtcNow)
        {
            _sessionService.SeedToken(profile.AppKey, profile.AccessToken, profile.ExpiresAtUtc.Value);
        }
    }

    private VaultStoreData LoadVaultUnsafe()
    {
        if (!File.Exists(_vaultFilePath)) return new VaultStoreData(null, new List<KisApiProfile>());
        try
        {
            var protectedPayload = File.ReadAllText(_vaultFilePath);
            var json = _protector.Unprotect(protectedPayload);
            var raw = JsonSerializer.Deserialize<VaultStoreData>(json);
            if (raw == null) return new VaultStoreData(null, new List<KisApiProfile>());

            // 구 포맷(Id / AllowedUserIds 미존재) 자동 마이그레이션
            var normalized = raw.Profiles.Select(p => new KisApiProfile(
                string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
                p.Name,
                p.AppKey,
                p.AppSecret,
                p.AllowedUserIds ?? new List<string>(),
                p.AccessToken,
                p.ExpiresAtUtc
            )).ToList();

            return new VaultStoreData(raw.ActiveProfileId, normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KIS DPAPI 금고] 암호화 금고 파일 복호화 실패 (새 금고로 초기화)");
            return new VaultStoreData(null, new List<KisApiProfile>());
        }
    }

    private void SaveVaultUnsafe(VaultStoreData data)
    {
        var json = JsonSerializer.Serialize(data);
        var protectedPayload = _protector.Protect(json);
        File.WriteAllText(_vaultFilePath, protectedPayload);
    }
}
