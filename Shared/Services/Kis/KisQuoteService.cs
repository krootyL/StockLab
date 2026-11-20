using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Master;
using StockLab.Settings;

namespace StockLab.Services.Kis;

public class KisQuoteService
{
    private sealed record QuoteCacheEntry(StockQuoteResponse Quote, DateTime NextRefreshUtc);

    private static readonly ConcurrentDictionary<string, QuoteCacheEntry> QuoteStore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> BackgroundQuoteRefreshing = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> QuoteRefreshLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, RealtimeStockStatus> OrderBookStore = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly KisSessionService _tokenService;
    private readonly IStockMasterProvider _store;
    private readonly IMemoryCache _cache;
    private readonly KisSettings _settings;
    private readonly ILogger<KisQuoteService> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(3); // 3초 갱신 주기 (다음 값을 받아올 때까지 기존 캐시 유지 후 교체)
    private static readonly TimeSpan ColdStartWaitBudget = TimeSpan.FromSeconds(2); // 미적재 종목 최초 조회 최대 대기시간
    private static readonly TimeSpan KisQuoteTimeout = TimeSpan.FromSeconds(10);

    public KisQuoteService(
        HttpClient httpClient,
        KisSessionService tokenService,
        IStockMasterProvider store,
        IMemoryCache cache,
        IOptions<KisSettings> options,
        ILogger<KisQuoteService> logger)
    {
        _httpClient = httpClient;
        _tokenService = tokenService;
        _store = store;
        _cache = cache;
        _settings = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 인기 순위 조회 시 이미 수신한 Top 20 종목의 현재가·전일대비·등락률·거래량·거래대금을
    /// 추가 API 호출 없이 시세 캐시에 초기값으로 선적재(Warm-up)한다.
    /// </summary>
    public static void SeedQuoteFromRanking(PopularStockItem item, decimal basePrice)
    {
        if (string.IsNullOrWhiteSpace(item.StockCode) || item.Price <= 0)
        {
            return;
        }

        var inferredBase = basePrice > 0
            ? basePrice
            : (item.Price - item.PriceChange > 0 ? item.Price - item.PriceChange : item.Price);

        var seeded = new StockQuoteResponse(
            StockCode: item.StockCode,
            StockName: item.StockName,
            MarketType: item.MarketType,
            ExchangeCode: item.ExchangeCode,
            Currency: item.Currency,
            CurrentPrice: item.Price,
            PriceChange: item.PriceChange,
            ChangeRate: item.ChangeRate,
            Volume: item.Volume,
            TradingValue: item.TradingValue,
            OpenPrice: item.Price,
            HighPrice: item.Price,
            LowPrice: item.Price,
            BasePrice: inferredBase,
            UpdatedAtUtc: DateTime.UtcNow);

        // 아직 단건 상세 시세가 없는 종목만 선적재하고, 클릭 시 즉시 반환 후 백그라운드에서 상세 시세로 교체되도록 NextRefreshUtc를 현재 시각으로 설정
        QuoteStore.TryAdd(item.StockCode, new QuoteCacheEntry(seeded, DateTime.UtcNow));
    }

    /// <summary>
    /// 국내 및 해외 주식 실시간 현재가 시세 조회 (유지 후 백그라운드 교체 캐시 적용)
    /// </summary>
    public async Task<StockQuoteResponse> GetQuoteAsync(string code, CancellationToken cancellationToken = default)
    {
        var master = await _store.GetByCodeAsync(code, cancellationToken)
            ?? throw new StockLabException(ErrorCode.StockNotFound, $"종목코드 '{code}'를 찾을 수 없습니다.");

        if (QuoteStore.TryGetValue(master.ShortCode, out var existingEntry))
        {
            if (DateTime.UtcNow >= existingEntry.NextRefreshUtc)
            {
                TriggerBackgroundQuoteRefresh(master);
            }

            return existingEntry.Quote;
        }

        // KIS 설정 미비 시 마스터 기준가 기반 Fallback 적재 후 반환
        if (string.IsNullOrWhiteSpace(_settings.AppKey) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            var fallbackNoKey = CreateFallbackQuote(master);
            QuoteStore[master.ShortCode] = new QuoteCacheEntry(fallbackNoKey, DateTime.UtcNow.Add(CacheDuration));
            return fallbackNoKey;
        }

        // 최초 미적재 종목 조회: 백그라운드 갱신을 시작하고 짧은 대기(2초) 내 완료되면 즉시 반환, 지연 시 기준가 스냅샷을 먼저 반환
        var refreshTask = RefreshQuoteCoreAsync(master);
        var completed = await Task.WhenAny(refreshTask, Task.Delay(ColdStartWaitBudget, cancellationToken));
        if (completed == refreshTask)
        {
            var fetched = await refreshTask;
            if (fetched != null)
            {
                return fetched;
            }
        }

        if (QuoteStore.TryGetValue(master.ShortCode, out var afterWait))
        {
            return afterWait.Quote;
        }

        var fallback = CreateFallbackQuote(master);
        QuoteStore.TryAdd(master.ShortCode, new QuoteCacheEntry(fallback, DateTime.UtcNow.Add(CacheDuration)));
        return fallback;
    }

    private void TriggerBackgroundQuoteRefresh(StockMasterItem master)
    {
        if (!BackgroundQuoteRefreshing.TryAdd(master.ShortCode, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshQuoteCoreAsync(master);
            }
            finally
            {
                BackgroundQuoteRefreshing.TryRemove(master.ShortCode, out _);
            }
        });
    }

