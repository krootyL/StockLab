using System.Globalization;
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

    private const string CacheKey = "KIS_ACCESS_TOKEN";
    private static string _approvalKey = string.Empty;
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

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // 1. 메모리 캐시 확인
        if (_cache.TryGetValue(CacheKey, out string? cachedToken) && !string.IsNullOrWhiteSpace(cachedToken))
        {
            return cachedToken;
        }

        // 2. 파일 캐시 확인 (서버 재실행 시 복원)
        var (loaded, fileToken, fileExpiresAt) = await TryLoadTokenFromFileAsync(cancellationToken);
        if (loaded)
        {
            SetMemoryCache(fileToken, fileExpiresAt);
            _logger.LogInformation("[파일 캐시] 저장된 .kis_token.json 파일에서 토큰 복원 완료 (만료 예정: {ExpiresAt:yyyy-MM-dd HH:mm:ss})", fileExpiresAt);
            return fileToken;
        }

        // 설정값 사전 검증
        if (string.IsNullOrWhiteSpace(_settings.AppKey) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            throw new StockLabException(ErrorCode.KisConfigMissing, "KIS OpenAPI AppKey 또는 AppSecret 환경변수 설정이 누락되었습니다.");
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

        SetMemoryCache(tokenResponse.AccessToken, expiresAt);
        await SaveTokenToFileAsync(tokenResponse.AccessToken, expiresAt, cancellationToken);

        _logger.LogInformation("{Prefix} 신규 접근 토큰 확보 완료 (만료 예정: {ExpiresAt:yyyy-MM-dd HH:mm:ss})", LogConstants.KisApiPrefix, expiresAt);

        return tokenResponse.AccessToken;
    }

    public async Task<string> GetWebSocketApprovalKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_approvalKey))
        {
            return _approvalKey;
        }

        if (string.IsNullOrWhiteSpace(_settings.AppKey) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            throw new StockLabException(ErrorCode.KisConfigMissing, "KIS OpenAPI AppKey 또는 AppSecret 환경변수 설정이 누락되었습니다.");
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

        _approvalKey = keyElement.GetString()!;
        _logger.LogInformation("{Prefix} WebSocket 접속키 발급 완료", LogConstants.KisApiPrefix);
        return _approvalKey;
    }

    private void SetMemoryCache(string token, DateTime expiresAt)
    {
        var safeExpiry = expiresAt - SafeBufferTime;
        if (safeExpiry > DateTime.Now)
        {
            _cache.Set(CacheKey, token, safeExpiry);
        }
    }

    private async Task<(bool Success, string Token, DateTime ExpiresAt)> TryLoadTokenFromFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(TokenFilePath)) return (false, string.Empty, DateTime.MinValue);

        try
        {
            var json = await File.ReadAllTextAsync(TokenFilePath, cancellationToken);
            var diskToken = JsonSerializer.Deserialize<DiskTokenInfo>(json);
            if (diskToken != null && (diskToken.ExpiresAt - SafeBufferTime) > DateTime.Now)
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

    private async Task SaveTokenToFileAsync(string token, DateTime expiresAt, CancellationToken cancellationToken)
    {
        try
        {
            var data = new DiskTokenInfo(token, expiresAt);
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

    private record DiskTokenInfo(string AccessToken, DateTime ExpiresAt);
}
