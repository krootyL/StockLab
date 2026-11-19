using System.IO.Compression;
using System.Net;
using StockLab.Common;
using StockLab.Dtos;

namespace StockLab.Services.Master;

public class StockMasterSyncService
{
    private readonly HttpClient _httpClient;
    private readonly StockMasterParser _parser;
    private readonly StockMasterStore _store;
    private readonly ILogger<StockMasterSyncService> _logger;

    // KIS 공식 마스터 CDN 파일 URL 목록
    private const string KospiZipUrl = "https://new.real.download.dws.co.kr/common/master/kospi_code.mst.zip";
    private const string KosdaqZipUrl = "https://new.real.download.dws.co.kr/common/master/kosdaq_code.mst.zip";
    private const string KonexZipUrl = "https://new.real.download.dws.co.kr/common/master/konex_code.mst.zip";
    private const string NxtKospiZipUrl = "https://new.real.download.dws.co.kr/common/master/nxt_kospi_code.mst.zip";

    // KIS 공식 미국 해외주식 파일 URL 목록 (.cod.zip)
    private const string NasZipUrl = "https://new.real.download.dws.co.kr/common/master/nasmst.cod.zip";
    private const string NysZipUrl = "https://new.real.download.dws.co.kr/common/master/nysmst.cod.zip";
    private const string AmsZipUrl = "https://new.real.download.dws.co.kr/common/master/amsmst.cod.zip";

    private static readonly string KospiFilePath = Path.Combine(AppContext.BaseDirectory, "Data", "kospi_code.mst");
    private static readonly string KosdaqFilePath = Path.Combine(AppContext.BaseDirectory, "Data", "kosdaq_code.mst");
    private static readonly string KonexFilePath = Path.Combine(AppContext.BaseDirectory, "Data", "konex_code.mst");
    private static readonly string NxtKospiFilePath = Path.Combine(AppContext.BaseDirectory, "Data", "nxt_kospi_code.mst");

    private static readonly string NasFilePath = Path.Combine(AppContext.BaseDirectory, "Data", "nasmst.cod");
    private static readonly string NysFilePath = Path.Combine(AppContext.BaseDirectory, "Data", "nysmst.cod");
    private static readonly string AmsFilePath = Path.Combine(AppContext.BaseDirectory, "Data", "amsmst.cod");

    private static readonly TimeZoneInfo KoreaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");

    // KIS 공식 일중 종목정보파일 업로드 시간표 (총 13회)
    public static readonly IReadOnlyList<TimeSpan> ReleaseSchedule = new[]
    {
        new TimeSpan(6, 0, 0),
        new TimeSpan(6, 55, 0),
        new TimeSpan(7, 35, 0),
        new TimeSpan(7, 55, 0),
        new TimeSpan(8, 45, 0),
        new TimeSpan(9, 46, 0),
        new TimeSpan(10, 55, 0),
        new TimeSpan(17, 10, 0),
        new TimeSpan(17, 30, 0),
        new TimeSpan(17, 55, 0),
        new TimeSpan(18, 10, 0),
        new TimeSpan(18, 30, 0),
        new TimeSpan(18, 55, 0)
    };

