using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Master;
using StockLab.Services.Trading;
using StockLab.Settings;

namespace StockLab.Services.Kis;

public class KisRankingService
{
    private sealed record RankingCacheEntry(IReadOnlyList<PopularStockItem> Items, DateTime NextRefreshUtc);

    // 다음 새 값을 받아올 때까지 기존 캐시를 삭제하지 않고 유지한 뒤 원자적으로 교체(Retain-Until-Replaced)
    private static readonly ConcurrentDictionary<string, RankingCacheEntry> RankingStore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> BackgroundRefreshing = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly KisSessionService _tokenService;
    private readonly IStockMasterProvider _store;
    private readonly IMemoryCache _cache;
    private readonly KisSettings _settings;
    private readonly ILogger<KisRankingService> _logger;

    private const string CacheKeyKr = "KIS_VOLUME_RANK_KR";
    private const string CacheKeyKospi = "KIS_VOLUME_RANK_KOSPI";
    private const string CacheKeyKosdaq = "KIS_VOLUME_RANK_KOSDAQ";
    private const string CacheKeyUs = "KIS_VOLUME_RANK_US";

    private static readonly TimeSpan MarketOpenCacheDuration = TimeSpan.FromMinutes(1); // 장중 갱신 주기: 1분
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(30);      // 실패 시 재시도 간격: 30초
    private static readonly TimeSpan KisRequestTimeout = TimeSpan.FromSeconds(15);      // KIS 단건 호출 최대 대기시간

    public KisRankingService(
        HttpClient httpClient,
        KisSessionService tokenService,
        IStockMasterProvider store,
        IMemoryCache cache,
        IOptions<KisSettings> options,
        ILogger<KisRankingService> logger)
    {
        _httpClient = httpClient;
        _tokenService = tokenService;
        _store = store;
        _cache = cache;
        _settings = options.Value;
        _logger = logger;
    }

    public static bool IsDomesticMarketOpen(DateTime utcNow)
        => MarketSessionHelper.IsMarketOpen("KOSPI", utcNow);

    public static bool IsUsMarketOpen(DateTime utcNow)
        => MarketSessionHelper.IsMarketOpen("USA", utcNow);

    private static TimeSpan GetDurationUntilNextKrMarketOpen(DateTime utcNow)
        => MarketSessionHelper.GetDurationUntilNextDomesticOpen(utcNow);

    private static TimeSpan GetDurationUntilNextUsMarketOpen(DateTime utcNow)
        => MarketSessionHelper.GetDurationUntilNextUsOpen(utcNow);

    private const decimal UsdToKrwApproxRate = 1400m; // 통합(ALL) 거래대금 비교용 기준 환산율

    private static decimal GetSortScore(PopularStockItem item, bool isValueSort)
    {
        if (!isValueSort) return item.Volume;
        return item.Currency == "USD" ? item.TradingValue * UsdToKrwApproxRate : item.TradingValue;
    }

    /// <summary>
    /// 시장별(ALL, KR, KOSPI, KOSDAQ, US/USA, KONEX 및 콤마 다중 선택) 실시간 거래대금/거래량 상위 종목 조회
    /// </summary>
    public async Task<IReadOnlyList<PopularStockItem>> GetPopularStocksAsync(
        string market = "ALL",
        string sort = "value",
        CancellationToken cancellationToken = default)
    {
        var isValueSort = !string.Equals(sort, "volume", StringComparison.OrdinalIgnoreCase);

        var tokens = market
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToUpperInvariant())
            .Distinct()
            .ToList();

        if (tokens.Count == 0 || tokens.Contains("ALL"))
        {
            return await GetAllVolumeRankTop20Async(isValueSort, cancellationToken);
        }

        if (tokens.Count == 1)
        {
            return await FetchSingleMarketAsync(tokens[0], isValueSort, cancellationToken);
        }

        var tasks = tokens.Select(m => FetchSingleMarketAsync(m, isValueSort, cancellationToken));
        var results = await Task.WhenAll(tasks);

