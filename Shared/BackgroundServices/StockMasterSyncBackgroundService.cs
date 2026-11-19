using Microsoft.Extensions.Options;
using StockLab.Services.Master;
using StockLab.Settings;

namespace StockLab.BackgroundServices;

public class StockMasterSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly KisSettings _kisSettings;
    private readonly ILogger<StockMasterSyncBackgroundService> _logger;

    private static readonly TimeZoneInfo KoreaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");

    public StockMasterSyncBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<KisSettings> kisOptions,
        ILogger<StockMasterSyncBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _kisSettings = kisOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_kisSettings.AppKey) || string.IsNullOrWhiteSpace(_kisSettings.AppSecret))
        {
            _logger.LogWarning("[마스터 스케줄러 중단] 로컬 KIS OpenAPI AppKey 또는 AppSecret이 설정되지 않아 스케줄러를 비활성화합니다.");
            return;
        }

        _logger.LogInformation("[마스터 스케줄러] KIS 공식 13회 일중 배포 스케줄러가 활성화되었습니다.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowKst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, KoreaTimeZone);
            var nextReleaseKst = StockMasterSyncService.GetNextReleaseTimeKst(nowKst);
            var delay = nextReleaseKst - nowKst;

            // 증권사 서버 업로드 처리 지연 시간을 감안하여 정각보다 30초 뒤에 안전하게 트리거
            var safetyBuffer = TimeSpan.FromSeconds(30);
            var actualDelay = delay + safetyBuffer;

            _logger.LogInformation("[마스터 스케줄러] 다음 배포 시각({NextTime:yyyy-MM-dd HH:mm:ss} KST)까지 {Minutes:N1}분 대기합니다.",
                nextReleaseKst, actualDelay.TotalMinutes);

            try
            {
                await Task.Delay(actualDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _logger.LogInformation("[마스터 스케줄러] 배포 예정 시각에 도달하여 마스터 버전 검사 및 갱신을 실행합니다.");

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var syncService = scope.ServiceProvider.GetRequiredService<StockMasterSyncService>();
                // If-Modified-Since 헤더를 통해 실제 변경분이 있을 때만 zip 다운로드
                await syncService.SyncMasterDataAsync(forceDownload: false, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[마스터 스케줄러 오류] 자동 마스터 갱신 작업 도중 예외가 발생했습니다.");
            }
        }
    }
}