    public StockMasterSyncService(
        HttpClient httpClient,
        StockMasterParser parser,
        StockMasterStore store,
        ILogger<StockMasterSyncService> logger)
    {
        _httpClient = httpClient;
        _parser = parser;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// 국내 4대 시장(코스피/코스닥/코넥스/넥스트) + 미국 3대 시장(나스닥/뉴욕/아멕스) 전방위 병렬 동기화
    /// </summary>
    public async Task<(bool Success, int TotalCount, bool WasUpdated)> SyncMasterDataAsync(bool forceDownload = false, CancellationToken cancellationToken = default)
    {
        // 1. 서버 기동 시: 로컬 캐시가 유효하고 메모리가 비어있으면 로컬 즉시 로드 (초고속 부팅)
        if (!forceDownload && _store.TotalCount == 0 && AreLocalMastersUpToDate(out var localKst, out var expectedKst))
        {
            _logger.LogInformation("[마스터 캐시 적중] 로컬 마스터(수정: {LastMod:yyyy-MM-dd HH:mm:ss} KST)가 유효 기준({Expected:yyyy-MM-dd HH:mm:ss} KST)보다 최신이므로 로컬 데이터를 즉시 적재합니다.",
                localKst, expectedKst);

            var localStocks = await LoadLocalMastersAsync(cancellationToken);
            if (localStocks.Count > 0)
            {
                _store.Initialize(localStocks);
                _logger.LogInformation("[로컬 로드 완료] 총 {Count:N0}개 종목(국내+해외)이 메모리에 적재되었습니다.", _store.TotalCount);
                return (true, _store.TotalCount, false);
            }
        }

        // 2. KIS 서버로부터 7대 시장 파일 비동기 병렬 다운로드 (Task.WhenAll)
        _logger.LogInformation("[마스터 동기화] KIS 국내외 7대 시장(코스피/코스닥/코넥스/NXT/나스닥/뉴욕/아멕스) 병렬 동기화 시작...");

        var kospiTask = FetchMarketMasterAsync(KospiZipUrl, "kospi_code.mst", KospiFilePath, forceDownload, cancellationToken);
        var kosdaqTask = FetchMarketMasterAsync(KosdaqZipUrl, "kosdaq_code.mst", KosdaqFilePath, forceDownload, cancellationToken);
        var konexTask = FetchMarketMasterAsync(KonexZipUrl, "konex_code.mst", KonexFilePath, forceDownload, cancellationToken);
        var nxtTask = FetchMarketMasterAsync(NxtKospiZipUrl, "nxt_kospi_code.mst", NxtKospiFilePath, forceDownload, cancellationToken);

        var nasTask = FetchMarketMasterAsync(NasZipUrl, "nasmst.cod", NasFilePath, forceDownload, cancellationToken);
        var nysTask = FetchMarketMasterAsync(NysZipUrl, "nysmst.cod", NysFilePath, forceDownload, cancellationToken);
        var amsTask = FetchMarketMasterAsync(AmsZipUrl, "amsmst.cod", AmsFilePath, forceDownload, cancellationToken);

        await Task.WhenAll(kospiTask, kosdaqTask, konexTask, nxtTask, nasTask, nysTask, amsTask);

        var kospiRes = await kospiTask;
        var kosdaqRes = await kosdaqTask;
        var konexRes = await konexTask;
        var nxtRes = await nxtTask;
        var nasRes = await nasTask;
        var nysRes = await nysTask;
        var amsRes = await amsTask;

        var allStocks = new List<StockMasterItem>(15000);

        // 코스피
        if (kospiRes.Bytes != null) allStocks.AddRange(_parser.ParseKospiBytes(kospiRes.Bytes));
        else allStocks.AddRange(await _parser.ParseFileAsync(KospiFilePath, "KOSPI", "KRX", cancellationToken));

        // 코스닥
        if (kosdaqRes.Bytes != null) allStocks.AddRange(_parser.ParseKosdaqBytes(kosdaqRes.Bytes));
        else allStocks.AddRange(await _parser.ParseFileAsync(KosdaqFilePath, "KOSDAQ", "KRX", cancellationToken));

        // 코넥스
        if (konexRes.Bytes != null) allStocks.AddRange(_parser.ParseKonexBytes(konexRes.Bytes));
        else allStocks.AddRange(await _parser.ParseFileAsync(KonexFilePath, "KONEX", "KRX", cancellationToken));

        // 넥스트 코스피
        if (nxtRes.Bytes != null) allStocks.AddRange(_parser.ParseNxtKospiBytes(nxtRes.Bytes));
        else allStocks.AddRange(await _parser.ParseFileAsync(NxtKospiFilePath, "NXT_KOSPI", "KRX", cancellationToken));

        // 미국 나스닥
        if (nasRes.Bytes != null) allStocks.AddRange(_parser.ParseOverseasCodBytes(nasRes.Bytes, "NAS"));
        else allStocks.AddRange(await _parser.ParseFileAsync(NasFilePath, "USA", "NAS", cancellationToken));

        // 미국 뉴욕(NYSE)
        if (nysRes.Bytes != null) allStocks.AddRange(_parser.ParseOverseasCodBytes(nysRes.Bytes, "NYS"));
        else allStocks.AddRange(await _parser.ParseFileAsync(NysFilePath, "USA", "NYS", cancellationToken));

        // 미국 아멕스(AMEX)
        if (amsRes.Bytes != null) allStocks.AddRange(_parser.ParseOverseasCodBytes(amsRes.Bytes, "AMS"));
        else allStocks.AddRange(await _parser.ParseFileAsync(AmsFilePath, "USA", "AMS", cancellationToken));

        if (allStocks.Count > 0)
        {
            _store.Initialize(allStocks);
            _logger.LogInformation("[마스터 동기화 완료] 국내외 총 {Count:N0}개 종목이 메모리에 무중단 적재되었습니다.", _store.TotalCount);
            return (true, _store.TotalCount, true);
        }

        // 전체 Fallback
        var fallbackStocks = await LoadLocalMastersAsync(cancellationToken);
        if (fallbackStocks.Count > 0)
        {
            _store.Initialize(fallbackStocks);
            _logger.LogWarning("[마스터 Fallback] 로컬 파일에서 총 {Count:N0}개 종목을 복구했습니다.", _store.TotalCount);
            return (true, _store.TotalCount, false);
        }

        _logger.LogCritical("[마스터 치명적 오류] 모든 마스터 데이터를 불러오는 데 실패했습니다.");
        return (false, 0, false);
    }

    private async Task<(byte[]? Bytes, bool IsNotModified)> FetchMarketMasterAsync(
        string zipUrl,
        string entryName,
        string localFilePath,
        bool forceDownload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, zipUrl);
        if (!forceDownload && File.Exists(localFilePath))
        {
            request.Headers.IfModifiedSince = File.GetLastWriteTimeUtc(localFilePath);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return (null, true);
            }

            response.EnsureSuccessStatusCode();

            await using var zipStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                ?? archive.Entries.FirstOrDefault();

            if (entry == null) return (null, false);

            await using var entryStream = entry.Open();
            using var memoryStream = new MemoryStream();
            await entryStream.CopyToAsync(memoryStream, cancellationToken);
            var bytes = memoryStream.ToArray();

            var directory = Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            await File.WriteAllBytesAsync(localFilePath, bytes, cancellationToken);
            var fileTimeUtc = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;
            File.SetLastWriteTimeUtc(localFilePath, fileTimeUtc);

            // 프로젝트 원본 소스 디렉터리(StockLab/Data/)에도 개발 편의를 위해 동시 복사 보존
            try
            {
                var projectDataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Data"));
                if (Directory.Exists(projectDataDir))
                {
                    var sourceCopyPath = Path.Combine(projectDataDir, Path.GetFileName(localFilePath));
                    await File.WriteAllBytesAsync(sourceCopyPath, bytes, cancellationToken);
                    File.SetLastWriteTimeUtc(sourceCopyPath, fileTimeUtc);
                }
            }
            catch
            {
                // 원본 폴더 동기화 실패는 무시 (운영 배포 환경 고려)
            }

            return (bytes, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[마스터 다운로드 경고] {Url} 수신 중 네트워크 오류 발생 (로컬 파일 Fallback 예정).", zipUrl);
            return (null, false);
        }
    }