        return results
            .SelectMany(list => list)
            .GroupBy(item => item.StockCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => GetSortScore(x, isValueSort))
            .Take(20)
            .Select((item, idx) => item with { Rank = idx + 1 })
            .ToList();
    }

    private Task<IReadOnlyList<PopularStockItem>> FetchSingleMarketAsync(string market, bool isValueSort, CancellationToken cancellationToken)
    {
        return market switch
        {
            "KR" => GetKrVolumeRankTop20Async("0000", CacheKeyKr, isValueSort, cancellationToken),
            "KOSPI" => GetKrVolumeRankTop20Async("0001", CacheKeyKospi, isValueSort, cancellationToken),
            "KOSDAQ" => GetKrVolumeRankTop20Async("1001", CacheKeyKosdaq, isValueSort, cancellationToken),
            "US" or "USA" => GetUsVolumeRankTop20Async(isValueSort, cancellationToken),
            "KONEX" => GetKonexPopularStocksAsync(cancellationToken),
            _ => GetAllVolumeRankTop20Async(isValueSort, cancellationToken)
        };
    }

    /// <summary>
    /// 국내 주식 실시간 거래대금/거래량 순위 (Top 20, 시장코드: 0000=전체, 0001=코스피, 1001=코스닥)
    /// </summary>
    public Task<IReadOnlyList<PopularStockItem>> GetKrVolumeRankTop20Async(
        string marketCode = "0000",
        string baseCacheKey = CacheKeyKr,
        bool isValueSort = true,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{baseCacheKey}_{(isValueSort ? "VALUE" : "VOLUME")}";
        return GetOrRefreshRankingAsync(
            cacheKey,
            ct => FetchKrFromKisAsync(marketCode, isValueSort, ct),
            () => GetFallbackKrPopularStocksAsync(cancellationToken));
    }

    private async Task<RankingCacheEntry?> FetchKrFromKisAsync(
        string marketCode,
        bool isValueSort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.AppKey) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            return null;
        }

        try
        {
            var isMarketOpen = IsDomesticMarketOpen(DateTime.UtcNow);
            var token = await _tokenService.GetAccessTokenAsync(ct);

            var queryParams = new Dictionary<string, string>
            {
                { "FID_COND_MRKT_DIV_CODE", "J" },
                { "FID_COND_SCR_DIV_CODE", "20171" },
                { "FID_INPUT_ISCD", marketCode },
                { "FID_DIV_CLS_CODE", "0" },
                { "FID_BLNG_CLS_CODE", isValueSort ? "3" : "0" },
                { "FID_TRGT_CLS_CODE", "111111111" },
                { "FID_TRGT_EXLS_CLS_CODE", "000000" },
                { "FID_INPUT_PRICE_1", "" },
                { "FID_INPUT_PRICE_2", "" },
                { "FID_VOL_CNT", "" },
                { "FID_INPUT_DATE_1", "" }
            };

            var queryString = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            var requestUrl = $"{_settings.RestBaseUrl}/uapi/domestic-stock/v1/quotations/volume-rank?{queryString}";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("appkey", _settings.AppKey);
            request.Headers.Add("appsecret", _settings.AppSecret);
            request.Headers.Add("tr_id", "FHPST01710000");
            request.Headers.Add("custtype", "P");

            _logger.LogInformation("{Prefix} 국내 거래량 순위 요청 전송 (TR_ID: FHPST01710000)", LogConstants.KisApiPrefix);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("{Prefix} 국내 거래량 순위 응답 실패 (코드: {StatusCode}, 사유: {Error}) -> 기존 캐시 유지",
                    LogConstants.KisApiPrefix, response.StatusCode, errorText);
                return null;
            }

            var jsonStream = await response.Content.ReadAsStreamAsync(ct);
            var rankResult = await JsonSerializer.DeserializeAsync<KisVolumeRankResponse>(jsonStream, cancellationToken: ct);

            if (rankResult?.Output == null || rankResult.Output.Count == 0)
            {
                return null;
            }

            var topItems = rankResult.Output.Take(20).ToList();
            var masterMap = await _store.GetByCodesAsync(topItems.Select(x => x.StockCode), ct);
            var list = new List<PopularStockItem>(topItems.Count);
            int rank = 1;

            foreach (var item in topItems)
            {
                decimal.TryParse(item.CurrentPrice, out var price);
                decimal.TryParse(item.PriceChange, out var priceChange);
                double.TryParse(item.ChangeRate, out var changeRate);
                long.TryParse(item.Volume, out var volume);
                decimal.TryParse(item.TradingValue, out var tradingValue);

                if ((item.Sign == "4" || item.Sign == "5" || changeRate < 0) && priceChange > 0)
                {
                    priceChange = -priceChange;
                }
                else if (priceChange < 0 && changeRate > 0)
                {
                    changeRate = -changeRate;
                }

                if (price > 0)
                {
                    _store.UpdatePrice(item.StockCode, price);
                }

                masterMap.TryGetValue(item.StockCode, out var master);
                var marketType = master?.MarketType ?? "KOSPI";
                var exchangeCode = master?.ExchangeCode ?? "KRX";

                var popularItem = new PopularStockItem(
                    Rank: rank++,
                    StockCode: item.StockCode,
                    StockName: item.StockName,
                    Price: price,
                    PriceChange: priceChange,
                    ChangeRate: changeRate,
                    Volume: volume,
                    MarketType: marketType,
                    ExchangeCode: exchangeCode,
                    Currency: "KRW",
                    TradingValue: tradingValue
                );

                KisQuoteService.SeedQuoteFromRanking(popularItem, master?.BasePrice ?? 0m);
                list.Add(popularItem);
            }

            DateTime nextRefreshUtc;
            if (isMarketOpen)
            {
                nextRefreshUtc = DateTime.UtcNow.Add(MarketOpenCacheDuration);
                _logger.LogInformation("{Prefix} [국내 장중] 실시간 거래량 순위 갱신 완료 ({MarketCode}, Top {Count}개, 갱신주기: 60초)",
                    LogConstants.KisApiPrefix, marketCode, list.Count);
            }
            else
            {
                var freezeDuration = GetDurationUntilNextKrMarketOpen(DateTime.UtcNow);
                nextRefreshUtc = DateTime.UtcNow.Add(freezeDuration);
                _logger.LogInformation("{Prefix} [국내 장 마감] 직전 마감 거래량 순위 캐싱 완료 ({MarketCode}). 다음 장 시작(평일 08:30 KST)까지 외부 호출 비활성화 (동결: {Hours:F1}시간)",
                    LogConstants.KisApiPrefix, marketCode, freezeDuration.TotalHours);
            }

            return new RankingCacheEntry(list, nextRefreshUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Prefix} 국내 거래량 순위 조회 예외({Message}) -> 기존 캐시/Fallback 유지",
                LogConstants.KisApiPrefix, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 해외(미국) 주식 실시간 거래대금/거래량 순위 (Top 20)
    /// </summary>
    public Task<IReadOnlyList<PopularStockItem>> GetUsVolumeRankTop20Async(
        bool isValueSort = true,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{CacheKeyUs}_{(isValueSort ? "VALUE" : "VOLUME")}";
        return GetOrRefreshRankingAsync(
            cacheKey,
            ct => FetchUsFromKisAsync(isValueSort, ct),
            () => GetFallbackUsPopularStocksAsync(cancellationToken));
    }

    private async Task<RankingCacheEntry?> FetchUsFromKisAsync(
        bool isValueSort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.AppKey) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            return null;
        }

        try
        {
            var isMarketOpen = IsUsMarketOpen(DateTime.UtcNow);
            var token = await _tokenService.GetAccessTokenAsync(ct);

            // 나스닥(NAS) 기준 거래대금(trade-pbmn) 또는 거래량(trade-vol) 순위 호출
            var endpointPath = isValueSort ? "trade-pbmn" : "trade-vol";
            var trId = isValueSort ? "HHDFS76320010" : "HHDFS76310010";
            var requestUrl = $"{_settings.RestBaseUrl}/uapi/overseas-stock/v1/ranking/{endpointPath}?AUTH=&EXCD=NAS&NDAY=0&VOL_RANG=0&KEYB=&PRC1=&PRC2=";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("appkey", _settings.AppKey);
            request.Headers.Add("appsecret", _settings.AppSecret);
            request.Headers.Add("tr_id", trId);
            request.Headers.Add("custtype", "P");

            _logger.LogInformation("{Prefix} 미국 해외주식 {SortType} 순위 요청 전송 (TR_ID: {TrId}, EXCD: NAS)",
                LogConstants.KisApiPrefix, isValueSort ? "거래대금" : "거래량", trId);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("{Prefix} 미국 순위 응답 실패 (코드: {StatusCode}, 사유: {Error}) -> 기존 캐시 유지",
                    LogConstants.KisApiPrefix, response.StatusCode, errorText);
                return null;
            }

            var jsonStream = await response.Content.ReadAsStreamAsync(ct);
            var rankResult = await JsonSerializer.DeserializeAsync<KisOverseasVolumeRankResponse>(jsonStream, cancellationToken: ct);

            if (rankResult?.Output == null || rankResult.Output.Count == 0)
            {
                var retryTtl = isMarketOpen ? MarketOpenCacheDuration : TimeSpan.FromMinutes(30);
                _logger.LogInformation("{Prefix} [미국 장외/데이터 없음] 기존 캐시 유지 (다음 재시도: {Minutes}분)",
                    LogConstants.KisApiPrefix, retryTtl.TotalMinutes);
                return null;
            }

            var list = new List<PopularStockItem>(rankResult.Output.Count);
            int rank = 1;

            var topItems = rankResult.Output.Take(20).ToList();
            var tickers = topItems
                .Select(x => string.IsNullOrWhiteSpace(x.Symbol) ? x.RealSymbol : x.Symbol)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            var masterMap = await _store.GetByCodesAsync(tickers, ct);

            foreach (var item in topItems)
            {
                decimal.TryParse(item.LastPrice, out var price);
                decimal.TryParse(item.PriceChange, out var priceChange);
                double.TryParse(item.ChangeRate, out var changeRate);
                long.TryParse(item.Volume, out var volume);
                decimal.TryParse(item.TradingValue, out var tradingValue);

                if ((item.Sign == "4" || item.Sign == "5" || changeRate < 0) && priceChange > 0)
                {
                    priceChange = -priceChange;
                }
                else if (priceChange < 0 && changeRate > 0)
                {
                    changeRate = -changeRate;
                }

                var ticker = string.IsNullOrWhiteSpace(item.Symbol) ? item.RealSymbol : item.Symbol;
                if (price > 0 && !string.IsNullOrWhiteSpace(ticker))
                {
                    _store.UpdatePrice(ticker, price);
                }

                masterMap.TryGetValue(ticker, out var master);
                var stockName = master?.KoreanName ?? (!string.IsNullOrWhiteSpace(item.Name) ? item.Name : ticker);

                var popularItem = new PopularStockItem(
                    Rank: rank++,
                    StockCode: ticker,
                    StockName: stockName,
                    Price: price,
                    PriceChange: priceChange,
                    ChangeRate: changeRate,
                    Volume: volume,
                    MarketType: "USA",
                    ExchangeCode: master?.ExchangeCode ?? "NAS",
                    Currency: "USD",
                    TradingValue: tradingValue
                );

                KisQuoteService.SeedQuoteFromRanking(popularItem, master?.BasePrice ?? 0m);
                list.Add(popularItem);
            }

            DateTime nextRefreshUtc;
            if (isMarketOpen)
            {
                nextRefreshUtc = DateTime.UtcNow.Add(MarketOpenCacheDuration);
                _logger.LogInformation("{Prefix} [미국 장중] 실시간 순위 갱신 완료 (Top {Count}개, 갱신주기: 60초)",
                    LogConstants.KisApiPrefix, list.Count);
            }
            else
            {
                var freezeDuration = GetDurationUntilNextUsMarketOpen(DateTime.UtcNow);
                nextRefreshUtc = DateTime.UtcNow.Add(freezeDuration);
                _logger.LogInformation("{Prefix} [미국 장 마감] 직전 마감 순위 캐싱 완료. 다음 장 시작(평일 09:30 EST)까지 외부 호출 비활성화 (동결: {Hours:F1}시간)",
                    LogConstants.KisApiPrefix, freezeDuration.TotalHours);
            }

            return new RankingCacheEntry(list, nextRefreshUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Prefix} 미국 순위 조회 예외({Message}) -> 기존 캐시/Fallback 유지",
                LogConstants.KisApiPrefix, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 캐시 조회 및 유지 후 교체(Retain-Until-Replaced) 공통 처리기:
    /// - 이미 캐시에 값이 있으면 만료 여부와 무관하게 기존 값을 즉시 반환한다.
    /// - 갱신 시각(NextRefreshUtc)이 지났다면 백그라운드에서 단일 갱신 작업을 수행하고, 새 값을 정상 수신했을 때만 원자적으로 교체한다.
    /// </summary>
    private async Task<IReadOnlyList<PopularStockItem>> GetOrRefreshRankingAsync(
        string cacheKey,
        Func<CancellationToken, Task<RankingCacheEntry?>> fetchFromKisAsync,
        Func<Task<IReadOnlyList<PopularStockItem>>> fallbackFactory)
    {
        if (RankingStore.TryGetValue(cacheKey, out var existing) && existing.Items.Count > 0)
        {
            if (DateTime.UtcNow >= existing.NextRefreshUtc)
            {
                TriggerBackgroundRefresh(cacheKey, fetchFromKisAsync);
            }

            return existing.Items;
        }

        var syncLock = RefreshLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(CancellationToken.None);
        try
        {
            if (RankingStore.TryGetValue(cacheKey, out existing) && existing.Items.Count > 0)
            {
                return existing.Items;
            }

            using var cts = new CancellationTokenSource(KisRequestTimeout);
            var freshEntry = await fetchFromKisAsync(cts.Token);
            if (freshEntry != null && freshEntry.Items.Count > 0)
            {
                RankingStore[cacheKey] = freshEntry;
                return freshEntry.Items;
            }

            var fallbackItems = await fallbackFactory();
            RankingStore[cacheKey] = new RankingCacheEntry(fallbackItems, DateTime.UtcNow.Add(FailureRetryDelay));
            return fallbackItems;
        }
        finally
        {
            syncLock.Release();
        }
    }

    private void TriggerBackgroundRefresh(
        string cacheKey,
        Func<CancellationToken, Task<RankingCacheEntry?>> fetchFromKisAsync)
    {
        if (!BackgroundRefreshing.TryAdd(cacheKey, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var syncLock = RefreshLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await syncLock.WaitAsync(CancellationToken.None);
            try
            {
                using var cts = new CancellationTokenSource(KisRequestTimeout);
                var freshEntry = await fetchFromKisAsync(cts.Token);
                if (freshEntry != null && freshEntry.Items.Count > 0)
                {
                    RankingStore[cacheKey] = freshEntry;
                }
                else if (RankingStore.TryGetValue(cacheKey, out var current))
                {
                    // 새 값 조회 실패 시 기존 캐시 데이터는 그대로 유지하고 다음 재시도 시각만 갱신
                    RankingStore[cacheKey] = current with { NextRefreshUtc = DateTime.UtcNow.Add(FailureRetryDelay) };
                }
            }
            finally
            {
                syncLock.Release();
                BackgroundRefreshing.TryRemove(cacheKey, out _);
            }
        });
    }

    /// <summary>
    /// 국내 + 해외 통합 실시간 인기 종목 (Top 20, 거래대금 또는 거래량 내림차순)
    /// </summary>
    public async Task<IReadOnlyList<PopularStockItem>> GetAllVolumeRankTop20Async(
        bool isValueSort = true,
        CancellationToken cancellationToken = default)
    {
        var krTask = GetKrVolumeRankTop20Async("0000", CacheKeyKr, isValueSort, cancellationToken);
        var usTask = GetUsVolumeRankTop20Async(isValueSort, cancellationToken);

        await Task.WhenAll(krTask, usTask);

        var krList = await krTask;
        var usList = await usTask;

        // 시장 구분 없이 선택된 정렬 기준(거래대금 또는 거래량) 내림차순으로 Top 20 정렬
        return krList
            .Concat(usList)
            .OrderByDescending(x => GetSortScore(x, isValueSort))
            .Take(20)
            .Select((item, idx) => item with { Rank = idx + 1 })
            .ToList();
    }

    private async Task<IReadOnlyList<PopularStockItem>> GetKonexPopularStocksAsync(CancellationToken cancellationToken)
    {
        var items = await _store.GetByMarketAsync("KONEX", 20, cancellationToken);
        return items
            .Select((master, idx) => new PopularStockItem(
                Rank: idx + 1,
                StockCode: master.ShortCode,
                StockName: master.KoreanName,
                Price: master.BasePrice,
                PriceChange: 0,
                ChangeRate: 0,
                Volume: 0,
                MarketType: master.MarketType,
                ExchangeCode: master.ExchangeCode,
                Currency: master.Currency
            ))
            .ToList();
    }

    /// <summary>
    /// 국내 대표 대장주 Fallback
    /// </summary>
    private async Task<IReadOnlyList<PopularStockItem>> GetFallbackKrPopularStocksAsync(CancellationToken cancellationToken)
    {
        var targetCodes = new[]
        {
            "005930", // 삼성전자
            "000660", // SK하이닉스
            "373220", // LG에너지솔루션
            "207940", // 삼성바이오로직스
            "005380", // 현대차
            "068270", // 셀트리온
            "000270", // 기아
            "105560", // KB금융
            "035420", // NAVER
            "035720"  // 카카오
        };

        var masterMap = await _store.GetByCodesAsync(targetCodes, cancellationToken);
        var list = new List<PopularStockItem>();
        int rank = 1;

        foreach (var code in targetCodes)
        {
            if (masterMap.TryGetValue(code, out var master))
            {
                list.Add(new PopularStockItem(
                    Rank: rank++,
                    StockCode: master.ShortCode,
                    StockName: master.KoreanName,
                    Price: master.BasePrice,
                    PriceChange: 0,
                    ChangeRate: 0,
                    Volume: 0,
                    MarketType: master.MarketType,
                    ExchangeCode: master.ExchangeCode,
                    Currency: master.Currency
                ));
            }
        }

        return list;
    }

    /// <summary>
    /// 미국 대표 대장주 Fallback
    /// </summary>
    private async Task<IReadOnlyList<PopularStockItem>> GetFallbackUsPopularStocksAsync(CancellationToken cancellationToken)
    {
        var usTickers = new[]
        {
            "TSLA", // 테슬라
            "AAPL", // 애플
            "NVDA", // 엔비디아
            "MSFT", // 마이크로소프트
            "AMZN", // 아마존
            "GOOGL",// 알파벳 A
            "META", // 메타
            "AMD",  // AMD
            "PLTR", // 팔란티어
            "QQQ",  // 인베스코 QQQ
            "AVGO", // 브로드컴
            "NFLX", // 넷플릭스
            "MU",   // 마이크론
            "INTC", // 인텔
            "ARM",  // ARM
            "COIN", // 코인베이스
            "SOXL", // 디렉시온 반도체 3배
            "TQQQ", // 프로셰어즈 나스닥 3배
            "SPY",  // SPDR S&P500
            "COST"  // 코스트코
        };

        var masterMap = await _store.GetByCodesAsync(usTickers, cancellationToken);
        var list = new List<PopularStockItem>();
        int rank = 1;

        foreach (var ticker in usTickers)
        {
            masterMap.TryGetValue(ticker, out var master);
            list.Add(new PopularStockItem(
                Rank: rank++,
                StockCode: ticker,
                StockName: master?.KoreanName ?? ticker,
                Price: master?.BasePrice ?? 0,
                PriceChange: 0,
                ChangeRate: 0,
                Volume: 0,
                MarketType: "USA",
                ExchangeCode: master?.ExchangeCode ?? "NAS",
                Currency: "USD"
            ));
        }

        return list;
    }
}

