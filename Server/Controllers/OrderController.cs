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
    public async Task<IActionResult> GetOrders([FromQuery] long scenarioId, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var orders = await _orderService.GetOrdersAsync(uid, scenarioId, ct);
        return Ok(ApiResponse<IReadOnlyList<OrderDto>>.Ok(orders));
    }

    [HttpGet("trades")]
    public async Task<IActionResult> GetTrades([FromQuery] long scenarioId, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var trades = await _orderService.GetTradesAsync(uid, scenarioId, ct);
        return Ok(ApiResponse<IReadOnlyList<TradeDto>>.Ok(trades));
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

