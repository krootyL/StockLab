using Microsoft.AspNetCore.Mvc;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Kis;
using StockLab.Services.Master;

namespace StockLab.Controllers;

[ApiController]
[Route("api/stocks")]
public class StockController : ControllerBase
{
    private readonly StockMasterStore _store;
    private readonly StockMasterSyncService _syncService;
    private readonly KisRankingService _rankingService;
    private readonly KisQuoteService _quoteService;

    public StockController(
        StockMasterStore store,
        StockMasterSyncService syncService,
        KisRankingService rankingService,
        KisQuoteService quoteService)
    {
        _store = store;
        _syncService = syncService;
        _rankingService = rankingService;
        _quoteService = quoteService;
    }

    // 0. 실시간 거래대금/거래량 상위 인기 종목 캐시 풀 조회 (정렬/슬라이싱은 클라이언트 수행): GET /api/stocks/popular?market=ALL&sort=value
    [HttpGet("popular")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PopularStockItem>>>> GetPopularStocks(
        [FromQuery] string market = "ALL",
        [FromQuery] string sort = "value",
        CancellationToken cancellationToken = default)
    {
        var popularStocks = await _rankingService.GetPopularStocksAsync(market, sort, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<PopularStockItem>>.Ok(popularStocks));
    }

    // 1. 실시간 현재가 시세 조회 (3초 단기 캐싱): GET /api/stocks/005930/quote
    [HttpGet("{code}/quote")]
    public async Task<ActionResult<ApiResponse<StockQuoteResponse>>> GetStockQuote(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "종목코드를 입력해주세요.");
        }

        var quote = await _quoteService.GetQuoteAsync(code, cancellationToken);
        return Ok(ApiResponse<StockQuoteResponse>.Ok(quote));
    }

    // 2. 단축코드로 마스터 메타데이터 조회: GET /api/stocks/005930
    [HttpGet("{code}")]
    public ActionResult<ApiResponse<StockMasterItem>> GetByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "종목코드를 입력해주세요.");
        }

        var stock = _store.GetByCode(code)
            ?? throw new StockLabException(ErrorCode.StockNotFound, $"종목코드 '{code}'를 찾을 수 없습니다.");

        return Ok(ApiResponse<StockMasterItem>.Ok(stock));
    }

    // 2. 종목명 검색: GET /api/stocks/search?name=삼성&market=ALL
    [HttpGet("search")]
    public ActionResult<ApiResponse<IReadOnlyList<StockMasterItem>>> SearchByName(
        [FromQuery] string name,
        [FromQuery] string market = "ALL")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(Array.Empty<StockMasterItem>()));
        }

        var results = _store.SearchByName(name, market);
        
        // 검색 결과가 0건인 것은 오류가 아니라 정상 검색의 빈 결과이므로 200 OK 반환
        return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(results));
    }

    // 2-1. 다건 단축코드 선택 일괄 조회 (클라이언트 인기순위/대장주 매핑용, 최대 50건): GET /api/stocks/batch?codes=005930,000660
    [HttpGet("batch")]
    public ActionResult<ApiResponse<IReadOnlyList<StockMasterItem>>> GetBatchByCodes([FromQuery] string codes)
    {
        if (string.IsNullOrWhiteSpace(codes))
        {
            return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(Array.Empty<StockMasterItem>()));
        }

        var codeList = codes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50);

        var items = _store.GetByCodes(codeList).Values.ToList();
        return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(items));
    }

    // 2-2. 특정 시장 종목 선택 조회 (코넥스 등 시장별 상위 N건, 최대 50건): GET /api/stocks/market/KONEX?limit=20
    [HttpGet("market/{marketType}")]
    public ActionResult<ApiResponse<IReadOnlyList<StockMasterItem>>> GetByMarket(
        string marketType,
        [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(marketType))
        {
            return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(Array.Empty<StockMasterItem>()));
        }

        var clampedLimit = Math.Clamp(limit, 1, 50);
        var items = _store.GetByMarket(marketType, clampedLimit);
        return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(items));
    }

    // 2-3. 시장/거래소/검색어별 전체 종목 페이징 탐색: GET /api/stocks/browse?market=ALL&page=1&pageSize=10&query=
    [HttpGet("browse")]
    public ActionResult<ApiResponse<PagedResult<StockMasterItem>>> BrowseByMarket(
        [FromQuery] string market = "ALL",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? query = null)
    {
        var paged = _store.BrowseByMarket(market, page, pageSize, query);
        return Ok(ApiResponse<PagedResult<StockMasterItem>>.Ok(paged));
    }

    // 2-4. 전체 마스터 캐시 스냅샷 조회: GET /api/stocks/snapshot
    [HttpGet("snapshot")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockMasterItem>>>> GetSnapshot(CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.GetSnapshotAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(snapshot));
    }

    // 3. 관리자/수동 마스터 데이터 동기화: POST /api/stocks/sync-master?force=true
    [HttpPost("sync-master")]
    public async Task<ActionResult<ApiResponse<object>>> SyncMaster([FromQuery] bool force = false, CancellationToken cancellationToken = default)
    {
        var (success, totalCount, wasUpdated) = await _syncService.SyncMasterDataAsync(forceDownload: force, cancellationToken);

        if (!success)
        {
            throw new StockLabException(ErrorCode.InternalServerError, "마스터 데이터 동기화에 실패했습니다. 서버 로그를 확인해주세요.");
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            Message = wasUpdated 
                ? "종목 마스터가 신규 버전으로 성공적으로 갱신되었습니다." 
                : "이미 최신 버전입니다 (변경 사항 없음 - 304 Not Modified).",
            TotalStocks = totalCount,
            WasUpdated = wasUpdated,
            ForcedDownload = force,
            SyncedAtUtc = DateTime.UtcNow
        }));
    }
}