// 국내 거래량 순위 KIS 응답 DTO
internal class KisVolumeRankResponse
{
    [JsonPropertyName("rt_cd")]
    public string ReturnCode { get; set; } = string.Empty;

    [JsonPropertyName("msg1")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("output")]
    public List<KisVolumeRankOutput>? Output { get; set; }
}

internal class KisVolumeRankOutput
{
    [JsonPropertyName("mksc_shrn_iscd")]
    public string StockCode { get; set; } = string.Empty;

    [JsonPropertyName("hts_kor_isnm")]
    public string StockName { get; set; } = string.Empty;

    [JsonPropertyName("stck_prpr")]
    public string CurrentPrice { get; set; } = string.Empty;

    [JsonPropertyName("prdy_vrss_sign")]
    public string Sign { get; set; } = string.Empty;

    [JsonPropertyName("prdy_vrss")]
    public string PriceChange { get; set; } = string.Empty;

    [JsonPropertyName("prdy_ctrt")]
    public string ChangeRate { get; set; } = string.Empty;

    [JsonPropertyName("acml_vol")]
    public string Volume { get; set; } = string.Empty;

    [JsonPropertyName("acml_tr_pbmn")]
    public string TradingValue { get; set; } = string.Empty;
}

// 해외 거래량 순위 KIS 응답 DTO
internal class KisOverseasVolumeRankResponse
{
    [JsonPropertyName("rt_cd")]
    public string ReturnCode { get; set; } = string.Empty;

    [JsonPropertyName("msg1")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("output2")]
    public List<KisOverseasVolumeRankOutput>? Output { get; set; }
}

internal class KisOverseasVolumeRankOutput
{
    [JsonPropertyName("symb")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("rsym")]
    public string RealSymbol { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("last")]
    public string LastPrice { get; set; } = string.Empty;

    [JsonPropertyName("sign")]
    public string Sign { get; set; } = string.Empty;

    [JsonPropertyName("diff")]
    public string PriceChange { get; set; } = string.Empty;

    [JsonPropertyName("rate")]
    public string ChangeRate { get; set; } = string.Empty;

    [JsonPropertyName("tvol")]
    public string Volume { get; set; } = string.Empty;

    [JsonPropertyName("tamt")]
    public string TradingValue { get; set; } = string.Empty;
}
