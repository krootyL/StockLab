using System.Net;

namespace StockLab.Common;

public enum ErrorCode
{
    // 공통 / 입력 오류 (400)
    InvalidInput = 4000,
    InsufficientBalance = 4001,

    // 인증 / 인가 오류 (401, 403)
    Unauthorized = 4010,
    Forbidden = 4030,

    // 리소스 없음 / 충돌 (404, 409)
    StockNotFound = 4040,
    AccountNotFound = 4041,
    ConcurrencyConflict = 4090,

    // 외부 연동 / 설정 오류 (500, 502)
    KisConfigMissing = 5001,
    KisAuthFailed = 5021,
    InternalServerError = 5000
}

public static class ErrorCodeExtensions
{
    public static HttpStatusCode ToHttpStatusCode(this ErrorCode errorCode) => errorCode switch
    {
        ErrorCode.InvalidInput => HttpStatusCode.BadRequest,
        ErrorCode.InsufficientBalance => HttpStatusCode.BadRequest,
        ErrorCode.Unauthorized => HttpStatusCode.Unauthorized,
        ErrorCode.Forbidden => HttpStatusCode.Forbidden,
        ErrorCode.StockNotFound => HttpStatusCode.NotFound,
        ErrorCode.AccountNotFound => HttpStatusCode.NotFound,
        ErrorCode.ConcurrencyConflict => HttpStatusCode.Conflict,
        ErrorCode.KisConfigMissing => HttpStatusCode.Forbidden,
        ErrorCode.KisAuthFailed => HttpStatusCode.BadGateway,
        _ => HttpStatusCode.InternalServerError
    };

    public static string GetDefaultMessage(this ErrorCode errorCode) => errorCode switch
    {
        ErrorCode.InvalidInput => "잘못된 요청 파라미터입니다.",
        ErrorCode.InsufficientBalance => "잔고(예수금)가 부족합니다.",
        ErrorCode.Unauthorized => "로그인이 필요합니다.",
        ErrorCode.Forbidden => "접근 권한이 없습니다.",
        ErrorCode.StockNotFound => "요청한 종목 정보를 찾을 수 없습니다.",
        ErrorCode.AccountNotFound => "계좌 정보를 찾을 수 없습니다.",
        ErrorCode.ConcurrencyConflict => "동시 요청 충돌이 발생했습니다. 다시 시도해 주세요.",
        ErrorCode.KisConfigMissing => "KIS OpenAPI AppKey 또는 AppSecret 설정이 누락되어 접근이 제한됩니다.",
        ErrorCode.KisAuthFailed => "KIS OpenAPI 인증 토큰 발급에 실패했습니다.",
        _ => "서버 내부 오류가 발생했습니다."
    };
}

