namespace StockLab.Services.Trading;

/// <summary>
/// 국내(KRX) 및 미국(NAS/NYS/AMS) 시장 운영 시간 판별 헬퍼
/// </summary>
public static class MarketSessionHelper
{
    private static readonly TimeZoneInfo KoreaTimeZone = ResolveTimeZone("Korea Standard Time", "Asia/Seoul");
    private static readonly TimeZoneInfo EasternTimeZone = ResolveTimeZone("Eastern Standard Time", "America/New_York");

    private static readonly TimeSpan KrOpen = new(9, 0, 0);
    private static readonly TimeSpan KrClose = new(15, 20, 0);

    // 미국 프리마켓(04:00) ~ 애프터마켓(20:00) ET (서머타임 자동 반영)
    private static readonly TimeSpan UsOpen = new(4, 0, 0);
    private static readonly TimeSpan UsClose = new(20, 0, 0);

    public static bool IsMarketOpen(string marketType, DateTime utcNow)
    {
        var isUs = string.Equals(marketType, "USA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(marketType, "US", StringComparison.OrdinalIgnoreCase)
            || string.Equals(marketType, "NAS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(marketType, "NYS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(marketType, "AMS", StringComparison.OrdinalIgnoreCase);

        if (isUs)
        {
            var etNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, EasternTimeZone);
            if (etNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                return false;
            }
            var tod = etNow.TimeOfDay;
            return tod >= UsOpen && tod < UsClose;
        }

        var kstNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, KoreaTimeZone);
        if (kstNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }
        var krTod = kstNow.TimeOfDay;
        return krTod >= KrOpen && krTod < KrClose;
    }

    public static TimeSpan GetDurationUntilNextDomesticOpen(DateTime utcNow)
    {
        var kst = TimeZoneInfo.ConvertTimeFromUtc(utcNow, KoreaTimeZone);
        var targetDate = kst.Date;

        if (kst.TimeOfDay >= KrClose || kst.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            targetDate = targetDate.AddDays(1);
            while (targetDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                targetDate = targetDate.AddDays(1);
            }
        }

        var nextOpen = targetDate.Add(KrOpen);
        var duration = nextOpen - kst;
        return duration > TimeSpan.Zero ? duration : TimeSpan.FromMinutes(30);
    }

    public static TimeSpan GetDurationUntilNextUsOpen(DateTime utcNow)
    {
        var et = TimeZoneInfo.ConvertTimeFromUtc(utcNow, EasternTimeZone);
        var targetDate = et.Date;

        if (et.TimeOfDay >= UsClose || et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            targetDate = targetDate.AddDays(1);
            while (targetDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                targetDate = targetDate.AddDays(1);
            }
        }

        var nextOpen = targetDate.Add(UsOpen);
        var duration = nextOpen - et;
        return duration > TimeSpan.Zero ? duration : TimeSpan.FromMinutes(30);
    }

    private static TimeZoneInfo ResolveTimeZone(string windowsId, string ianaId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
    }
}

