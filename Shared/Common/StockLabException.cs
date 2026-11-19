namespace StockLab.Common;

public class StockLabException : Exception
{
    public ErrorCode ErrorCode { get; }

    public StockLabException(ErrorCode errorCode, string? message = null, Exception? innerException = null)
        : base(message ?? errorCode.GetDefaultMessage(), innerException)
    {
        ErrorCode = errorCode;
    }
}

