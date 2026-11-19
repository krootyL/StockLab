using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StockLab.Dtos;
using StockLab.Hubs;
using StockLab.Persistence;
using StockLab.Persistence.Entities;
using StockLab.Services.Kis;

namespace StockLab.Services.Trading;

public class OrderMatchingService : IRealtimeOrderBookConsumer
{
    public Task OnOrderBookReceivedAsync(RealtimeStockStatus status, CancellationToken cancellationToken = default)
        => MatchByOrderBookAsync(status, cancellationToken);
    private const decimal TickLiquidityRatio = 0.10m;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StockLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly AppDbContext _db;
    private readonly PendingList _pendingList;
    private readonly IHubContext<StockQuoteHub> _hubContext;
    private readonly ILogger<OrderMatchingService> _logger;

    private sealed class TickLevelBudget
    {
        public decimal Price { get; init; }
        public long RemainingQuantity { get; set; }
    }

    public OrderMatchingService(
        AppDbContext db,
        PendingList pendingList,
        IHubContext<StockQuoteHub> hubContext,
        ILogger<OrderMatchingService> logger)
    {
        _db = db;
        _pendingList = pendingList;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// 실시간 호가 틱 수신 시 해당 호가 잔량의 10%를 이번 틱의 체결 한도로 새로 설정하고(덮어쓰기),
    /// 가격-시간 우선(FIFO) 원칙으로 미체결 주문을 체결합니다.
    /// </summary>
    public async Task MatchByOrderBookAsync(RealtimeStockStatus orderBook, CancellationToken ct = default)
    {
        _pendingList.RecordStockChecked(orderBook.StockCode);

        var askBudgets = orderBook.Asks
            .Where(a => a.Price > 0 && a.Quantity > 0)
            .OrderBy(a => a.Price)
            .Select(a => new TickLevelBudget
            {
                Price = a.Price,
                RemainingQuantity = Math.Max(1, (long)(a.Quantity * TickLiquidityRatio))
            })
            .ToList();

        var bidBudgets = orderBook.Bids
            .Where(b => b.Price > 0 && b.Quantity > 0)
            .OrderByDescending(b => b.Price)
            .Select(b => new TickLevelBudget
            {
                Price = b.Price,
                RemainingQuantity = Math.Max(1, (long)(b.Quantity * TickLiquidityRatio))
            })
            .ToList();

        if (askBudgets.Count == 0 && bidBudgets.Count == 0)
        {
            return;
        }

        var stockLock = StockLocks.GetOrAdd(orderBook.StockCode, _ => new SemaphoreSlim(1, 1));
        await stockLock.WaitAsync(ct);

        try
        {
            // 트랜잭션 및 FOR UPDATE 3중 락 진입 전, 이번 호가로 실제 체결(또는 시장가 정산) 가능한 주문 존재 여부만 인메모리 경량 검사
            var bestAsk = askBudgets.Count > 0 ? askBudgets[0].Price : (decimal?)null;
            var bestBid = bidBudgets.Count > 0 ? bidBudgets[0].Price : (decimal?)null;

            if (!_pendingList.HasMatchableOrders(orderBook.StockCode, bestAsk, bestBid))
            {
                return;
            }

            // 동일 DbContext 재사용 시 이전 트랜잭션의 stale xmin이 Identity Resolution으로 재사용되는 것을 방지
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var openOrders = await _db.Orders
                .FromSqlInterpolated(
                    $"SELECT * FROM orders WHERE \"StockCode\" = {orderBook.StockCode} AND \"Status\" IN ({(int)OrderStatus.Pending}, {(int)OrderStatus.PartiallyFilled}) FOR UPDATE")
                .ToListAsync(ct);

            if (openOrders.Count == 0)
            {
                return;
            }

            // 교착 상태(Deadlock 40P01) 방지: 다중 종목 병렬 체결 시 ScenarioId 오름차순으로 지갑/보유종목 일괄 선점
            var scenarioIds = openOrders.Select(o => o.ScenarioId).Distinct().OrderBy(id => id).ToArray();
            var lockedWallets = await _db.Wallets
                .FromSqlInterpolated(
                    $"SELECT *, xmin FROM wallets WHERE \"ScenarioId\" = ANY({scenarioIds}) AND \"Currency\" = {orderBook.Currency} ORDER BY \"ScenarioId\" FOR UPDATE")
                .ToListAsync(ct);

            var lockedHoldings = await _db.Stocks
                .FromSqlInterpolated(
                    $"SELECT *, xmin FROM stocks WHERE \"ScenarioId\" = ANY({scenarioIds}) AND \"StockCode\" = {orderBook.StockCode} ORDER BY \"ScenarioId\" FOR UPDATE")
                .ToListAsync(ct);

            var walletsByScenario = lockedWallets.ToDictionary(w => w.ScenarioId);
            var holdingsByScenario = lockedHoldings.ToDictionary(s => s.ScenarioId);

            var buyOrders = openOrders
                .Where(o => o.Side == OrderSide.Buy)
                .OrderBy(o => o.OrderType)
                .ThenByDescending(o => o.OrderPrice)
                .ThenBy(o => o.OrderedAtUtc)
                .ToList();

            var sellOrders = openOrders
                .Where(o => o.Side == OrderSide.Sell)
                .OrderBy(o => o.OrderType)
                .ThenBy(o => o.OrderPrice)
                .ThenBy(o => o.OrderedAtUtc)
                .ToList();

            var createdTrades = new List<(long Uid, Trade Entity)>();
            var now = DateTime.UtcNow;
            var stateChanged = false;

            // 루프 내부에서 .Local / .Add 호출마다 EF Core DetectChanges()가 반복 실행되는 O(N^2) 오버헤드 차단
            _db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (askBudgets.Count > 0 && buyOrders.Count > 0)
                {
                    if (await ProcessBuyOrdersAsync(buyOrders, askBudgets, orderBook.Currency, walletsByScenario, holdingsByScenario, now, createdTrades, ct))
                        stateChanged = true;
                }

                if (bidBudgets.Count > 0 && sellOrders.Count > 0)
                {
                    if (await ProcessSellOrdersAsync(sellOrders, bidBudgets, orderBook.Currency, walletsByScenario, holdingsByScenario, now, createdTrades, ct))
                        stateChanged = true;
                }
            }
            finally
            {
                _db.ChangeTracker.AutoDetectChangesEnabled = true;
            }

            if (!stateChanged)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _pendingList.ApplyOrders(buyOrders.Concat(sellOrders));

            foreach (var (uid, tradeEntity) in createdTrades)
            {
                var trade = new TradeDto(
                    tradeEntity.Id,
                    tradeEntity.OrderId,
                    tradeEntity.ScenarioId,
                    tradeEntity.StockCode,
                    tradeEntity.Side,
                    tradeEntity.ExecutedPrice,
                    tradeEntity.ExecutedQuantity,
                    tradeEntity.TotalAmount,
                    tradeEntity.ExecutedAtUtc);

                _logger.LogInformation(
                    "[체결됨] TradeId={TradeId}, OrderId={OrderId}, UID={Uid}, Stock={Stock}, Side={Side}, Price={Price}, Qty={Qty}",
                    trade.TradeId, trade.OrderId, uid, trade.StockCode, trade.Side, trade.ExecutedPrice, trade.ExecutedQuantity);

                await _hubContext.Clients
                    .Group($"stock:{trade.StockCode}")
                    .SendAsync("ReceiveOrderFilled", trade, ct);
            }

            var updatedOrderIds = createdTrades.Select(x => x.Entity.OrderId)
                .Concat(buyOrders.Where(o => o.Status == OrderStatus.Rejected).Select(o => o.OrderId))
                .Distinct()
                .ToArray();

            if (updatedOrderIds.Length > 0)
            {
                await _hubContext.Clients
                    .Group("orders:all")
                    .SendAsync("ReceiveOrderUpdated", new
                    {
                        eventType = "Matched",
                        stockCode = orderBook.StockCode,
                        updatedOrderIds,
                        tradeCount = createdTrades.Count,
                        updatedAtUtc = now
                    }, ct);
            }
        }
        finally
        {
            stockLock.Release();
        }
    }

