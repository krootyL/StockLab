using System.Text.Json;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using StockLab.Client.Services.Kis;
using StockLab.Common;
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

// 1. 클라이언트 로컬 개인 KIS API 설정 바인딩 (기동 시에는 빈 상태로 시작하며, 로그인 확인 후에만 잠금 해제/적재)
builder.Services.Configure<KisSettings>(options =>
{
    options.AppKey = string.Empty;
    options.AppSecret = string.Empty;
    options.RestBaseUrl = "https://openapi.koreainvestment.com:9443";
    options.WebSocketKey = "ws://ops.koreainvestment.com:21000";
});

// 2. KIS 직접 통신, 계정별 초당 호출 제한(18회), 원격 마스터 TTL 캐시 프로바이더 및 SignalR 허브 등록
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<KisRateLimiter>();
builder.Services.AddTransient<KisRateLimitHandler>();
builder.Services.AddHttpClient<KisSessionService>().AddHttpMessageHandler<KisRateLimitHandler>();
builder.Services.AddHttpClient<KisRankingService>().AddHttpMessageHandler<KisRateLimitHandler>();
builder.Services.AddHttpClient<KisQuoteService>().AddHttpMessageHandler<KisRateLimitHandler>();
builder.Services.AddSingleton<KisProfileVaultService>();

// 마스터 파일(.mst/.cod)을 로컬에 두지 않고, Server API에서 필요한 종목만 선택 조회해 TTL 캐싱
builder.Services.AddHttpClient<RemoteStockMasterProvider>();
builder.Services.AddSingleton<IStockMasterProvider>(sp => sp.GetRequiredService<RemoteStockMasterProvider>());
builder.Services.AddSingleton<KisSocketService>();

if (!args.Contains("--kis-setup", StringComparer.OrdinalIgnoreCase))
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<KisSocketService>());
}

var app = builder.Build();

// 3. --kis-setup CLI 마법사 모드 (웹 서버 기동 시에는 로그인 전까지 키를 읽지 않고 잠금/둘러보기 상태로 시작)
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var vaultService = app.Services.GetRequiredService<KisProfileVaultService>();

if (args.Contains("--kis-setup", StringComparer.OrdinalIgnoreCase))
{
    vaultService.UnlockFromVault("*");
    await RunKisSetupCliAsync(vaultService);
    return;
}

logger.LogInformation("[클라이언트 시작] 둘러보기 모드로 시작합니다. 로그인 시 로컬 금고 파일(kis-profiles.vault)의 KIS 키가 잠금 해제됩니다.");

app.UseMiddleware<GlobalExceptionMiddleware>();

// 둘러보기 모드 개방: UI 정적 파일·스냅샷(/api/stocks/snapshot)·로컬 금고(/api/kis/*)는 허용하고,
// 실제 KIS OpenAPI 호출이 필요한 엔드포인트만 키 미설정 시 403 KisConfigMissing 차단
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isKisEndpoint =
        path.StartsWithSegments("/hubs") ||
        path.StartsWithSegments("/api/stocks/popular") ||
        (path.StartsWithSegments("/api/stocks") && path.Value != null &&
            (path.Value.EndsWith("/quote", StringComparison.OrdinalIgnoreCase) ||
             path.Value.EndsWith("/candles", StringComparison.OrdinalIgnoreCase) ||
             path.Value.EndsWith("/ticks", StringComparison.OrdinalIgnoreCase)));

    if (isKisEndpoint)
    {
        var settings = context.RequestServices.GetRequiredService<IOptions<KisSettings>>().Value;
        if (string.IsNullOrWhiteSpace(settings.AppKey) || string.IsNullOrWhiteSpace(settings.AppSecret))
        {
            throw new StockLabException(
                ErrorCode.KisConfigMissing,
                "로컬 KIS OpenAPI AppKey 또는 AppSecret이 연동되지 않아 실시간 시세/순위/호가 접근이 제한됩니다.");
        }
    }

    await next();
});

