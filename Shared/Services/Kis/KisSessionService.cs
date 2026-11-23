using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StockLab.Common;
using StockLab.Settings;

namespace StockLab.Services.Kis;

public class KisSessionService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly KisSettings _settings;
    private readonly ILogger<KisSessionService> _logger;

    private const string CacheKeyPrefix = "KIS_ACCESS_TOKEN:";
    private static readonly ConcurrentDictionary<string, string> _approvalKeys = new(StringComparer.Ordinal);
    private static readonly TimeSpan SafeBufferTime = TimeSpan.FromMinutes(10);
    private static readonly string TokenFilePath = Path.Combine(AppContext.BaseDirectory, ".kis_token.json");

    public KisSessionService(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<KisSettings> options,
        ILogger<KisSessionService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _settings = options.Value;
        _logger = logger;
    }

    private static string GetAppKeyHash(string appKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(appKey));
        return Convert.ToHexString(hash)[..16];
    }

    public void SeedToken(string appKey, string token, DateTime expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(token)) return;
        var localExpiry = expiresAtUtc.Kind == DateTimeKind.Utc ? expiresAtUtc.ToLocalTime() : expiresAtUtc;
        SetMemoryCache(appKey, token, localExpiry);
    }

    public void ClearCachedToken(string? appKey = null)
    {
        var target = string.IsNullOrWhiteSpace(appKey) ? _settings.AppKey : appKey;
        if (!string.IsNullOrWhiteSpace(target))
        {
            _cache.Remove($"{CacheKeyPrefix}{target}");
            _approvalKeys.TryRemove(target, out _);
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var (token, _) = await GetAccessTokenWithExpiryAsync(cancellationToken);
        return token;
    }

    public async Task<(string AccessToken, DateTime ExpiresAtUtc)> GetAccessTokenWithExpiryAsync(CancellationToken cancellationToken = default)
    {
        // 0. 설정값 최우선 검증 (키 누락 시 메모리/파일 캐시 토큰 재사용도 전면 차단)
        if (string.IsNullOrWhiteSpace(_settings.AppKey) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            throw new StockLabException(ErrorCode.KisConfigMissing, "로컬 KIS OpenAPI AppKey 또는 AppSecret이 설정되지 않아 접근이 제한됩니다.");
        }

        var cacheKey = $"{CacheKeyPrefix}{_settings.AppKey}";

        // 1. 메모리 캐시 확인
        if (_cache.TryGetValue(cacheKey, out CachedTokenEntry? cachedEntry) && cachedEntry != null && !string.IsNullOrWhiteSpace(cachedEntry.Token))
        {
            return (cachedEntry.Token, cachedEntry.ExpiresAtUtc);
        }

        // 2. 파일 캐시 확인 (동일 AppKey 해시일 때만 복원)
        var keyHash = GetAppKeyHash(_settings.AppKey);
        var (loaded, fileToken, fileExpiresAt) = await TryLoadTokenFromFileAsync(keyHash, cancellationToken);
        if (loaded)
        {
            SetMemoryCache(_settings.AppKey, fileToken, fileExpiresAt);
            _logger.LogInformation("[파일 캐시] 저장된 .kis_token.json 파일에서 토큰 복원 완료 (만료 예정: {ExpiresAt:yyyy-MM-dd HH:mm:ss})", fileExpiresAt);
            return (fileToken, fileExpiresAt.ToUniversalTime());
        }

        // 3. 증권사 신규 발급 요청
        _logger.LogInformation("{Prefix} 토큰이 없거나 만료되어 한국투자증권 서버에 신규 발급을 요청합니다. (POST /oauth2/tokenP)", LogConstants.KisApiPrefix);

        var requestBody = new
        {
            grant_type = "client_credentials",
            appkey = _settings.AppKey,
            appsecret = _settings.AppSecret
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(
                $"{_settings.RestBaseUrl}/oauth2/tokenP",
                requestBody,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            throw new StockLabException(ErrorCode.KisAuthFailed, $"KIS 인증 서버와 통신 중 에러가 발생했습니다: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new StockLabException(ErrorCode.KisAuthFailed, $"KIS 인증 실패 (상태코드: {response.StatusCode}, 사유: {errorBody})");
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<KisTokenResponse>(cancellationToken: cancellationToken);
        if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            throw new StockLabException(ErrorCode.KisAuthFailed, "KIS 토큰 발급 응답 데이터가 올바르지 않습니다.");
        }

        DateTime expiresAt;
        if (DateTime.TryParseExact(tokenResponse.AccessTokenTokenExpired, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            expiresAt = parsedDate;
        }
        else
        {
            expiresAt = DateTime.Now.AddSeconds(tokenResponse.ExpiresIn);
        }

        SetMemoryCache(_settings.AppKey, tokenResponse.AccessToken, expiresAt);
        await SaveTokenToFileAsync(keyHash, tokenResponse.AccessToken, expiresAt, cancellationToken);

        _logger.LogInformation("{Prefix} 신규 접근 토큰 확보 완료 (만료 예정: {ExpiresAt:yyyy-MM-dd HH:mm:ss})", LogConstants.KisApiPrefix, expiresAt);

        return (tokenResponse.AccessToken, expiresAt.ToUniversalTime());
    }

    public async Task<string> GetWebSocketApprovalKeyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.AppKey) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            throw new StockLabException(ErrorCode.KisConfigMissing, "로컬 KIS OpenAPI AppKey 또는 AppSecret이 설정되지 않아 접근이 제한됩니다.");
        }

        if (_approvalKeys.TryGetValue(_settings.AppKey, out var cachedApproval) && !string.IsNullOrWhiteSpace(cachedApproval))
        {
            return cachedApproval;
        }

        _logger.LogInformation("{Prefix} 실시간 WebSocket 접속키(Approval Key) 발급 요청 (POST /oauth2/Approval)", LogConstants.KisApiPrefix);

        var requestBody = new
        {
            grant_type = "client_credentials",
            appkey = _settings.AppKey,
            secretkey = _settings.AppSecret
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"{_settings.RestBaseUrl}/oauth2/Approval",
            requestBody,
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new StockLabException(ErrorCode.KisAuthFailed, $"KIS WebSocket 접속키 발급 실패 (상태코드: {response.StatusCode}, 사유: {errorBody})");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        if (!json.TryGetProperty("approval_key", out var keyElement) || string.IsNullOrWhiteSpace(keyElement.GetString()))
        {
            throw new StockLabException(ErrorCode.KisAuthFailed, "KIS WebSocket 접속키 응답 데이터가 올바르지 않습니다.");
        }

        var issuedApprovalKey = keyElement.GetString()!;
        _approvalKeys[_settings.AppKey] = issuedApprovalKey;
        _logger.LogInformation("{Prefix} WebSocket 접속키 발급 완료", LogConstants.KisApiPrefix);
        return issuedApprovalKey;
    }

    private void SetMemoryCache(string appKey, string token, DateTime expiresAtLocal)
    {
        var safeExpiry = expiresAtLocal - SafeBufferTime;
        if (safeExpiry > DateTime.Now)
        {
            var entry = new CachedTokenEntry(token, expiresAtLocal.ToUniversalTime());
            _cache.Set($"{CacheKeyPrefix}{appKey}", entry, safeExpiry);
        }
    }

    private async Task<(bool Success, string Token, DateTime ExpiresAt)> TryLoadTokenFromFileAsync(string keyHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(TokenFilePath)) return (false, string.Empty, DateTime.MinValue);

        try
        {
            var json = await File.ReadAllTextAsync(TokenFilePath, cancellationToken);
            var diskToken = JsonSerializer.Deserialize<DiskTokenInfo>(json);
            if (diskToken != null
                && string.Equals(diskToken.AppKeyHash, keyHash, StringComparison.Ordinal)
                && (diskToken.ExpiresAt - SafeBufferTime) > DateTime.Now)
            {
                return (true, diskToken.AccessToken, diskToken.ExpiresAt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[파일 캐시] 토큰 파일 역직렬화 실패");
        }

        return (false, string.Empty, DateTime.MinValue);
    }

    private async Task SaveTokenToFileAsync(string keyHash, string token, DateTime expiresAt, CancellationToken cancellationToken)
    {
        try
        {
            var data = new DiskTokenInfo(token, expiresAt, keyHash);
            var json = JsonSerializer.Serialize(data);
            await File.WriteAllTextAsync(TokenFilePath, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[파일 캐시] 토큰 파일 저장 실패");
        }
    }

    private record KisTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("access_token_token_expired")] string AccessTokenTokenExpired
    );

    private record CachedTokenEntry(string Token, DateTime ExpiresAtUtc);
    private record DiskTokenInfo(string AccessToken, DateTime ExpiresAt, string? AppKeyHash = null);
}
