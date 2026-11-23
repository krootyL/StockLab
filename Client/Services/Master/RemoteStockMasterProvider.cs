using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using StockLab.Common;
using StockLab.Dtos;

namespace StockLab.Services.Master;

/// <summary>
/// 로컬 마스터 파일(.mst/.cod)을 보유하지 않고, 클라이언트가 필요로 하는 종목/검색어/시장 데이터만
/// 중앙 Server API에서 선택적으로 조회하여 일정 시간(TTL) 로컬 메모리에 캐싱하는 프로바이더.
/// 스냅샷은 ETag(If-None-Match)로 Server 변동 시에만 전체 수신하고, 종목별 UpdatedAtUtc 비교로 최신 가격을 유지한다.
/// </summary>
public class RemoteStockMasterProvider : IStockMasterProvider
{
    private sealed record CachedItemEntry(StockMasterItem Item, DateTime ExpiresAtUtc);
    private sealed record CachedListEntry(IReadOnlyList<StockMasterItem> Items, DateTime ExpiresAtUtc);
    private sealed record CachedBrowseEntry(PagedResult<StockMasterItem> Result, DateTime ExpiresAtUtc);
    private sealed record LocalPriceOverride(decimal Price, DateTime UpdatedAtUtc);
    private sealed record CachedSnapshotState(
        IReadOnlyList<StockMasterItem> Items,
        ConcurrentDictionary<string, StockMasterItem> ByCode,
        string ServerETag,
        long MasterVersion,
        DateTime MasterSyncedAtUtc);

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
    private readonly ConcurrentDictionary<string, CachedBrowseEntry> _browseCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LocalPriceOverride> _localPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private CachedSnapshotState? _snapshotState;
    private long _localPriceRevision;

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
                return CacheItem(apiResp.Data);
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
                        var merged = CacheItem(item);
                        result[merged.ShortCode] = merged;
                        returnedSet.Add(merged.ShortCode);
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
                    var mergedList = apiResp.Data.Select(CacheItem).ToList();

                    _searchCache[cacheKey] = new CachedListEntry(mergedList, DateTime.UtcNow.Add(SearchTtl));
                    return mergedList;
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
                    var mergedList = apiResp.Data.Select(CacheItem).ToList();

                    _marketCache[cacheKey] = new CachedListEntry(mergedList, DateTime.UtcNow.Add(MarketTtl));
                    return mergedList;
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

    public async ValueTask<PagedResult<StockMasterItem>> BrowseByMarketAsync(
        string market,
        int page,
        int pageSize,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedMarket = string.IsNullOrWhiteSpace(market) ? "ALL" : market.Trim().ToUpperInvariant();
        var trimmedQuery = query?.Trim() ?? string.Empty;
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 50);
        var cacheKey = $"{normalizedMarket}|{trimmedQuery}|{safePage}|{safePageSize}";
        var now = DateTime.UtcNow;

        if (_browseCache.TryGetValue(cacheKey, out var cached) && now < cached.ExpiresAtUtc)
        {
            return cached.Result;
        }

        try
        {
            var url = $"api/stocks/browse?market={Uri.EscapeDataString(normalizedMarket)}&page={safePage}&pageSize={safePageSize}";
            if (!string.IsNullOrEmpty(trimmedQuery))
            {
                url += $"&query={Uri.EscapeDataString(trimmedQuery)}";
            }
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var apiResp = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<StockMasterItem>>>(JsonOptions, cancellationToken);
                if (apiResp is { Success: true, Data: not null })
                {
                    var mergedItems = apiResp.Data.Items.Select(CacheItem).ToList();
                    var mergedPage = apiResp.Data with { Items = mergedItems };

                    _browseCache[cacheKey] = new CachedBrowseEntry(mergedPage, DateTime.UtcNow.Add(MarketTtl));
                    return mergedPage;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[원격 마스터 탐색 실패] 시장 '{Market}' p.{Page} 서버 조회 중 예외 발생({Message})", normalizedMarket, safePage, ex.Message);
            if (cached != null)
            {
                return cached.Result;
            }
        }

        return new PagedResult<StockMasterItem>(Array.Empty<StockMasterItem>(), safePage, safePageSize, 0, 1);
    }

