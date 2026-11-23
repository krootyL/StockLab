using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using StockLab.Client.Services.Kis;
using StockLab.Common;

namespace StockLab.Client.Controllers;

[ApiController]
[Route("api/kis")]
public class KisConfigController : ControllerBase
{
    public sealed record CreateKisProfileRequest(string Name, string AppKey, string AppSecret, List<string>? SharedUserIds = null);
    public sealed record UpdateKisProfileRequest(string Name, List<string>? SharedUserIds = null, string? AppKey = null, string? AppSecret = null);
    public sealed record SelectKisProfileRequest(string Id);

    private readonly KisProfileVaultService _vault;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public KisConfigController(
        KisProfileVaultService vault,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _vault = vault;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    private async Task<string?> TryGetAuthenticatedUidAsync(CancellationToken cancellationToken)
    {
        var cookieHeader = Request.Headers.Cookie.ToString();
        if (string.IsNullOrWhiteSpace(cookieHeader)) return null;

        var serverBase = _config["Server:BaseUrl"];
        if (string.IsNullOrWhiteSpace(serverBase))
        {
            serverBase = _config["Server:HttpBaseUrl"] ?? "http://localhost:5087";
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{serverBase.TrimEnd('/')}/api/auth/me");
            req.Headers.Add("Cookie", cookieHeader);

            using var res = await client.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("success", out var succ) || !succ.GetBoolean()) return null;
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            if (data.TryGetProperty("uid", out var uidProp))
            {
                return uidProp.GetString();
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private async Task<string> EnsureAuthenticatedUidAsync(CancellationToken cancellationToken)
    {
        var uid = await TryGetAuthenticatedUidAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(uid))
        {
            throw new StockLabException(ErrorCode.Unauthorized, "로그인이 필요합니다.");
        }
        return uid;
    }

    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<KisVaultStatus>>> GetStatus(CancellationToken cancellationToken)
    {
        var uid = await TryGetAuthenticatedUidAsync(cancellationToken);
        return Ok(ApiResponse<KisVaultStatus>.Ok(_vault.GetStatus(uid)));
    }

    /// <summary>
    /// 중앙 Server 세션(/api/auth/me)이 유효한 로그인 사용자에 한해 허용된 로컬 금고 KIS 키를 잠금 해제한다.
    /// </summary>
    [HttpPost("unlock")]
    public async Task<ActionResult<ApiResponse<KisVaultStatus>>> Unlock(CancellationToken cancellationToken)
    {
        var uid = await EnsureAuthenticatedUidAsync(cancellationToken);
        var status = _vault.UnlockFromVault(uid);
        return Ok(ApiResponse<KisVaultStatus>.Ok(status));
    }

    [HttpPost("lock")]
    public ActionResult<ApiResponse<object>> Lock()
    {
        _vault.Lock();
        return Ok(ApiResponse<object>.Ok(new { locked = true }));
    }

    [HttpPost("profiles")]
    public async Task<ActionResult<ApiResponse<KisVaultStatus>>> CreateProfile(
        [FromBody] CreateKisProfileRequest request,
        CancellationToken cancellationToken)
    {
        var uid = await EnsureAuthenticatedUidAsync(cancellationToken);
        var status = await _vault.CreateAndActivateAsync(
            uid, request.Name, request.AppKey, request.AppSecret, request.SharedUserIds, cancellationToken);
        return Ok(ApiResponse<KisVaultStatus>.Ok(status));
    }

    [HttpPatch("profiles/{id}")]
    public async Task<ActionResult<ApiResponse<KisVaultStatus>>> UpdateProfile(
        string id,
        [FromBody] UpdateKisProfileRequest request,
        CancellationToken cancellationToken)
    {
        var uid = await EnsureAuthenticatedUidAsync(cancellationToken);
        var status = await _vault.UpdateProfileMetaAsync(
            id, uid, request.Name, request.SharedUserIds, request.AppKey, request.AppSecret, cancellationToken);
        return Ok(ApiResponse<KisVaultStatus>.Ok(status));
    }

    [HttpPost("profiles/select")]
    public async Task<ActionResult<ApiResponse<KisVaultStatus>>> SelectProfile(
        [FromBody] SelectKisProfileRequest request,
        CancellationToken cancellationToken)
    {
        var uid = await EnsureAuthenticatedUidAsync(cancellationToken);
        var status = await _vault.SelectProfileAsync(request.Id, uid, cancellationToken);
        return Ok(ApiResponse<KisVaultStatus>.Ok(status));
    }

    [HttpDelete("profiles/{id}")]
    public async Task<ActionResult<ApiResponse<KisVaultStatus>>> DeleteProfile(
        string id,
        CancellationToken cancellationToken)
    {
        var uid = await EnsureAuthenticatedUidAsync(cancellationToken);
        var status = await _vault.DeleteProfileAsync(id, uid, cancellationToken);
        return Ok(ApiResponse<KisVaultStatus>.Ok(status));
    }
}
