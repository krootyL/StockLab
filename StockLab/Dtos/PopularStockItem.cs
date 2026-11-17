namespace StockLab.Dtos;

public record PopularStockItem(
    int Rank,                   // 거래량 순위 (1~20)
    string StockCode,           // 종목 단축코드 / 티커 (예: 005930, AAPL)
    string StockName,           // 한글 종목명
    decimal Price,              // 현재가
    decimal PriceChange,        // 전일 대비 변동폭
    double ChangeRate,          // 등락률 (%)
    long Volume,                // 누적 거래량
    string MarketType,          // 시장 구분 (KOSPI, KOSDAQ, USA 등)
    string ExchangeCode = "KRX",// 거래소 (KRX, NAS, NYS, AMS)
    string Currency = "KRW",    // 결제 통화 (KRW, USD)
    decimal TradingValue = 0    // 누적 거래대금 (국내: KRW, 해외: USD)
);
