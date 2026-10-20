// ===== 模拟银行程序入口：服务注册、中间件管道与端点映射 =====

using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MockBank.Api.Data;
using MockBank.Api.Domain;
using MockBank.Api.Endpoints;
using MockBank.Api.Middleware;
using MockBank.Api.Services;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicyName = "AllowAll";
const string HttpUrl = "http://localhost:5200";

// ---- 结构化日志：控制台 JSON 输出（关键业务动作另行落盘到 logs/mockbank-requests.log）----
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = false;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fffZ";
});

// ---- 跨域：允许 AI Agent Host 从任意来源调用模拟银行接口 ----
builder.Services.AddCors(options => options.AddPolicy(CorsPolicyName, policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders(MockScenarioMiddleware.HeaderName)));

// ---- JSON：枚举以字符串输出，便于 AI Agent 直接消费 ----
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---- OpenAPI：使用 ASP.NET Core 内置 ApiExplorer 元数据自建文档（无第三方依赖）----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<OpenApiDocumentBuilder>();

// ===== 模拟银行核心系统：每家银行一个独立数据库文件 =====
//
// 装配顺序有依赖，不能调换：
//   1. 配置项 MockBank:* —— 数据目录、要启用哪些行
//   2. 账本工厂 —— 负责建库、写种子数据、缓存账本实例
//   3. 每家启用银行的账本实例 —— 注册进容器，MockBankStore 靠它聚合
builder.Services.Configure<MockBankOptions>(
    builder.Configuration.GetSection(MockBankOptions.SectionName));

var mockBankOptions = builder.Configuration
    .GetSection(MockBankOptions.SectionName).Get<MockBankOptions>() ?? new MockBankOptions();

// 数据目录相对路径按内容根目录解析，保证从任何工作目录启动都落在同一处。
var dataDirectory = Path.IsPathRooted(mockBankOptions.DataDirectory)
    ? mockBankOptions.DataDirectory
    : Path.Combine(builder.Environment.ContentRootPath, mockBankOptions.DataDirectory);

builder.Services.AddSingleton(sp => new BankLedgerFactory(
    sp.GetRequiredService<IOptions<MockBankOptions>>(),
    sp.GetRequiredService<ILoggerFactory>(),
    dataDirectory));

// 只注册「启用」的银行。工厂内部按行代码缓存实例，
// 保证同一进程内 MockBankStore 拿到的和账本工厂的是同一批对象（写锁才能真正互斥）。
var requestedBankCodes = mockBankOptions.Banks
    .Where(code => !string.IsNullOrWhiteSpace(code))
    .Select(code => code.Trim())
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

foreach (var bank in BankCatalog.All
             .Where(b => requestedBankCodes.Count == 0 || requestedBankCodes.Contains(b.BankCode)))
{
    var capturedBank = bank;
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<BankLedgerFactory>().Get(capturedBank));
}

// ---- 应用服务 ----
builder.Services.AddSingleton<MockBankStore>();
builder.Services.AddSingleton<BusinessLogWriter>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<StatementService>();
builder.Services.AddScoped<CardService>();
builder.Services.AddScoped<WealthService>();
builder.Services.AddScoped<FundService>();

var app = builder.Build();

// ---- 管道：全局异常 -> 故障注入 -> 跨域 -> 端点 ----
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<MockScenarioMiddleware>();
app.UseCors(CorsPolicyName);

// ---- 启动即建库并写入各行种子数据（不等第一个请求触发）----
var store = app.Services.GetRequiredService<MockBankStore>();
app.Logger.LogInformation(
    "模拟银行核心系统已启动：{HttpUrl}，已装载 {BankCount} 家银行（{Banks}）",
    HttpUrl, store.Banks.Count, string.Join(" / ", store.Banks.Select(b => b.BankName)));

// ---- 回显可用的故障注入场景，便于调用方自检 ----
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Mock-Scenario-Supported"] = MockScenarioMiddleware.SupportedScenarios;
        return Task.CompletedTask;
    });
    await next(context);
});

// ---- 端点映射 ----
app.MapGet("/", () => Results.Redirect("/openapi/v1.json")).ExcludeFromDescription();
app.MapAccountEndpoints();
app.MapCustomerEndpoints();
app.MapTransferEndpoints();
app.MapStatementEndpoints();
app.MapCardEndpoints();
app.MapWealthEndpoints();
app.MapFundEndpoints();
app.MapBankEndpoints();
app.MapOpsEndpoints();

app.Logger.LogInformation("模拟银行核心系统已启动：{HttpUrl}（OpenAPI 文档 {HttpUrl}/openapi/v1.json）", HttpUrl, HttpUrl);
app.Run();