// 관리자 페이지(/admin, /Admin/*) 비인가 접근 시 리다이렉트가 아닌 404 Not Found 반환 (페이지 존재 및 정적 소스 은닉)
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase))
    {
        var isAdmin = false;
        var cookieHeader = context.Request.Headers.Cookie.ToString();

        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            try
            {
                var config = context.RequestServices.GetRequiredService<IConfiguration>();
                var serverBase = config["Server:BaseUrl"];
                if (string.IsNullOrWhiteSpace(serverBase))
                {
                    serverBase = config["Server:HttpBaseUrl"] ?? "http://localhost:5087";
                }

                var httpFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
                using var client = httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(3);

                using var req = new HttpRequestMessage(HttpMethod.Get, $"{serverBase.TrimEnd('/')}/api/auth/me");
                req.Headers.Add("Cookie", cookieHeader);

                using var res = await client.SendAsync(req, context.RequestAborted);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(context.RequestAborted), cancellationToken: context.RequestAborted);
                    if (doc.RootElement.TryGetProperty("success", out var succ) && succ.GetBoolean() &&
                        doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("type", out var typeProp) &&
                        typeProp.TryGetInt32(out var userType) && userType == 99)
                    {
                        isAdmin = true;
                    }
                }
            }
            catch
            {
                isAdmin = false;
            }
        }

        if (!isAdmin)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }

    await next();
});

app.MapGet("/", () => Results.Redirect("/Dashboard/index.html"));
app.MapGet("/auth", () => Results.Redirect("/Dashboard/index.html"));
app.MapGet("/Auth/index.html", () => Results.Redirect("/Dashboard/index.html"));
app.MapGet("/admin", () => Results.Redirect("/Admin/index.html"));
app.MapGet("/user", () => Results.Redirect("/User/index.html"));

app.UseDefaultFiles();
app.UseStaticFiles();

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

static async Task RunKisSetupCliAsync(KisProfileVaultService vault)
{
    Console.WriteLine("==============================================");
    Console.WriteLine("  StockLab KIS API 프로필 금고 (DPAPI 암호화)");
    Console.WriteLine("==============================================");

    while (true)
    {
        var status = vault.GetStatus("*");
        Console.WriteLine();
        Console.WriteLine($"현재 상태: {(status.IsConnected ? $"연동됨 [{status.ActiveProfileName}] (출처: {status.Source})" : "미연동 (둘러보기 모드)")}");
        Console.WriteLine($"저장된 프로필 ({status.Profiles.Count}개):");
        for (int i = 0; i < status.Profiles.Count; i++)
        {
            var p = status.Profiles[i];
            var mark = string.Equals(p.Id, status.ActiveProfileId, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            var tok = p.HasValidToken ? $"토큰유효(~{p.ExpiresAtUtc?.ToLocalTime():MM-dd HH:mm})" : "토큰만료/미발급";
            Console.WriteLine($"  [{i + 1}] {mark} {p.Name} - {tok}");
        }

        Console.WriteLine();
        Console.WriteLine("메뉴: [1] 프로필 추가  [2] 활성 프로필 전환  [3] 프로필 삭제  [0] 종료");
        Console.Write("선택 > ");
        var choice = Console.ReadLine()?.Trim();

        if (choice == "0" || string.IsNullOrEmpty(choice)) break;

        try
        {
            if (choice == "1")
            {
                Console.Write("프로필 이름 (예: 본계정): ");
                var name = Console.ReadLine()?.Trim() ?? "기본 계정";
                Console.Write("허용 계정 UID (쉼표 구분, 비우면 전체 허용): ");
                var rawOwners = Console.ReadLine()?.Trim() ?? string.Empty;
                var owners = rawOwners.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Console.Write("KIS AppKey (36자리): ");
                var appKey = Console.ReadLine()?.Trim() ?? string.Empty;
                Console.Write("KIS AppSecret (180자리): ");
                var appSecret = Console.ReadLine()?.Trim() ?? string.Empty;

                Console.WriteLine("KIS 서버에서 1일 접근토큰 발급 검증 중...");
                await vault.CreateAndActivateAsync("*", name, appKey, appSecret, owners);
                Console.WriteLine($"완료! '{name}' 프로필이 DPAPI 금고에 암호화 저장 및 활성화되었습니다.");
            }
            else if (choice == "2")
            {
                Console.Write("전환할 번호: ");
                if (int.TryParse(Console.ReadLine()?.Trim(), out var num) && num >= 1 && num <= status.Profiles.Count)
                {
                    var target = status.Profiles[num - 1];
                    await vault.SelectProfileAsync(target.Id, "*");
                    Console.WriteLine($"'{target.Name}' 프로필로 전환 완료.");
                }
            }
            else if (choice == "3")
            {
                Console.Write("삭제할 번호: ");
                if (int.TryParse(Console.ReadLine()?.Trim(), out var num) && num >= 1 && num <= status.Profiles.Count)
                {
                    var target = status.Profiles[num - 1];
                    await vault.DeleteProfileAsync(target.Id, "*");
                    Console.WriteLine($"'{target.Name}' 프로필이 삭제되었습니다.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[오류] {ex.Message}");
        }
    }
}
