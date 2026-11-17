using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace StockLab.Common;

/// <summary>
/// 콘솔 출력 시 [체결됨](녹색), [거절됨](빨강), [취소됨](노랑) 말머리에 ANSI 색상을 입히는 포매터.
/// </summary>
public sealed class TradeColorConsoleFormatter : ConsoleFormatter, IDisposable
{
    public const string FormatterKey = "tradeColor";

    private const string AnsiGreen = "\x1b[32m";
    private const string AnsiRed = "\x1b[31m";
    private const string AnsiYellow = "\x1b[33m";
    private const string AnsiReset = "\x1b[0m";

    private readonly IDisposable? _optionsReloadToken;
    private ConsoleFormatterOptions _options;

    public TradeColorConsoleFormatter(IOptionsMonitor<ConsoleFormatterOptions> options)
        : base(FormatterKey)
    {
        _options = options.CurrentValue;
        _optionsReloadToken = options.OnChange(updated => _options = updated);
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception == null)
        {
            return;
        }

        var levelTag = GetShortLevel(logEntry.LogLevel);
        var now = _options.UseUtcTimestamp ? DateTime.UtcNow : DateTime.Now;
        var coloredMessage = ColorizeTradePrefix(message ?? string.Empty);

        textWriter.WriteLine($"[{now:HH:mm:ss}] [{levelTag}] [{logEntry.Category}] {coloredMessage}");
        if (logEntry.Exception != null)
        {
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }

    private static string ColorizeTradePrefix(string message)
    {
        if (message.StartsWith("[체결됨]", StringComparison.Ordinal))
            return $"{AnsiGreen}[체결됨]{AnsiReset}{message.Substring(5)}";

        if (message.StartsWith("[거절됨]", StringComparison.Ordinal))
            return $"{AnsiRed}[거절됨]{AnsiReset}{message.Substring(5)}";

        if (message.StartsWith("[취소됨]", StringComparison.Ordinal))
            return $"{AnsiYellow}[취소됨]{AnsiReset}{message.Substring(5)}";

        return message;
    }

    private static string GetShortLevel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "LOG"
    };

    public void Dispose() => _optionsReloadToken?.Dispose();
}

