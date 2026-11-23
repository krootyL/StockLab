using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Trading;

namespace StockLab.Controllers;

[Authorize]
[ApiController]
[Route("api/orders")]
public class OrderController : ControllerBase
{
    private readonly OrderService _orderService;

    public OrderController(OrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost]
    public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var order = await _orderService.PlaceOrderAsync(uid, request, ct);
        return Ok(ApiResponse<OrderDto>.Ok(order));
    }

    [HttpDelete("{orderId:long}")]
    public async Task<IActionResult> CancelOrder(long orderId, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var order = await _orderService.CancelOrderAsync(uid, orderId, ct);
        return Ok(ApiResponse<OrderDto>.Ok(order));
    }

    [HttpGet]
    public async Task<IActionResult> GetOrders([FromQuery] long strategyId, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var orders = await _orderService.GetOrdersAsync(uid, strategyId, ct);
        return Ok(ApiResponse<IReadOnlyList<OrderDto>>.Ok(orders));
    }

    [HttpGet("paged")]
    public async Task<IActionResult> GetOrdersPaged(
        [FromQuery] long? strategyId = null,
        [FromQuery] string? scope = "all",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var uid = GetCurrentUid();
        var paged = await _orderService.GetUserOrdersPagedAsync(uid, strategyId, scope, page, pageSize, ct);
        return Ok(ApiResponse<PagedResult<OrderDto>>.Ok(paged));
    }

    [HttpGet("trades")]
    public async Task<IActionResult> GetTrades([FromQuery] long strategyId, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var trades = await _orderService.GetTradesAsync(uid, strategyId, ct);
        return Ok(ApiResponse<IReadOnlyList<TradeDto>>.Ok(trades));
    }

    [HttpGet("trades/paged")]
    public async Task<IActionResult> GetTradesPaged(
        [FromQuery] long? strategyId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var uid = GetCurrentUid();
        var paged = await _orderService.GetUserTradesPagedAsync(uid, strategyId, page, pageSize, ct);
        return Ok(ApiResponse<PagedResult<TradeDto>>.Ok(paged));
    }

    private long GetCurrentUid()
    {
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(uidStr, out var uid))
        {
            throw new StockLabException(ErrorCode.Unauthorized);
        }
        return uid;
    }
}

