namespace StockLab.Persistence.Entities;

public enum UserType
{
    Local = 1,   // 사이트 직접 가입 계정
    Discord = 2, // 디스코드 OAuth2 인증 계정
    // 3 = 추후 Naver, Google, Email 등 추가 인증 수단 예약
    Admin = 99   // 개발자 / 관리자 특수계정
}

public enum OrderSide
{
    Buy = 1,
    Sell = 2
}

public enum OrderType
{
    Market = 1,
    Limit = 2
}

public enum OrderStatus
{
    Pending = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Cancelled = 4,
    Rejected = 5
}
