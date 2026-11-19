namespace StockLab.Dtos;

public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public static class PagedResult
{
    public static (int Page, int PageSize) Normalize(int page, int pageSize, int maxPageSize = 100)
    {
        var normalizedSize = Math.Clamp(pageSize, 1, maxPageSize);
        var normalizedPage = Math.Max(1, page);
        return (normalizedPage, normalizedSize);
    }

    public static PagedResult<T> Create<T>(IReadOnlyList<T> items, int page, int pageSize, int totalCount)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var clampedPage = Math.Clamp(page, 1, totalPages);
        return new PagedResult<T>(items, clampedPage, pageSize, totalCount, totalPages);
    }
}

