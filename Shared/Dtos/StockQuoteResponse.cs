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

/// <summary>
/// 캔들 차트 단일 봉 데이터 DTO
/// </summary>
public record StockCandlePoint(
    long Time,        // 차트 X축 타임스탬프 (Unix Seconds, 표시 기준 시각)
    decimal Open,     // 시가
    decimal High,     // 고가
    decimal Low,      // 저가
    decimal Close,    // 종가
    long Volume       // 거래량
);

/// <summary>
/// 종목 캔들 차트 응답 DTO
/// </summary>
public record StockCandleResponse(
    string StockCode,
    string Period,      // 1M(분봉), D(일), W(주), M(월), Y(년)
    string Currency,
    bool IsIntraday,
    IReadOnlyList<StockCandlePoint> Candles,
    DateTime UpdatedAtUtc
);

/// <summary>
/// 종목 최근 체결 틱 단건 DTO
/// </summary>
public record StockTickItem(
    string Time,        // HH:MM:SS
    decimal Price,      // 체결가
    decimal PriceChange,// 전일 대비
    double ChangeRate,  // 등락률 (%)
    long Volume,        // 체결량
    string Currency,
    decimal Strength = 0m // 체결강도 (%)
);