    private async Task<StockQuoteResponse?> RefreshQuoteCoreAsync(StockMasterItem master)
    {
        if (string.IsNullOrWhiteSpace(_settings.AppKey) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            return null;
        }

        var syncLock = QuoteRefreshLocks.GetOrAdd(master.ShortCode, _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(CancellationToken.None);
        try
        {
            if (QuoteStore.TryGetValue(master.ShortCode, out var current) && DateTime.UtcNow < current.NextRefreshUtc)
            {
                return current.Quote;
            }

            using var cts = new CancellationTokenSource(KisQuoteTimeout);
            var token = await _tokenService.GetAccessTokenAsync(cts.Token);

            var quote = master.MarketType == "USA"
                ? await FetchOverseasQuoteAsync(master, token, cts.Token)
                : await FetchDomesticQuoteAsync(master, token, cts.Token);

            QuoteStore[master.ShortCode] = new QuoteCacheEntry(quote, DateTime.UtcNow.Add(CacheDuration));
            return quote;
        }
        catch (Exception ex) when (ex is not StockLabException)
        {
            if (QuoteStore.TryGetValue(master.ShortCode, out var retained))
            {
                QuoteStore[master.ShortCode] = retained with { NextRefreshUtc = DateTime.UtcNow.Add(CacheDuration) };
                _logger.LogWarning("{Prefix} '{Code}'({Name}) 실시간 시세 조회 실패({Message}) -> 직전 캐시 시세를 유지합니다.",
                    LogConstants.KisApiPrefix, master.ShortCode, master.KoreanName, ex.Message);
                return retained.Quote;
            }

            _logger.LogWarning("{Prefix} '{Code}'({Name}) 실시간 시세 조회 중 오류({Message}) -> 마스터 기준가로 대체합니다.",
                LogConstants.KisApiPrefix, master.ShortCode, master.KoreanName, ex.Message);
            var fallback = CreateFallbackQuote(master);
            QuoteStore[master.ShortCode] = new QuoteCacheEntry(fallback, DateTime.UtcNow.Add(CacheDuration));
            return fallback;
        }
        finally
        {
            syncLock.Release();
        }
    }

    /// <summary>
    /// 체결 엔진 폴링용 단건 10호가 스냅샷 REST 조회 (국내: FHKST01010200, 해외: HHDFS76200100)
    /// 예외 또는 빈 호가 수신 시 직전 유효 호가 또는 기준가 기반 가상 호가로 대체하여 가상 체결이 중단되지 않도록 보장한다.
    /// </summary>
    public async Task<RealtimeStockStatus?> GetOrderBookAsync(string code, CancellationToken cancellationToken = default)
    {
        var master = await _store.GetByCodeAsync(code, cancellationToken);
        if (master == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_settings.AppKey) || string.IsNullOrWhiteSpace(_settings.AppSecret))
        {
            return OrderBookStore.TryGetValue(master.ShortCode, out var cachedNoKey)
                ? cachedNoKey
                : CreateFallbackOrderBook(master);
        }

        try
        {
            var token = await _tokenService.GetAccessTokenAsync(cancellationToken);
            var status = master.MarketType == "USA"
                ? await FetchOverseasOrderBookAsync(master, token, cancellationToken)
                : await FetchDomesticOrderBookAsync(master, token, cancellationToken);

            if (status != null && (status.Asks.Count > 0 || status.Bids.Count > 0))
            {
                var topPrice = status.Bids.FirstOrDefault(b => b.Price > 0)?.Price
                               ?? status.Asks.FirstOrDefault(a => a.Price > 0)?.Price
                               ?? 0m;
                if (topPrice > 0)
                {
                    _store.UpdatePrice(master.ShortCode, topPrice);
                }

                OrderBookStore[master.ShortCode] = status;
                return status;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Prefix} '{Code}' 호가 조회 예외({Message}) -> 직전 캐시/기준가 호가로 대체합니다.",
                LogConstants.KisApiPrefix, master.ShortCode, ex.Message);
        }

