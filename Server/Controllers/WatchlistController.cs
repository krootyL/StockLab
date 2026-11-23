using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Account;

namespace StockLab.Controllers;

[Authorize]
[ApiController]
[Route("api/watchlist")]
public class WatchlistController : ControllerBase
{
    private readonly AccountService _accountService;

    public WatchlistController(AccountService accountService)
    {
        _accountService = accountService;
    }

    [HttpGet]
    public async Task<IActionResult> GetWatchlist(CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var items = await _accountService.GetWatchlistAsync(uid, ct);
        return Ok(ApiResponse<IReadOnlyList<WatchlistItemDto>>.Ok(items));
    }

    [HttpPost]
    public async Task<IActionResult> AddWatchlist([FromBody] AddWatchlistRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var added = await _accountService.AddWatchlistAsync(uid, request.StockCode, ct);
        return Ok(ApiResponse<WatchlistItemDto>.Ok(added));
    }

    [HttpDelete("{stockCode}")]
    public async Task<IActionResult> RemoveWatchlist(string stockCode, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        await _accountService.RemoveWatchlistAsync(uid, stockCode, ct);
        return Ok(ApiResponse<object>.Ok(new { removed = true }));
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

