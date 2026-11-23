using Microsoft.AspNetCore.SignalR;
using StockLab.Services.Kis;

namespace StockLab.Hubs;

public class StockQuoteHub : Hub
{
    private readonly KisSocketService _socketService;
    private readonly ILogger<StockQuoteHub> _logger;

    public StockQuoteHub(KisSocketService socketService, ILogger<StockQuoteHub> logger)
    {
        _socketService = socketService;
        _logger = logger;
    }

    public async Task RegisterStock(string stockCode)
    {
        if (string.IsNullOrWhiteSpace(stockCode)) return;

        var normalized = stockCode.Trim().ToUpperInvariant();
        await Groups.AddToGroupAsync(Context.ConnectionId, $"stock:{normalized}");
        _logger.LogInformation("[SignalR] 클라이언트({ConnectionId}) 등록: {StockCode}", Context.ConnectionId, normalized);

        await _socketService.GetRealtimeStockAsync(normalized, Context.ConnectionId);
    }

    public async Task ReleaseStock(string stockCode)
    {
        if (string.IsNullOrWhiteSpace(stockCode)) return;

        var normalized = stockCode.Trim().ToUpperInvariant();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"stock:{normalized}");
        _logger.LogInformation("[SignalR] 클라이언트({ConnectionId}) 해제: {StockCode}", Context.ConnectionId, normalized);

        await _socketService.ReleaseRealtimeStockAsync(normalized, Context.ConnectionId);
    }

    public async Task SyncWatchlistStocks(IReadOnlyList<string>? stockCodes)
    {
        var normalizedCodes = (stockCodes ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var code in normalizedCodes)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"stock:{code}");
        }

        _logger.LogInformation("[SignalR] 클라이언트({ConnectionId}) 관심종목 웹소켓 동기화: {Count}종목", Context.ConnectionId, normalizedCodes.Count);
        await _socketService.SyncWatchlistStocksAsync(normalizedCodes, Context.ConnectionId);
    }

    public async Task RegisterOrderUpdates()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "orders:all");
        _logger.LogInformation("[SignalR] 클라이언트({ConnectionId}) 주문 실시간 알림 등록", Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _socketService.RemoveConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
