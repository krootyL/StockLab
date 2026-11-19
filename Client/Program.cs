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

// 1. 클라이언트 로컬 개인 KIS API 설정 바인딩 (공통 키 폴백 없음 — 본인 로컬 키 필수)
builder.Services.Configure<KisSettings>(options =>
{
    options.AppKey = Environment.GetEnvironmentVariable("KIS_OPENAPI_AppKey") ?? string.Empty;
    options.AppSecret = Environment.GetEnvironmentVariable("KIS_OPENAPI_AppSecret") ?? string.Empty;
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

// 3. 클라이언트 기동 시 로컬 KIS 키 검증 및 마스터 데이터 적재 (키 미설정 시 접근 전면 제한)
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var kisSettings = app.Services.GetRequiredService<IOptions<KisSettings>>().Value;
var hasValidKisConfig = !string.IsNullOrWhiteSpace(kisSettings.AppKey) && !string.IsNullOrWhiteSpace(kisSettings.AppSecret);

if (!hasValidKisConfig)
{
    logger.LogError("[클라이언트 KIS 접근 제한] 로컬 KIS_OPENAPI_AppKey 또는 KIS_OPENAPI_AppSecret이 설정되지 않았습니다. 개인 KIS 키 없이는 모든 접근이 제한됩니다.");
}
else
{
    logger.LogInformation("[클라이언트 KIS 설정 성공] AppKey({KeyLen}자리), AppSecret({SecretLen}자리) 로드 완료.",
        kisSettings.AppKey.Length, kisSettings.AppSecret.Length);

    using var scope = app.Services.CreateScope();
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

app.UseMiddleware<GlobalExceptionMiddleware>();

// 로컬 KIS AppKey / AppSecret 미설정 시 모든 클라이언트 접근(UI, API, SignalR) 차단
app.Use(async (context, next) =>
{
    var settings = context.RequestServices.GetRequiredService<IOptions<KisSettings>>().Value;
    if (string.IsNullOrWhiteSpace(settings.AppKey) || string.IsNullOrWhiteSpace(settings.AppSecret))
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
        {
            throw new StockLabException(
                ErrorCode.KisConfigMissing,
                "로컬 KIS OpenAPI AppKey 또는 AppSecret이 설정되지 않아 접근이 제한됩니다.");
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            "접근 제한 (403 Forbidden): 로컬 KIS_OPENAPI_AppKey 및 KIS_OPENAPI_AppSecret 환경변수가 설정되지 않았습니다. 개인 KIS OpenAPI 키 설정 후 다시 실행해 주세요.");
        return;
    }

    await next();
});

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
