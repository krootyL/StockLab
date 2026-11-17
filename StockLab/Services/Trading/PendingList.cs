using Microsoft.EntityFrameworkCore;
using StockLab.Persistence;
using StockLab.Persistence.Entities;

namespace StockLab.Services.Trading;

public readonly record struct PendingStockSummary(
    string StockCode,
    int OpenCount,
    bool HasMarketOrder,
    int MarketBuyCount,
    int MarketSellCount,
    DateTime LastCheckedUtc
);

/// <summary>
/// 체결 대기(Pending/PartiallyFilled) 주문을 종목 및 호가 버킷 단위로 관리하는 싱글톤 인메모리 캐시.
/// 서버 시작 및 장 개장/마감 전환 시에만 DB 스냅샷을 전체 로드하고,
/// 런타임 중에는 DB 커밋 이후 메모리 증분 갱신만으로 폴링·사전 필터링을 O(1)로 처리합니다.
/// </summary>
public class PendingList
{
    private static readonly TimeSpan FinalizedTombstoneTtl = TimeSpan.FromMinutes(1);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _dbSyncLock = new(1, 1);

    private readonly Dictionary<long, PendingOrderEntry> _ordersById = new();
    private readonly Dictionary<string, StockBucket> _bucketsByStock = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastCheckedUtcByStock = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, DateTime> _recentlyFinalizedUtc = new();
    private readonly Queue<(long OrderId, DateTime FinalizedUtc)> _finalizedOrderQueue = new();
    private readonly List<(bool IsActive, long OrderId, string StockCode, OrderSide Side, OrderType OrderType, decimal OrderPrice)> _mutationsDuringSync = new();

    private volatile bool _initialized;
    private int _activeSyncCount;

    private readonly record struct PendingOrderEntry(
        string StockCode,
        OrderSide Side,
        OrderType OrderType,
        decimal OrderPrice);

    private sealed class StockBucket
    {
        public int OpenCount { get; set; }
        public int MarketBuyCount { get; set; }
        public int MarketSellCount { get; set; }
        public Dictionary<decimal, int> BuyLimitCounts { get; } = new();
        public Dictionary<decimal, int> SellLimitCounts { get; } = new();

        public bool HasMarketOrder => MarketBuyCount > 0 || MarketSellCount > 0;
    }

    public bool IsInitialized => _initialized;

