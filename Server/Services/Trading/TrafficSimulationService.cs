using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Persistence;
using StockLab.Persistence.Entities;
using StockLab.Services.Auth;
using StockLab.Services.Account;
using StockLab.Services.Kis;
using StockLab.Services.Master;

namespace StockLab.Services.Trading;

public record SimulateTrafficRequest(
    int OrderCount = 100,
    bool CleanupAfter = true
);

public record SimulateTrafficResult(
    int RequestedOrderCount,
    int PlacedOrderCount,
    int TotalTradesExecuted,
    long TotalExecutedShares,
    int FilledCount,
    int RejectedCount,
    int CancelledCount,
    int PartiallyFilledCount,
    int PendingCount,
    double ElapsedMs,
    bool CleanedUp
);

/// <summary>
/// 개발/테스트 환경에서 지정 건수의 가상 주문 및 모의 호가 틱을 생성해 실제 체결 엔진을 검증하고,
/// 종료 시 임시 더미 데이터를 DB에서 깨끗하게 정리하는 시뮬레이션 서비스.
/// </summary>
public class TrafficSimulationService
{
    private const int MajorStockSampleCount = 7;
    private const int MinorStockSampleCount = 3;

    private readonly AppDbContext _db;
    private readonly PendingList _pendingList;
    private readonly GuestAuthService _guestAuthService;
    private readonly AccountService _accountService;
    private readonly OrderService _orderService;
    private readonly OrderMatchingService _matchingService;
    private readonly KisRankingService _rankingService;
    private readonly StockMasterStore _masterStore;
    private readonly ILogger<TrafficSimulationService> _logger;

    public TrafficSimulationService(
        AppDbContext db,
        PendingList pendingList,
        GuestAuthService guestAuthService,
        AccountService accountService,
        OrderService orderService,
        OrderMatchingService matchingService,
        KisRankingService rankingService,
        StockMasterStore masterStore,
        ILogger<TrafficSimulationService> logger)
    {
        _db = db;
        _pendingList = pendingList;
        _guestAuthService = guestAuthService;
        _accountService = accountService;
        _orderService = orderService;
        _matchingService = matchingService;
        _rankingService = rankingService;
        _masterStore = masterStore;
        _logger = logger;
    }