    private async Task<List<StockMasterItem>> LoadLocalMastersAsync(CancellationToken cancellationToken)
    {
        var list = new List<StockMasterItem>(15000);
        list.AddRange(await _parser.ParseFileAsync(KospiFilePath, "KOSPI", "KRX", cancellationToken));
        list.AddRange(await _parser.ParseFileAsync(KosdaqFilePath, "KOSDAQ", "KRX", cancellationToken));
        list.AddRange(await _parser.ParseFileAsync(KonexFilePath, "KONEX", "KRX", cancellationToken));
        list.AddRange(await _parser.ParseFileAsync(NxtKospiFilePath, "NXT_KOSPI", "KRX", cancellationToken));

        list.AddRange(await _parser.ParseFileAsync(NasFilePath, "USA", "NAS", cancellationToken));
        list.AddRange(await _parser.ParseFileAsync(NysFilePath, "USA", "NYS", cancellationToken));
        list.AddRange(await _parser.ParseFileAsync(AmsFilePath, "USA", "AMS", cancellationToken));
        return list;
    }

    public bool AreLocalMastersUpToDate(out DateTime lastModifiedKst, out DateTime expectedReleaseKst)
    {
        lastModifiedKst = DateTime.MinValue;
        expectedReleaseKst = DateTime.MinValue;

        var paths = new[] { KospiFilePath, KosdaqFilePath, KonexFilePath, NxtKospiFilePath, NasFilePath, NysFilePath, AmsFilePath };
        foreach (var p in paths)
        {
            if (!File.Exists(p)) return false;
        }

        var oldestUtc = paths.Select(p => File.GetLastWriteTimeUtc(p)).Min();
        lastModifiedKst = TimeZoneInfo.ConvertTimeFromUtc(oldestUtc, KoreaTimeZone);
        var nowKst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, KoreaTimeZone);
        expectedReleaseKst = GetPreviousReleaseTimeKst(nowKst);

        return lastModifiedKst >= expectedReleaseKst;
    }

    public static DateTime GetPreviousReleaseTimeKst(DateTime nowKst)
    {
        var today = nowKst.Date;
        for (int i = ReleaseSchedule.Count - 1; i >= 0; i--)
        {
            var scheduledTime = today.Add(ReleaseSchedule[i]);
            if (nowKst >= scheduledTime)
            {
                return scheduledTime;
            }
        }

        var previousDay = today.AddDays(-1);
        while (previousDay.DayOfWeek == DayOfWeek.Saturday || previousDay.DayOfWeek == DayOfWeek.Sunday)
        {
            previousDay = previousDay.AddDays(-1);
        }

        return previousDay.Add(ReleaseSchedule[^1]);
    }

    public static DateTime GetNextReleaseTimeKst(DateTime nowKst)
    {
        var today = nowKst.Date;
        foreach (var schedule in ReleaseSchedule)
        {
            var scheduledTime = today.Add(schedule);
            if (scheduledTime > nowKst && nowKst.DayOfWeek != DayOfWeek.Saturday && nowKst.DayOfWeek != DayOfWeek.Sunday)
            {
                return scheduledTime;
            }
        }

        var nextDay = today.AddDays(1);
        while (nextDay.DayOfWeek == DayOfWeek.Saturday || nextDay.DayOfWeek == DayOfWeek.Sunday)
        {
            nextDay = nextDay.AddDays(1);
        }

        return nextDay.Add(ReleaseSchedule[0]);
    }
}
