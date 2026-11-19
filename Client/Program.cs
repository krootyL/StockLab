using System.Text.Json;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using StockLab.BackgroundServices;
using StockLab.Common;
using StockLab.Controllers;
using StockLab.Hubs;
using StockLab.Middlewares;
using StockLab.Services.Kis;
using StockLab.Services.Master;
using StockLab.Settings;

var builder = WebApplication.CreateBuilder(args);

// 0. 콘솔 색상 포매터 및 기능/날짜/크기(10MB)별 파일 로거 등록 (Client/Logs/)
builder.Logging.AddConsole(options => options.FormatterName = TradeColorConsoleFormatter.FormatterKey)
    .AddConsoleFormatter<TradeColorConsoleFormatter, ConsoleFormatterOptions>();
var logDirectory = Path.Combine(builder.Environment.ContentRootPath, "Logs");
builder.Logging.AddProvider(new DailyFileLoggerProvider(logDirectory));

// 1. 클라이언트 개인 KIS API 설정 바인딩 (KIS_CLIENT_* 우선, 미설정 시 KIS_OPENAPI_* 폴백)
builder.Services.Configure<KisSettings>(options =>
{
    var clientAppKey = Environment.GetEnvironmentVariable("KIS_CLIENT_AppKey");
    var clientAppSecret = Environment.GetEnvironmentVariable("KIS_CLIENT_AppSecret");

    options.AppKey = !string.IsNullOrWhiteSpace(clientAppKey)
        ? clientAppKey
        : (Environment.GetEnvironmentVariable("KIS_OPENAPI_AppKey") ?? string.Empty);
    options.AppSecret = !string.IsNullOrWhiteSpace(clientAppSecret)
        ? clientAppSecret
        : (Environment.GetEnvironmentVariable("KIS_OPENAPI_AppSecret") ?? string.Empty);
    options.RestBaseUrl = "https://openapi.koreainvestment.com:9443";
    options.WebSocketKey = "ws://ops.koreainvestment.com:21000";
});

// 2. KIS 직접 통신, 계정별 초당 호출 제한(18회), 마스터 동기화 및 SignalR 허브 등록
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
builder.Services.AddControllers()
    .AddApplicationPart(typeof(StockController).Assembly);
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<KisRateLimiter>();
builder.Services.AddTransient<KisRateLimitHandler>();
builder.Services.AddHttpClient<KisSessionService>().AddHttpMessageHandler<KisRateLimitHandler>();
builder.Services.AddHttpClient<StockMasterSyncService>().AddHttpMessageHandler<KisRateLimitHandler>();
builder.Services.AddHttpClient<KisRankingService>().AddHttpMessageHandler<KisRateLimitHandler>();
builder.Services.AddHttpClient<KisQuoteService>().AddHttpMessageHandler<KisRateLimitHandler>();

builder.Services.AddSingleton<StockMasterStore>();
builder.Services.AddSingleton<StockMasterParser>();
builder.Services.AddSingleton<KisSocketService>();

builder.Services.AddHostedService<StockMasterSyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KisSocketService>());

var app = builder.Build();

// 3. 클라이언트 기동 시 로컬 마스터 데이터 적재 및 KIS 키 검증
var logger = app.Services.GetRequiredService<ILogger<Program>>();

using (var scope = app.Services.CreateScope())
{
    var syncService = scope.ServiceProvider.GetRequiredService<StockMasterSyncService>();
    var (success, count, _) = await syncService.SyncMasterDataAsync(forceDownload: false);
    if (success)
    {
        logger.LogInformation("[클라이언트 기동] 마스터 데이터 {Count:N0}건이 메모리에 준비되었습니다.", count);
    }
    else
    {
        logger.LogWarning("[클라이언트 기동 경고] 초기 마스터 데이터 동기화에 실패했습니다.");
    }
}

var kisSettings = app.Services.GetRequiredService<IOptions<KisSettings>>().Value;
if (string.IsNullOrWhiteSpace(kisSettings.AppKey) || string.IsNullOrWhiteSpace(kisSettings.AppSecret))
{
    logger.LogWarning("[클라이언트 KIS 설정 경고] KIS_CLIENT_AppKey/AppSecret(또는 KIS_OPENAPI_*)이 설정되지 않았습니다.");
}
else
{
    logger.LogInformation("[클라이언트 KIS 설정 성공] AppKey({KeyLen}자리), AppSecret({SecretLen}자리) 로드 완료.",
        kisSettings.AppKey.Length, kisSettings.AppSecret.Length);
}

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/Dashboard/index.html"));
app.MapGet("/admin", () => Results.Redirect("/Admin/index.html"));
app.MapGet("/user", () => Results.Redirect("/User/index.html"));

// 클라이언트 정적 UI에 중앙 모의투자 API 서버 주소를 동적으로 주입 (http/https 프로토콜 일치 유지)
app.MapGet("/client-config.js", (HttpContext ctx, IConfiguration config) =>
{
    var explicitBaseUrl = config["Server:BaseUrl"];
    var targetUrl = !string.IsNullOrWhiteSpace(explicitBaseUrl)
        ? explicitBaseUrl.TrimEnd('/')
        : (ctx.Request.IsHttps
            ? (config["Server:HttpsBaseUrl"] ?? "https://localhost:7112")
            : (config["Server:HttpBaseUrl"] ?? "http://localhost:5087")).TrimEnd('/');

    var js = $"window.STOCKLAB_SERVER_URL = {JsonSerializer.Serialize(targetUrl)};";
    return Results.Text(js, "application/javascript; charset=utf-8");
});

app.MapControllers();
app.MapHub<StockQuoteHub>("/hubs/quote");

app.Run();
