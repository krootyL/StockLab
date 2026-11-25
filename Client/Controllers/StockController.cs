using Microsoft.AspNetCore.Mvc;
using StockLab.Common;
using StockLab.Dtos;
using StockLab.Services.Kis;
using StockLab.Services.Master;

namespace StockLab.Client.Controllers;

/// <summary>
/// 클라이언트 전용 주식 시세·순위·검색 컨트롤러.
/// 마스터 원본 파일 동기화(sync-master)는 보유하지 않으며,
/// 종목 마스터 메타데이터는 IStockMasterProvider(RemoteStockMasterProvider)의 로컬 TTL 캐시를 통해
/// Server로부터 필요한 항목만 선택 조회한다.
/// </summary>
[ApiController]
[Route("api/stocks")]
public class StockController : ControllerBase
{
    private readonly IStockMasterProvider _masterProvider;
    private readonly KisRankingService _rankingService;
    private readonly KisQuoteService _quoteService;

    public StockController(
        IStockMasterProvider masterProvider,
        KisRankingService rankingService,
        KisQuoteService quoteService)
    {
        _masterProvider = masterProvider;
        _rankingService = rankingService;
        _quoteService = quoteService;
    }

    // 1. 실시간 거래대금/거래량 상위 인기 종목 캐시 풀 조회 (정렬/슬라이싱은 클라이언트 수행)
    [HttpGet("popular")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PopularStockItem>>>> GetPopularStocks(
        [FromQuery] string market = "ALL",
        [FromQuery] string sort = "value",
        CancellationToken cancellationToken = default)
    {
        var popularStocks = await _rankingService.GetPopularStocksAsync(market, sort, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<PopularStockItem>>.Ok(popularStocks));
    }

    // 2. 실시간 현재가 시세 조회 (클라이언트 개인 KIS 키 직접 호출)
    [HttpGet("{code}/quote")]
    public async Task<ActionResult<ApiResponse<StockQuoteResponse>>> GetStockQuote(
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "종목코드를 입력해주세요.");
        }

        var quote = await _quoteService.GetQuoteAsync(code, cancellationToken);
        return Ok(ApiResponse<StockQuoteResponse>.Ok(quote));
    }

    // 2-1. 종목 캔들 차트 데이터 조회 (분봉/일/주/월/년 - 클라이언트 개인 KIS 키 직접 호출)
    [HttpGet("{code}/candles")]
    public async Task<ActionResult<ApiResponse<StockCandleResponse>>> GetStockCandles(
        string code,
        [FromQuery] string period = "D",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "종목코드를 입력해주세요.");
        }

        var candles = await _quoteService.GetCandlesAsync(code, period, cancellationToken);
        return Ok(ApiResponse<StockCandleResponse>.Ok(candles));
    }

    // 2-2. 종목 최근 체결 틱 내역 조회 (클라이언트 개인 KIS 키 직접 호출)
    [HttpGet("{code}/ticks")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockTickItem>>>> GetStockTicks(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "종목코드를 입력해주세요.");
        }

        var ticks = await _quoteService.GetRecentTicksAsync(code, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<StockTickItem>>.Ok(ticks));
    }

    // 3. 단축코드로 마스터 메타데이터 조회 (로컬 TTL 캐시 히트 시 즉시 반환, 미스 시 Server 선택 조회)
    [HttpGet("{code}")]
    public async Task<ActionResult<ApiResponse<StockMasterItem>>> GetByCode(
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new StockLabException(ErrorCode.InvalidInput, "종목코드를 입력해주세요.");
        }

        var stock = await _masterProvider.GetByCodeAsync(code, cancellationToken)
            ?? throw new StockLabException(ErrorCode.StockNotFound, $"종목코드 '{code}'를 찾을 수 없습니다.");

        return Ok(ApiResponse<StockMasterItem>.Ok(stock));
    }

    // 4. 종목명 검색 (로컬 TTL 캐시 히트 시 즉시 반환, 미스 시 Server 선택 조회)
    [HttpGet("search")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockMasterItem>>>> SearchByName(
        [FromQuery] string name,
        [FromQuery] string market = "ALL",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(Array.Empty<StockMasterItem>()));
        }

        var results = await _masterProvider.SearchByNameAsync(name, market, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(results));
    }

    // 5. 시장/거래소/검색어별 전체 종목 페이징 탐색 (로컬 TTL 캐시 히트 시 즉시 반환, 미스 시 Server 선택 조회)
    [HttpGet("browse")]
    public async Task<ActionResult<ApiResponse<PagedResult<StockMasterItem>>>> BrowseByMarket(
        [FromQuery] string market = "ALL",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? query = null,
        CancellationToken cancellationToken = default)
    {
        var paged = await _masterProvider.BrowseByMarketAsync(market, page, pageSize, query, cancellationToken);
        return Ok(ApiResponse<PagedResult<StockMasterItem>>.Ok(paged));
    }

    // 6. 전체 마스터 캐시 스냅샷 조회 (ETag/If-None-Match 기반 304 지원: 변동 시에만 전체 전송)
    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot(CancellationToken cancellationToken = default)
    {
        var ifNoneMatch = Request.Headers.IfNoneMatch.FirstOrDefault();
        var state = await _masterProvider.GetSnapshotStateAsync(ifNoneMatch, cancellationToken);

        if (!string.IsNullOrWhiteSpace(state.ETag))
        {
            Response.Headers.ETag = state.ETag;
        }
        Response.Headers["X-Master-Version"] = state.MasterVersion.ToString();
        Response.Headers["X-Master-Synced-At"] = state.MasterSyncedAtUtc.ToString("O");
        Response.Headers.CacheControl = "no-cache";

        if (state.NotModified)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(ApiResponse<IReadOnlyList<StockMasterItem>>.Ok(state.Items));
    }
}

