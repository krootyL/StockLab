using System.Collections.Concurrent;
using System.Text.Json;
using StockLab.Common;
using StockLab.Dtos;

namespace StockLab.Services.Master;

/// <summary>
/// 로컬 마스터 파일(.mst/.cod)을 보유하지 않고, 클라이언트가 필요로 하는 종목/검색어/시장 데이터만
/// 중앙 Server API에서 선택적으로 조회하여 일정 시간(TTL) 로컬 메모리에 캐싱하는 프로바이더.
/// </summary>
public class RemoteStockMasterProvider : IStockMasterProvider
{
    private sealed record CachedItemEntry(StockMasterItem Item, DateTime ExpiresAtUtc);
    private sealed record CachedListEntry(IReadOnlyList<StockMasterItem> Items, DateTime ExpiresAtUtc);

    private static readonly TimeSpan ItemTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MarketTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<RemoteStockMasterProvider> _logger;

    private readonly ConcurrentDictionary<string, CachedItemEntry> _itemCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _negativeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedListEntry> _searchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedListEntry> _marketCache = new(StringComparer.OrdinalIgnoreCase);

    public RemoteStockMasterProvider(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<RemoteStockMasterProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var baseUrl = configuration["Server:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = configuration["Server:HttpBaseUrl"] ?? "http://localhost:5087";
        }

        _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    public StockMasterItem? TryGetCachedByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var normalized = code.Trim();
        if (_itemCache.TryGetValue(normalized, out var entry))
        {
            if (DateTime.UtcNow < entry.ExpiresAtUtc)
            {
                return entry.Item;
            }
        }

        return null;
    }

    public async ValueTask<StockMasterItem?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var normalized = code.Trim();
        var now = DateTime.UtcNow;

        if (_itemCache.TryGetValue(normalized, out var cached) && now < cached.ExpiresAtUtc)
        {
            return cached.Item;
        }

        if (_negativeCache.TryGetValue(normalized, out var negExpires) && now < negExpires)
        {
            return null;
        }

        try
        {
            using var response = await _httpClient.GetAsync($"api/stocks/{Uri.EscapeDataString(normalized)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 404)
                {
                    _negativeCache[normalized] = DateTime.UtcNow.Add(NegativeTtl);
                }

                return cached?.Item;
            }

            var apiResp = await response.Content.ReadFromJsonAsync<ApiResponse<StockMasterItem>>(JsonOptions, cancellationToken);
            if (apiResp is { Success: true, Data: not null })
            {
                CacheItem(apiResp.Data);
                return apiResp.Data;
            }

            _negativeCache[normalized] = DateTime.UtcNow.Add(NegativeTtl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[원격 마스터 조회 실패] 종목 '{Code}' 서버 조회 중 예외 발생({Message}) -> 로컬 캐시 폴백", normalized, ex.Message);
            if (cached != null)
            {
                return cached.Item;
            }
        }

        return null;
    }