        if (OrderBookStore.TryGetValue(master.ShortCode, out var retainedBook))
        {
            return retainedBook;
        }

        var fallbackBook = CreateFallbackOrderBook(master);
        if (fallbackBook != null)
        {
            OrderBookStore[master.ShortCode] = fallbackBook;
        }

        return fallbackBook;
    }

    private async Task<RealtimeStockStatus?> FetchDomesticOrderBookAsync(
        StockMasterItem master,
        string accessToken,
        CancellationToken ct)
    {
        var url = $"{_settings.RestBaseUrl}/uapi/domestic-stock/v1/quotations/inquire-asking-price-exp-ccn?FID_COND_MRKT_DIV_CODE=J&FID_INPUT_ISCD={Uri.EscapeDataString(master.ShortCode)}";
        using var doc = await SendKisJsonAsync(master.ShortCode, url, "FHKST01010200", accessToken, ct);
        if (!doc.RootElement.TryGetProperty("output1", out var output1) || output1.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var asks = ExtractLevels(output1, "askp", "askp_rsqn", 10);
        var bids = ExtractLevels(output1, "bidp", "bidp_rsqn", 10);
        var updatedTime = GetJsonString(output1, "aspr_acpt_hour");
        var totalAsk = ParseLong(GetJsonString(output1, "total_askp_rsqn"));
        var totalBid = ParseLong(GetJsonString(output1, "total_bidp_rsqn"));

        return new RealtimeStockStatus(
            master.ShortCode,
            master.MarketType,
            master.ExchangeCode,
            master.Currency,
            updatedTime,
            asks,
            bids,
            totalAsk,
            totalBid);
    }

    private async Task<RealtimeStockStatus?> FetchOverseasOrderBookAsync(
        StockMasterItem master,
        string accessToken,
        CancellationToken ct)
    {
        var exchange = string.IsNullOrWhiteSpace(master.ExchangeCode) ? "NAS" : master.ExchangeCode;
        var url = $"{_settings.RestBaseUrl}/uapi/overseas-price/v1/quotations/inquire-asking-price?AUTH=&EXCD={Uri.EscapeDataString(exchange)}&SYMB={Uri.EscapeDataString(master.ShortCode)}";
        using var doc = await SendKisJsonAsync(master.ShortCode, url, "HHDFS76200100", accessToken, ct);

        if (!doc.RootElement.TryGetProperty("output2", out var output2))
        {
            return null;
        }

        var levelNode = output2.ValueKind == JsonValueKind.Array && output2.GetArrayLength() > 0
            ? output2[0]
            : output2;

        if (levelNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var asks = ExtractLevels(levelNode, "pask", "vask", 10);
        var bids = ExtractLevels(levelNode, "pbid", "vbid", 10);

        var updatedTime = string.Empty;
        long totalAsk = 0;
        long totalBid = 0;
        if (doc.RootElement.TryGetProperty("output1", out var output1) && output1.ValueKind == JsonValueKind.Object)
        {
            updatedTime = GetJsonString(output1, "dhms");
            totalAsk = ParseLong(GetJsonString(output1, "avol"));
            totalBid = ParseLong(GetJsonString(output1, "bvol"));
        }

        return new RealtimeStockStatus(
            master.ShortCode,
            master.MarketType,
            exchange,
            master.Currency,
            updatedTime,
            asks,
            bids,
            totalAsk,
            totalBid);
    }

    private async Task<JsonDocument> SendKisJsonAsync(string stockCode, string url, string trId, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("appkey", _settings.AppKey);
        request.Headers.Add("appsecret", _settings.AppSecret);
        request.Headers.Add("tr_id", trId);
        request.Headers.Add("custtype", "P");

        _logger.LogInformation("{Prefix} 호가 스냅샷 요청 전송 [종목: {Code}, TR_ID: {TrId}]",
            LogConstants.KisApiPrefix, stockCode, trId);

        using var response = await _httpClient.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"KIS API({trId}) 실패 ({(int)response.StatusCode}): {content}");
        }

        var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("rt_cd", out var rtCd) && rtCd.GetString() != "0")
        {
            var msgCd = GetJsonString(doc.RootElement, "msg_cd");
            var msg1 = GetJsonString(doc.RootElement, "msg1");
            doc.Dispose();
            throw new HttpRequestException($"KIS API({trId}) 오류 [{msgCd}]: {msg1}");
        }

        return doc;
    }

    private static List<OrderBookLevel> ExtractLevels(JsonElement node, string pricePrefix, string qtyPrefix, int count)
    {
        var list = new List<OrderBookLevel>(count);
        for (var i = 1; i <= count; i++)
        {
            var price = ParseDecimal(GetJsonString(node, $"{pricePrefix}{i}"));
            var qty = ParseLong(GetJsonString(node, $"{qtyPrefix}{i}"));
            if (price > 0 && qty > 0)
            {
                list.Add(new OrderBookLevel(price, qty));
            }
        }
        return list;
    }

    private static string GetJsonString(JsonElement node, string propertyName)
        => node.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;

    private static decimal ParseDecimal(string value)
        => decimal.TryParse(value, out var parsed) ? parsed : 0m;

    private static long ParseLong(string value)
        => long.TryParse(value, out var parsed) ? parsed : 0L;

    /// <summary>
    /// 국내주식 현재가 조회 (TR_ID: FHKST01010100)
    /// </summary>
    private async Task<StockQuoteResponse> FetchDomesticQuoteAsync(
        StockMasterItem master,
        string token,
        CancellationToken cancellationToken)
    {
        var requestUrl = $"{_settings.RestBaseUrl}/uapi/domestic-stock/v1/quotations/inquire-price?FID_COND_MRKT_DIV_CODE=J&FID_INPUT_ISCD={Uri.EscapeDataString(master.ShortCode)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("appkey", _settings.AppKey);
        request.Headers.Add("appsecret", _settings.AppSecret);
        request.Headers.Add("tr_id", "FHKST01010100");
        request.Headers.Add("custtype", "P");

        _logger.LogInformation("{Prefix} 국내주식 시세 요청 전송 [종목: {Code} {Name}, TR_ID: FHKST01010100]",
            LogConstants.KisApiPrefix, master.ShortCode, master.KoreanName);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("{Prefix} 국내 시세 수신 실패 (상태: {Status}, 사유: {Error})",
                LogConstants.KisApiPrefix, response.StatusCode, errorText);

            return CreateFallbackQuote(master);
        }

        var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<KisDomesticQuoteApiResponse>(jsonStream, cancellationToken: cancellationToken);

        if (result?.Output == null)
        {
            return CreateFallbackQuote(master);
        }

        var output = result.Output;
        decimal.TryParse(output.Price, out var currentPrice);
        decimal.TryParse(output.PriceChange, out var priceChange);
        double.TryParse(output.ChangeRate, out var changeRate);
        long.TryParse(output.Volume, out var volume);
        decimal.TryParse(output.TradingValue, out var tradingValue);
        decimal.TryParse(output.OpenPrice, out var openPrice);
        decimal.TryParse(output.HighPrice, out var highPrice);
        decimal.TryParse(output.LowPrice, out var lowPrice);
        decimal.TryParse(output.BasePrice, out var basePrice);

        // 부호에 따른 전일대비 부호 처리 (4: 하한, 5: 하락 또는 등락률 음수인 경우 음수 처리)
        if ((output.Sign == "4" || output.Sign == "5" || changeRate < 0) && priceChange > 0)
        {
            priceChange = -priceChange;
        }
        else if (priceChange < 0 && changeRate > 0)
        {
            changeRate = -changeRate;
        }

        if (currentPrice == 0 && master.BasePrice > 0)
        {
            currentPrice = master.BasePrice;
        }
        else if (currentPrice > 0)
        {
            _store.UpdatePrice(master.ShortCode, currentPrice);
        }

        return new StockQuoteResponse(
            StockCode: master.ShortCode,
            StockName: master.KoreanName,
            MarketType: master.MarketType,
            ExchangeCode: master.ExchangeCode,
            Currency: master.Currency,
            CurrentPrice: currentPrice,
            PriceChange: priceChange,
            ChangeRate: changeRate,
            Volume: volume,
            TradingValue: tradingValue,
            OpenPrice: openPrice > 0 ? openPrice : currentPrice,
            HighPrice: highPrice > 0 ? highPrice : currentPrice,
            LowPrice: lowPrice > 0 ? lowPrice : currentPrice,
            BasePrice: basePrice > 0 ? basePrice : master.BasePrice,
            UpdatedAtUtc: DateTime.UtcNow
        );
    }

    /// <summary>
    /// 해외주식 현재가 조회 (TR_ID: HHDFS00000300)
    /// </summary>
    private async Task<StockQuoteResponse> FetchOverseasQuoteAsync(
        StockMasterItem master,
        string token,
        CancellationToken cancellationToken)
    {
        var exchange = string.IsNullOrWhiteSpace(master.ExchangeCode) ? "NAS" : master.ExchangeCode;
        var requestUrl = $"{_settings.RestBaseUrl}/uapi/overseas-price/v1/quotations/price?AUTH=&EXCD={Uri.EscapeDataString(exchange)}&SYMB={Uri.EscapeDataString(master.ShortCode)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("appkey", _settings.AppKey);
        request.Headers.Add("appsecret", _settings.AppSecret);
        request.Headers.Add("tr_id", "HHDFS00000300");
        request.Headers.Add("custtype", "P");

        _logger.LogInformation("{Prefix} 해외주식 시세 요청 전송 [티커: {Code} ({Exchange}), TR_ID: HHDFS00000300]",
            LogConstants.KisApiPrefix, master.ShortCode, exchange);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("{Prefix} 해외 시세 수신 실패 (상태: {Status}, 사유: {Error})",
                LogConstants.KisApiPrefix, response.StatusCode, errorText);

            return CreateFallbackQuote(master);
        }

        var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<KisOverseasQuoteApiResponse>(jsonStream, cancellationToken: cancellationToken);

        if (result?.Output == null)
        {
            return CreateFallbackQuote(master);
        }

        var output = result.Output;
        decimal.TryParse(output.LastPrice, out var currentPrice);
        decimal.TryParse(output.PriceChange, out var priceChange);
        double.TryParse(output.ChangeRate, out var changeRate);
        long.TryParse(output.Volume, out var volume);
        decimal.TryParse(output.TradingValue, out var tradingValue);
        decimal.TryParse(output.OpenPrice, out var openPrice);
        decimal.TryParse(output.HighPrice, out var highPrice);
        decimal.TryParse(output.LowPrice, out var lowPrice);
        decimal.TryParse(output.BasePrice, out var basePrice);

        // 해외 시세 diff는 부호 없는 절대값으로 반환되므로 sign(4: 하한, 5: 하락) 또는 음수 등락률 기준으로 음수 부호 일원화
        if ((output.Sign == "4" || output.Sign == "5" || changeRate < 0) && priceChange > 0)
        {
            priceChange = -priceChange;
        }
        else if (priceChange < 0 && changeRate > 0)
        {
            changeRate = -changeRate;
        }

        if (currentPrice == 0 && master.BasePrice > 0)
        {
            currentPrice = master.BasePrice;
        }
        else if (currentPrice > 0)
        {
            _store.UpdatePrice(master.ShortCode, currentPrice);
        }

        return new StockQuoteResponse(
            StockCode: master.ShortCode,
            StockName: master.KoreanName,
            MarketType: master.MarketType,
            ExchangeCode: master.ExchangeCode,
            Currency: master.Currency,
            CurrentPrice: currentPrice,
            PriceChange: priceChange,
            ChangeRate: changeRate,
            Volume: volume,
            TradingValue: tradingValue,
            OpenPrice: openPrice > 0 ? openPrice : currentPrice,
            HighPrice: highPrice > 0 ? highPrice : currentPrice,
            LowPrice: lowPrice > 0 ? lowPrice : currentPrice,
            BasePrice: basePrice > 0 ? basePrice : master.BasePrice,
            UpdatedAtUtc: DateTime.UtcNow
        );
    }

    private static RealtimeStockStatus? CreateFallbackOrderBook(StockMasterItem master)
    {
        if (master.BasePrice <= 0)
        {
            return null;
        }

        var isUsd = string.Equals(master.Currency, "USD", StringComparison.OrdinalIgnoreCase);
        var minUnit = isUsd ? 0.01m : 1m;
        var basePrice = Math.Max(minUnit, Math.Round(master.BasePrice, isUsd ? 2 : 0, MidpointRounding.AwayFromZero));

        var asks = new List<OrderBookLevel>(3)
        {
            new(basePrice, 20),
            new(Math.Max(minUnit, Math.Round(basePrice * 1.001m, isUsd ? 2 : 0, MidpointRounding.AwayFromZero)), 20),
            new(Math.Max(minUnit, Math.Round(basePrice * 1.002m, isUsd ? 2 : 0, MidpointRounding.AwayFromZero)), 20)
        };

        var bids = new List<OrderBookLevel>(3)
        {
            new(basePrice, 20),
            new(Math.Max(minUnit, Math.Round(basePrice * 0.999m, isUsd ? 2 : 0, MidpointRounding.AwayFromZero)), 20),
            new(Math.Max(minUnit, Math.Round(basePrice * 0.998m, isUsd ? 2 : 0, MidpointRounding.AwayFromZero)), 20)
        };

        return new RealtimeStockStatus(
            StockCode: master.ShortCode,
            MarketType: master.MarketType,
            ExchangeCode: string.IsNullOrWhiteSpace(master.ExchangeCode) ? "KRX" : master.ExchangeCode,
            Currency: master.Currency,
            UpdatedTime: "기준가 대체",
            Asks: asks,
            Bids: bids,
            TotalAskSize: 60,
            TotalBidSize: 60);
    }

    private static StockQuoteResponse CreateFallbackQuote(StockMasterItem master)
    {
        return new StockQuoteResponse(
            StockCode: master.ShortCode,
            StockName: master.KoreanName,
            MarketType: master.MarketType,
            ExchangeCode: master.ExchangeCode,
            Currency: master.Currency,
            CurrentPrice: master.BasePrice,
            PriceChange: 0,
            ChangeRate: 0,
            Volume: 0,
            TradingValue: 0m,
            OpenPrice: master.BasePrice,
            HighPrice: master.BasePrice,
            LowPrice: master.BasePrice,
            BasePrice: master.BasePrice,
            UpdatedAtUtc: DateTime.UtcNow
        );
    }
}

