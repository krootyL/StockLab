using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using StockLab.Dtos;
using StockLab.Persistence;
using StockLab.Persistence.Entities;
using StockLab.Services.Kis;
using StockLab.Services.Master;
using StockLab.Services.Trading;

namespace StockLab.BackgroundServices;

/// <summary>
/// 관리자 스위치로 제어되는 강제 체결 워커.
/// 활성화 시 미체결 종목을 순회하며 종가 ±1% 범위 내에서 초당 3건의 소량 가상 호가 공급을 생성해 점진적으로 체결시킵니다.
/// </summary>
public class ForcedMatchingService : BackgroundService
{
    private static readonly TimeSpan SupplyInterval = TimeSpan.FromMilliseconds(333);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(500);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PendingList _pendingList;
    private readonly StockMasterStore _masterStore;
    private readonly ILogger<ForcedMatchingService> _logger;
    private readonly ConcurrentDictionary<string, decimal> _basePriceCache = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _enabled;
    private int _roundRobinIndex;

    public bool IsEnabled => _enabled;

    public ForcedMatchingService(
        IServiceScopeFactory scopeFactory,
        PendingList pendingList,
        StockMasterStore masterStore,
        ILogger<ForcedMatchingService> logger)
    {
        _scopeFactory = scopeFactory;
        _pendingList = pendingList;
        _masterStore = masterStore;
        _logger = logger;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        _logger.LogInformation("[강제 체결 워커] 스위치가 {State} 상태로 변경되었습니다.", enabled ? "ON (종가 ±1%, 초당 3건)" : "OFF");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_enabled)
            {
                try
                {
                    await Task.Delay(IdleInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            try
            {
                await SupplyOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[강제 체결 워커] 가상 호가 공급 사이클 중 오류가 발생했습니다.");
            }

            try
            {
                await Task.Delay(SupplyInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SupplyOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<string> openStockCodes;
        if (_pendingList.IsInitialized)
        {
            openStockCodes = _pendingList.GetPendingStockCodes();
            if (openStockCodes.Count == 0)
            {
                return;
            }
        }
        else
        {
            await using var syncScope = _scopeFactory.CreateAsyncScope();
            var syncDb = syncScope.ServiceProvider.GetRequiredService<AppDbContext>();
            openStockCodes = await syncDb.Orders
                .AsNoTracking()
                .Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled)
                .Select(o => o.StockCode)
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync(ct);

            if (openStockCodes.Count == 0)
            {
                return;
            }
        }

        var idx = (uint)Interlocked.Increment(ref _roundRobinIndex) % (uint)openStockCodes.Count;
        var stockCode = openStockCodes[(int)idx];
        var master = _masterStore.GetByCode(stockCode);
        if (master == null)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var basePrice = await ResolveBasePriceAsync(master, scope.ServiceProvider, db, ct);
        if (basePrice <= 0)
        {
            return;
        }

        var orderBook = BuildVirtualOrderBook(master, basePrice);
        var matchingService = scope.ServiceProvider.GetRequiredService<OrderMatchingService>();
        await matchingService.MatchByOrderBookAsync(orderBook, ct);
    }

    private async Task<decimal> ResolveBasePriceAsync(
        StockMasterItem master,
        IServiceProvider sp,
        AppDbContext db,
        CancellationToken ct)
    {
        if (_basePriceCache.TryGetValue(master.ShortCode, out var cached) && cached > 0)
        {
            return cached;
        }

        decimal resolved = master.BasePrice;

        if (resolved <= 0)
        {
            try
            {
                var quoteService = sp.GetRequiredService<KisQuoteService>();
                var quote = await quoteService.GetQuoteAsync(master.ShortCode, ct);
                resolved = quote.BasePrice > 0 ? quote.BasePrice : quote.CurrentPrice;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "[강제 체결 워커] {StockCode} 종가 조회 실패로 주문가 폴백을 사용합니다.", master.ShortCode);
            }
        }

        if (resolved <= 0)
        {
            resolved = await db.Orders
                .AsNoTracking()
                .Where(o => o.StockCode == master.ShortCode && o.OrderPrice > 0)
                .OrderByDescending(o => o.OrderId)
                .Select(o => o.OrderPrice)
                .FirstOrDefaultAsync(ct);
        }

        if (resolved > 0)
        {
            _basePriceCache[master.ShortCode] = resolved;
        }

        return resolved;
    }

    private static RealtimeStockStatus BuildVirtualOrderBook(StockMasterItem master, decimal basePrice)
    {
        var minBound = RoundPrice(basePrice * 0.99m, master.Currency);
        var maxBound = RoundPrice(basePrice * 1.01m, master.Currency);

        // 종가의 -1% ~ +1% 범위에서 기준 호가 산출
        var ratio = (decimal)(Random.Shared.NextDouble() * 0.02 - 0.01);
        var midPrice = ClampPrice(RoundPrice(basePrice * (1m + ratio), master.Currency), minBound, maxBound);

        var asks = new List<OrderBookLevel>(3);
        var bids = new List<OrderBookLevel>(3);
        long totalAsk = 0;
        long totalBid = 0;

        for (var i = 0; i < 3; i++)
        {
            var askPrice = ClampPrice(RoundPrice(midPrice * (1m + i * 0.0005m), master.Currency), minBound, maxBound);
            var bidPrice = ClampPrice(RoundPrice(midPrice * (1m - i * 0.0005m), master.Currency), minBound, maxBound);

            // OrderMatchingService가 호가 잔량의 10%(최소 1주)를 한도로 체결하므로 10~30주 공급 시 1~3주씩 점진 체결됨
            var askQty = Random.Shared.Next(10, 31);
            var bidQty = Random.Shared.Next(10, 31);

            asks.Add(new OrderBookLevel(askPrice, askQty));
            bids.Add(new OrderBookLevel(bidPrice, bidQty));
            totalAsk += askQty;
            totalBid += bidQty;
        }

        return new RealtimeStockStatus(
            master.ShortCode,
            master.MarketType,
            string.IsNullOrWhiteSpace(master.ExchangeCode) ? "KRX" : master.ExchangeCode,
            master.Currency,
            DateTime.UtcNow.ToString("HHmmss"),
            asks,
            bids,
            totalAsk,
            totalBid);
    }

    private static decimal RoundPrice(decimal price, string currency)
    {
        var isUsd = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase);
        var rounded = Math.Round(price, isUsd ? 2 : 0, MidpointRounding.AwayFromZero);
        var minUnit = isUsd ? 0.01m : 1m;
        return Math.Max(minUnit, rounded);
    }

    private static decimal ClampPrice(decimal price, decimal min, decimal max)
        => Math.Clamp(price, min, Math.Max(min, max));
}

