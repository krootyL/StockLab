using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using StockLab.BackgroundServices;
using StockLab.Common;
using StockLab.Hubs;
using StockLab.Middlewares;
using StockLab.Persistence;
using StockLab.Services.Account;
using StockLab.Services.Auth;
using StockLab.Services.Kis;
using StockLab.Services.Master;
using StockLab.Services.Trading;
using StockLab.Settings;

var builder = WebApplication.CreateBuilder(args);

// 0. 콘솔 색상 포매터([체결됨]=녹색, [거절됨]=빨강, [취소됨]=노랑) 및 날짜별 파일 로거 등록
builder.Logging.AddConsole(options => options.FormatterName = TradeColorConsoleFormatter.FormatterKey)
    .AddConsoleFormatter<TradeColorConsoleFormatter, ConsoleFormatterOptions>();
var logDirectory = Path.Combine(builder.Environment.ContentRootPath, "Logs");
builder.Logging.AddProvider(new DailyFileLoggerProvider(logDirectory));

// 1. API 사용을 위한 설정값 바인딩
builder.Services.Configure<KisSettings>(options =>
{
    options.AppKey = Environment.GetEnvironmentVariable("KIS_OPENAPI_AppKey") ?? string.Empty;
    options.AppSecret = Environment.GetEnvironmentVariable("KIS_OPENAPI_AppSecret") ?? string.Empty;
    options.RestBaseUrl = "https://openapi.koreainvestment.com:9443";
    options.WebSocketKey = "ws://ops.koreainvestment.com:21000";
});

// 2. 서비스 등록
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (allowedOrigins is not { Length: > 0 })
{
    allowedOrigins = ["http://localhost:5187", "https://localhost:7212"];
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("ClientOrigins", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddMemoryCache();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
}

var sessionStore = new InMemorySessionStore();
builder.Services.AddSingleton(sessionStore);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "stocklab_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events.OnValidatePrincipal = async ctx =>
        {
            var sid = ctx.Principal?.FindFirst("sid")?.Value;
            if (!sessionStore.Validate(sid))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return ctx.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(ErrorCode.Unauthorized));
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ctx.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(ErrorCode.Forbidden));
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddScoped<GuestAuthService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<OrderMatchingService>();
builder.Services.AddScoped<IRealtimeOrderBookConsumer>(sp => sp.GetRequiredService<OrderMatchingService>());
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<TrafficSimulationService>();
builder.Services.AddSingleton<KisRateLimiter>();
builder.Services.AddTransient<KisRateLimitHandler>();
builder.Services.AddHttpClient<KisSessionService>().AddHttpMessageHandler<KisRateLimitHandler>();
builder.Services.AddHttpClient<StockMasterSyncService>().AddHttpMessageHandler<KisRateLimitHandler>();
builder.Services.AddHttpClient<KisRankingService>().AddHttpMessageHandler<KisRateLimitHandler>();
builder.Services.AddHttpClient<KisQuoteService>().AddHttpMessageHandler<KisRateLimitHandler>();

// 싱글톤 데이터 파서, 인메모리 스토어 및 KIS 소켓 서비스
builder.Services.AddSingleton<PendingList>();
builder.Services.AddSingleton<StockMasterStore>();
builder.Services.AddSingleton<IStockMasterProvider>(sp => sp.GetRequiredService<StockMasterStore>());
builder.Services.AddSingleton<StockMasterParser>();
builder.Services.AddSingleton<KisSocketService>();
builder.Services.AddSingleton<ForcedMatchingService>();

// 백그라운드 워커 등록
builder.Services.AddHostedService<StockMasterSyncBackgroundService>();
builder.Services.AddHostedService<OrderBookPollingBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KisSocketService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ForcedMatchingService>());

var app = builder.Build();

// 3. 서버 기동 시 초기 마스터 동기화, DB 스키마 초기화 및 KIS 설정 검증
var logger = app.Services.GetRequiredService<ILogger<Program>>();

if (string.IsNullOrWhiteSpace(connectionString))
{
    logger.LogWarning("[PostgreSQL 누락] ConnectionStrings:DefaultConnection이 설정되지 않아 DB 초기화를 건너뜁니다.");
}
else
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.MigratePreservingBaselineAsync(logger);

        var pendingList = app.Services.GetRequiredService<PendingList>();
        var pendingStockCount = await pendingList.SyncFromDatabaseAsync(db);
        logger.LogInformation("[초기화] PendingList 초기 DB 동기화 완료 (미체결 보유 종목={StockCount})", pendingStockCount);

        var authService = scope.ServiceProvider.GetRequiredService<GuestAuthService>();
        var adminLoginId = app.Configuration["Admin:LoginId"];
        var adminPass = app.Configuration["Admin:Password"];
        if (!string.IsNullOrWhiteSpace(adminLoginId) && !string.IsNullOrWhiteSpace(adminPass))
        {
            await authService.EnsureAdminAccountAsync(adminLoginId, adminPass);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[PostgreSQL 연결 실패] {Message}", ex.Message);
    }
}
var kisSettings = app.Services.GetRequiredService<IOptions<KisSettings>>().Value;

// [초기화] 서버 기동 시 최신 마스터 다운로드 시도 (실패 시 로컬 백업 Fallback)
var syncService = app.Services.GetRequiredService<StockMasterSyncService>();
var (syncSuccess, loadedCount, wasUpdated) = await syncService.SyncMasterDataAsync();

if (syncSuccess)
{
    logger.LogInformation("[서버 기동 완료] 마스터 데이터 {Count:N0}건이 메모리에 준비되었습니다.", loadedCount);
}
else
{
    logger.LogCritical("[서버 기동 경고] 마스터 데이터를 불러오지 못했습니다. 즉시 점검이 필요합니다.");
}

// [검증] KIS 설정 확인 후 토큰 준비 상태 점검
if (string.IsNullOrWhiteSpace(kisSettings.AppKey) || string.IsNullOrWhiteSpace(kisSettings.AppSecret))
{
    logger.LogWarning("[KIS 설정 누락] 환경변수 KIS_OPENAPI_AppKey / KIS_OPENAPI_AppSecret을 찾을 수 없어 토큰 사전 발급을 건너뜁니다.");
}
else
{
    logger.LogInformation("[KIS 설정 성공] AppKey({KeyLength}자리), AppSecret({SecretLength}자리) 로드 완료.",
        kisSettings.AppKey.Length, kisSettings.AppSecret.Length);

    var tokenService = app.Services.GetRequiredService<KisSessionService>();
    try
    {
        await tokenService.GetAccessTokenAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[토큰 검증 실패] {Message}", ex.Message);
    }
}

// 4. HTTP 요청 파이프라인 구성 (순수 API + SignalR 서버)
app.UseMiddleware<GlobalExceptionMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("ClientOrigins");
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<StockQuoteHub>("/hubs/quote");

app.Run();
