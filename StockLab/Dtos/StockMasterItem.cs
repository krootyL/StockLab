namespace StockLab.Dtos;

public record StockMasterItem(
    string ShortCode,               // 단축코드 또는 해외 티커 (예: 005930, AAPL)
    string StandardCode,            // 표준코드 (예: KR7005930003, US0378331005)
    string KoreanName,              // 한글 종목명 (예: 삼성전자, 애플)
    string SecurityGroup,           // 증권그룹 (ST: 일반주권, EF: ETF, FS: 외국주 등)
    decimal BasePrice,              // 주식 기준가 (전일 종가)
    string MarketType,              // 시장 구분 ("KOSPI", "KOSDAQ", "KONEX", "USA")
    string ExchangeCode = "KRX",     // 거래소 코드 ("KRX", "NAS", "NYS", "AMS")
    string Currency = "KRW"          // 결제 통화 ("KRW", "USD")
);