// 국내주식 KIS 응답 DTO
internal class KisDomesticQuoteApiResponse
{
    [JsonPropertyName("rt_cd")]
    public string ReturnCode { get; set; } = string.Empty;

    [JsonPropertyName("msg1")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("output")]
    public KisDomesticQuoteOutput? Output { get; set; }
}

internal class KisDomesticQuoteOutput
{
    [JsonPropertyName("stck_prpr")]
    public string Price { get; set; } = string.Empty;

    [JsonPropertyName("prdy_vrss")]
    public string PriceChange { get; set; } = string.Empty;

    [JsonPropertyName("prdy_vrss_sign")]
    public string Sign { get; set; } = string.Empty;

    [JsonPropertyName("prdy_ctrt")]
    public string ChangeRate { get; set; } = string.Empty;

    [JsonPropertyName("acml_vol")]
    public string Volume { get; set; } = string.Empty;

    [JsonPropertyName("acml_tr_pbmn")]
    public string TradingValue { get; set; } = string.Empty;

    [JsonPropertyName("stck_oprc")]
    public string OpenPrice { get; set; } = string.Empty;

    [JsonPropertyName("stck_hgpr")]
    public string HighPrice { get; set; } = string.Empty;

    [JsonPropertyName("stck_lwpr")]
    public string LowPrice { get; set; } = string.Empty;

    [JsonPropertyName("stck_sdpr")]
    public string BasePrice { get; set; } = string.Empty;
}

// 해외주식 KIS 응답 DTO
internal class KisOverseasQuoteApiResponse
{
    [JsonPropertyName("rt_cd")]
    public string ReturnCode { get; set; } = string.Empty;

    [JsonPropertyName("msg1")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("output")]
    public KisOverseasQuoteOutput? Output { get; set; }
}

internal class KisOverseasQuoteOutput
{
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

    [JsonPropertyName("open")]
    public string OpenPrice { get; set; } = string.Empty;

    [JsonPropertyName("high")]
    public string HighPrice { get; set; } = string.Empty;

    [JsonPropertyName("low")]
    public string LowPrice { get; set; } = string.Empty;

    [JsonPropertyName("base")]
    public string BasePrice { get; set; } = string.Empty;
}