    public async Task<SimulateTrafficResult> RunAsync(SimulateTrafficRequest request, CancellationToken ct = default)
    {
        var targetCount = Math.Clamp(request.OrderCount, 1, 5000);
        var sw = Stopwatch.StartNew();

        var stocks = await ResolveTargetStocksAsync(ct);
        if (stocks.Count == 0)
        {
            throw new StockLabException(ErrorCode.InvalidInput, "시뮬레이션에 사용할 종목 마스터 데이터가 없습니다.");
        }

        var userCount = Math.Clamp(targetCount / 20, 1, 5);
        var dummyUsers = new List<(long Uid, long ScenarioId)>();
        var batchTag = Guid.NewGuid().ToString("N")[..8];

        try
        {
            for (var u = 0; u < userCount; u++)
            {
                var profile = await _guestAuthService.CreateGuestSessionAsync($"__dummy_{batchTag}_{u}", ct);
                var uid = long.Parse(profile.Uid);
                var scenarioId = profile.Scenarios[0].ScenarioId;
                dummyUsers.Add((uid, scenarioId));

                await _accountService.AdjustBalanceAsync(scenarioId, "KRW", 200_000_000m, ct);
                await _accountService.AdjustBalanceAsync(scenarioId, "USD", 200_000m, ct);
            }

            var quotedBase = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var placedCount = 0;

            // 1. 시드 매수 물량 (매도 테스트용 보유수량 확보, 1인·종목당 충분한 수량 매수)
            var pairCount = Math.Max(1, userCount * stocks.Count);
            var seedTarget = Math.Min(targetCount, pairCount);
            var seedQty = Math.Max(20, (targetCount / pairCount) * 3);
            for (var i = 0; i < seedTarget; i++)
            {
                var user = dummyUsers[i % userCount];
                var stock = stocks[i % stocks.Count];
                var placed = await _orderService.PlaceOrderAsync(
                    user.Uid,
                    new PlaceOrderRequest(user.ScenarioId, stock.ShortCode, OrderSide.Buy, OrderType.Market, seedQty, null),
                    ct);
                quotedBase.TryAdd(stock.ShortCode, placed.OrderPrice);
                placedCount++;
            }

            // 시드 매수 즉시 체결 (10% 틱 유동성 한도를 고려해 10배 이상 잔량 공급)
            var seedTickQty = Math.Max(200, seedQty * userCount * 12);
            foreach (var stock in stocks)
            {
                if (!quotedBase.TryGetValue(stock.ShortCode, out var refPrice) || refPrice <= 0)
                    refPrice = stock.BasePrice > 0 ? stock.BasePrice : 10000m;

                await _matchingService.MatchByOrderBookAsync(BuildTick(stock, refPrice, refPrice * 0.99m, seedTickQty), ct);
            }

            // 2. 나머지 주문 생성 (지정가/시장가 매수·매도 + 일부 취소 대상)
            var rng = new Random(42);
            var cancelTargets = new List<(long Uid, long OrderId)>();
            var cancelTargetLimit = Math.Max(0, (targetCount - placedCount) / 15);
            var remainingToPlace = targetCount - placedCount;

            for (var i = 0; i < remainingToPlace; i++)
            {
                var user = dummyUsers[i % userCount];
                var stock = stocks[(i + i / userCount) % stocks.Count];
                var refPrice = quotedBase.GetValueOrDefault(stock.ShortCode, stock.BasePrice > 0 ? stock.BasePrice : 10000m);
                var roll = i % 10;

                if (cancelTargets.Count < cancelTargetLimit)
                {
                    var lowLimit = Math.Round(refPrice * 0.85m, 2);
                    var ord = await _orderService.PlaceOrderAsync(
                        user.Uid,
                        new PlaceOrderRequest(user.ScenarioId, stock.ShortCode, OrderSide.Buy, OrderType.Limit, 2, lowLimit),
                        ct);
                    cancelTargets.Add((user.Uid, ord.OrderId));
                    placedCount++;
                    continue;
                }

                if (roll < 3)
                {
                    var mult = new[] { 0.95m, 1.00m, 1.02m, 1.05m }[rng.Next(0, 4)];
                    await _orderService.PlaceOrderAsync(
                        user.Uid,
                        new PlaceOrderRequest(user.ScenarioId, stock.ShortCode, OrderSide.Buy, OrderType.Limit, rng.Next(1, 5), Math.Round(refPrice * mult, 2)),
                        ct);
                }
                else if (roll < 5)
                {
                    await _orderService.PlaceOrderAsync(
                        user.Uid,
                        new PlaceOrderRequest(user.ScenarioId, stock.ShortCode, OrderSide.Buy, OrderType.Market, rng.Next(1, 5), null),
                        ct);
                }
                else if (roll < 8)
                {
                    var sellMult = new[] { 0.98m, 1.00m, 1.03m, 1.08m }[rng.Next(0, 4)];
                    try
                    {
                        await _orderService.PlaceOrderAsync(
                            user.Uid,
                            new PlaceOrderRequest(user.ScenarioId, stock.ShortCode, OrderSide.Sell, OrderType.Limit, rng.Next(1, 4), Math.Round(refPrice * sellMult, 2)),
                            ct);
                    }
                    catch (StockLabException)
                    {
                        await _orderService.PlaceOrderAsync(
                            user.Uid,
                            new PlaceOrderRequest(user.ScenarioId, stock.ShortCode, OrderSide.Buy, OrderType.Limit, rng.Next(1, 4), Math.Round(refPrice * 0.97m, 2)),
                            ct);
                    }
                }
                else
                {
                    try
                    {
                        await _orderService.PlaceOrderAsync(
                            user.Uid,
                            new PlaceOrderRequest(user.ScenarioId, stock.ShortCode, OrderSide.Sell, OrderType.Market, rng.Next(1, 4), null),
                            ct);
                    }
                    catch (StockLabException)
                    {
                        await _orderService.PlaceOrderAsync(
                            user.Uid,
                            new PlaceOrderRequest(user.ScenarioId, stock.ShortCode, OrderSide.Buy, OrderType.Market, 2, null),
                            ct);
                    }
                }

                placedCount++;
            }

            foreach (var (uid, orderId) in cancelTargets)
            {
                await _orderService.CancelOrderAsync(uid, orderId, ct);
            }

            // 3. 호가 변동 틱 주입 (+12%, -3%, +18%)
            var tickRounds = new (decimal AskMult, decimal BidMult, long Qty)[]
            {
                (1.12m, 1.01m, Math.Max(20, targetCount / 6)),
                (0.97m, 1.02m, Math.Max(30, targetCount / 3)),
                (1.18m, 0.99m, Math.Max(50, targetCount / 2))
            };

            foreach (var (askMult, bidMult, qty) in tickRounds)
            {
                foreach (var stock in stocks)
                {
                    var refPrice = quotedBase.GetValueOrDefault(stock.ShortCode, stock.BasePrice > 0 ? stock.BasePrice : 10000m);
                    await _matchingService.MatchByOrderBookAsync(
                        BuildTick(stock, Math.Round(refPrice * askMult, 2), Math.Round(refPrice * bidMult, 2), qty),
                        ct);
                }
            }

            // 4. 결과 집계
            var uids = dummyUsers.Select(d => d.Uid).ToArray();
            var orderStats = await _db.Orders
                .AsNoTracking()
                .Where(o => uids.Contains(o.Uid))
                .GroupBy(o => o.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var scenarioIds = dummyUsers.Select(d => d.ScenarioId).ToArray();
            var tradeStats = await _db.Trades
                .AsNoTracking()
                .Where(t => scenarioIds.Contains(t.ScenarioId))
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), Shares = g.Sum(t => t.ExecutedQuantity) })
                .FirstOrDefaultAsync(ct);

            sw.Stop();

            int CountOf(OrderStatus status) => orderStats.FirstOrDefault(s => s.Status == status)?.Count ?? 0;