    /// <summary>
    /// DB에서 미체결 주문 전체 스냅샷을 읽어 PendingList를 재구성합니다.
    /// 기존 종목별 마지막 호가 조회 시각(_lastCheckedUtcByStock)은 보존됩니다.
    /// </summary>
    public async Task<int> SyncFromDatabaseAsync(AppDbContext db, CancellationToken ct = default)
    {
        await _dbSyncLock.WaitAsync(ct);
        lock (_sync)
        {
            _activeSyncCount++;
            _mutationsDuringSync.Clear();
        }

        try
        {
            var openOrders = await db.Orders
                .AsNoTracking()
                .Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.PartiallyFilled)
                .Select(o => new
                {
                    o.OrderId,
                    o.StockCode,
                    o.Side,
                    o.OrderType,
                    o.OrderPrice
                })
                .ToListAsync(ct);

            lock (_sync)
            {
                _ordersById.Clear();
                _bucketsByStock.Clear();
                _recentlyFinalizedUtc.Clear();
                _finalizedOrderQueue.Clear();

                foreach (var item in openOrders)
                {
                    AddOrderLocked(item.OrderId, item.StockCode, item.Side, item.OrderType, item.OrderPrice);
                }

                // DB 스냅샷 조회 도중 다른 요청/엔진에서 커밋된 변경 사항 재적용 (선형성 보장)
                var replayNow = DateTime.UtcNow;
                foreach (var m in _mutationsDuringSync)
                {
                    if (m.IsActive)
                    {
                        AddOrderLocked(m.OrderId, m.StockCode, m.Side, m.OrderType, m.OrderPrice);
                    }
                    else
                    {
                        RemoveOrderLocked(m.OrderId);
                        MarkFinalizedLocked(m.OrderId, replayNow);
                    }
                }

                _mutationsDuringSync.Clear();
                _initialized = true;
                return _bucketsByStock.Count;
            }
        }
        finally
        {
            lock (_sync)
            {
                _activeSyncCount = Math.Max(0, _activeSyncCount - 1);
                if (_activeSyncCount == 0)
                {
                    _mutationsDuringSync.Clear();
                }
            }
            _dbSyncLock.Release();
        }
    }

    public void RecordStockChecked(string stockCode)
    {
        if (string.IsNullOrWhiteSpace(stockCode)) return;
        lock (_sync)
        {
            _lastCheckedUtcByStock[stockCode] = DateTime.UtcNow;
        }
    }

    public DateTime GetLastCheckedUtc(string stockCode)
    {
        if (string.IsNullOrWhiteSpace(stockCode)) return DateTime.MinValue;
        lock (_sync)
        {
            return _lastCheckedUtcByStock.TryGetValue(stockCode, out var dt) ? dt : DateTime.MinValue;
        }
    }

    public IReadOnlyList<PendingStockSummary> GetPendingStockSummaries()
    {
        lock (_sync)
        {
            if (_bucketsByStock.Count == 0)
            {
                return Array.Empty<PendingStockSummary>();
            }

            var result = new List<PendingStockSummary>(_bucketsByStock.Count);
            foreach (var (stockCode, bucket) in _bucketsByStock)
            {
                if (bucket.OpenCount <= 0) continue;

                var lastChecked = _lastCheckedUtcByStock.TryGetValue(stockCode, out var dt)
                    ? dt
                    : DateTime.MinValue;

                result.Add(new PendingStockSummary(
                    stockCode,
                    bucket.OpenCount,
                    bucket.HasMarketOrder,
                    bucket.MarketBuyCount,
                    bucket.MarketSellCount,
                    lastChecked));
            }

            return result;
        }
    }

    public IReadOnlyList<string> GetPendingStockCodes()
    {
        lock (_sync)
        {
            if (_bucketsByStock.Count == 0)
            {
                return Array.Empty<string>();
            }

            return _bucketsByStock
                .Where(kv => kv.Value.OpenCount > 0)
                .Select(kv => kv.Key)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyDictionary<decimal, int> GetBuyLimitCounts(string stockCode)
    {
        lock (_sync)
        {
            if (!_bucketsByStock.TryGetValue(stockCode, out var bucket) || bucket.BuyLimitCounts.Count == 0)
            {
                return new Dictionary<decimal, int>();
            }

            return new Dictionary<decimal, int>(bucket.BuyLimitCounts);
        }
    }

    public IReadOnlyDictionary<decimal, int> GetSellLimitCounts(string stockCode)
    {
        lock (_sync)
        {
            if (!_bucketsByStock.TryGetValue(stockCode, out var bucket) || bucket.SellLimitCounts.Count == 0)
            {
                return new Dictionary<decimal, int>();
            }

            return new Dictionary<decimal, int>(bucket.SellLimitCounts);
        }
    }

    /// <summary>
    /// 주어진 최우선 매도1호가(bestAsk) / 매수1호가(bestBid) 기준으로
    /// 이번 호가 틱에서 체결(또는 시장가 정산) 시도할 대상 주문이 메모리에 존재하는지 확인합니다.
    /// 아직 DB 스냅샷 초기화 전이라면 안전을 위해 true를 반환해 DB 검사를 허용합니다.
    /// </summary>
    public bool HasMatchableOrders(string stockCode, decimal? bestAsk, decimal? bestBid)
    {
        if (!_initialized)
        {
            return true;
        }

        lock (_sync)
        {
            if (!_bucketsByStock.TryGetValue(stockCode, out var bucket) || bucket.OpenCount <= 0)
            {
                return false;
            }

            if (bestAsk.HasValue)
            {
                if (bucket.MarketBuyCount > 0)
                {
                    return true;
                }

                foreach (var (price, count) in bucket.BuyLimitCounts)
                {
                    if (count > 0 && price >= bestAsk.Value)
                    {
                        return true;
                    }
                }
            }

            if (bestBid.HasValue)
            {
                if (bucket.MarketSellCount > 0)
                {
                    return true;
                }

                foreach (var (price, count) in bucket.SellLimitCounts)
                {
                    if (count > 0 && price <= bestBid.Value)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 신규 접수 또는 상태 변경된 주문 1건을 DB 커밋 이후 메모리에 반영합니다.
    /// </summary>
    public void UpsertOrder(Order order)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            PruneFinalizedTombstonesLocked(now);
            ApplyOrderLocked(order, now);
        }
    }

    /// <summary>
    /// 체결 엔진 처리 결과 등 다수의 주문을 DB 커밋 이후 일괄 반영합니다.
    /// </summary>
    public void ApplyOrders(IEnumerable<Order> orders)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            PruneFinalizedTombstonesLocked(now);

            foreach (var order in orders)
            {
                ApplyOrderLocked(order, now);
            }
        }
    }

    /// <summary>
    /// 취소 또는 더미 정리 등으로 종료된 주문 ID 목록을 DB 커밋/삭제 이후 메모리에서 제거합니다.
    /// </summary>
    public void RemoveOrders(IEnumerable<long> orderIds)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            PruneFinalizedTombstonesLocked(now);

            foreach (var orderId in orderIds)
            {
                RemoveOrderLocked(orderId);
                MarkFinalizedLocked(orderId, now);
                if (_activeSyncCount > 0)
                {
                    _mutationsDuringSync.Add((false, orderId, string.Empty, OrderSide.Buy, OrderType.Limit, 0m));
                }
            }
        }
    }

    private void ApplyOrderLocked(Order order, DateTime now)
    {
        if (order.Status == OrderStatus.Pending || order.Status == OrderStatus.PartiallyFilled)
        {
            // 동시성 경합으로 이미 종료 처리된 주문이 뒤늦게 Pending으로 재등록되는 현상 방지
            if (_recentlyFinalizedUtc.ContainsKey(order.OrderId))
            {
                return;
            }

            AddOrderLocked(order.OrderId, order.StockCode, order.Side, order.OrderType, order.OrderPrice);
            if (_activeSyncCount > 0)
            {
                _mutationsDuringSync.Add((true, order.OrderId, order.StockCode, order.Side, order.OrderType, order.OrderPrice));
            }
            return;
        }

        RemoveOrderLocked(order.OrderId);
        MarkFinalizedLocked(order.OrderId, now);
        if (_activeSyncCount > 0)
        {
            _mutationsDuringSync.Add((false, order.OrderId, order.StockCode, order.Side, order.OrderType, order.OrderPrice));
        }
    }

    private void AddOrderLocked(long orderId, string stockCode, OrderSide side, OrderType orderType, decimal orderPrice)
    {
        if (string.IsNullOrWhiteSpace(stockCode)) return;

        if (_ordersById.TryGetValue(orderId, out var existing))
        {
            if (string.Equals(existing.StockCode, stockCode, StringComparison.OrdinalIgnoreCase)
                && existing.Side == side
                && existing.OrderType == orderType
                && existing.OrderPrice == orderPrice)
            {
                return;
            }

            RemoveOrderLocked(orderId);
        }

        var entry = new PendingOrderEntry(stockCode, side, orderType, orderPrice);
        _ordersById[orderId] = entry;

        if (!_bucketsByStock.TryGetValue(stockCode, out var bucket))
        {
            bucket = new StockBucket();
            _bucketsByStock[stockCode] = bucket;
        }

        bucket.OpenCount++;
        AdjustBucketCounts(bucket, entry, delta: 1);
    }

    private void RemoveOrderLocked(long orderId)
    {
        if (!_ordersById.TryGetValue(orderId, out var existing))
        {
            return;
        }

        _ordersById.Remove(orderId);

        if (!_bucketsByStock.TryGetValue(existing.StockCode, out var bucket))
        {
            return;
        }

        bucket.OpenCount = Math.Max(0, bucket.OpenCount - 1);
        AdjustBucketCounts(bucket, existing, delta: -1);

        if (bucket.OpenCount == 0)
        {
            _bucketsByStock.Remove(existing.StockCode);
        }
    }

    private static void AdjustBucketCounts(StockBucket bucket, PendingOrderEntry entry, int delta)
    {
        if (entry.OrderType == OrderType.Market)
        {
            if (entry.Side == OrderSide.Buy)
            {
                bucket.MarketBuyCount = Math.Max(0, bucket.MarketBuyCount + delta);
            }
            else
            {
                bucket.MarketSellCount = Math.Max(0, bucket.MarketSellCount + delta);
            }

            return;
        }

        var priceMap = entry.Side == OrderSide.Buy ? bucket.BuyLimitCounts : bucket.SellLimitCounts;
        priceMap.TryGetValue(entry.OrderPrice, out var currentCount);
        var nextCount = currentCount + delta;
        if (nextCount > 0)
        {
            priceMap[entry.OrderPrice] = nextCount;
        }
        else
        {
            priceMap.Remove(entry.OrderPrice);
        }
    }

    private void MarkFinalizedLocked(long orderId, DateTime now)
    {
        _recentlyFinalizedUtc[orderId] = now;
        _finalizedOrderQueue.Enqueue((orderId, now));
    }

    private void PruneFinalizedTombstonesLocked(DateTime now)
    {
        while (_finalizedOrderQueue.Count > 0)
        {
            var (orderId, finalizedUtc) = _finalizedOrderQueue.Peek();
            if (now - finalizedUtc < FinalizedTombstoneTtl)
            {
                break;
            }

            _finalizedOrderQueue.Dequeue();
            if (_recentlyFinalizedUtc.TryGetValue(orderId, out var recordedUtc) && recordedUtc == finalizedUtc)
            {
                _recentlyFinalizedUtc.Remove(orderId);
            }
        }
    }
}
