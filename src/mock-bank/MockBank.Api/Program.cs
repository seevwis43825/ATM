// ===== MockBank.Api 程序入口：服务注册、中间件管道与端点映射 =====

using System.Text.Json.Serialization;
using MockBank.Api.Data;
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

// ---- 应用服务 ----
builder.Services.AddSingleton<MockBankStore>();
builder.Services.AddSingleton<BusinessLogWriter>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<StatementService>();
builder.Services.AddScoped<CardService>();
builder.Services.AddScoped<WealthService>();

var app = builder.Build();

// ---- 管道：全局异常 -> 故障注入 -> 跨域 -> 端点 ----
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<MockScenarioMiddleware>();
app.UseCors(CorsPolicyName);

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
app.MapTransferEndpoints();
app.MapStatementEndpoints();
app.MapCardEndpoints();
app.MapWealthEndpoints();
app.MapOpsEndpoints();

app.Logger.LogInformation("模拟银行核心系统已启动：{HttpUrl}（OpenAPI 文档 {HttpUrl}/openapi/v1.json）", HttpUrl, HttpUrl);
app.Run();
