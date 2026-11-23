using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using StockLab.BackgroundServices;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Account;
using StockLab.Services.Trading;

namespace StockLab.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private const string AdminKeyHeader = "X-Admin-Key";
    private const string DevFallbackAdminKey = "stocklab-dev-admin";
    private static int _devFallbackWarningLogged;

    private readonly AccountService _accountService;
    private readonly OrderService _orderService;
    private readonly OrderMatchingService _matchingService;
    private readonly TrafficSimulationService _trafficSimulationService;
    private readonly ForcedMatchingService _forcedMatchingService;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        AccountService accountService,
        OrderService orderService,
        OrderMatchingService matchingService,
        TrafficSimulationService trafficSimulationService,
        ForcedMatchingService forcedMatchingService,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<AdminController> logger)
    {
        _accountService = accountService;
        _orderService = orderService;
        _matchingService = matchingService;
        _trafficSimulationService = trafficSimulationService;
        _forcedMatchingService = forcedMatchingService;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? query,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        ValidateAdminKey();
        var users = await _accountService.GetUsersPagedAsync(query, page, pageSize, ct);
        return Ok(ApiResponse<PagedResult<UserProfileDto>>.Ok(users));
    }

    [HttpPatch("users/{uid:long}")]
    public async Task<IActionResult> UpdateNickname(long uid, [FromBody] UpdateUserNicknameRequest request, CancellationToken ct)
    {
        ValidateAdminKey();
        var updated = await _accountService.UpdateNicknameAsync(uid, request.Nickname, ct);
        return Ok(ApiResponse<UserProfileDto>.Ok(updated));
    }

    [HttpGet("orders")]
    public async Task<IActionResult> GetAllOrders(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        ValidateAdminKey();
        var orders = await _orderService.GetAllOrdersPagedAsAdminAsync(status, page, pageSize, ct);
        return Ok(ApiResponse<PagedResult<AdminOrderDto>>.Ok(orders));
    }

    [HttpGet("strategies/{strategyId:long}/orders")]
    public async Task<IActionResult> GetStrategyOrders(
        long strategyId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        ValidateAdminKey();
        var orders = await _orderService.GetStrategyOrdersPagedAsAdminAsync(strategyId, page, pageSize, ct);
        return Ok(ApiResponse<PagedResult<OrderDto>>.Ok(orders));
    }

    [HttpGet("strategies/{strategyId:long}/trades")]
    public async Task<IActionResult> GetStrategyTrades(
        long strategyId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        ValidateAdminKey();
        var trades = await _orderService.GetStrategyTradesPagedAsAdminAsync(strategyId, page, pageSize, ct);
        return Ok(ApiResponse<PagedResult<TradeDto>>.Ok(trades));
    }

    [HttpPost("orders/{orderId:long}/cancel")]
    public async Task<IActionResult> CancelOrder(long orderId, CancellationToken ct)
    {
        ValidateAdminKey();
        var cancelled = await _orderService.CancelOrderAsAdminAsync(orderId, ct);
        return Ok(ApiResponse<OrderDto>.Ok(cancelled));
    }

    /// <summary>
    /// 관리자 전용: 대기 중인 주문(Pending/PartiallyFilled)을 조건(투자 전략·종목) 또는 전체 단위로 일괄 취소하고 예수금·예약 수량을 환불/해제합니다.
    /// </summary>
    [HttpPost("orders/cancel-pending")]
    public async Task<IActionResult> CancelPendingOrders(
        [FromQuery] long? strategyId = null,
        [FromQuery] string? stockCode = null,
        CancellationToken ct = default)
    {
        ValidateAdminKey();
        var result = await _orderService.CancelPendingOrdersAsAdminAsync(strategyId, stockCode, ct);
        return Ok(ApiResponse<BulkCancelOrdersResult>.Ok(result));
    }

    /// <summary>
    /// 개발 환경 전용: 모의 호가 틱을 주입하여 체결 엔진(MatchByOrderBookAsync)을 즉시 실행합니다.
    /// </summary>
    [HttpPost("orders/simulate-tick")]
    public async Task<IActionResult> SimulateOrderBookTick([FromBody] RealtimeStockStatus orderBook, CancellationToken ct)
    {
        ValidateAdminKey();
        if (!_environment.IsDevelopment())
        {
            throw new StockLabException(ErrorCode.Forbidden, "개발 환경(Development)에서만 모의 호가 주입을 사용할 수 있습니다.");
        }

        await _matchingService.MatchByOrderBookAsync(orderBook, ct);
        return Ok(ApiResponse<object>.Ok(new { message = $"{orderBook.StockCode} 모의 호가 틱 체결 처리 완료" }));
    }

    /// <summary>
    /// 개발 환경 전용: 지정 건수의 가상 주문 및 모의 호가 틱을 실행하고, 종료 시 임시 더미 데이터를 자동 삭제합니다.
    /// </summary>
    [HttpPost("orders/simulate-traffic")]
    public async Task<IActionResult> SimulateTraffic(
        [FromQuery] int? count,
        [FromQuery] bool? cleanupAfter,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] SimulateTrafficRequest? request,
        CancellationToken ct)
    {
        ValidateAdminKey();
        if (!_environment.IsDevelopment())
        {
            throw new StockLabException(ErrorCode.Forbidden, "개발 환경(Development)에서만 더미 트래픽 생성을 사용할 수 있습니다.");
        }

        var resolvedRequest = new SimulateTrafficRequest(
            OrderCount: count ?? request?.OrderCount ?? 100,
            CleanupAfter: cleanupAfter ?? request?.CleanupAfter ?? true);

        var result = await _trafficSimulationService.RunAsync(resolvedRequest, ct);
        return Ok(ApiResponse<SimulateTrafficResult>.Ok(result));
    }

    /// <summary>
    /// 관리자 전용: 강제 체결 스위치(종가 ±1%, 초당 3건 가상 공급) 현재 상태를 조회합니다.
    /// </summary>
    [HttpGet("matching/forced")]
    public IActionResult GetForcedMatchingStatus()
    {
        ValidateAdminKey();
        return Ok(ApiResponse<object>.Ok(new { enabled = _forcedMatchingService.IsEnabled }));
    }

    /// <summary>
    /// 개발 환경 전용: 강제 체결 스위치(종가 ±1%, 초당 3건 가상 공급)를 켜거나 끕니다.
    /// </summary>
    [HttpPost("matching/forced")]
    public IActionResult SetForcedMatchingStatus([FromBody] ToggleForcedMatchingRequest request)
    {
        ValidateAdminKey();
        if (!_environment.IsDevelopment())
        {
            throw new StockLabException(ErrorCode.Forbidden, "개발 환경(Development)에서만 강제 체결 스위치를 사용할 수 있습니다.");
        }

        _forcedMatchingService.SetEnabled(request.Enabled);
        return Ok(ApiResponse<object>.Ok(new { enabled = _forcedMatchingService.IsEnabled }));
    }

    /// <summary>
    /// 관리자 전용: 테스트 환경에서 특정 투자 전략 지갑의 예수가능금을 증감(+/-)합니다.
    /// </summary>
    [HttpPost("wallets/adjust-balance")]
    public async Task<IActionResult> AdjustBalance([FromBody] AdjustBalanceRequest request, CancellationToken ct)
    {
        ValidateAdminKey();

        var updatedWallet = await _accountService.AdjustBalanceAsync(
            request.StrategyId,
            request.Currency,
            request.Amount,
            ct);

        return Ok(ApiResponse<WalletSummaryDto>.Ok(updatedWallet));
    }

    private void ValidateAdminKey()
    {
        var configuredKey = _configuration["Admin:ApiKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            if (!_environment.IsDevelopment())
            {
                throw new StockLabException(ErrorCode.Forbidden, "관리자 API 키가 구성되지 않았습니다.");
            }

            configuredKey = DevFallbackAdminKey;
            if (Interlocked.Exchange(ref _devFallbackWarningLogged, 1) == 0)
            {
                _logger.LogWarning("[관리자 API] Admin:ApiKey 미설정으로 Development 기본 키가 사용 중입니다. (이후 동일 경고 생략)");
            }
        }

        if (!Request.Headers.TryGetValue(AdminKeyHeader, out var providedValues))
        {
            throw new StockLabException(ErrorCode.Forbidden, "관리자 인증 헤더(X-Admin-Key)가 필요합니다.");
        }

        var expectedBytes = Encoding.UTF8.GetBytes(configuredKey);
        var providedBytes = Encoding.UTF8.GetBytes(providedValues.ToString());

        if (expectedBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            throw new StockLabException(ErrorCode.Forbidden, "유효하지 않은 관리자 API 키입니다.");
        }
    }
}

public record ToggleForcedMatchingRequest(bool Enabled);
