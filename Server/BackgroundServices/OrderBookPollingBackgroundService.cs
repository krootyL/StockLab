using Microsoft.EntityFrameworkCore;
using StockLab.Dtos;
using StockLab.Persistence;
using StockLab.Persistence.Entities;
using StockLab.Services.Kis;
using StockLab.Services.Master;
using StockLab.Services.Trading;

namespace StockLab.BackgroundServices;

/// <summary>
/// 24시간 상시 가동되며 시장이 열린 미체결 종목 중 가장 오래 미조회된 종목을
/// 0.5초 주기로 능동 선정하여 KIS REST 호가 조회 및 체결 엔진을 실행하는 워커.
/// </summary>
public class OrderBookPollingBackgroundService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MinStockCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PendingList _pendingList;
    private readonly StockMasterStore _masterStore;
    private readonly ILogger<OrderBookPollingBackgroundService> _logger;
    private (bool KrOpen, bool UsOpen)? _lastMarketState;

    public OrderBookPollingBackgroundService(
        IServiceScopeFactory scopeFactory,
        PendingList pendingList,
        StockMasterStore masterStore,
        ILogger<OrderBookPollingBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _pendingList = pendingList;
        _masterStore = masterStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[체결 폴링 워커] 24시간 능동 호가 스케줄러가 시작되었습니다. (주기: 0.5초)");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "[체결 폴링 워커] KIS 호가 조회 네트워크/유량 제한으로 잠시 대기합니다.");
                delay = RateLimitBackoff;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[체결 폴링 워커] 호가 폴링 사이클 중 예기치 않은 오류가 발생했습니다.");
                delay = RateLimitBackoff;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var currentMarketState = (
            KrOpen: MarketSessionHelper.IsMarketOpen("KR", now),
            UsOpen: MarketSessionHelper.IsMarketOpen("US", now)
        );

        var needsInitialSync = !_pendingList.IsInitialized;
        var marketSessionChanged = _lastMarketState.HasValue && _lastMarketState.Value != currentMarketState;

        if (needsInitialSync || marketSessionChanged)
        {
            await using var syncScope = _scopeFactory.CreateAsyncScope();
            var db = syncScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stockCount = await _pendingList.SyncFromDatabaseAsync(db, ct);
            var reason = needsInitialSync ? "초기 로드" : "장 개장/마감 전환";
            _logger.LogInformation(
                "[체결 폴링 워커] DB 미체결 주문 스냅샷 동기화 완료 (사유={Reason}, KR={KrOpen}, US={UsOpen}, 종목={StockCount})",
                reason, currentMarketState.KrOpen, currentMarketState.UsOpen, stockCount);
        }

        _lastMarketState = currentMarketState;

        var openStockStats = _pendingList.GetPendingStockSummaries();
        if (openStockStats.Count == 0)
        {
            return;
        }

        string? bestStockCode = null;
        var bestScore = double.MinValue;

        foreach (var item in openStockStats)
        {
            var master = _masterStore.GetByCode(item.StockCode);
            if (master == null || !MarketSessionHelper.IsMarketOpen(master.MarketType, now))
            {
                continue;
            }

            var lastChecked = item.LastCheckedUtc;
            var elapsedSeconds = lastChecked == DateTime.MinValue
                ? 86400.0
                : Math.Max(0.0, (now - lastChecked).TotalSeconds);

            // 각 종목별 최소 5초 쿨다운(캐싱) 유지: 최근 5초 이내 조회된 종목은 이번 0.5초 사이클에서 제외
            if (lastChecked != DateTime.MinValue && elapsedSeconds < MinStockCooldown.TotalSeconds)
            {
                continue;
            }

            var score = elapsedSeconds
                + (item.HasMarketOrder ? 30.0 : 0.0)
                + Math.Min(item.OpenCount, 20) * 0.5;

            if (score > bestScore)
            {
                bestScore = score;
                bestStockCode = item.StockCode;
            }
        }

        if (bestStockCode == null)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var quoteService = scope.ServiceProvider.GetRequiredService<KisQuoteService>();
        RealtimeStockStatus? orderBook;
        try
        {
            orderBook = await quoteService.GetOrderBookAsync(bestStockCode, ct);
        }
        finally
        {
            _pendingList.RecordStockChecked(bestStockCode);
        }

        if (orderBook == null)
        {
            return;
        }

        var matchingService = scope.ServiceProvider.GetRequiredService<OrderMatchingService>();
        await matchingService.MatchByOrderBookAsync(orderBook, ct);
    }
}

