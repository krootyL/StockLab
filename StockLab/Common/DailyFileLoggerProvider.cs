using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;

namespace StockLab.Common;

/// <summary>
/// 애플리케이션 및 런타임 로그를 성격별 하위 폴더(Main, Api, Trade, Admin, Sql, Debug) 아래에
/// 날짜별 텍스트 파일로 분리 기록하는 파일 로거 프로바이더.
/// </summary>
public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private enum LogChannel
    {
        Main,
        Api,
        Sql,
        Trade,
        Admin,
        Debug
    }

    private sealed class ChannelState
    {
        public StreamWriter? Writer;
        public int PartIndex;
        public string? LastEntrySignature;

        public void Reset()
        {
            Writer?.Dispose();
            Writer = null;
            PartIndex = 0;
            LastEntrySignature = null;
        }
    }

    [ThreadStatic]
    private static bool _inInternalDiagnosticWrite;

    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-9;]*m", RegexOptions.Compiled);

    private readonly string _logDirectory;
    private readonly LogLevel _minLevel;
    private readonly long _maxFileSizeBytes;
    private readonly ConcurrentDictionary<string, DailyFileLogger> _loggers = new();
    private readonly object _writeLock = new();
    private readonly Dictionary<LogChannel, ChannelState> _channels = new()
    {
        [LogChannel.Main] = new ChannelState(),
        [LogChannel.Api] = new ChannelState(),
        [LogChannel.Sql] = new ChannelState(),
        [LogChannel.Trade] = new ChannelState(),
        [LogChannel.Admin] = new ChannelState(),
        [LogChannel.Debug] = new ChannelState()
    };

    private readonly RuntimeDebugEventListener _runtimeEventListener;

    private string? _currentDate;
    private bool _disposed;

    private const string EfCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";
    private const string AdminControllerCategory = "StockLab.Controllers.AdminController";
    private const string ForcedMatchingCategory = "StockLab.BackgroundServices.ForcedMatchingService";
    private const string TrafficSimulationCategory = "StockLab.Services.Trading.TrafficSimulationService";
    private const string KisRateLimitCategory = "StockLab.Services.Kis.KisRateLimitHandler";
    private const string RuntimeCategory = "DotNetRuntime";
    private const long DefaultMaxFileSizeBytes = 10 * 1024 * 1024; // 10MB 단위 분할

    public DailyFileLoggerProvider(
        string logDirectory,
        LogLevel minLevel = LogLevel.Debug,
        long maxFileSizeBytes = DefaultMaxFileSizeBytes)
    {
        _logDirectory = logDirectory;
        _minLevel = minLevel;
        _maxFileSizeBytes = maxFileSizeBytes > 0 ? maxFileSizeBytes : DefaultMaxFileSizeBytes;

        EnsureSubdirectories();
        MigrateLegacyRootLogs();

        _runtimeEventListener = new RuntimeDebugEventListener(this);
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new DailyFileLogger(this, name));
    }

    internal bool IsEnabled(LogLevel logLevel) => !_disposed && logLevel >= _minLevel && logLevel != LogLevel.None;

    internal void WriteEntry(LogLevel logLevel, string categoryName, string message, Exception? exception)
    {
        if (!IsEnabled(logLevel)) return;

        var cleanMessage = AnsiEscapeRegex.Replace(message, string.Empty).TrimStart();
        var now = DateTime.Now;
        var dateKey = now.ToString("yyyy-MM-dd");
        var levelTag = GetShortLevel(logLevel);
        var formattedLine = $"[{now:HH:mm:ss}] [{levelTag}] [{categoryName}] {cleanMessage}";
        var channel = ResolveChannel(logLevel, categoryName, cleanMessage);

        // Sql 채널은 실제 데이터를 바꾸는 쓰기/스키마 변경 명령만 기록해 오버헤드를 최소화한다.
        if (channel == LogChannel.Sql && !IsSqlMutationOrDdl(cleanMessage))
        {
            return;
        }

        lock (_writeLock)
        {
            if (_disposed) return;

            var state = _channels[channel];
            if (channel != LogChannel.Api && channel != LogChannel.Trade)
            {
                var signature = $"{logLevel}|{categoryName}|{cleanMessage}|{exception?.GetType().FullName}";
                if (state.LastEntrySignature == signature)
                {
                    return;
                }
                state.LastEntrySignature = signature;
            }

            var writer = EnsureWriter(dateKey, channel);
            writer.WriteLine(formattedLine);
            if (exception != null)
            {
                writer.WriteLine(exception.ToString());
            }
        }
    }

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (_disposed || _inInternalDiagnosticWrite) return;

        try
        {
            _inInternalDiagnosticWrite = true;
            var ex = e.Exception;
            WriteEntry(
                LogLevel.Debug,
                RuntimeCategory,
                $"[1차 예외 감지] {ex.GetType().FullName}: {ex.Message}",
                null);
        }
        catch
        {
            // 진단 로깅 중 발생한 예외는 무시
        }
        finally
        {
            _inInternalDiagnosticWrite = false;
        }
    }

    private void EnsureSubdirectories()
    {
        Directory.CreateDirectory(_logDirectory);
        foreach (var channel in Enum.GetValues<LogChannel>())
        {
            var (folderName, _) = GetChannelLocation(channel);
            Directory.CreateDirectory(Path.Combine(_logDirectory, folderName));
        }
    }

    /// <summary>
    /// 기존 Logs 루트 폴더에 평면으로 쌓여 있던 로그 파일들을 성격별 하위 폴더로 이동한다.
    /// </summary>
    private void MigrateLegacyRootLogs()
    {
        try
        {
            foreach (var filePath in Directory.GetFiles(_logDirectory, "stocklab-*.log"))
            {
                var fileName = Path.GetFileName(filePath);
                var channel = fileName switch
                {
                    _ when fileName.StartsWith("stocklab-api-", StringComparison.OrdinalIgnoreCase) => LogChannel.Api,
                    _ when fileName.StartsWith("stocklab-sql-", StringComparison.OrdinalIgnoreCase) => LogChannel.Sql,
                    _ when fileName.StartsWith("stocklab-trade-", StringComparison.OrdinalIgnoreCase) => LogChannel.Trade,
                    _ when fileName.StartsWith("stocklab-admin-", StringComparison.OrdinalIgnoreCase) => LogChannel.Admin,
                    _ when fileName.StartsWith("stocklab-debug-", StringComparison.OrdinalIgnoreCase) => LogChannel.Debug,
                    _ => LogChannel.Main
                };

                var (folderName, _) = GetChannelLocation(channel);
                var destDir = Path.Combine(_logDirectory, folderName);
                Directory.CreateDirectory(destDir);
                var destPath = Path.Combine(destDir, fileName);

                try
                {
                    if (!File.Exists(destPath))
                    {
                        File.Move(filePath, destPath);
                    }
                    else
                    {
                        var content = File.ReadAllText(filePath, Encoding.UTF8);
                        File.AppendAllText(destPath, content, Encoding.UTF8);
                        File.Delete(filePath);
                    }
                }
                catch
                {
                    // 다른 프로세스가 점유 중인 파일은 다음 기동 시 이동
                }
            }
        }
        catch
        {
            // 마이그레이션 실패는 서버 기동을 막지 않음
        }
    }

    private static LogChannel ResolveChannel(LogLevel logLevel, string categoryName, string message)
    {
        if (categoryName == RuntimeCategory || logLevel <= LogLevel.Debug)
        {
            return LogChannel.Debug;
        }

        if (categoryName == EfCommandCategory && logLevel < LogLevel.Warning)
        {
            return LogChannel.Sql;
        }

        if (IsApiLog(categoryName, message))
        {
            return LogChannel.Api;
        }

        if (IsTradeLogMessage(message))
        {
            return LogChannel.Trade;
        }

        if (IsAdminLog(categoryName, message))
        {
            return LogChannel.Admin;
        }

        return LogChannel.Main;
    }

    private static (string FolderName, string FilePrefix) GetChannelLocation(LogChannel channel) => channel switch
    {
        LogChannel.Api => ("Api", "stocklab-api"),
        LogChannel.Sql => ("Sql", "stocklab-sql"),
        LogChannel.Trade => ("Trade", "stocklab-trade"),
        LogChannel.Admin => ("Admin", "stocklab-admin"),
        LogChannel.Debug => ("Debug", "stocklab-debug"),
        _ => ("Main", "stocklab")
    };

    private static bool IsApiLog(string categoryName, string message)
        => categoryName == KisRateLimitCategory
           || message.Contains("[KIS API]", StringComparison.Ordinal);

    private static bool IsSqlMutationOrDdl(string message)
        => message.Contains("\nINSERT ", StringComparison.OrdinalIgnoreCase)
           || message.Contains("\nUPDATE ", StringComparison.OrdinalIgnoreCase)
           || message.Contains("\nDELETE ", StringComparison.OrdinalIgnoreCase)
           || message.Contains("\nCREATE ", StringComparison.OrdinalIgnoreCase)
           || message.Contains("\nALTER ", StringComparison.OrdinalIgnoreCase)
           || message.Contains("\nDROP ", StringComparison.OrdinalIgnoreCase);

    private static bool IsTradeLogMessage(string message)
        => message.StartsWith("[주문 접수]", StringComparison.Ordinal)
           || message.StartsWith("[체결됨]", StringComparison.Ordinal)
           || message.StartsWith("[거절됨]", StringComparison.Ordinal)
           || message.StartsWith("[취소됨]", StringComparison.Ordinal);

    private static bool IsAdminLog(string categoryName, string message)
        => categoryName is AdminControllerCategory or ForcedMatchingCategory or TrafficSimulationCategory
           || message.StartsWith("[관리자 API]", StringComparison.Ordinal)
           || message.StartsWith("[강제 체결 워커]", StringComparison.Ordinal)
           || message.StartsWith("[더미 트래픽]", StringComparison.Ordinal)
           || message.StartsWith("[관리자 시드머니 조정]", StringComparison.Ordinal)
           || (message.StartsWith("[게스트 생성]", StringComparison.Ordinal) && message.Contains("__dummy_", StringComparison.Ordinal))
           || message.EndsWith("주문 실시간 알림 등록", StringComparison.Ordinal);

    private StreamWriter EnsureWriter(string dateKey, LogChannel channel)
    {
        if (_currentDate != dateKey)
        {
            foreach (var state in _channels.Values)
            {
                state.Reset();
            }
            _currentDate = dateKey;
        }

        var (folderName, prefix) = GetChannelLocation(channel);
        var channelDirectory = Path.Combine(_logDirectory, folderName);
        Directory.CreateDirectory(channelDirectory);

        var target = _channels[channel];
        return EnsureChannelWriter(channelDirectory, ref target.Writer, ref target.PartIndex, $"{prefix}-{dateKey}");
    }

    private StreamWriter EnsureChannelWriter(
        string channelDirectory,
        ref StreamWriter? writer,
        ref int partIndex,
        string baseFileName)
    {
        if (writer != null && writer.BaseStream.Length >= _maxFileSizeBytes)
        {
            writer.Dispose();
            writer = null;
            partIndex++;
        }

        if (writer == null)
        {
            while (true)
            {
                var suffix = partIndex == 0 ? string.Empty : $".{partIndex}";
                var candidatePath = Path.Combine(channelDirectory, $"{baseFileName}{suffix}.log");
                if (!File.Exists(candidatePath) || new FileInfo(candidatePath).Length < _maxFileSizeBytes)
                {
                    writer = CreateWriter(candidatePath);
                    break;
                }
                partIndex++;
            }
        }

        return writer;
    }

    private static StreamWriter CreateWriter(string filePath)
    {
        var stream = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
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

    public void Dispose()
    {
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        _runtimeEventListener.Dispose();

        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var state in _channels.Values)
            {
                state.Reset();
            }
        }
        _loggers.Clear();
    }

    private sealed class DailyFileLogger : ILogger
    {
        private readonly DailyFileLoggerProvider _provider;
        private readonly string _categoryName;

        public DailyFileLogger(DailyFileLoggerProvider provider, string categoryName)
        {
            _provider = provider;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception == null) return;

            _provider.WriteEntry(logLevel, _categoryName, message, exception);
        }
    }

    /// <summary>
    /// .NET 런타임의 스레드풀(TP Worker) 워커 수 증감/조정 이벤트(EventId 50)만 선별해 Debug 채널 파일에 기록한다.
    /// </summary>
    private sealed class RuntimeDebugEventListener : EventListener
    {
        private const EventKeywords ThreadingKeywords = (EventKeywords)0x10000;
        private readonly DailyFileLoggerProvider? _provider;

        public RuntimeDebugEventListener(DailyFileLoggerProvider provider)
        {
            _provider = provider;
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Informational, ThreadingKeywords);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (_provider == null || _inInternalDiagnosticWrite) return;

            // EventId 50: ThreadPoolWorkerThreadAdjustmentAdjustment (실제 워커 스레드 수 증감/조정 시에만 기록)
            if (eventData.EventId != 50) return;

            try
            {
                _inInternalDiagnosticWrite = true;
                var payloadSummary = eventData.Payload != null && eventData.Payload.Count > 0
                    ? string.Join(", ", eventData.Payload.Select((v, i) => $"{eventData.PayloadNames?[i] ?? $"p{i}"}={v}"))
                    : string.Empty;

                _provider.WriteEntry(
                    LogLevel.Debug,
                    RuntimeCategory,
                    $"[.NET TP Worker] {eventData.EventName} ({payloadSummary})",
                    null);
            }
            catch
            {
                // 무시
            }
            finally
            {
                _inInternalDiagnosticWrite = false;
            }
        }
    }
}
