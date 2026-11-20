using StockLab.Dtos;

namespace StockLab.Services.Master;

/// <summary>
/// 종목 마스터 메타데이터 조회 및 실시간 기준가 갱신을 위한 추상화 인터페이스.
/// Server는 전체 인메모리 스토어(StockMasterStore)로 구현하고,
/// Client는 Server API 선택 조회 + 로컬 TTL 캐시(RemoteStockMasterProvider)로 구현한다.
/// </summary>
public interface IStockMasterProvider
{
    StockMasterItem? TryGetCachedByCode(string code);
    ValueTask<StockMasterItem?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyDictionary<string, StockMasterItem>> GetByCodesAsync(IEnumerable<string> codes, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<StockMasterItem>> SearchByNameAsync(string query, string market = "ALL", CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<StockMasterItem>> GetByMarketAsync(string marketType, int limit = 20, CancellationToken cancellationToken = default);
    ValueTask<PagedResult<StockMasterItem>> BrowseByMarketAsync(string market, int page, int pageSize, string? query = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<StockMasterItem>> GetSnapshotAsync(CancellationToken cancellationToken = default);
    void UpdatePrice(string code, decimal newPrice);
}