    public async ValueTask<IReadOnlyList<StockMasterItem>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetSnapshotStateAsync(null, cancellationToken);
        return state.Items;
    }

    public async ValueTask<StockMasterSnapshotResult> GetSnapshotStateAsync(
        string? ifNoneMatchETag = null,
        CancellationToken cancellationToken = default)
    {
        await _snapshotLock.WaitAsync(cancellationToken);
        var current = Volatile.Read(ref _snapshotState);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "api/stocks/snapshot");
            if (current != null && !string.IsNullOrWhiteSpace(current.ServerETag))
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", current.ServerETag);
            }

            using var response = await _httpClient.SendAsync(req, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && current != null)
            {
                var mergedCurrent = RefreshLocalPricesInSnapshot(current);
                Volatile.Write(ref _snapshotState, mergedCurrent);
                var clientETag = FormatClientETag(mergedCurrent.ServerETag, Interlocked.Read(ref _localPriceRevision));
                if (!string.IsNullOrWhiteSpace(ifNoneMatchETag) &&
                    string.Equals(ifNoneMatchETag.Trim(), clientETag, StringComparison.Ordinal))
                {
                    return new StockMasterSnapshotResult(
                        Array.Empty<StockMasterItem>(),
                        clientETag,
                        mergedCurrent.MasterVersion,
                        mergedCurrent.MasterSyncedAtUtc,
                        NotModified: true);
                }

                return new StockMasterSnapshotResult(
                    mergedCurrent.Items,
                    clientETag,
                    mergedCurrent.MasterVersion,
                    mergedCurrent.MasterSyncedAtUtc,
                    NotModified: false);
            }

            if (response.IsSuccessStatusCode)
            {
                var apiResp = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<StockMasterItem>>>(JsonOptions, cancellationToken);
                if (apiResp is { Success: true, Data: not null })
                {
                    var serverETag = response.Headers.ETag?.Tag
                        ?? (response.Headers.TryGetValues("ETag", out var etags) ? etags.FirstOrDefault() : null)
                        ?? $"\"master-{apiResp.Data.Count}-{DateTime.UtcNow.Ticks}\"";

                    long masterVersion = (current?.MasterVersion ?? 0) + 1;
                    if (response.Headers.TryGetValues("X-Master-Version", out var mvVals) &&
                        long.TryParse(mvVals.FirstOrDefault(), out var parsedMv))
                    {
                        masterVersion = parsedMv;
                    }

                    DateTime masterSyncedAtUtc = DateTime.UtcNow;
                    if (response.Headers.TryGetValues("X-Master-Synced-At", out var msVals) &&
                        DateTime.TryParse(msVals.FirstOrDefault(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedMs))
                    {
                        masterSyncedAtUtc = parsedMs.ToUniversalTime();
                    }

                    // Server 마스터 스냅샷 목록 기준으로만 구성하므로 상장폐지/제외 종목은 자동 제거됨
                    var newByCode = new ConcurrentDictionary<string, StockMasterItem>(StringComparer.OrdinalIgnoreCase);
                    var mergedList = new List<StockMasterItem>(apiResp.Data.Count);
                    foreach (var item in apiResp.Data)
                    {
                        if (string.IsNullOrWhiteSpace(item.ShortCode)) continue;
                        var merged = CacheItem(item);
                        newByCode[merged.ShortCode] = merged;
                        mergedList.Add(merged);
                    }

                    // 마스터 버전이 변경된 경우 삭제(상장폐지)된 종목의 로컬 잔여 캐시도 정리
                    if (current != null && masterVersion != current.MasterVersion)
                    {
                        PruneRemovedCodes(newByCode);
                    }

                    var nextState = new CachedSnapshotState(mergedList, newByCode, serverETag, masterVersion, masterSyncedAtUtc);
                    Volatile.Write(ref _snapshotState, nextState);

                    var clientETag = FormatClientETag(serverETag, Interlocked.Read(ref _localPriceRevision));
                    if (!string.IsNullOrWhiteSpace(ifNoneMatchETag) &&
                        string.Equals(ifNoneMatchETag.Trim(), clientETag, StringComparison.Ordinal))
                    {
                        return new StockMasterSnapshotResult(
                            Array.Empty<StockMasterItem>(),
                            clientETag,
                            masterVersion,
                            masterSyncedAtUtc,
                            NotModified: true);
                    }

                    return new StockMasterSnapshotResult(
                        mergedList,
                        clientETag,
                        masterVersion,
                        masterSyncedAtUtc,
                        NotModified: false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[원격 마스터 스냅샷 실패] 서버 조회 중 예외 발생({Message})", ex.Message);
            if (current != null)
            {
                var clientETag = FormatClientETag(current.ServerETag, Interlocked.Read(ref _localPriceRevision));
                if (!string.IsNullOrWhiteSpace(ifNoneMatchETag) &&
                    string.Equals(ifNoneMatchETag.Trim(), clientETag, StringComparison.Ordinal))
                {
                    return new StockMasterSnapshotResult(
                        Array.Empty<StockMasterItem>(),
                        clientETag,
                        current.MasterVersion,
                        current.MasterSyncedAtUtc,
                        NotModified: true);
                }

                return new StockMasterSnapshotResult(
                    current.Items,
                    clientETag,
                    current.MasterVersion,
                    current.MasterSyncedAtUtc,
                    NotModified: false);
            }
        }
        finally
        {
            _snapshotLock.Release();
        }

        return new StockMasterSnapshotResult(Array.Empty<StockMasterItem>(), string.Empty, 0, DateTime.UtcNow, NotModified: false);
    }

    public void UpdatePrice(string code, decimal newPrice)
    {
        if (string.IsNullOrWhiteSpace(code) || newPrice <= 0) return;

        var trimmed = code.Trim();
        var now = DateTime.UtcNow;

        _localPrices.AddOrUpdate(
            trimmed,
            new LocalPriceOverride(newPrice, now),
            (_, prev) => now >= prev.UpdatedAtUtc ? new LocalPriceOverride(newPrice, now) : prev);

        if (_itemCache.TryGetValue(trimmed, out var existing))
        {
            if (!existing.Item.UpdatedAtUtc.HasValue || now >= existing.Item.UpdatedAtUtc.Value)
            {
                _itemCache[trimmed] = existing with
                {
                    Item = existing.Item with { BasePrice = newPrice, UpdatedAtUtc = now }
                };
            }
        }

        var snap = Volatile.Read(ref _snapshotState);
        if (snap != null && snap.ByCode.TryGetValue(trimmed, out var snapItem))
        {
            if (!snapItem.UpdatedAtUtc.HasValue || now >= snapItem.UpdatedAtUtc.Value)
            {
                snap.ByCode[trimmed] = snapItem with { BasePrice = newPrice, UpdatedAtUtc = now };
                Interlocked.Increment(ref _localPriceRevision);
            }
        }
    }

    private static string FormatClientETag(string serverETag, long localRevision)
    {
        var bare = (serverETag ?? string.Empty).Trim().Trim('"');
        return $"\"{bare}-c{localRevision}\"";
    }

    private CachedSnapshotState RefreshLocalPricesInSnapshot(CachedSnapshotState current)
    {
        bool changed = false;
        foreach (var kv in _localPrices)
        {
            if (current.ByCode.TryGetValue(kv.Key, out var item))
            {
                if (!item.UpdatedAtUtc.HasValue || kv.Value.UpdatedAtUtc > item.UpdatedAtUtc.Value)
                {
                    current.ByCode[kv.Key] = item with
                    {
                        BasePrice = kv.Value.Price,
                        UpdatedAtUtc = kv.Value.UpdatedAtUtc
                    };
                    changed = true;
                }
            }
        }

        return changed
            ? current with { Items = current.ByCode.Values.ToList() }
            : current;
    }

    private void PruneRemovedCodes(ConcurrentDictionary<string, StockMasterItem> validByCode)
    {
        foreach (var key in _localPrices.Keys)
        {
            if (!validByCode.ContainsKey(key))
            {
                _localPrices.TryRemove(key, out _);
            }
        }
        foreach (var key in _itemCache.Keys)
        {
            if (!validByCode.ContainsKey(key))
            {
                _itemCache.TryRemove(key, out _);
            }
        }
        foreach (var key in _negativeCache.Keys)
        {
            if (!validByCode.ContainsKey(key))
            {
                _negativeCache.TryRemove(key, out _);
            }
        }
    }

    private StockMasterItem CacheItem(StockMasterItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ShortCode)) return item;

        var merged = item;
        var serverUpdated = item.UpdatedAtUtc;

        // 클라이언트 로컬 가격(또는 직전 캐시) 갱신 시각이 서버보다 더 최신이면 구조 메타데이터만 서버 기준으로 갱신하고 가격은 최신값 유지
        DateTime? bestLocalTime = null;
        decimal bestLocalPrice = 0;

        if (_localPrices.TryGetValue(item.ShortCode, out var localOverride))
        {
            bestLocalTime = localOverride.UpdatedAtUtc;
            bestLocalPrice = localOverride.Price;
        }

        if (_itemCache.TryGetValue(item.ShortCode, out var existing) && existing.Item.UpdatedAtUtc.HasValue)
        {
            if (!bestLocalTime.HasValue || existing.Item.UpdatedAtUtc.Value > bestLocalTime.Value)
            {
                bestLocalTime = existing.Item.UpdatedAtUtc.Value;
                bestLocalPrice = existing.Item.BasePrice;
            }
        }

        if (bestLocalTime.HasValue && bestLocalPrice > 0 && (!serverUpdated.HasValue || bestLocalTime.Value > serverUpdated.Value))
        {
            merged = item with { BasePrice = bestLocalPrice, UpdatedAtUtc = bestLocalTime.Value };
        }
        else if (!merged.UpdatedAtUtc.HasValue)
        {
            merged = merged with { UpdatedAtUtc = DateTime.UtcNow };
        }

        _itemCache[merged.ShortCode] = new CachedItemEntry(merged, DateTime.UtcNow.Add(ItemTtl));
        _negativeCache.TryRemove(merged.ShortCode, out _);
        return merged;
    }
}

