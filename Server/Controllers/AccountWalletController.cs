using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Account;

namespace StockLab.Controllers;

[Authorize]
[ApiController]
[Route("api/account/wallets")]
public class AccountWalletController : ControllerBase
{
    private readonly AccountService _accountService;

    public AccountWalletController(AccountService accountService)
    {
        _accountService = accountService;
    }

    [HttpGet]
    public async Task<IActionResult> GetWallets(CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var wallets = await _accountService.GetAccountWalletsAsync(uid, ct);
        return Ok(ApiResponse<IReadOnlyList<AccountWalletDto>>.Ok(wallets));
    }

    [HttpPost("deposit")]
    public async Task<IActionResult> Deposit([FromBody] AccountWalletTransactionRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var wallets = await _accountService.DepositAccountWalletAsync(uid, request.Currency, request.Amount, ct);
        return Ok(ApiResponse<IReadOnlyList<AccountWalletDto>>.Ok(wallets));
    }

    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw([FromBody] AccountWalletTransactionRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var wallets = await _accountService.WithdrawAccountWalletAsync(uid, request.Currency, request.Amount, ct);
        return Ok(ApiResponse<IReadOnlyList<AccountWalletDto>>.Ok(wallets));
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

