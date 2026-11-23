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
    private readonly InMemorySessionStore _sessionStore;

    public AuthController(GuestAuthService guestAuthService, InMemorySessionStore sessionStore)
    {
        _guestAuthService = guestAuthService;
        _sessionStore = sessionStore;
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

        var expiresAtUtc = await SignInProfileAsync(profile);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile with { SessionExpiresAtUtc = expiresAtUtc }));
    }

    /// <summary>
    /// 일반/관리자 계정 로그인 (아이디/비밀번호) 및 세션 쿠키 발급
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var profile = await _guestAuthService.LoginAsync(request.LoginId, request.Password, ct);
        var expiresAtUtc = await SignInProfileAsync(profile);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile with { SessionExpiresAtUtc = expiresAtUtc }));
    }

    /// <summary>
    /// 게스트 계정 발급 엔드포인트 (비활성화됨 — 비로그인 둘러보기는 조회 전용으로 유지, 거래/인증은 정식 계정 사용)
    /// </summary>
    [HttpPost("guest")]
    public IActionResult LoginAsGuest([FromBody] GuestLoginRequest? request)
    {
        throw new StockLabException(ErrorCode.Forbidden, "게스트 로그인 기능은 비활성화되었습니다. 일반 로그인 또는 회원가입을 이용해 주세요.");
    }

    private async Task<DateTimeOffset> SignInProfileAsync(UserProfileDto profile, string? existingSessionId = null)
    {
        var (sessionId, expiresAtUtc) = existingSessionId != null
            ? _sessionStore.ResetTimer(existingSessionId)
            : _sessionStore.IssueSession();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, profile.Uid),
            new(ClaimTypes.Name, profile.Nickname),
            new("user_type", ((int)profile.Type).ToString()),
            new("sid", sessionId)
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
    /// 현재 로그인된 사용자의 DB 투자 전략/지갑/보유종목 프로필 및 세션 만료 시각 조회
    /// </summary>
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var authResult = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var expiresAtUtc = authResult.Properties?.ExpiresUtc ?? DateTimeOffset.UtcNow.Add(InMemorySessionStore.SessionLifetime);

        var profile = await _guestAuthService.GetProfileAsync(uid, ct);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile with { SessionExpiresAtUtc = expiresAtUtc }));
    }

    /// <summary>
    /// 본인 닉네임 수정 (정식 계정 전용) 및 세션 쿠키 클레임 갱신
    /// </summary>
    [Authorize]
    [HttpPatch("me/nickname")]
    public async Task<IActionResult> UpdateMyNickname([FromBody] UpdateUserNicknameRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var existingSid = User.FindFirstValue("sid");
        var profile = await _guestAuthService.UpdateOwnNicknameAsync(uid, request.Nickname, ct);
        var expiresAtUtc = await SignInProfileAsync(profile, existingSid);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile with { SessionExpiresAtUtc = expiresAtUtc }));
    }

    /// <summary>
    /// 본인 비밀번호 변경 (정식 계정 전용)
    /// </summary>
    [Authorize]
    [HttpPatch("me/password")]
    public async Task<IActionResult> ChangeMyPassword([FromBody] UpdatePasswordRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUid();
        await _guestAuthService.ChangePasswordAsync(uid, request.CurrentPassword, request.NewPassword, ct);
        return Ok(ApiResponse<object>.Ok(new { message = "비밀번호가 변경되었습니다." }));
    }

    /// <summary>
    /// 로그인 세션 30분 타이머 리셋 (요청 시점 기준 30분으로 초기화)
    /// </summary>
    [Authorize]
    [HttpPost("extend")]
    public async Task<IActionResult> ExtendSession(CancellationToken ct)
    {
        var uid = GetCurrentUid();
        var existingSid = User.FindFirstValue("sid");
        var profile = await _guestAuthService.GetProfileAsync(uid, ct);
        var expiresAtUtc = await SignInProfileAsync(profile, existingSid);
        return Ok(ApiResponse<object>.Ok(new { sessionExpiresAtUtc = expiresAtUtc }));
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

    /// <summary>
    /// 로그아웃 (서버 인메모리 세션 즉시 파기 + 세션 쿠키 제거)
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var sid = User.FindFirstValue("sid");
        _sessionStore.Revoke(sid);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(ApiResponse<object>.Ok(new { message = "로그아웃되었습니다." }));
    }
}