    private async Task<bool> ProcessBuyOrdersAsync(
        List<Order> buyOrders,
        List<TickLevelBudget> askBudgets,
        string currency,
        Dictionary<long, Wallet> walletsByScenario,
        Dictionary<long, Stocks> holdingsByScenario,
        DateTime now,
        List<(long Uid, Trade Entity)> createdTrades,
        CancellationToken ct)
    {
        var anyChanged = false;

        var partiallyFilledOrderIds = buyOrders
            .Where(o => o.FilledQuantity > 0)
            .Select(o => o.OrderId)
            .ToArray();

        var spentByOrderId = partiallyFilledOrderIds.Length > 0
            ? await _db.Trades
                .AsNoTracking()
                .Where(t => partiallyFilledOrderIds.Contains(t.OrderId))
                .GroupBy(t => t.OrderId)
                .Select(g => new { OrderId = g.Key, TotalSpent = g.Sum(t => t.TotalAmount) })
                .ToDictionaryAsync(x => x.OrderId, x => x.TotalSpent, ct)
            : new Dictionary<long, decimal>();

        foreach (var order in buyOrders)
        {
            var remainingOrderQty = order.Quantity - order.FilledQuantity;
            if (remainingOrderQty <= 0) continue;

            if (!walletsByScenario.TryGetValue(order.ScenarioId, out var wallet))
            {
                wallet = await _db.Wallets
                    .FromSqlInterpolated(
                        $"SELECT *, xmin FROM wallets WHERE \"ScenarioId\" = {order.ScenarioId} AND \"Currency\" = {currency} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);

                if (wallet == null) continue;
                walletsByScenario[order.ScenarioId] = wallet;
            }

            holdingsByScenario.TryGetValue(order.ScenarioId, out var holding);

            // 주문 요청 시점에 묶인 총 예수금(order.TotalAmount)에서 기체결 누적액을 제외한 잔여 예약금만 사용
            // (체결 중 가용금액 AvailableCash에서 추가 인출하지 않음)
            var spentSoFar = spentByOrderId.GetValueOrDefault(order.OrderId, 0m);
            var orderReservedRemaining = Math.Max(0m, Math.Min(wallet.PendingCash, order.TotalAmount - spentSoFar));

            if (order.OrderType == OrderType.Market && askBudgets.Count > 0 && askBudgets[0].Price > 0)
            {
                var bestAskPrice = askBudgets[0].Price;
                if (order.OrderPrice != bestAskPrice)
                {
                    order.OrderPrice = bestAskPrice;
                    anyChanged = true;
                }
            }

            var insufficientCashForMarket = false;

            foreach (var ask in askBudgets)
            {
                if (remainingOrderQty <= 0) break;
                if (ask.RemainingQuantity <= 0) continue;
                if (order.OrderType == OrderType.Limit && ask.Price > order.OrderPrice) break;

                var fillQty = Math.Min(remainingOrderQty, ask.RemainingQuantity);
                var actualCost = ask.Price * fillQty;

                if (actualCost > orderReservedRemaining)
                {
                    var maxAffordableQty = (long)(orderReservedRemaining / ask.Price);
                    if (maxAffordableQty <= 0)
                    {
                        if (order.OrderType == OrderType.Market)
                            insufficientCashForMarket = true;
                        break;
                    }

                    fillQty = Math.Min(fillQty, maxAffordableQty);
                    actualCost = ask.Price * fillQty;

                    if (order.OrderType == OrderType.Market)
                        insufficientCashForMarket = true;
                }

                wallet.PendingCash -= actualCost;
                orderReservedRemaining -= actualCost;
                wallet.UpdatedAtUtc = now;

                if (holding == null)
                {
                    holding = new Stocks
                    {
                        ScenarioId = order.ScenarioId,
                        StockCode = order.StockCode,
                        Quantity = fillQty,
                        ReservedQuantity = 0,
                        AverageBuyPrice = ask.Price,
                        UpdatedAtUtc = now
                    };
                    _db.Stocks.Add(holding);
                    holdingsByScenario[order.ScenarioId] = holding;
                }
                else
                {
                    var prevTotalCost = holding.AverageBuyPrice * holding.Quantity;
                    var nextQty = holding.Quantity + fillQty;
                    holding.AverageBuyPrice = nextQty > 0 ? (prevTotalCost + actualCost) / nextQty : 0m;
                    holding.Quantity = nextQty;
                    holding.UpdatedAtUtc = now;
                }

                var trade = new Trade
                {
                    OrderId = order.OrderId,
                    ScenarioId = order.ScenarioId,
                    StockCode = order.StockCode,
                    Side = OrderSide.Buy,
                    ExecutedPrice = ask.Price,
                    ExecutedQuantity = fillQty,
                    TotalAmount = actualCost,
                    ExecutedAtUtc = now
                };
                _db.Trades.Add(trade);

                order.FilledQuantity += fillQty;
                order.Status = order.FilledQuantity >= order.Quantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

                ask.RemainingQuantity -= fillQty;
                remainingOrderQty -= fillQty;
                anyChanged = true;

                createdTrades.Add((order.Uid, trade));

                if (insufficientCashForMarket)
                    break;
            }

            if (order.Status == OrderStatus.Filled && orderReservedRemaining > 0)
            {
                var refund = Math.Min(wallet.PendingCash, orderReservedRemaining);
                if (refund > 0)
                {
                    wallet.PendingCash -= refund;
                    wallet.AvailableCash += refund;
                    wallet.UpdatedAtUtc = now;
                    anyChanged = true;
                }
            }
            else if (order.OrderType == OrderType.Market && remainingOrderQty > 0 && insufficientCashForMarket)
            {
                order.Status = OrderStatus.Rejected;
                var refund = Math.Min(wallet.PendingCash, orderReservedRemaining);
                if (refund > 0)
                {
                    wallet.PendingCash -= refund;
                    wallet.AvailableCash += refund;
                    wallet.UpdatedAtUtc = now;
                }
                anyChanged = true;

                _logger.LogInformation(
                    "[거절됨] OrderId={OrderId}, Stock={Stock}, Filled={Filled}/{Qty}, Refunded={Refund}",
                    order.OrderId, order.StockCode, order.FilledQuantity, order.Quantity, refund);
            }
        }

        return anyChanged;
    }

    private async Task<bool> ProcessSellOrdersAsync(
        List<Order> sellOrders,
        List<TickLevelBudget> bidBudgets,
        string currency,
        Dictionary<long, Wallet> walletsByScenario,
        Dictionary<long, Stocks> holdingsByScenario,
        DateTime now,
        List<(long Uid, Trade Entity)> createdTrades,
        CancellationToken ct)
    {
        var anyChanged = false;

        foreach (var order in sellOrders)
        {
            var remainingOrderQty = order.Quantity - order.FilledQuantity;
            if (remainingOrderQty <= 0) continue;

            if (!walletsByScenario.TryGetValue(order.ScenarioId, out var wallet))
            {
                wallet = await _db.Wallets
                    .FromSqlInterpolated(
                        $"SELECT *, xmin FROM wallets WHERE \"ScenarioId\" = {order.ScenarioId} AND \"Currency\" = {currency} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);

                if (wallet != null)
                {
                    walletsByScenario[order.ScenarioId] = wallet;
                }
            }

            if (!holdingsByScenario.TryGetValue(order.ScenarioId, out var holding))
            {
                holding = await _db.Stocks
                    .FromSqlInterpolated(
                        $"SELECT *, xmin FROM stocks WHERE \"ScenarioId\" = {order.ScenarioId} AND \"StockCode\" = {order.StockCode} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);

                if (holding != null)
                {
                    holdingsByScenario[order.ScenarioId] = holding;
                }
            }

            if (wallet == null || holding == null) continue;

            if (order.OrderType == OrderType.Market && bidBudgets.Count > 0 && bidBudgets[0].Price > 0)
            {
                var bestBidPrice = bidBudgets[0].Price;
                if (order.OrderPrice != bestBidPrice)
                {
                    order.OrderPrice = bestBidPrice;
                    order.TotalAmount = bestBidPrice * order.Quantity;
                    anyChanged = true;
                }
            }

            foreach (var bid in bidBudgets)
            {
                if (remainingOrderQty <= 0) break;
                if (bid.RemainingQuantity <= 0) continue;
                if (order.OrderType == OrderType.Limit && bid.Price < order.OrderPrice) break;

                var fillQty = Math.Min(remainingOrderQty, Math.Min(bid.RemainingQuantity, holding.ReservedQuantity));
                if (fillQty <= 0) break;

                var proceedAmount = bid.Price * fillQty;

                holding.Quantity -= fillQty;
                holding.ReservedQuantity -= fillQty;
                holding.UpdatedAtUtc = now;

                wallet.AvailableCash += proceedAmount;
                wallet.UpdatedAtUtc = now;

                var trade = new Trade
                {
                    OrderId = order.OrderId,
                    ScenarioId = order.ScenarioId,
                    StockCode = order.StockCode,
                    Side = OrderSide.Sell,
                    ExecutedPrice = bid.Price,
                    ExecutedQuantity = fillQty,
                    TotalAmount = proceedAmount,
                    ExecutedAtUtc = now
                };
                _db.Trades.Add(trade);

                order.FilledQuantity += fillQty;
                order.Status = order.FilledQuantity >= order.Quantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

                bid.RemainingQuantity -= fillQty;
                remainingOrderQty -= fillQty;
                anyChanged = true;

                createdTrades.Add((order.Uid, trade));
            }
        }

        return anyChanged;
    }
}

