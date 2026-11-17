using System.Text;
using StockLab.Dtos;

namespace StockLab.Services.Master;

public class StockMasterParser
{
    private readonly Encoding _cp949;
    private readonly ILogger<StockMasterParser> _logger;

    public StockMasterParser(ILogger<StockMasterParser> logger)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _cp949 = Encoding.GetEncoding(949);
        _logger = logger;
    }

    /// <summary>
    /// 코스피 마스터 바이트 슬라이싱 파싱 (레코드 288B, 기준가 오프셋 102B)
    /// </summary>
    public List<StockMasterItem> ParseKospiBytes(ReadOnlySpan<byte> span)
    {
        return ParseFixedRecord(span, recordLength: 288, basePriceOffset: 102, marketType: "KOSPI", exchangeCode: "KRX");
    }

    /// <summary>
    /// 코스닥 마스터 바이트 슬라이싱 파싱 (레코드 282B, 기준가 오프셋 96B)
    /// </summary>
    public List<StockMasterItem> ParseKosdaqBytes(ReadOnlySpan<byte> span)
    {
        return ParseFixedRecord(span, recordLength: 282, basePriceOffset: 96, marketType: "KOSDAQ", exchangeCode: "KRX");
    }

    /// <summary>
    /// 코넥스 마스터 바이트 슬라이싱 파싱 (레코드 245B, 기준가 오프셋 63B)
    /// </summary>
    public List<StockMasterItem> ParseKonexBytes(ReadOnlySpan<byte> span)
    {
        return ParseFixedRecord(span, recordLength: 245, basePriceOffset: 63, marketType: "KONEX", exchangeCode: "KRX");
    }

    /// <summary>
    /// 넥스트 코스피 마스터 바이트 슬라이싱 파싱 (레코드 288B, 기준가 오프셋 102B)
    /// </summary>
    public List<StockMasterItem> ParseNxtKospiBytes(ReadOnlySpan<byte> span)
    {
        return ParseFixedRecord(span, recordLength: 288, basePriceOffset: 102, marketType: "NXT_KOSPI", exchangeCode: "KRX");
    }

    /// <summary>
    /// KIS 해외주식 마스터(.cod) TSV 탭(\t) 구분자 파싱 (인코딩: CP949)
    /// 컬럼: [0]국가(US) | [2]거래소(NAS/NYS/AMS) | [4]티커(AAPL) | [5]표준코드 | [6]한글명 | [7]영문명 | [8]증권구분 | [9]통화(USD) | [12]기준가
    /// </summary>
    public List<StockMasterItem> ParseOverseasCodBytes(ReadOnlySpan<byte> span, string fallbackExchangeCode)
    {
        var result = new List<StockMasterItem>(5000);
        var text = _cp949.GetString(span);
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = line.Split('\t');
            if (cols.Length < 7) continue;

            var exchangeCode = cols.Length > 2 && !string.IsNullOrWhiteSpace(cols[2]) ? cols[2].Trim() : fallbackExchangeCode;
            var symbol = cols.Length > 4 ? cols[4].Trim() : "";
            var stdCode = cols.Length > 5 ? cols[5].Trim() : symbol;
            var korName = cols.Length > 6 ? cols[6].Trim() : "";
            var engName = cols.Length > 7 ? cols[7].Trim() : "";
            var currency = cols.Length > 9 && !string.IsNullOrWhiteSpace(cols[9]) ? cols[9].Trim() : "USD";
            var rawPrice = cols.Length > 12 ? cols[12].Trim() : "0";

            decimal.TryParse(rawPrice, out var basePrice);

            // 한글명이 비어있으면 영문명 사용, 둘 다 있으면 '한글명(영문명)' 또는 '한글명' 사용
            var displayName = !string.IsNullOrWhiteSpace(korName) ? korName : engName;

            if (!string.IsNullOrWhiteSpace(symbol))
            {
                result.Add(new StockMasterItem(
                    ShortCode: symbol,
                    StandardCode: stdCode,
                    KoreanName: displayName,
                    SecurityGroup: "ST",
                    BasePrice: basePrice,
                    MarketType: "USA",
                    ExchangeCode: exchangeCode,
                    Currency: currency
                ));
            }
        }

        return result;
    }

    private List<StockMasterItem> ParseFixedRecord(ReadOnlySpan<byte> span, int recordLength, int basePriceOffset, string marketType, string exchangeCode)
    {
        var result = new List<StockMasterItem>(3000);
        int offset = 0;
        int totalLength = span.Length;

        while (offset < totalLength)
        {
            if (offset + recordLength > totalLength) break;

            var record = span.Slice(offset, recordLength);

            var shortCode = Encoding.ASCII.GetString(record.Slice(0, 9)).Trim();
            var stdCode = Encoding.ASCII.GetString(record.Slice(9, 12)).Trim();
            var korName = _cp949.GetString(record.Slice(21, 40)).Trim();
            var secGroup = Encoding.ASCII.GetString(record.Slice(61, 2)).Trim();

            var rawBasePrice = Encoding.ASCII.GetString(record.Slice(basePriceOffset, 9)).Trim();
            decimal.TryParse(rawBasePrice, out var basePrice);

            if (!string.IsNullOrWhiteSpace(shortCode))
            {
                result.Add(new StockMasterItem(
                    ShortCode: shortCode,
                    StandardCode: stdCode,
                    KoreanName: korName,
                    SecurityGroup: secGroup,
                    BasePrice: basePrice,
                    MarketType: marketType,
                    ExchangeCode: exchangeCode,
                    Currency: "KRW"
                ));
            }

            offset += recordLength;
            while (offset < totalLength && (span[offset] == (byte)'\r' || span[offset] == (byte)'\n'))
            {
                offset++;
            }
        }

        return result;
    }

    public async Task<List<StockMasterItem>> ParseFileAsync(string filePath, string marketType, string exchangeCode = "KRX", CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogError("[마스터 파일 오류] 해당 경로에 파일이 없습니다: {FilePath}", filePath);
            return new List<StockMasterItem>();
        }

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        return marketType.ToUpperInvariant() switch
        {
            "KOSDAQ" => ParseKosdaqBytes(bytes),
            "KONEX" => ParseKonexBytes(bytes),
            "NXT_KOSPI" => ParseNxtKospiBytes(bytes),
            "USA" => ParseOverseasCodBytes(bytes, exchangeCode),
            _ => ParseKospiBytes(bytes)
        };
    }
}
