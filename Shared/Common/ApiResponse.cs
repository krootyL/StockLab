namespace StockLab.Common;

public record ApiError(int Code, string Message);

public record ApiResponse<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public ApiError? Error { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public static ApiResponse<T> Ok(T data) => new()
    {
        Success = true,
        Data = data,
        Error = null
    };

    public static ApiResponse<T> Fail(ErrorCode errorCode, string? message = null) => new()
    {
        Success = false,
        Data = default,
        Error = new ApiError((int)errorCode, message ?? errorCode.GetDefaultMessage())
    };
}

