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
    /// 회원가입 시 아이디 중복 여부 검사
    /// </summary>
    [HttpGet("check-id")]
    public async Task<IActionResult> CheckLoginId([FromQuery] string loginId, CancellationToken ct)
    {
        var available = await _guestAuthService.IsLoginIdAvailableAsync(loginId, ct);
        return Ok(ApiResponse<object>.Ok(new
        {
            available,
            message = available ? "사용 가능한 아이디입니다." : "이미 사용 중인 아이디입니다."
        }));
    }

    /// <summary>
    /// 일반 계정 회원가입 (아이디/비밀번호/닉네임) 및 세션 쿠키 발급
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var profile = await _guestAuthService.RegisterLocalUserAsync(
            request.LoginId,
            request.Password,
            request.Nickname,
            ct);

        await SignInProfileAsync(profile);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile));
    }

    /// <summary>
    /// 일반/관리자 계정 로그인 (아이디/비밀번호) 및 세션 쿠키 발급
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var profile = await _guestAuthService.LoginAsync(request.LoginId, request.Password, ct);
        await SignInProfileAsync(profile);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile));
    }

    /// <summary>
    /// 게스트 계정(18자리 UID), 기본 시나리오 및 HttpOnly 세션 쿠키 발급 (초기 시드: KRW 1,000,000 / USD $1,000)
    /// </summary>
    [HttpPost("guest")]
    public async Task<IActionResult> LoginAsGuest([FromBody] GuestLoginRequest? request, CancellationToken ct)
    {
        var profile = await _guestAuthService.CreateGuestSessionAsync(request?.Nickname, ct);
        await SignInProfileAsync(profile);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile));
    }

    private async Task<DateTimeOffset> SignInProfileAsync(UserProfileDto profile)
    {
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, profile.Uid),
            new(ClaimTypes.Name, profile.Nickname),
            new("user_type", ((int)profile.Type).ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = false,
                ExpiresUtc = expiresAtUtc
            });

        return expiresAtUtc;
    }

    /// <summary>
    /// 현재 로그인된 사용자의 DB 시나리오/지갑/보유종목 프로필 및 세션 만료 시각 조회
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

        var authResult = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var expiresAtUtc = authResult.Properties?.ExpiresUtc ?? DateTimeOffset.UtcNow.AddMinutes(30);

        var profile = await _guestAuthService.GetProfileAsync(uid, ct);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile with { SessionExpiresAtUtc = expiresAtUtc }));
    }

    /// <summary>
    /// 로그인 세션 30분 선택적 연장
    /// </summary>
    [Authorize]
    [HttpPost("extend")]
    public async Task<IActionResult> ExtendSession(CancellationToken ct)
    {
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(uidStr, out var uid))
        {
            throw new StockLabException(ErrorCode.Unauthorized);
        }

        var profile = await _guestAuthService.GetProfileAsync(uid, ct);
        var expiresAtUtc = await SignInProfileAsync(profile);
        return Ok(ApiResponse<object>.Ok(new { sessionExpiresAtUtc = expiresAtUtc }));
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
