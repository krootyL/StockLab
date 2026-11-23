using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Account;

namespace StockLab.Controllers;

[Authorize]
[ApiController]
[Route("api/strategies")]
public class StrategyController : ControllerBase
{
    private readonly AccountService _accountService;

    public StrategyController(AccountService accountService)
    {
        _accountService = accountService;
    }

    [HttpGet]
    public async Task<IActionResult> GetStrategies(CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var items = await _accountService.GetStrategiesAsync(uid, ct);
        return Ok(ApiResponse<IReadOnlyList<StrategySummaryDto>>.Ok(items));
    }

    [HttpPost]
    public async Task<IActionResult> CreateStrategy([FromBody] CreateStrategyRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var created = await _accountService.CreateStrategyAsync(uid, request.Name, ct);
        return Ok(ApiResponse<StrategySummaryDto>.Ok(created));
    }

    [HttpPatch("{strategyId:long}")]
    public async Task<IActionResult> RenameStrategy(long strategyId, [FromBody] UpdateStrategyRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var updated = await _accountService.RenameStrategyAsync(uid, strategyId, request.Name, ct);
        return Ok(ApiResponse<StrategySummaryDto>.Ok(updated));
    }

    [HttpPost("{strategyId:long}/transfer")]
    public async Task<IActionResult> TransferCash(long strategyId, [FromBody] StrategyTransferRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var result = await _accountService.TransferBetweenAccountAndStrategyAsync(
            uid, strategyId, request.Direction, request.Currency, request.Amount, ct);
        return Ok(ApiResponse<StrategyTransferResultDto>.Ok(result));
    }

    [HttpDelete("{strategyId:long}")]
    public async Task<IActionResult> DeleteStrategy(long strategyId, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var result = await _accountService.DeleteStrategyAsync(uid, strategyId, ct);
        return Ok(ApiResponse<DeleteStrategyResultDto>.Ok(result));
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

