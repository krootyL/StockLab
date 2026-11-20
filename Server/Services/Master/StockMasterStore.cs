using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using StockLab.Common;
using StockLab.Dtos;

namespace StockLab.Services.Master;

public sealed record PreSerializedSnapshot(byte[] JsonBytes, byte[] GzipBytes, string ETag, DateTime ExpiresAtUtc);

public class StockMasterStore : IStockMasterProvider
{
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    // 스레드 안전한 무중단(Zero-Downtime) 원자적 스왑 및 실시간 가격 갱신을 위해 ConcurrentDictionary로 관리
    private ConcurrentDictionary<string, StockMasterItem> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _snapshotLock = new();
    private PreSerializedSnapshot? _cachedSnapshot;
    private int _snapshotDirty = 1;
    private long _snapshotVersion;

    public StockMasterItem? TryGetCachedByCode(string code) => GetByCode(code);

    public ValueTask<StockMasterItem?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(GetByCode(code));

    public ValueTask<IReadOnlyDictionary<string, StockMasterItem>> GetByCodesAsync(
        IEnumerable<string> codes,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(GetByCodes(codes));

    public ValueTask<IReadOnlyList<StockMasterItem>> SearchByNameAsync(
        string query,
        string market = "ALL",
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SearchByName(query, market));

    public ValueTask<IReadOnlyList<StockMasterItem>> GetByMarketAsync(
        string marketType,
        int limit = 20,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(GetByMarket(marketType, limit));

    public ValueTask<PagedResult<StockMasterItem>> BrowseByMarketAsync(
        string market,
        int page,
        int pageSize,
        string? query = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(BrowseByMarket(market, page, pageSize, query));

    public ValueTask<IReadOnlyList<StockMasterItem>> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StockMasterItem>>(_byCode.Values.ToList());

    /// <summary>
    /// 고정 주기(30초 TTL) + 변경 감지(_snapshotDirty) 기반으로 사전 직렬화된 JSON/Gzip 스냅샷을 반환한다.
    /// TTL 내 동시 요청은 추가 직렬화·힙 할당 없이 동일 바이트 배열을 공유한다.
    /// </summary>
    public PreSerializedSnapshot GetSerializedSnapshot()
    {
        var current = Volatile.Read(ref _cachedSnapshot);
        var now = DateTime.UtcNow;
        if (current != null && (Volatile.Read(ref _snapshotDirty) == 0 || now < current.ExpiresAtUtc))
        {
            return current;
        }

        // 기존 캐시가 이미 있으면 1개 스레드만 백그라운드로 재포장하고, 나머지 요청은 대기 없이 기존 캐시 즉시 반환
        bool lockTaken = false;
        try
        {
            if (current != null)
            {
                Monitor.TryEnter(_snapshotLock, ref lockTaken);
                if (!lockTaken)
                {
                    return current;
                }
            }
            else
            {
                Monitor.Enter(_snapshotLock, ref lockTaken);
            }

            current = _cachedSnapshot;
            now = DateTime.UtcNow;
            if (current != null && (Volatile.Read(ref _snapshotDirty) == 0 || now < current.ExpiresAtUtc))
            {
                return current;
            }

            Volatile.Write(ref _snapshotDirty, 0);
            var items = _byCode.Values.ToList();
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
                ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(items),
                SnapshotJsonOptions);

            byte[] gzipBytes;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    gz.Write(jsonBytes, 0, jsonBytes.Length);
                }
                gzipBytes = ms.ToArray();
            }

            var version = Interlocked.Increment(ref _snapshotVersion);
            var etag = $"\"master-{items.Count}-{version}\"";
            var rebuilt = new PreSerializedSnapshot(jsonBytes, gzipBytes, etag, now.Add(SnapshotTtl));
            Volatile.Write(ref _cachedSnapshot, rebuilt);
            return rebuilt;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_snapshotLock);
            }
        }
    }

    public PagedResult<StockMasterItem> BrowseByMarket(string market, int page, int pageSize, string? query = null)
    {
        var safePageSize = Math.Clamp(pageSize, 1, 50);
        var markets = ParseMarketFilter(market);
        var trimmedQuery = query?.Trim() ?? string.Empty;

        var filtered = _byCode.Values
            .Where(s => (markets.Count == 0 || markets.Contains(s.MarketType) || markets.Contains(s.ExchangeCode)) &&
                        (string.IsNullOrEmpty(trimmedQuery) ||
                         s.KoreanName.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                         s.ShortCode.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.BasePrice > 0)
            .ThenBy(s => s.ShortCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalCount = filtered.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)safePageSize));
        var safePage = Math.Clamp(page, 1, totalPages);
        var items = filtered
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToList();

        return new PagedResult<StockMasterItem>(items, safePage, safePageSize, totalCount, totalPages);
    }

    public IReadOnlyDictionary<string, StockMasterItem> GetByCodes(IEnumerable<string> codes)
    {
        var result = new Dictionary<string, StockMasterItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in codes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            if (_byCode.TryGetValue(trimmed, out var item))
            {
                result[item.ShortCode] = item;
            }
        }

        return result;
    }

    public void Initialize(IEnumerable<StockMasterItem> items)
    {
        var newDict = new ConcurrentDictionary<string, StockMasterItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            newDict[item.ShortCode] = item;
        }

        // 메모리 참조를 원자적으로 교체하고 기존 스냅샷 캐시는 유지한 채 갱신 플래그만 표시
        Interlocked.Exchange(ref _byCode, newDict);
        Volatile.Write(ref _snapshotDirty, 1);
    }

    // 실시간 호가/시세 수신 시 디스크 마스터 파일은 유지하고 인메모리 DB 가격만 덮어쓰기
    public void UpdatePrice(string code, decimal newPrice)
    {
        if (string.IsNullOrWhiteSpace(code) || newPrice <= 0) return;

        var trimmed = code.Trim();
        if (_byCode.TryGetValue(trimmed, out var existing))
        {
            _byCode[trimmed] = existing with { BasePrice = newPrice };
            Volatile.Write(ref _snapshotDirty, 1);
        }
    }

    // 종목코드로 즉시 단건 조회 (O(1))
    public StockMasterItem? GetByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        _byCode.TryGetValue(code.Trim(), out var item);
        return item;
    }

    // 종목명 포함 검색 (최대 20건 제한, 다중 시장 필터 지원)
    public IReadOnlyList<StockMasterItem> SearchByName(string query, string market = "ALL")
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<StockMasterItem>();

        var trimmedQuery = query.Trim();
        var markets = ParseMarketFilter(market);

        return _byCode.Values
            .Where(s => (markets.Count == 0 || markets.Contains(s.MarketType) || markets.Contains(s.ExchangeCode)) &&
                        (s.KoreanName.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                         s.ShortCode.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)))
            .Take(200)
            .ToList();
    }

    // 특정 시장 종목 기본 목록 조회 (코넥스 등 거래량 순위 미지원 시장용)
    public IReadOnlyList<StockMasterItem> GetByMarket(string marketType, int limit = 20)
    {
        return _byCode.Values
            .Where(s => s.MarketType.Equals(marketType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.BasePrice > 0)
            .Take(limit)
            .ToList();
    }

    // 지정된 제외 목록을 빼고 기준가가 있는 종목 중 무작위 N건 추출
    public IReadOnlyList<StockMasterItem> GetRandomStocks(int count, IReadOnlySet<string>? excludeCodes = null)
    {
        if (count <= 0) return Array.Empty<StockMasterItem>();

        var candidates = _byCode.Values
            .Where(s => s.BasePrice > 0 && (excludeCodes == null || !excludeCodes.Contains(s.ShortCode)))
            .ToList();

        if (candidates.Count <= count)
        {
            return candidates;
        }

        // Fisher-Yates 부분 셔플로 무작위 count건 추출
        for (var i = 0; i < count; i++)
        {
            var swapIdx = Random.Shared.Next(i, candidates.Count);
            (candidates[i], candidates[swapIdx]) = (candidates[swapIdx], candidates[i]);
        }

        return candidates.Take(count).ToList();
    }

    private static HashSet<string> ParseMarketFilter(string market)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(market)) return set;

        foreach (var part in market.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                set.Clear();
                return set;
            }
            if (part.Equals("US", StringComparison.OrdinalIgnoreCase))
            {
                set.Add("USA");
            }
            else if (part.Equals("KR", StringComparison.OrdinalIgnoreCase))
            {
                set.Add("KOSPI");
                set.Add("KOSDAQ");
                set.Add("KONEX");
                set.Add("NXT_KOSPI");
            }
            else
            {
                set.Add(part);
            }
        }
        return set;
    }

    public int TotalCount => _byCode.Count;
}