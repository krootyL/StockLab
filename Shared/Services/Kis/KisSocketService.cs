using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Hubs;
using StockLab.Services.Master;
using StockLab.Services.Trading;
using StockLab.Settings;

namespace StockLab.Services.Kis;

public class KisSocketService : IHostedService
{
    private struct CachedOrderBook
    {
        public RealtimeStockStatus Data;
        public DateTime UpdatedAt;
    }

    private readonly KisSessionService _sessionService;
    private readonly IStockMasterProvider _masterStore;
    private readonly IHubContext<StockQuoteHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KisSettings _settings;
    private readonly ILogger<KisSocketService> _logger;

    private readonly ConcurrentDictionary<string, CachedOrderBook> _realtimeStockCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _subscribersByStock = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _stocksByConnection = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _socketLock = new(1, 1);

    private ClientWebSocket? _webSocket;
    private string _approvalKey = string.Empty;
    private Task? _receiveTask;
    private CancellationTokenSource? _receiveCts;

    public KisSocketService(
        KisSessionService sessionService,
        IStockMasterProvider masterStore,
        IHubContext<StockQuoteHub> hubContext,
        IServiceScopeFactory scopeFactory,
        IOptions<KisSettings> options,
        ILogger<KisSocketService> logger)
    {
        _sessionService = sessionService;
        _masterStore = masterStore;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
        _settings = options.Value;
        _logger = logger;
    }

    public RealtimeStockStatus? GetCachedOrderBook(string stockCode)
    {
        return _realtimeStockCache.TryGetValue(stockCode, out var cached) ? cached.Data : null;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _receiveCts?.Cancel();

        if (_webSocket != null)
        {
            _webSocket.Abort();
            _webSocket.Dispose();
            _webSocket = null;
            _logger.LogInformation("{Prefix} KIS 실시간 WebSocket 연결 즉시 중단 완료", LogConstants.KisApiPrefix);
        }

        if (_receiveTask != null)
        {
            await Task.WhenAny(_receiveTask, Task.Delay(500, cancellationToken));
        }

        _receiveCts?.Dispose();
        _receiveCts = null;
    }

