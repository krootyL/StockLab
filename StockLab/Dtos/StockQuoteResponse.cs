namespace StockLab.Dtos;

/// <summary>
/// 주식 실시간 현재가 상세 시세 응답 DTO (국내/해외 통합)
/// </summary>
public record StockQuoteResponse(
    string StockCode,          // 종목 코드 / 티커 (예: 005930, AAPL)
    string StockName,          // 종목 이름
    string MarketType,         // 시장 구분 (KOSPI, KOSDAQ, USA 등)
    string ExchangeCode,       // 거래소 코드 (KRX, NAS, NYS, AMS)
    string Currency,           // 통화 단위 (KRW, USD)
    decimal CurrentPrice,      // 현재가 / 현재체결가
    decimal PriceChange,       // 전일 대비 변동폭
    double ChangeRate,         // 등락률 (%)
    long Volume,               // 누적 거래량
    decimal TradingValue,      // 누적 거래대금
    decimal OpenPrice,         // 시가
    decimal HighPrice,         // 고가
    decimal LowPrice,          // 저가
    decimal BasePrice,         // 기준가 / 전일 종가
    DateTime UpdatedAtUtc      // 조회 시각 (UTC)
);
