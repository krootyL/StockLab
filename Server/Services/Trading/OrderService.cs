using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StockLab.BackgroundServices;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Hubs;
using StockLab.Persistence;
using StockLab.Persistence.Entities;
using StockLab.Services.Kis;
using StockLab.Services.Master;

namespace StockLab.Services.Trading;

public class OrderService
{
    private readonly AppDbContext _db;
    private readonly PendingList _pendingList;
    private readonly StockMasterStore _masterStore;
    private readonly KisQuoteService _quoteService;
    private readonly KisSocketService _socketService;
    private readonly OrderMatchingService _matchingService;
    private readonly ForcedMatchingService _forcedMatchingService;
    private readonly IHubContext<StockQuoteHub> _hubContext;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        AppDbContext db,
        PendingList pendingList,
        StockMasterStore masterStore,
        KisQuoteService quoteService,
        KisSocketService socketService,
        OrderMatchingService matchingService,
        ForcedMatchingService forcedMatchingService,
        IHubContext<StockQuoteHub> hubContext,
        ILogger<OrderService> logger)
    {
        _db = db;
        _pendingList = pendingList;
        _masterStore = masterStore;
        _quoteService = quoteService;
        _socketService = socketService;
        _matchingService = matchingService;
        _forcedMatchingService = forcedMatchingService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<OrderDto> PlaceOrderAsync(long uid, PlaceOrderRequest request, CancellationToken ct = default)
    {
        if (request.Quantity <= 0)
        {
            throw new StockLabException(ErrorCode.InvalidInput, "주문 수량은 1주 이상이어야 합니다.");
        }

        var master = _masterStore.GetByCode(request.StockCode)
            ?? throw new StockLabException(ErrorCode.StockNotFound, $"종목코드 '{request.StockCode}'를 찾을 수 없습니다.");

        var userStrategies = await _db.Strategies
            .AsNoTracking()
            .Where(s => s.Uid == uid)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        if (userStrategies.Count == 0)
        {
            throw new StockLabException(
                ErrorCode.InvalidInput,
                "등록된 투자 전략이 없습니다. 거래는 투자 전략을 기반으로 이루어지므로 마이페이지에서 투자 전략을 먼저 생성해 주세요.");
        }

        if (request.StrategyId <= 0)
        {
            throw new StockLabException(
                ErrorCode.InvalidInput,
                "주문을 진행할 투자 전략을 선택해 주세요.");
        }

        var strategy = userStrategies.FirstOrDefault(s => s.Id == request.StrategyId);
        if (strategy == null)
        {
            throw new StockLabException(ErrorCode.Forbidden, "해당 투자 전략에 대한 접근 권한이 없거나 존재하지 않는 전략입니다.");
        }

        decimal resolvedPrice;
        if (request.OrderType == OrderType.Limit)
        {
            if (!request.OrderPrice.HasValue || request.OrderPrice.Value <= 0)
            {
                throw new StockLabException(ErrorCode.InvalidInput, "지정가 주문은 0보다 큰 주문 단가가 필요합니다.");
            }
            resolvedPrice = request.OrderPrice.Value;
        }
        else
        {
            resolvedPrice = await ResolveMarketReferencePriceAsync(master.ShortCode, request.Side, ct);
        }

        var totalAmount = resolvedPrice * request.Quantity;
        var now = DateTime.UtcNow;

        // 동일 DbContext 재사용 시 이전 트랜잭션의 stale xmin이 Identity Resolution으로 재사용되는 것을 방지
        _db.ChangeTracker.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (request.Side == OrderSide.Buy)
        {
            var wallet = await _db.Wallets
                .FromSqlInterpolated(
                    $"SELECT *, xmin FROM wallets WHERE \"StrategyId\" = {request.StrategyId} AND \"Currency\" = {master.Currency} FOR UPDATE")
                .FirstOrDefaultAsync(ct);

            if (wallet == null)
            {
                throw new StockLabException(ErrorCode.AccountNotFound, $"{master.Currency} 지갑을 찾을 수 없습니다.");
            }

            if (wallet.AvailableCash < totalAmount)
            {
                throw new StockLabException(
                    ErrorCode.InsufficientBalance,
                    $"예수가능금이 부족합니다. (필요 금액: {totalAmount:N4} {master.Currency}, 가용 금액: {wallet.AvailableCash:N4} {master.Currency})");
            }

            wallet.AvailableCash -= totalAmount;
            wallet.PendingCash += totalAmount;
            wallet.UpdatedAtUtc = now;
        }
        else
        {
            var holding = await _db.Stocks
                .FromSqlInterpolated(
                    $"SELECT *, xmin FROM stocks WHERE \"StrategyId\" = {request.StrategyId} AND \"StockCode\" = {master.ShortCode} FOR UPDATE")
                .FirstOrDefaultAsync(ct);

            var sellableQty = holding == null ? 0 : holding.Quantity - holding.ReservedQuantity;
            if (holding == null || sellableQty < request.Quantity)
            {
                throw new StockLabException(
                    ErrorCode.InsufficientBalance,
                    $"매도 가능 수량이 부족합니다. (요청: {request.Quantity}주, 매도 가능: {sellableQty}주)");
            }

            holding.ReservedQuantity += request.Quantity;
            holding.UpdatedAtUtc = now;
        }

        var order = new Order
        {
            StrategyId = request.StrategyId,
            Uid = uid,
            StockCode = master.ShortCode,
            Side = request.Side,
            OrderType = request.OrderType,
            Status = OrderStatus.Pending,
            OrderPrice = resolvedPrice,
            Quantity = request.Quantity,
            FilledQuantity = 0,
            TotalAmount = totalAmount,
            OrderedAtUtc = now
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _pendingList.UpsertOrder(order);

        _logger.LogInformation(
            "[주문 접수] OrderId={OrderId}, UID={Uid}, StrategyId={StrategyId}, Stock={Stock}, Side={Side}, Type={Type}, Price={Price}, Qty={Qty}",
            order.OrderId, uid, request.StrategyId, master.ShortCode, request.Side, request.OrderType, resolvedPrice, request.Quantity);

        var cachedOrderBook = _socketService.GetCachedOrderBook(master.ShortCode);
        if (cachedOrderBook != null)
        {
            await _matchingService.MatchByOrderBookAsync(cachedOrderBook, ct);
        }

        var refreshed = await _db.Orders
            .AsNoTracking()
            .FirstAsync(o => o.OrderId == order.OrderId, ct);

        return ToOrderDto(refreshed);
    }

    public Task<OrderDto> CancelOrderAsync(long uid, long orderId, CancellationToken ct = default)
        => CancelOrderCoreAsync(uid, orderId, ct);

    public Task<OrderDto> CancelOrderAsAdminAsync(long orderId, CancellationToken ct = default)
        => CancelOrderCoreAsync(null, orderId, ct);

    public Task<BulkCancelOrdersResult> CancelPendingOrdersAsAdminAsync(
        long? strategyId = null,
        string? stockCode = null,
        CancellationToken ct = default)
        => CancelOpenOrdersCoreAsync(null, strategyId, stockCode, ct);

    private async Task<BulkCancelOrdersResult> CancelOpenOrdersCoreAsync(
        long? expectedUid,
        long? strategyId,
        string? stockCode,
        CancellationToken ct)
    {
        var normalizedStock = string.IsNullOrWhiteSpace(stockCode) ? null : stockCode.Trim().ToUpperInvariant();

        var preCheckQuery = _db.Orders
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled);

        if (expectedUid.HasValue)
        {
            preCheckQuery = preCheckQuery.Where(o => o.Uid == expectedUid.Value);
        }

        if (strategyId.HasValue)
        {
            preCheckQuery = preCheckQuery.Where(o => o.StrategyId == strategyId.Value);
        }

        if (normalizedStock != null)
        {
            preCheckQuery = preCheckQuery.Where(o => o.StockCode == normalizedStock);
        }

        if (!await preCheckQuery.AnyAsync(ct))
        {
            return new BulkCancelOrdersResult(0, 0, new Dictionary<string, decimal>(), Array.Empty<long>());
        }

        _db.ChangeTracker.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var hasUidFilter = expectedUid.HasValue;
        var uidFilterValue = expectedUid ?? 0L;
        var hasStrategyFilter = strategyId.HasValue;
        var strategyFilterValue = strategyId ?? 0L;
        var hasStockFilter = normalizedStock != null;
        var stockFilterValue = normalizedStock ?? string.Empty;

        var targetOrders = await _db.Orders
            .FromSqlInterpolated($"""
                SELECT * FROM orders
                WHERE "Status" IN ({(int)OrderStatus.Pending}, {(int)OrderStatus.PartiallyFilled})
                  AND ({!hasUidFilter} OR "Uid" = {uidFilterValue})
                  AND ({!hasStrategyFilter} OR "StrategyId" = {strategyFilterValue})
                  AND ({!hasStockFilter} OR UPPER("StockCode") = {stockFilterValue})
                ORDER BY "OrderId"
                FOR UPDATE
                """)
            .ToListAsync(ct);

        if (targetOrders.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return new BulkCancelOrdersResult(0, 0, new Dictionary<string, decimal>(), Array.Empty<long>());
        }

        var buyOrders = targetOrders.Where(o => o.Side == OrderSide.Buy).ToList();
        var sellOrders = targetOrders.Where(o => o.Side == OrderSide.Sell).ToList();

        var walletsMap = new Dictionary<(long StrategyId, string Currency), Wallet>();
        if (buyOrders.Count > 0)
        {
            var buyStrategyIds = buyOrders.Select(o => o.StrategyId).Distinct().OrderBy(id => id).ToArray();
            var buyCurrencies = buyOrders
                .Select(o => _masterStore.GetByCode(o.StockCode)?.Currency ?? "KRW")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var lockedWallets = await _db.Wallets
                .FromSqlInterpolated(
                    $"SELECT *, xmin FROM wallets WHERE \"StrategyId\" = ANY({buyStrategyIds}) AND \"Currency\" = ANY({buyCurrencies}) ORDER BY \"StrategyId\", \"Currency\" FOR UPDATE")
                .ToListAsync(ct);

            walletsMap = lockedWallets.ToDictionary(w => (w.StrategyId, w.Currency));
        }

        var holdingsMap = new Dictionary<(long StrategyId, string StockCode), Stocks>();
        if (sellOrders.Count > 0)
        {
            var sellStrategyIds = sellOrders.Select(o => o.StrategyId).Distinct().OrderBy(id => id).ToArray();
            var sellStockCodes = sellOrders.Select(o => o.StockCode).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToArray();

            var lockedHoldings = await _db.Stocks
                .FromSqlInterpolated(
                    $"SELECT *, xmin FROM stocks WHERE \"StrategyId\" = ANY({sellStrategyIds}) AND \"StockCode\" = ANY({sellStockCodes}) ORDER BY \"StrategyId\", \"StockCode\" FOR UPDATE")
                .ToListAsync(ct);

            holdingsMap = lockedHoldings.ToDictionary(s => (s.StrategyId, s.StockCode));
        }

        var partialBuyOrderIds = buyOrders
            .Where(o => o.FilledQuantity > 0)
            .Select(o => o.OrderId)
            .ToArray();

        var spentByOrderId = partialBuyOrderIds.Length > 0
            ? await _db.Trades
                .AsNoTracking()
                .Where(t => partialBuyOrderIds.Contains(t.OrderId))
                .GroupBy(t => t.OrderId)
                .Select(g => new { OrderId = g.Key, TotalSpent = g.Sum(t => t.TotalAmount) })
                .ToDictionaryAsync(x => x.OrderId, x => x.TotalSpent, ct)
            : new Dictionary<long, decimal>();

        var now = DateTime.UtcNow;
        var refundedByCurrency = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        long totalReleasedShares = 0;

        _db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var order in targetOrders)
            {
                var remainingQty = order.Quantity - order.FilledQuantity;
                if (remainingQty > 0)
                {
                    if (order.Side == OrderSide.Buy)
                    {
                        var currency = _masterStore.GetByCode(order.StockCode)?.Currency ?? "KRW";
                        if (walletsMap.TryGetValue((order.StrategyId, currency), out var wallet))
                        {
                            var spentSoFar = spentByOrderId.GetValueOrDefault(order.OrderId, 0m);
                            var remainingReserved = Math.Max(0m, order.TotalAmount - spentSoFar);
                            var releaseCash = Math.Min(wallet.PendingCash, remainingReserved);
                            if (releaseCash > 0)
                            {
                                wallet.PendingCash -= releaseCash;
                                wallet.AvailableCash += releaseCash;
                                wallet.UpdatedAtUtc = now;
                                refundedByCurrency[currency] = refundedByCurrency.GetValueOrDefault(currency, 0m) + releaseCash;
                            }
                        }
                    }
                    else
                    {
                        if (holdingsMap.TryGetValue((order.StrategyId, order.StockCode), out var holding))
                        {
                            var releaseQty = Math.Min(holding.ReservedQuantity, remainingQty);
                            if (releaseQty > 0)
                            {
                                holding.ReservedQuantity -= releaseQty;
                                holding.UpdatedAtUtc = now;
                                totalReleasedShares += releaseQty;
                            }
                        }
                    }
                }

                order.Status = OrderStatus.Cancelled;
            }
        }
        finally
        {
            _db.ChangeTracker.AutoDetectChangesEnabled = true;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _pendingList.RemoveOrders(targetOrders.Select(o => o.OrderId));

        var isAdmin = !expectedUid.HasValue;
        foreach (var order in targetOrders)
        {
            var remainingQty = Math.Max(0, order.Quantity - order.FilledQuantity);
            _logger.LogInformation("[취소됨] OrderId={OrderId}, UID={Uid}, RemainingQty={RemainingQty}, Admin={IsAdmin}",
                order.OrderId, order.Uid, remainingQty, isAdmin);
        }

        var cancelledIds = targetOrders.Select(o => o.OrderId).ToArray();
        await _hubContext.Clients
            .Group("orders:all")
            .SendAsync("ReceiveOrderUpdated", new
            {
                eventType = "BulkCancelled",
                stockCode = normalizedStock,
                updatedOrderIds = cancelledIds,
                tradeCount = 0,
                updatedAtUtc = now
            }, ct);

        return new BulkCancelOrdersResult(
            targetOrders.Count,
            totalReleasedShares,
            refundedByCurrency,
            cancelledIds);
    }

    private async Task<OrderDto> CancelOrderCoreAsync(long? expectedUid, long orderId, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var order = await _db.Orders
            .FromSqlInterpolated($"SELECT * FROM orders WHERE \"OrderId\" = {orderId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (order == null || (expectedUid.HasValue && order.Uid != expectedUid.Value))
        {
            throw new StockLabException(ErrorCode.Forbidden, "취소할 수 있는 주문을 찾을 수 없습니다.");
        }

        if (order.Status is not (OrderStatus.Pending or OrderStatus.PartiallyFilled))
        {
            throw new StockLabException(ErrorCode.InvalidInput, $"현재 상태({order.Status})의 주문은 취소할 수 없습니다.");
        }

        var remainingQty = order.Quantity - order.FilledQuantity;
        var now = DateTime.UtcNow;

        if (remainingQty > 0)
        {
            if (order.Side == OrderSide.Buy)
            {
                var master = _masterStore.GetByCode(order.StockCode);
                var currency = master?.Currency ?? "KRW";

                var wallet = await _db.Wallets
                    .FromSqlInterpolated(
                        $"SELECT *, xmin FROM wallets WHERE \"StrategyId\" = {order.StrategyId} AND \"Currency\" = {currency} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);

                if (wallet != null)
                {
                    var spentSoFar = order.FilledQuantity > 0
                        ? await _db.Trades
                            .AsNoTracking()
                            .Where(t => t.OrderId == order.OrderId)
                            .SumAsync(t => (decimal?)t.TotalAmount, ct) ?? 0m
                        : 0m;
                    var remainingReserved = Math.Max(0m, order.TotalAmount - spentSoFar);
                    var releaseCash = Math.Min(wallet.PendingCash, remainingReserved);
                    wallet.PendingCash -= releaseCash;
                    wallet.AvailableCash += releaseCash;
                    wallet.UpdatedAtUtc = now;
                }
            }
            else
            {
                var holding = await _db.Stocks
                    .FromSqlInterpolated(
                        $"SELECT *, xmin FROM stocks WHERE \"StrategyId\" = {order.StrategyId} AND \"StockCode\" = {order.StockCode} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);

                if (holding != null)
                {
                    holding.ReservedQuantity = Math.Max(0, holding.ReservedQuantity - remainingQty);
                    holding.UpdatedAtUtc = now;
                }
            }
        }

        order.Status = OrderStatus.Cancelled;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _pendingList.RemoveOrders([order.OrderId]);

        _logger.LogInformation("[취소됨] OrderId={OrderId}, UID={Uid}, RemainingQty={RemainingQty}, Admin={IsAdmin}",
            order.OrderId, order.Uid, remainingQty, !expectedUid.HasValue);

        await _hubContext.Clients
            .Group("orders:all")
            .SendAsync("ReceiveOrderUpdated", new
            {
                eventType = "Cancelled",
                stockCode = order.StockCode,
                updatedOrderIds = new[] { order.OrderId },
                tradeCount = 0,
                updatedAtUtc = now
            }, ct);

        return ToOrderDto(order);
    }

    public async Task<IReadOnlyList<OrderDto>> GetOrdersAsync(long uid, long strategyId, CancellationToken ct = default)
    {
        await EnsureStrategyOwnershipAsync(uid, strategyId, ct);
        return await GetStrategyOrdersAsAdminAsync(strategyId, ct);
    }

    public async Task<IReadOnlyList<OrderDto>> GetStrategyOrdersAsAdminAsync(long strategyId, CancellationToken ct = default)
    {
        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.StrategyId == strategyId)
            .OrderByDescending(o => o.OrderedAtUtc)
            .Take(100)
            .ToListAsync(ct);

        return orders.Select(ToOrderDto).ToList();
    }

    public async Task<PagedResult<OrderDto>> GetStrategyOrdersPagedAsAdminAsync(
        long strategyId,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult.Normalize(page, pageSize);
        var query = _db.Orders.AsNoTracking().Where(o => o.StrategyId == strategyId);
        var totalCount = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)normalizedSize));
        var clampedPage = Math.Clamp(normalizedPage, 1, totalPages);

        var orders = await query
            .OrderByDescending(o => o.OrderedAtUtc)
            .Skip((clampedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .ToListAsync(ct);

        return PagedResult.Create(orders.Select(ToOrderDto).ToList(), clampedPage, normalizedSize, totalCount);
    }

    public async Task<PagedResult<AdminOrderDto>> GetAllOrdersPagedAsAdminAsync(
        string? statusFilter,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult.Normalize(page, pageSize);
        var query = BuildAllOrdersQuery(statusFilter);
        var totalCount = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)normalizedSize));
        var clampedPage = Math.Clamp(normalizedPage, 1, totalPages);

        var rows = await query
            .OrderByDescending(o => o.OrderedAtUtc)
            .Skip((clampedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .Select(o => new
            {
                o.OrderId,
                o.StrategyId,
                Uid = o.Uid.ToString(),
                o.User.Nickname,
                o.StockCode,
                o.Side,
                o.OrderType,
                o.Status,
                o.OrderPrice,
                o.Quantity,
                o.FilledQuantity,
                o.TotalAmount,
                o.OrderedAtUtc
            })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var items = rows.Select(o =>
        {
            var (isMarketOpen, refPrice, waitCode, waitReason) = ResolveWaitDiagnostics(
                o.StockCode, o.Side, o.OrderType, o.Status, o.OrderPrice, now);

            return new AdminOrderDto(
                o.OrderId,
                o.StrategyId,
                o.Uid,
                o.Nickname,
                o.StockCode,
                o.Side,
                o.OrderType,
                o.Status,
                o.OrderPrice,
                o.Quantity,
                o.FilledQuantity,
                o.TotalAmount,
                o.OrderedAtUtc,
                isMarketOpen,
                refPrice,
                waitCode,
                waitReason);
        }).ToList();

        return PagedResult.Create(items, clampedPage, normalizedSize, totalCount);
    }

    private (bool IsMarketOpen, decimal? ReferencePrice, string? WaitReasonCode, string? WaitReason) ResolveWaitDiagnostics(
        string stockCode,
        OrderSide side,
        OrderType orderType,
        OrderStatus status,
        decimal orderPrice,
        DateTime utcNow)
    {
        var master = _masterStore.GetByCode(stockCode);
        var isMarketOpen = master != null && MarketSessionHelper.IsMarketOpen(master.MarketType, utcNow);
        decimal? refPrice = master != null && master.BasePrice > 0 ? master.BasePrice : null;

        if (status != OrderStatus.Pending && status != OrderStatus.PartiallyFilled)
        {
            return (isMarketOpen, refPrice, null, null);
        }

        var forcedEnabled = _forcedMatchingService.IsEnabled;
        if (!isMarketOpen && !forcedEnabled)
        {
            return (false, refPrice, "MarketClosed", "휴장 대기 (장외·주말)");
        }

        if (orderType == OrderType.Limit && refPrice is > 0)
        {
            var refStr = refPrice.Value.ToString("N2");
            if (side == OrderSide.Buy && orderPrice < refPrice.Value)
            {
                var outOfForcedBand = !isMarketOpen && forcedEnabled && orderPrice < refPrice.Value * 0.99m;
                var msg = outOfForcedBand
                    ? $"시세 불일치 (강제체결 ±1% 범위 밖 · 기준가 {refStr})"
                    : $"시세 불일치 (기준가 {refStr} > 매수가)";
                return (isMarketOpen, refPrice, "PriceMismatch", msg);
            }

            if (side == OrderSide.Sell && orderPrice > refPrice.Value)
            {
                var outOfForcedBand = !isMarketOpen && forcedEnabled && orderPrice > refPrice.Value * 1.01m;
                var msg = outOfForcedBand
                    ? $"시세 불일치 (강제체결 ±1% 범위 밖 · 기준가 {refStr})"
                    : $"시세 불일치 (기준가 {refStr} < 매도가)";
                return (isMarketOpen, refPrice, "PriceMismatch", msg);
            }
        }

        if (status == OrderStatus.PartiallyFilled)
        {
            return (isMarketOpen, refPrice, "PartialLiquidity", "부분 체결 (잔량 10% 한도)");
        }

        if (_pendingList.GetLastCheckedUtc(stockCode) == DateTime.MinValue)
        {
            return (isMarketOpen, refPrice, "PendingRegistration", "등록 대기");
        }

        if (!isMarketOpen && forcedEnabled)
        {
            return (false, refPrice, "WaitingLiquidity", "체결 대기 (강제체결 순차)");
        }

        return (isMarketOpen, refPrice, "WaitingLiquidity", "체결 대기 (매물·선순위)");
    }

    private IQueryable<Order> BuildAllOrdersQuery(string? statusFilter)
    {
        var query = _db.Orders
            .AsNoTracking()
            .Include(o => o.User)
            .AsQueryable();

        var normalized = statusFilter?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "pending" => query.Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled),
            "filled" => query.Where(o => o.Status == OrderStatus.Filled),
            "cancelled" => query.Where(o => o.Status == OrderStatus.Cancelled),
            _ => query
        };
    }

    public async Task<IReadOnlyList<TradeDto>> GetTradesAsync(long uid, long strategyId, CancellationToken ct = default)
    {
        await EnsureStrategyOwnershipAsync(uid, strategyId, ct);
        return await GetStrategyTradesAsAdminAsync(strategyId, ct);
    }

    public async Task<IReadOnlyList<TradeDto>> GetStrategyTradesAsAdminAsync(long strategyId, CancellationToken ct = default)
    {
        return await _db.Trades
            .AsNoTracking()
            .Where(t => t.StrategyId == strategyId)
            .OrderByDescending(t => t.ExecutedAtUtc)
            .Take(100)
            .Select(t => new TradeDto(
                t.Id,
                t.OrderId,
                t.StrategyId,
                t.StockCode,
                t.Side,
                t.ExecutedPrice,
                t.ExecutedQuantity,
                t.TotalAmount,
                t.ExecutedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<PagedResult<TradeDto>> GetStrategyTradesPagedAsAdminAsync(
        long strategyId,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult.Normalize(page, pageSize);
        var query = _db.Trades.AsNoTracking().Where(t => t.StrategyId == strategyId);
        var totalCount = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)normalizedSize));
        var clampedPage = Math.Clamp(normalizedPage, 1, totalPages);

        var items = await query
            .OrderByDescending(t => t.ExecutedAtUtc)
            .Skip((clampedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .Select(t => new TradeDto(
                t.Id,
                t.OrderId,
                t.StrategyId,
                t.StockCode,
                t.Side,
                t.ExecutedPrice,
                t.ExecutedQuantity,
                t.TotalAmount,
                t.ExecutedAtUtc))
            .ToListAsync(ct);

        return PagedResult.Create(items, clampedPage, normalizedSize, totalCount);
    }

    public async Task<PagedResult<OrderDto>> GetUserOrdersPagedAsync(
        long uid,
        long? strategyId,
        string? scope = "all",
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default)
    {
        if (strategyId.HasValue)
        {
            await EnsureStrategyOwnershipAsync(uid, strategyId.Value, ct);
        }

        var (normalizedPage, normalizedSize) = PagedResult.Normalize(page, pageSize);
        var query = _db.Orders.AsNoTracking().Where(o => o.Uid == uid);

        if (strategyId.HasValue)
        {
            query = query.Where(o => o.StrategyId == strategyId.Value);
        }

        var normalizedScope = scope?.Trim().ToLowerInvariant();
        if (normalizedScope == "active")
        {
            query = query.Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled);
        }
        else if (normalizedScope == "completed")
        {
            query = query.Where(o => o.Status == OrderStatus.Filled || o.Status == OrderStatus.Cancelled || o.Status == OrderStatus.Rejected);
        }

        var totalCount = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)normalizedSize));
        var clampedPage = Math.Clamp(normalizedPage, 1, totalPages);

        var items = await query
            .OrderByDescending(o => o.OrderedAtUtc)
            .Skip((clampedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .Select(o => new OrderDto(
                o.OrderId,
                o.StrategyId,
                o.StockCode,
                o.Side,
                o.OrderType,
                o.Status,
                o.OrderPrice,
                o.Quantity,
                o.FilledQuantity,
                o.TotalAmount,
                o.OrderedAtUtc))
            .ToListAsync(ct);

        return PagedResult.Create(items, clampedPage, normalizedSize, totalCount);
    }

    public async Task<PagedResult<TradeDto>> GetUserTradesPagedAsync(
        long uid,
        long? strategyId,
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default)
    {
        if (strategyId.HasValue)
        {
            await EnsureStrategyOwnershipAsync(uid, strategyId.Value, ct);
        }

        var (normalizedPage, normalizedSize) = PagedResult.Normalize(page, pageSize);
        var query = _db.Trades
            .AsNoTracking()
            .Where(t => t.Strategy.Uid == uid);

        if (strategyId.HasValue)
        {
            query = query.Where(t => t.StrategyId == strategyId.Value);
        }

        var totalCount = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)normalizedSize));
        var clampedPage = Math.Clamp(normalizedPage, 1, totalPages);

        var items = await query
            .OrderByDescending(t => t.ExecutedAtUtc)
            .Skip((clampedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .Select(t => new TradeDto(
                t.Id,
                t.OrderId,
                t.StrategyId,
                t.StockCode,
                t.Side,
                t.ExecutedPrice,
                t.ExecutedQuantity,
                t.TotalAmount,
                t.ExecutedAtUtc))
            .ToListAsync(ct);

        return PagedResult.Create(items, clampedPage, normalizedSize, totalCount);
    }

    private async Task EnsureStrategyOwnershipAsync(long uid, long strategyId, CancellationToken ct)
    {
        var ownsStrategy = await _db.Strategies
            .AsNoTracking()
            .AnyAsync(s => s.Id == strategyId && s.Uid == uid, ct);

        if (!ownsStrategy)
        {
            throw new StockLabException(ErrorCode.Forbidden, "해당 투자 전략에 대한 접근 권한이 없습니다.");
        }
    }

    private async Task<decimal> ResolveMarketReferencePriceAsync(string stockCode, OrderSide side, CancellationToken ct)
    {
        var cachedBook = _socketService.GetCachedOrderBook(stockCode);
        if (cachedBook != null)
        {
            var levelPrice = side == OrderSide.Buy
                ? cachedBook.Asks.FirstOrDefault(a => a.Price > 0)?.Price
                : cachedBook.Bids.FirstOrDefault(b => b.Price > 0)?.Price;

            if (levelPrice is > 0)
            {
                return levelPrice.Value;
            }
        }

        var quote = await _quoteService.GetQuoteAsync(stockCode, ct);
        if (quote.CurrentPrice > 0)
        {
            return quote.CurrentPrice;
        }

        throw new StockLabException(ErrorCode.InvalidInput, "시장가 주문 기준 시세를 확인할 수 없습니다.");
    }

    private static OrderDto ToOrderDto(Order order) => new(
        order.OrderId,
        order.StrategyId,
        order.StockCode,
        order.Side,
        order.OrderType,
        order.Status,
        order.OrderPrice,
        order.Quantity,
        order.FilledQuantity,
        order.TotalAmount,
        order.OrderedAtUtc);
}