    public async ValueTask<IReadOnlyDictionary<string, StockMasterItem>> GetByCodesAsync(
        IEnumerable<string> codes,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, StockMasterItem>(StringComparer.OrdinalIgnoreCase);
        var missingCodes = new List<string>();
        var now = DateTime.UtcNow;

        foreach (var raw in codes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var code = raw.Trim();
            if (result.ContainsKey(code)) continue;

            if (_itemCache.TryGetValue(code, out var cached) && now < cached.ExpiresAtUtc)
            {
                result[code] = cached.Item;
                continue;
            }

            if (_negativeCache.TryGetValue(code, out var negExpires) && now < negExpires)
            {
                continue;
            }

            missingCodes.Add(code);
        }

        if (missingCodes.Count == 0)
        {
            return result;
        }

        try
        {
            var joined = string.Join(',', missingCodes.Distinct(StringComparer.OrdinalIgnoreCase).Take(50));
            using var response = await _httpClient.GetAsync($"api/stocks/batch?codes={Uri.EscapeDataString(joined)}", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var apiResp = await response.Content.ReadFromJsonAsync<ApiResponse<List<StockMasterItem>>>(JsonOptions, cancellationToken);
                if (apiResp is { Success: true, Data: not null })
                {
                    var returnedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in apiResp.Data)
                    {
                        CacheItem(item);
                        result[item.ShortCode] = item;
                        returnedSet.Add(item.ShortCode);
                    }

                    var negExpiry = DateTime.UtcNow.Add(NegativeTtl);
                    foreach (var requested in missingCodes)
                    {
                        if (!returnedSet.Contains(requested))
                        {
                            _negativeCache[requested] = negExpiry;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[원격 마스터 일괄 조회 실패] {Count}건 서버 조회 중 예외 발생({Message})", missingCodes.Count, ex.Message);
            foreach (var code in missingCodes)
            {
                if (_itemCache.TryGetValue(code, out var stale))
                {
                    result[code] = stale.Item;
                }
            }
        }

        return result;
    }

    public async ValueTask<IReadOnlyList<StockMasterItem>> SearchByNameAsync(
        string query,
        string market = "ALL",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<StockMasterItem>();
        }

        var trimmedQuery = query.Trim();
        var normalizedMarket = string.IsNullOrWhiteSpace(market) ? "ALL" : market.Trim().ToUpperInvariant();
        var cacheKey = $"{normalizedMarket}|{trimmedQuery}";
        var now = DateTime.UtcNow;

        if (_searchCache.TryGetValue(cacheKey, out var cached) && now < cached.ExpiresAtUtc)
        {
            return cached.Items;
        }

        try
        {
            var url = $"api/stocks/search?name={Uri.EscapeDataString(trimmedQuery)}&market={Uri.EscapeDataString(normalizedMarket)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var apiResp = await response.Content.ReadFromJsonAsync<ApiResponse<List<StockMasterItem>>>(JsonOptions, cancellationToken);
                if (apiResp is { Success: true, Data: not null })
                {
                    foreach (var item in apiResp.Data)
                    {
                        CacheItem(item);
                    }

                    _searchCache[cacheKey] = new CachedListEntry(apiResp.Data, DateTime.UtcNow.Add(SearchTtl));
                    return apiResp.Data;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[원격 마스터 검색 실패] 검색어 '{Query}' 서버 조회 중 예외 발생({Message})", trimmedQuery, ex.Message);
            if (cached != null)
            {
                return cached.Items;
            }
        }

        return Array.Empty<StockMasterItem>();
    }

    public async ValueTask<IReadOnlyList<StockMasterItem>> GetByMarketAsync(
        string marketType,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(marketType))
        {
            return Array.Empty<StockMasterItem>();
        }

        var normalizedMarket = marketType.Trim().ToUpperInvariant();
        var cacheKey = $"{normalizedMarket}|{limit}";
        var now = DateTime.UtcNow;

        if (_marketCache.TryGetValue(cacheKey, out var cached) && now < cached.ExpiresAtUtc)
        {
            return cached.Items;
        }

        try
        {
            var url = $"api/stocks/market/{Uri.EscapeDataString(normalizedMarket)}?limit={limit}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var apiResp = await response.Content.ReadFromJsonAsync<ApiResponse<List<StockMasterItem>>>(JsonOptions, cancellationToken);
                if (apiResp is { Success: true, Data: not null })
                {
                    foreach (var item in apiResp.Data)
                    {
                        CacheItem(item);
                    }

                    _marketCache[cacheKey] = new CachedListEntry(apiResp.Data, DateTime.UtcNow.Add(MarketTtl));
                    return apiResp.Data;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[원격 마스터 시장별 조회 실패] 시장 '{Market}' 서버 조회 중 예외 발생({Message})", normalizedMarket, ex.Message);
            if (cached != null)
            {
                return cached.Items;
            }
        }

        return Array.Empty<StockMasterItem>();
    }

    public void UpdatePrice(string code, decimal newPrice)
    {
        if (string.IsNullOrWhiteSpace(code) || newPrice <= 0) return;

        var trimmed = code.Trim();
        if (_itemCache.TryGetValue(trimmed, out var existing))
        {
            _itemCache[trimmed] = existing with
            {
                Item = existing.Item with { BasePrice = newPrice }
            };
        }
    }

    private void CacheItem(StockMasterItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ShortCode)) return;
        _itemCache[item.ShortCode] = new CachedItemEntry(item, DateTime.UtcNow.Add(ItemTtl));
        _negativeCache.TryRemove(item.ShortCode, out _);
    }
}