    public async Task GetRealtimeStockAsync(
        string stockCode,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stockCode) || string.IsNullOrWhiteSpace(connectionId)) return;
        var code = stockCode.Trim().ToUpperInvariant();

        var master = await _masterStore.GetByCodeAsync(code, cancellationToken);
        var isMarketOpen = MarketSessionHelper.IsMarketOpen(master?.MarketType ?? "KOSPI", DateTime.UtcNow);

        // 이미 캐싱된 호가가 있으면 즉시 전달, 없으면 인메모리 마스터 DB의 현재가/기준가로 초기 스냅샷 생성
        if (_realtimeStockCache.TryGetValue(code, out var cached))
        {
            await _hubContext.Clients
                .Group($"stock:{code}")
                .SendAsync("ReceiveRealtimeStock", cached.Data, cancellationToken);
        }
        else if (master != null)
        {
            var initialLevel = master.BasePrice > 0
                ? new List<OrderBookLevel> { new(master.BasePrice, 0) }
                : new List<OrderBookLevel>();

            var initialStatus = new RealtimeStockStatus(
                StockCode: master.ShortCode,
                MarketType: master.MarketType,
                ExchangeCode: master.ExchangeCode,
                Currency: master.Currency,
                UpdatedTime: isMarketOpen ? "메모리 기준" : "장 마감 (메모리 기준)",
                Asks: initialLevel,
                Bids: initialLevel,
                TotalAskSize: 0,
                TotalBidSize: 0
            );

            await _hubContext.Clients
                .Group($"stock:{code}")
                .SendAsync("ReceiveRealtimeStock", initialStatus, cancellationToken);
        }

        // 현재 장 운영 시간이 아니라면 KIS WebSocket 연결 및 등록을 건너뜀
        if (!isMarketOpen)
        {
            _logger.LogInformation("[실시간 호가] 종목({StockCode}) 장 마감 상태 -> KIS WebSocket 연결 및 등록 생략", code);
            return;
        }

        await _socketLock.WaitAsync(cancellationToken);
        try
        {
            // 동일 클라이언트가 이전에 열어둔 다른 종목이 남아 있다면 먼저 자동 해제
            if (_stocksByConnection.TryGetValue(connectionId, out var connStocks))
            {
                var staleCodes = connStocks
                    .Where(prev => !prev.Equals(code, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var staleCode in staleCodes)
                {
                    await RemoveSubscriptionLockedAsync(staleCode, connectionId, "종목 전환", cancellationToken);
                }
            }
            else
            {
                connStocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _stocksByConnection[connectionId] = connStocks;
            }

            connStocks.Add(code);

            if (!_subscribersByStock.TryGetValue(code, out var subscribers))
            {
                subscribers = new HashSet<string>(StringComparer.Ordinal);
                _subscribersByStock[code] = subscribers;
            }

            if (!subscribers.Add(connectionId))
            {
                return;
            }

            var nextCount = subscribers.Count;
            _logger.LogInformation("[실시간 호가] 종목({StockCode}) 열람자 수 증가: {Count}명", code, nextCount);

            // 최초 열람자(0 -> 1)일 때만 KIS 소켓 연결 및 등록 요청 전송
            if (nextCount == 1)
            {
                await EnsureConnectedAsync(cancellationToken);
                await SendSubscriptionPacketAsync(code, "1", cancellationToken);
            }
        }
        finally
        {
            _socketLock.Release();
        }
    }

    public async Task ReleaseRealtimeStockAsync(
        string stockCode,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stockCode) || string.IsNullOrWhiteSpace(connectionId)) return;
        var code = stockCode.Trim().ToUpperInvariant();

        await _socketLock.WaitAsync(cancellationToken);
        try
        {
            await RemoveSubscriptionLockedAsync(code, connectionId, null, cancellationToken);
        }
        finally
        {
            _socketLock.Release();
        }
    }

    /// <summary>
    /// 클라이언트의 관심종목 목록 전체를 로컬 KIS 토큰 기반 WebSocket에 다중 구독·동기화합니다.
    /// 상세 모달 단일 구독(connectionId)과 간섭하지 않도록 "{connectionId}:watchlist" 구독 키로 분리 관리합니다.
    /// </summary>
    public async Task SyncWatchlistStocksAsync(
        IReadOnlyCollection<string> stockCodes,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;
        var watchlistSubscriberId = $"{connectionId}:watchlist";

        var desiredCodes = (stockCodes ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40) // KIS 단일 웹소켓 세션당 최대 구독 한도 보호
            .ToList();

        var desiredSet = new HashSet<string>(desiredCodes, StringComparer.OrdinalIgnoreCase);
        var openMarketDesired = new List<string>();

        foreach (var code in desiredCodes)
        {
            var master = await _masterStore.GetByCodeAsync(code, cancellationToken);
            var isMarketOpen = MarketSessionHelper.IsMarketOpen(master?.MarketType ?? "KOSPI", DateTime.UtcNow);

            if (_realtimeStockCache.TryGetValue(code, out var cached))
            {
                await _hubContext.Clients
                    .Group($"stock:{code}")
                    .SendAsync("ReceiveRealtimeStock", cached.Data, cancellationToken);
            }
            else if (master != null)
            {
                var initialLevel = master.BasePrice > 0
                    ? new List<OrderBookLevel> { new(master.BasePrice, 0) }
                    : new List<OrderBookLevel>();

                var initialStatus = new RealtimeStockStatus(
                    StockCode: master.ShortCode,
                    MarketType: master.MarketType,
                    ExchangeCode: master.ExchangeCode,
                    Currency: master.Currency,
                    UpdatedTime: isMarketOpen ? "메모리 기준" : "장 마감 (메모리 기준)",
                    Asks: initialLevel,
                    Bids: initialLevel,
                    TotalAskSize: 0,
                    TotalBidSize: 0
                );

                await _hubContext.Clients
                    .Group($"stock:{code}")
                    .SendAsync("ReceiveRealtimeStock", initialStatus, cancellationToken);
            }

            if (isMarketOpen)
            {
                openMarketDesired.Add(code);
            }
        }

        await _socketLock.WaitAsync(cancellationToken);
        try
        {
            if (_stocksByConnection.TryGetValue(watchlistSubscriberId, out var existingCodes))
            {
                var removedCodes = existingCodes
                    .Where(prev => !desiredSet.Contains(prev))
                    .ToList();

                foreach (var removed in removedCodes)
                {
                    await RemoveSubscriptionLockedAsync(removed, watchlistSubscriberId, "관심종목 해제", cancellationToken);
                }
            }

            if (openMarketDesired.Count == 0)
            {
                return;
            }

            if (!_stocksByConnection.TryGetValue(watchlistSubscriberId, out var connStocks))
            {
                connStocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _stocksByConnection[watchlistSubscriberId] = connStocks;
            }

            foreach (var code in openMarketDesired)
            {
                connStocks.Add(code);

                if (!_subscribersByStock.TryGetValue(code, out var subscribers))
                {
                    subscribers = new HashSet<string>(StringComparer.Ordinal);
                    _subscribersByStock[code] = subscribers;
                }

                if (!subscribers.Add(watchlistSubscriberId))
                {
                    continue;
                }

                var nextCount = subscribers.Count;
                _logger.LogInformation("[실시간 호가/관심종목] 종목({StockCode}) 열람자 수 증가: {Count}명", code, nextCount);

                if (nextCount == 1)
                {
                    await EnsureConnectedAsync(cancellationToken);
                    await SendSubscriptionPacketAsync(code, "1", cancellationToken);
                }
            }
        }
        finally
        {
            _socketLock.Release();
        }
    }

    /// <summary>
    /// 브라우저 새로고침, 탭 종료, 서버/네트워크 재연결 등으로 SignalR 연결이 끊어졌을 때
    /// 해당 클라이언트가 열람 중이던 모든 종목(단일 모달 및 관심종목)의 구독을 자동 정리한다.
    /// </summary>
    public async Task RemoveConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;

        await _socketLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var key in new[] { connectionId, $"{connectionId}:watchlist" })
            {
                if (!_stocksByConnection.TryGetValue(key, out var subscribedStocks) || subscribedStocks.Count == 0)
                {
                    _stocksByConnection.Remove(key);
                    continue;
                }

                var codes = subscribedStocks.ToList();
                foreach (var code in codes)
                {
                    await RemoveSubscriptionLockedAsync(code, key, "연결 종료", cancellationToken);
                }

                _stocksByConnection.Remove(key);
            }
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private async Task RemoveSubscriptionLockedAsync(
        string code,
        string connectionId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (_stocksByConnection.TryGetValue(connectionId, out var connStocks))
        {
            connStocks.Remove(code);
            if (connStocks.Count == 0)
            {
                _stocksByConnection.Remove(connectionId);
            }
        }

        if (!_subscribersByStock.TryGetValue(code, out var subscribers) || !subscribers.Remove(connectionId))
        {
            return;
        }

        var nextCount = subscribers.Count;
        var reasonPrefix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"({reason}) ";
        if (nextCount <= 0)
        {
            _subscribersByStock.Remove(code);
            _logger.LogInformation("[실시간 호가] {Reason}종목({StockCode}) 열람자 0명 -> KIS 등록 해제 전송", reasonPrefix, code);
            await SendSubscriptionPacketAsync(code, "2", cancellationToken);
        }
        else
        {
            _logger.LogInformation("[실시간 호가] {Reason}종목({StockCode}) 열람자 수 감소: {Count}명 (등록 유지)", reasonPrefix, code, nextCount);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_webSocket != null && _webSocket.State == WebSocketState.Open) return;

        _approvalKey = await _sessionService.GetWebSocketApprovalKeyAsync(cancellationToken);
        _webSocket = new ClientWebSocket();

        _logger.LogInformation("{Prefix} KIS 실시간 WebSocket 연결 시도 ({Url})", LogConstants.KisApiPrefix, _settings.WebSocketKey);
        await _webSocket.ConnectAsync(new Uri(_settings.WebSocketKey), cancellationToken);
        _logger.LogInformation("{Prefix} KIS 실시간 WebSocket 연결 성공", LogConstants.KisApiPrefix);

        _receiveCts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_webSocket, _receiveCts.Token));
    }

    private async Task SendSubscriptionPacketAsync(string stockCode, string trType, CancellationToken cancellationToken)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

        var masterItem = await _masterStore.GetByCodeAsync(stockCode, cancellationToken);
        if (masterItem == null)
        {
            _logger.LogWarning("{Prefix} 마스터에서 종목을 찾을 수 없습니다: {StockCode}", LogConstants.KisApiPrefix, stockCode);
            return;
        }

        var isOverseas = masterItem.MarketType == "USA";
        var trId = isOverseas ? "HDFSASP0" : "H0STASP0";
        var trKey = isOverseas ? $"D{masterItem.ExchangeCode}{masterItem.ShortCode}" : masterItem.ShortCode;

        var payload = new
        {
            header = new
            {
                approval_key = _approvalKey,
                custtype = "P",
                tr_type = trType,
                content_type = "utf-8"
            },
            body = new
            {
                input = new
                {
                    tr_id = trId,
                    tr_key = trKey
                }
            }
        };

        var json = JsonSerializer.Serialize(payload).Replace("content_type", "content-type");
        var bytes = Encoding.UTF8.GetBytes(json);

        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        _logger.LogInformation("{Prefix} 실시간 호가 {Action} 전송 (TR_ID: {TrId}, TR_KEY: {TrKey})",
            LogConstants.KisApiPrefix, trType == "1" ? "등록" : "해제", trId, trKey);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken stoppingToken)
    {
        var buffer = new byte[16384];
        var builder = new StringBuilder();

        try
        {
            while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                builder.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("{Prefix} KIS WebSocket 연결 종료 수신", LogConstants.KisApiPrefix);
                        return;
                    }
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                await ProcessMessageAsync(ws, builder.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            // 서버 종료 시 정상 취소
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} WebSocket 수신 루프 오류: {Message}", LogConstants.KisApiPrefix, ex.Message);
        }
    }

    private async Task ProcessMessageAsync(ClientWebSocket ws, string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage)) return;

        if (rawMessage.StartsWith('{'))
        {
            if (rawMessage.Contains("PINGPONG", StringComparison.OrdinalIgnoreCase))
            {
                var pongBytes = Encoding.UTF8.GetBytes(rawMessage);
                await ws.SendAsync(new ArraySegment<byte>(pongBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                return;
            }

            _logger.LogInformation("{Prefix} WebSocket 제어 응답: {Message}", LogConstants.KisApiPrefix, rawMessage);
            return;
        }

        // 실시간 데이터 포맷: 암호화여부|TR_ID|데이터건수|본문
        var parts = rawMessage.Split('|');
        if (parts.Length < 4) return;

        var trId = parts[1];
        var body = parts[3];

        RealtimeStockStatus? status = trId switch
        {
            "H0STASP0" => ParseDomesticOrderBook(body),
            "HDFSASP0" => ParseOverseasOrderBook(body),
            _ => null
        };

        if (status != null)
        {
            _realtimeStockCache[status.StockCode] = new CachedOrderBook
            {
                Data = status,
                UpdatedAt = DateTime.UtcNow
            };

            var topPrice = status.Bids.FirstOrDefault()?.Price ?? status.Asks.FirstOrDefault()?.Price ?? 0m;
            if (topPrice > 0)
            {
                _masterStore.UpdatePrice(status.StockCode, topPrice);
            }

            await _hubContext.Clients
                .Group($"stock:{status.StockCode}")
                .SendAsync("ReceiveRealtimeStock", status);

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var consumer = scope.ServiceProvider.GetService<IRealtimeOrderBookConsumer>();
                if (consumer != null)
                {
                    await consumer.OnOrderBookReceivedAsync(status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[체결 매칭 오류] 종목({StockCode}) 호가 틱 처리 중 오류 발생", status.StockCode);
            }
        }
    }

    private RealtimeStockStatus? ParseDomesticOrderBook(string body)
    {
        var fields = body.Split('^');
        if (fields.Length < 45) return null;

        var stockCode = fields[0].Trim();
        var updatedTime = FormatHhMmSs(fields[1]);
        var master = _masterStore.TryGetCachedByCode(stockCode);

        var asks = new List<OrderBookLevel>(10);
        var bids = new List<OrderBookLevel>(10);

        for (int i = 0; i < 10; i++)
        {
            var askPrice = ParseDecimal(fields[3 + i]);
            var askQty = ParseLong(fields[23 + i]);
            if (askPrice > 0) asks.Add(new OrderBookLevel(askPrice, askQty));

            var bidPrice = ParseDecimal(fields[13 + i]);
            var bidQty = ParseLong(fields[33 + i]);
            if (bidPrice > 0) bids.Add(new OrderBookLevel(bidPrice, bidQty));
        }

        return new RealtimeStockStatus(
            StockCode: stockCode,
            MarketType: master?.MarketType ?? "KOSPI",
            ExchangeCode: master?.ExchangeCode ?? "KRX",
            Currency: "KRW",
            UpdatedTime: updatedTime,
            Asks: asks,
            Bids: bids,
            TotalAskSize: ParseLong(fields[43]),
            TotalBidSize: ParseLong(fields[44])
        );
    }

    private RealtimeStockStatus? ParseOverseasOrderBook(string body)
    {
        var fields = body.Split('^');
        if (fields.Length < 15) return null;

        var symbol = fields[1].Trim();
        var updatedTime = FormatHhMmSs(fields[6]);
        var master = _masterStore.TryGetCachedByCode(symbol);

        var bidPrice = ParseDecimal(fields[11]);
        var askPrice = ParseDecimal(fields[12]);
        var bidQty = ParseLong(fields[13]);
        var askQty = ParseLong(fields[14]);

        var asks = askPrice > 0 ? new List<OrderBookLevel> { new(askPrice, askQty) } : new List<OrderBookLevel>();
        var bids = bidPrice > 0 ? new List<OrderBookLevel> { new(bidPrice, bidQty) } : new List<OrderBookLevel>();

        return new RealtimeStockStatus(
            StockCode: symbol,
            MarketType: master?.MarketType ?? "USA",
            ExchangeCode: master?.ExchangeCode ?? "NAS",
            Currency: "USD",
            UpdatedTime: updatedTime,
            Asks: asks,
            Bids: bids,
            TotalAskSize: ParseLong(fields[8]),
            TotalBidSize: ParseLong(fields[7])
        );
    }

    private static string FormatHhMmSs(string raw)
        => raw.Length == 6 ? $"{raw[..2]}:{raw[2..4]}:{raw[4..6]}" : raw;

    private static decimal ParseDecimal(string value)
        => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var res) ? res : 0m;

    private static long ParseLong(string value)
        => long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var res) ? res : 0L;
}

