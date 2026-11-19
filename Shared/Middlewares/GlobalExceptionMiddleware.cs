using System.Text.Json;
using StockLab.Common;

namespace StockLab.Middlewares;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (StockLabException ex)
        {
            _logger.LogWarning(ex, "비즈니스 예외 발생 [{ErrorCode}]: {Message}", ex.ErrorCode, ex.Message);
            await WriteErrorResponseAsync(context, ex.ErrorCode, ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("[클라이언트 요청 취소] {Method} {Path}", context.Request.Method, context.Request.Path);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 499;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "처리되지 않은 예외 발생: {Message}", ex.Message);
            await WriteErrorResponseAsync(context, ErrorCode.InternalServerError, ErrorCode.InternalServerError.GetDefaultMessage());
        }
    }

    private static async Task WriteErrorResponseAsync(HttpContext context, ErrorCode errorCode, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = (int)errorCode.ToHttpStatusCode();
        context.Response.ContentType = "application/json; charset=utf-8";

        var response = ApiResponse<object>.Fail(errorCode, message);
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }
}