            return new SimulateTrafficResult(
                RequestedOrderCount: targetCount,
                PlacedOrderCount: placedCount,
                TotalTradesExecuted: tradeStats?.Count ?? 0,
                TotalExecutedShares: tradeStats?.Shares ?? 0L,
                FilledCount: CountOf(OrderStatus.Filled),
                RejectedCount: CountOf(OrderStatus.Rejected),
                CancelledCount: CountOf(OrderStatus.Cancelled),
                PartiallyFilledCount: CountOf(OrderStatus.PartiallyFilled),
                PendingCount: CountOf(OrderStatus.Pending),
                ElapsedMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                CleanedUp: request.CleanupAfter);
        }
        finally
        {
            if (request.CleanupAfter && dummyUsers.Count > 0)
            {
                await CleanupDummyDataAsync(dummyUsers);
            }
        }
    }

    private async Task<List<StockMasterItem>> ResolveTargetStocksAsync(CancellationToken ct)
    {
        var result = new List<StockMasterItem>();
        var top20Codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 거래량 기준 캐시 풀에서 메이저 후보군 조회 후 5종목 무작위 추출
            var top20 = await _rankingService.GetAllVolumeRankPoolAsync(isValueSort: false, ct);
            var majorCandidates = new List<StockMasterItem>();

            foreach (var item in top20)
            {
                top20Codes.Add(item.StockCode);
                var master = _masterStore.GetByCode(item.StockCode);
                if (master != null && !majorCandidates.Any(m => m.ShortCode.Equals(master.ShortCode, StringComparison.OrdinalIgnoreCase)))
                {
                    majorCandidates.Add(master);
                }
            }

            for (var i = 0; i < Math.Min(MajorStockSampleCount, majorCandidates.Count); i++)
            {
                var swapIdx = Random.Shared.Next(i, majorCandidates.Count);
                (majorCandidates[i], majorCandidates[swapIdx]) = (majorCandidates[swapIdx], majorCandidates[i]);
                result.Add(majorCandidates[i]);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[더미 트래픽] 거래량 Top 20 조회 실패로 마스터 랜덤 종목으로 대체합니다.");
        }

        if (result.Count < MajorStockSampleCount)
        {
            var fallbackExcluded = result.Select(s => s.ShortCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.AddRange(_masterStore.GetRandomStocks(MajorStockSampleCount - result.Count, fallbackExcluded));
        }

        // Top 20을 제외한 나머지 마스터에서 비주류 5종목 추출
        var minorExcluded = new HashSet<string>(top20Codes, StringComparer.OrdinalIgnoreCase);
        foreach (var s in result) minorExcluded.Add(s.ShortCode);
        result.AddRange(_masterStore.GetRandomStocks(MinorStockSampleCount, minorExcluded));

        if (result.Count == 0)
        {
            result.AddRange(_masterStore.GetByMarket("KOSPI", 5));
        }

        return result;
    }

    private static RealtimeStockStatus BuildTick(StockMasterItem stock, decimal askPrice, decimal bidPrice, long qty)
    {
        return new RealtimeStockStatus(
            StockCode: stock.ShortCode,
            MarketType: stock.MarketType,
            ExchangeCode: stock.ExchangeCode,
            Currency: stock.Currency,
            UpdatedTime: DateTime.UtcNow.ToString("HHmmss"),
            Asks: [new OrderBookLevel(Math.Max(1m, askPrice), qty)],
            Bids: [new OrderBookLevel(Math.Max(1m, bidPrice), qty)],
            TotalAskSize: qty,
            TotalBidSize: qty);
    }

    private async Task CleanupDummyDataAsync(List<(long Uid, long ScenarioId)> dummyUsers)
    {
        try
        {
            var uids = dummyUsers.Select(d => d.Uid).ToArray();
            var scenarioIds = dummyUsers.Select(d => d.ScenarioId).ToArray();

            var dummyOpenOrderIds = await _db.Orders
                .AsNoTracking()
                .Where(o => uids.Contains(o.Uid) && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled))
                .Select(o => o.OrderId)
                .ToArrayAsync(CancellationToken.None);

            await _db.Trades.Where(t => scenarioIds.Contains(t.ScenarioId)).ExecuteDeleteAsync(CancellationToken.None);
            await _db.Orders.Where(o => uids.Contains(o.Uid)).ExecuteDeleteAsync(CancellationToken.None);
            _pendingList.RemoveOrders(dummyOpenOrderIds);
            await _db.Stocks.Where(s => scenarioIds.Contains(s.ScenarioId)).ExecuteDeleteAsync(CancellationToken.None);
            await _db.Wallets.Where(w => scenarioIds.Contains(w.ScenarioId)).ExecuteDeleteAsync(CancellationToken.None);
            await _db.Scenarios.Where(s => scenarioIds.Contains(s.Id)).ExecuteDeleteAsync(CancellationToken.None);
            await _db.Users.Where(u => uids.Contains(u.Uid)).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[더미 트래픽] 임시 데이터 정리 중 오류가 발생했습니다.");
        }
    }
}

