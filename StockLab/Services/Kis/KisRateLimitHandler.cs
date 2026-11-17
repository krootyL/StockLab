using System.Diagnostics;
using System.Threading.RateLimiting;
using StockLab.Common;

namespace StockLab.Services.Kis;

/// <summary>
/// KIS OpenAPI 전체 호출 유량을 초당 18회 이하로 제한하는 프로세스 전역 토큰/고정창 제한기.
/// </summary>
public sealed class KisRateLimiter : IDisposable
{
    public const int PermitsPerSecond = 18;

    private readonly FixedWindowRateLimiter _limiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = PermitsPerSecond,
        Window = TimeSpan.FromSeconds(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 200,
        AutoReplenishment = true
    });

    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
        => _limiter.AcquireAsync(1, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// KIS용 HttpClient 요청 직전에 KisRateLimiter를 거쳐 초당 유량 초과를 방지하는 HTTP 핸들러.
/// </summary>
public sealed class KisRateLimitHandler : DelegatingHandler
{
    private readonly KisRateLimiter _rateLimiter;
    private readonly ILogger<KisRateLimitHandler> _logger;

    public KisRateLimitHandler(KisRateLimiter rateLimiter, ILogger<KisRateLimitHandler> logger)
    {
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var lease = await _rateLimiter.AcquireAsync(cancellationToken);
        var waitMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        if (!lease.IsAcquired)
        {
            _logger.LogWarning(
                "{Prefix} 초당 {Limit}건 유량 대기열 한도 초과로 요청 거부 ({Method} {Path})",
                LogConstants.KisApiPrefix,
                KisRateLimiter.PermitsPerSecond,
                request.Method,
                request.RequestUri?.AbsolutePath);
            throw new HttpRequestException("KIS API 초당 유량 대기열 한도를 초과했습니다.");
        }

        if (waitMs >= 100)
        {
            _logger.LogWarning(
                "{Prefix} 초당 {Limit}건 유량 제어로 {WaitMs:F0}ms 대기 후 전송 ({Method} {Path})",
                LogConstants.KisApiPrefix,
                KisRateLimiter.PermitsPerSecond,
                waitMs,
                request.Method,
                request.RequestUri?.AbsolutePath);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

