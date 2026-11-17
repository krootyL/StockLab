using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Auth;

namespace StockLab.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly GuestAuthService _guestAuthService;

    public AuthController(GuestAuthService guestAuthService)
    {
        _guestAuthService = guestAuthService;
    }

    /// <summary>
    /// 게스트 계정(18자리 UID), 기본 시나리오 및 HttpOnly 세션 쿠키 발급 (초기 시드: KRW 1,000,000 / USD $1,000)
    /// </summary>
    [HttpPost("guest")]
    public async Task<IActionResult> LoginAsGuest([FromBody] GuestLoginRequest? request, CancellationToken ct)
    {
        var profile = await _guestAuthService.CreateGuestSessionAsync(request?.Nickname, ct);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, profile.Uid),
            new(ClaimTypes.Name, profile.Nickname),
            new("user_type", profile.Type.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
            });

        return Ok(ApiResponse<UserProfileDto>.Ok(profile));
    }

    /// <summary>
    /// 현재 로그인된 사용자의 DB 시나리오/지갑/보유종목 프로필 조회
    /// </summary>
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(uidStr, out var uid))
        {
            throw new StockLabException(ErrorCode.Unauthorized);
        }

        var profile = await _guestAuthService.GetProfileAsync(uid, ct);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile));
    }

    /// <summary>
    /// 로그아웃 (세션 쿠키 제거)
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(ApiResponse<object>.Ok(new { message = "로그아웃되었습니다." }));
    }
}
